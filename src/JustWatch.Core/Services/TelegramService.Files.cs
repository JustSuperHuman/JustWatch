using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using JustWatch.Core.Models;
using Minio;
using Minio.DataModel.Args;

namespace JustWatch.Core.Services;

// ---------------------------------------------------------------------
// Files and media — POST /api/telegram/files.
//
//   ≤ 50 MB   uploaded straight to Telegram (photo, video, document, …). Videos are
//             probed with ffmpeg first so Telegram gets their real width/height and
//             a thumbnail — without those it previews portrait video squashed.
//   > 50 MB   over the Bot API limit: uploaded to the storage bucket and posted as a link.
//   url       handed to Telegram to fetch; if it can't (20 MB fetch limit), posted as a link.
// ---------------------------------------------------------------------
public partial class TelegramService
{
    /// <summary>Bot API multipart upload ceiling. Anything larger goes via storage.</summary>
    public const long MaxUploadBytes = 50L * 1024 * 1024;

    /// <summary>Bot API caption ceiling; longer captions follow the file as a separate message.</summary>
    private const int MaxCaption = 1024;

    public static readonly string[] MediaTypes = ["photo", "video", "animation", "audio", "voice", "document"];

    /// <summary>A file to send: bytes in memory, a file on disk, or a URL Telegram fetches itself.</summary>
    public sealed record OutgoingFile(
        string FileName,
        byte[]? Bytes = null,
        string? LocalPath = null,
        string? Url = null,
        string? ContentType = null)
    {
        public long Length => Bytes?.LongLength ?? (LocalPath is not null ? new FileInfo(LocalPath).Length : 0);
    }

    /// <summary>True when oversized files can be hosted in the bucket and sent as links.</summary>
    public bool StorageConfigured => _config.Storage.IsConfigured;

    /// <summary>Picks the Telegram media type from the file extension; anything unrecognised is a document.</summary>
    public static string DetectMediaType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".webp" => "photo",
            ".gif" => "animation",
            ".mp4" or ".mov" or ".m4v" => "video",
            ".mp3" or ".m4a" or ".aac" or ".flac" or ".wav" => "audio",
            ".ogg" or ".oga" or ".opus" => "voice",
            _ => "document"
        };

    /// <summary>Width/height/duration and a thumbnail, so Telegram previews the video at its real shape.</summary>
    private sealed record VideoMeta(int Width, int Height, int Duration, byte[]? Thumbnail);

    /// <summary>
    /// Sends one file to each group. The storage upload and ffmpeg probe happen once,
    /// not per group.
    /// </summary>
    public async Task<IReadOnlyList<GroupSendResult>> SendFileToGroupsAsync(
        IEnumerable<TelegramGroup> groups,
        OutgoingFile file,
        string type,
        string? caption = null,
        string? parseMode = null,
        bool silent = false,
        CancellationToken ct = default)
    {
        var targets = groups.ToList();

        if (file.Url is null && file.Length > MaxUploadBytes)
        {
            if (!StorageConfigured)
                return Fail($"{FormatSize(file.Length)} is over Telegram's 50 MB limit, and telegram.storage isn't configured to host it as a link.");

            string url;
            try { url = await UploadToStorageAsync(file, ct); }
            catch (Exception ex)
            {
                Log?.Invoke($"Storage upload of '{file.FileName}' failed: {ex.Message}");
                return Fail($"Upload to storage failed: {ex.Message}");
            }
            return await SendLinkAsync(targets, file.FileName, file.Length, url, caption, silent, ct);
        }

        var meta = type is "video" or "animation" ? await ProbeVideoAsync(file, ct) : null;

        var results = new List<GroupSendResult>();
        foreach (var group in targets)
            results.Add(await SendFileToGroupAsync(group, file, type, caption, parseMode, silent, meta, ct));
        return results;

        List<GroupSendResult> Fail(string error) =>
            targets.Select(g => new GroupSendResult(g.Id, false, null, error)).ToList();
    }

    /// <summary>
    /// One file to one group. If Telegram rejects the media type (too big to preview,
    /// odd codec) it's retried as a document; if it can't fetch a URL at all, the URL
    /// is posted as a link so the file still reaches the chat.
    /// </summary>
    private async Task<GroupSendResult> SendFileToGroupAsync(
        TelegramGroup group, OutgoingFile file, string type, string? caption,
        string? parseMode, bool silent, VideoMeta? meta, CancellationToken ct)
    {
        var token = string.IsNullOrWhiteSpace(group.BotToken) ? _config.BotToken : group.BotToken;
        if (string.IsNullOrWhiteSpace(token))
            return new GroupSendResult(group.Id, false, null, "No bot token configured for this group.");

        // Long captions go out as a follow-up message rather than being cut off.
        var overflow = caption is { Length: > MaxCaption } ? caption : null;
        var inlineCaption = overflow is null ? caption : null;

        try
        {
            var (ok, messageId, error) = await PostFileAsync(token, group, file, type, inlineCaption, parseMode, silent, meta, ct);
            if (!ok && type != "document")
            {
                Log?.Invoke($"Telegram group '{group.Id}' rejected {type} ({error}); retrying as document");
                (ok, messageId, error) = await PostFileAsync(token, group, file, "document", inlineCaption, parseMode, silent, null, ct);
                type = "document";
            }

            if (!ok && file.Url is not null)
            {
                Log?.Invoke($"Telegram group '{group.Id}' couldn't fetch {file.Url} ({error}); posting it as a link");
                return (await SendLinkAsync([group], file.FileName, null, file.Url, caption, silent, ct))[0];
            }

            if (!ok) return new GroupSendResult(group.Id, false, null, error);

            if (overflow is not null)
                await SendToGroupAsync(group, overflow, parseMode, disableWebPagePreview: true, silent: silent, ct: ct);

            return new GroupSendResult(group.Id, true, messageId, null, type);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Telegram group '{group.Id}' file send failed: {ex.Message}");
            return new GroupSendResult(group.Id, false, null, ex.Message);
        }
    }

    /// <summary>Posts "caption + 📎 name (size) + url" as a plain message.</summary>
    private async Task<List<GroupSendResult>> SendLinkAsync(
        IEnumerable<TelegramGroup> groups, string fileName, long? size, string url,
        string? caption, bool silent, CancellationToken ct)
    {
        var line = size > 0 ? $"📎 {fileName} ({FormatSize(size.Value)})\n{url}" : $"📎 {fileName}\n{url}";
        var text = string.IsNullOrWhiteSpace(caption) ? line : $"{caption}\n\n{line}";

        var results = new List<GroupSendResult>();
        foreach (var group in groups)
        {
            // Plain text: a caption written for HTML mode would choke on the raw '&' in a signed URL.
            var r = await SendToGroupAsync(group, text, parseMode: null, disableWebPagePreview: false, silent: silent, ct: ct);
            results.Add(r with { SentAs = r.Ok ? "link" : null, Url = url });
        }
        return results;
    }

    private async Task<(bool Ok, long? MessageId, string? Error)> PostFileAsync(
        string token, TelegramGroup group, OutgoingFile file, string type,
        string? caption, string? parseMode, bool silent, VideoMeta? meta, CancellationToken ct)
    {
        // sendPhoto/photo, sendVideo/video, sendDocument/document, …
        var method = "send" + char.ToUpperInvariant(type[0]) + type[1..];

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(group.ChatId), "chat_id");
        if (group.ThreadId > 0) form.Add(new StringContent(group.ThreadId.ToString()), "message_thread_id");
        if (!string.IsNullOrEmpty(caption)) form.Add(new StringContent(caption), "caption");
        if (!string.IsNullOrWhiteSpace(parseMode)) form.Add(new StringContent(parseMode), "parse_mode");
        if (silent) form.Add(new StringContent("true"), "disable_notification");
        if (type == "video") form.Add(new StringContent("true"), "supports_streaming");

        if (meta is not null)
        {
            form.Add(new StringContent(meta.Width.ToString()), "width");
            form.Add(new StringContent(meta.Height.ToString()), "height");
            if (meta.Duration > 0) form.Add(new StringContent(meta.Duration.ToString()), "duration");
            if (meta.Thumbnail is not null)
            {
                var thumb = new ByteArrayContent(meta.Thumbnail);
                thumb.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                form.Add(thumb, "thumb_file", "thumb.jpg");
                form.Add(new StringContent("attach://thumb_file"), "thumbnail");
            }
        }

        if (file.Url is not null)
        {
            form.Add(new StringContent(file.Url), type);
        }
        else
        {
            HttpContent content = file.Bytes is not null
                ? new ByteArrayContent(file.Bytes)
                : new StreamContent(File.OpenRead(file.LocalPath!));
            content.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
            form.Add(content, type, file.FileName);
        }

        var resp = await _uploadHttp.PostAsync($"https://api.telegram.org/bot{token}/{method}", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            Log?.Invoke($"Telegram group '{group.Id}' {method} failed: {body}");
            return (false, null, DescribeError(body, (int)resp.StatusCode));
        }

        using var doc = JsonDocument.Parse(body);
        long? id = doc.RootElement.TryGetProperty("result", out var result) &&
                   result.TryGetProperty("message_id", out var idEl) ? idEl.GetInt64() : null;
        return (true, id, null);
    }

    // --- ffmpeg probe ---

    /// <summary>
    /// Reads the video's display size (rotation and non-square pixels applied), duration,
    /// and grabs a ≤320px JPEG frame. Null when ffmpeg is missing or can't read the
    /// file — the send still goes ahead, just without the hints.
    /// </summary>
    private async Task<VideoMeta?> ProbeVideoAsync(OutgoingFile file, CancellationToken ct)
    {
        var ffmpeg = !string.IsNullOrWhiteSpace(_ffmpegPath) && File.Exists(_ffmpegPath) ? _ffmpegPath : "ffmpeg";
        var workDir = Path.Combine(Path.GetTempPath(), "JustWatch.Outgoing");
        Directory.CreateDirectory(workDir);
        var stamp = Guid.NewGuid().ToString("N");
        var thumbPath = Path.Combine(workDir, $"{stamp}.jpg");
        string? tempSource = null;

        try
        {
            var source = file.LocalPath ?? file.Url;
            if (file.Bytes is not null)
            {
                tempSource = Path.Combine(workDir, stamp + Path.GetExtension(file.FileName));
                await File.WriteAllBytesAsync(tempSource, file.Bytes, ct);
                source = tempSource;
            }
            if (source is null) return null;

            var info = await RunFfmpegAsync(ffmpeg, ct, "-hide_banner", "-i", source);

            var size = Regex.Match(info, @"Video:.*?[\s,](\d{2,5})x(\d{2,5})(?:\s*\[SAR (\d+):(\d+))?");
            if (!size.Success) return null;
            var width = int.Parse(size.Groups[1].Value);
            var height = int.Parse(size.Groups[2].Value);

            if (size.Groups[3].Success && int.Parse(size.Groups[4].Value) is > 0 and var sarDen
                && int.Parse(size.Groups[3].Value) is > 0 and var sarNum && sarNum != sarDen)
                width = (int)Math.Round(width * (double)sarNum / sarDen);

            var rotation = Regex.Match(info, @"rotation of (-?\d+(?:\.\d+)?) degrees");
            if (rotation.Success && Math.Abs((int)Math.Round(double.Parse(rotation.Groups[1].Value, CultureInfo.InvariantCulture))) % 180 == 90)
                (width, height) = (height, width);

            var duration = 0;
            var dur = Regex.Match(info, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
            if (dur.Success)
                duration = (int)Math.Round(int.Parse(dur.Groups[1].Value) * 3600 + int.Parse(dur.Groups[2].Value) * 60
                    + double.Parse(dur.Groups[3].Value, CultureInfo.InvariantCulture));

            // A frame a second in dodges the black first frame most renders start on.
            await RunFfmpegAsync(ffmpeg, ct, "-hide_banner", "-loglevel", "error", "-y",
                "-ss", duration >= 2 ? "1" : "0", "-i", source, "-frames:v", "1",
                "-vf", "scale=320:320:force_original_aspect_ratio=decrease", "-q:v", "4", thumbPath);

            byte[]? thumbnail = null;
            if (File.Exists(thumbPath) && new FileInfo(thumbPath).Length is > 0 and < 200_000)
                thumbnail = await File.ReadAllBytesAsync(thumbPath, ct);

            return new VideoMeta(width, height, duration, thumbnail);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Video probe of '{file.FileName}' failed, sending without size hints: {ex.Message}");
            return null;
        }
        finally
        {
            foreach (var p in new[] { tempSource, thumbPath })
                if (p is not null) try { File.Delete(p); } catch { }
        }
    }

    /// <summary>Runs ffmpeg and returns its stderr (where it writes stream info). 30 s cap.</summary>
    private static async Task<string> RunFfmpegAsync(string ffmpeg, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
        _ = proc.StandardOutput.ReadToEndAsync(timeout.Token);
        try { await proc.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch { } throw; }
        return await stderr;
    }

    // --- storage ---

    /// <summary>Uploads to the configured bucket and returns the public URL.</summary>
    private async Task<string> UploadToStorageAsync(OutgoingFile file, CancellationToken ct)
    {
        var s = _config.Storage;
        var endpoint = new Uri(s.ServiceUrl);
        var client = new MinioClient()
            .WithEndpoint(endpoint.Host, endpoint.Port)
            .WithCredentials(s.AccessKey, s.SecretKey)
            .WithSSL(endpoint.Scheme == Uri.UriSchemeHttps)
            .WithTimeout((int)TimeSpan.FromMinutes(30).TotalMilliseconds)
            .Build();

        var safeName = Regex.Replace(file.FileName, @"[^A-Za-z0-9._-]+", "-").Trim('-');
        var key = $"{s.KeyPrefix.Trim('/')}/{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid().ToString("N")[..8]}-{(safeName.Length > 0 ? safeName : "file")}";
        var contentType = !string.IsNullOrWhiteSpace(file.ContentType) && file.ContentType != "application/octet-stream"
            ? file.ContentType
            : MimeFor(file.FileName);

        var started = Stopwatch.StartNew();
        await using Stream data = file.Bytes is not null ? new MemoryStream(file.Bytes) : File.OpenRead(file.LocalPath!);
        await client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(s.Bucket)
            .WithObject(key)
            .WithStreamData(data)
            .WithObjectSize(data.Length)
            .WithContentType(contentType), ct);
        Log?.Invoke($"Uploaded '{file.FileName}' ({FormatSize(data.Length)}) to {s.Bucket}/{key} in {started.Elapsed.TotalSeconds:0}s");

        return $"{s.PublicBaseUrl.TrimEnd('/')}/{string.Join('/', key.Split('/').Select(Uri.EscapeDataString))}";
    }

    private static string MimeFor(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".apk" => "application/vnd.android.package-archive",
            ".txt" or ".log" => "text/plain; charset=utf-8",
            _ => "application/octet-stream"
        };

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
