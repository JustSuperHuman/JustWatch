using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JustWatch.Core.Models;

namespace JustWatch.Core.Services;

public partial class TelegramService : IDisposable
{
    private readonly TelegramConfig _config;
    private readonly HttpClient _http = new();

    // File sends push up to 50 MB through a home uplink; the default 100 s timeout
    // cuts a 35 MB video off mid-upload. Separate client so polling keeps the short one.
    private readonly HttpClient _uploadHttp = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly string? _ffmpegPath;
    private readonly CancellationTokenSource _cts = new();
    private string BaseUrl => $"https://api.telegram.org/bot{_config.BotToken}";
    private string FileBaseUrl => $"https://api.telegram.org/file/bot{_config.BotToken}";

    public event Action<string>? MessageReceived;
    public event Action<string>? Log;

    public bool IsConfigured => _config.Enabled
        && !string.IsNullOrEmpty(_config.BotToken)
        && _config.ChatId != 0;

    public TelegramService(TelegramConfig config, string? ffmpegPath = null)
    {
        _config = config;
        _ffmpegPath = ffmpegPath;
    }

    public async Task SendTextAsync(string text)
    {
        if (!IsConfigured) return;

        // Telegram caps messages at 4096 chars; chunk longer relays (agent answers
        // can be long) so nothing is silently dropped.
        foreach (var chunk in ChunkText(text, 4000))
            await SendChunkAsync(chunk);
    }

    private async Task SendChunkAsync(string text)
    {
        try
        {
            var payload = new { chat_id = _config.ChatId, text, parse_mode = "HTML" };
            var resp = await _http.PostAsync($"{BaseUrl}/sendMessage",
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), _cts.Token);

            // Relayed agent output is markdown/code and routinely contains '<', '>',
            // '&' that Telegram's HTML parser rejects (400). Retry as plain text so
            // the content still reaches the user.
            if (!resp.IsSuccessStatusCode)
            {
                var plain = new { chat_id = _config.ChatId, text };
                await _http.PostAsync($"{BaseUrl}/sendMessage",
                    new StringContent(JsonSerializer.Serialize(plain), Encoding.UTF8, "application/json"), _cts.Token);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Telegram send failed: {ex.Message}");
        }
    }

    private static IEnumerable<string> ChunkText(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        for (int i = 0; i < text.Length; i += max)
            yield return text.Substring(i, Math.Min(max, text.Length - i));
    }

    public async Task SendVideoAsync(string filePath, string? caption = null)
    {
        if (!IsConfigured || !File.Exists(filePath)) return;

        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(_config.ChatId.ToString()), "chat_id");

            if (!string.IsNullOrEmpty(caption))
                form.Add(new StringContent(caption), "caption");

            var fileBytes = await File.ReadAllBytesAsync(filePath, _cts.Token);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            form.Add(fileContent, "video", Path.GetFileName(filePath));

            var response = await _http.PostAsync($"{BaseUrl}/sendVideo", form, _cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(_cts.Token);
                // If video is too large for sendVideo (50MB limit), try as document
                if (body.Contains("too big") || fileBytes.Length > 50_000_000)
                {
                    Log?.Invoke("Video too large for Telegram video, sending as document...");
                    await SendDocumentAsync(filePath, caption);
                }
                else
                {
                    Log?.Invoke($"Telegram video send failed: {body}");
                }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Telegram video send failed: {ex.Message}");
        }
    }

    private async Task SendDocumentAsync(string filePath, string? caption)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(_config.ChatId.ToString()), "chat_id");
        if (!string.IsNullOrEmpty(caption))
            form.Add(new StringContent(caption), "caption");

        var fileContent = new ByteArrayContent(await File.ReadAllBytesAsync(filePath, _cts.Token));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "document", Path.GetFileName(filePath));

        await _http.PostAsync($"{BaseUrl}/sendDocument", form, _cts.Token);
    }

    // ---------------------------------------------------------------------
    // Named groups — the status-update path used by POST /api/telegram/messages.
    // Independent of the conversational bot above: a group carries its own chat id
    // and (optionally) its own bot token, so one JustWatch can post to several
    // channels owned by different bots.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Configured groups, newest config wins. When none are declared, the legacy
    /// single BotToken/ChatId pair is surfaced as a group called "default" so the
    /// route works without any extra configuration.
    /// </summary>
    public IReadOnlyList<TelegramGroup> Groups
    {
        get
        {
            var groups = _config.Groups
                .Where(g => !string.IsNullOrWhiteSpace(g.Id) && !string.IsNullOrWhiteSpace(g.ChatId))
                .ToList();

            if (groups.Count == 0 && _config.ChatId != 0 && !string.IsNullOrWhiteSpace(_config.BotToken))
                groups.Add(new TelegramGroup { Id = "default", Name = "Default", ChatId = _config.ChatId.ToString() });

            return groups;
        }
    }

    /// <summary>Group used when a request names none: the configured default, else the first.</summary>
    public TelegramGroup? DefaultGroup
    {
        get
        {
            var groups = Groups;
            if (!string.IsNullOrWhiteSpace(_config.DefaultGroup))
            {
                var named = groups.FirstOrDefault(g => g.Id.Equals(_config.DefaultGroup, StringComparison.OrdinalIgnoreCase));
                if (named != null) return named;
            }
            return groups.FirstOrDefault();
        }
    }

    /// <summary>
    /// Resolves requested group ids to configured groups. Null/empty selects the
    /// default group; "all" selects every group not marked ExcludeFromAll.
    /// <paramref name="unknown"/> receives ids that matched nothing, so the caller
    /// can answer with a useful 400 instead of silently dropping a destination.
    /// </summary>
    public IReadOnlyList<TelegramGroup> ResolveGroups(IEnumerable<string>? requested, out IReadOnlyList<string> unknown)
    {
        var groups = Groups;
        var ids = requested?.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).ToList() ?? [];
        unknown = [];

        if (ids.Count == 0)
        {
            var fallback = DefaultGroup;
            return fallback is null ? [] : [fallback];
        }

        if (ids.Any(id => id.Equals("all", StringComparison.OrdinalIgnoreCase)))
            return groups.Where(g => !g.ExcludeFromAll).ToList();

        var missing = new List<string>();
        var resolved = new List<TelegramGroup>();
        foreach (var id in ids)
        {
            var match = groups.FirstOrDefault(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (match is null) missing.Add(id);
            else if (!resolved.Contains(match)) resolved.Add(match);
        }

        unknown = missing;
        return resolved;
    }

    /// <summary>
    /// For file sends, <paramref name="SentAs"/> is what actually went out: the media type,
    /// "document" after a fallback, or "link" when the file was posted as <paramref name="Url"/>.
    /// </summary>
    public record GroupSendResult(string Group, bool Ok, long? MessageId, string? Error, string? SentAs = null, string? Url = null);

    /// <summary>
    /// Posts one status message to a named group. Long text is chunked at
    /// Telegram's 4096-char limit; the returned message id is the last chunk's.
    /// </summary>
    public async Task<GroupSendResult> SendToGroupAsync(
        TelegramGroup group,
        string text,
        string? parseMode = null,
        bool disableWebPagePreview = false,
        bool silent = false,
        CancellationToken ct = default)
    {
        var token = string.IsNullOrWhiteSpace(group.BotToken) ? _config.BotToken : group.BotToken;
        if (string.IsNullOrWhiteSpace(token))
            return new GroupSendResult(group.Id, false, null, "No bot token configured for this group.");

        long? lastId = null;
        try
        {
            foreach (var chunk in ChunkText(text, 4000))
            {
                var payload = new Dictionary<string, object>
                {
                    ["chat_id"] = group.ChatId,
                    ["text"] = chunk,
                    ["disable_web_page_preview"] = disableWebPagePreview,
                    ["disable_notification"] = silent
                };
                if (!string.IsNullOrWhiteSpace(parseMode)) payload["parse_mode"] = parseMode;
                if (group.ThreadId > 0) payload["message_thread_id"] = group.ThreadId;

                var resp = await _http.PostAsync($"https://api.telegram.org/bot{token}/sendMessage",
                    new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    Log?.Invoke($"Telegram group '{group.Id}' send failed: {body}");
                    return new GroupSendResult(group.Id, false, null, DescribeError(body, (int)resp.StatusCode));
                }

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("message_id", out var idEl))
                    lastId = idEl.GetInt64();
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Telegram group '{group.Id}' send failed: {ex.Message}");
            return new GroupSendResult(group.Id, false, null, ex.Message);
        }

        return new GroupSendResult(group.Id, true, lastId, null);
    }

    public async Task<IReadOnlyList<GroupSendResult>> SendToGroupsAsync(
        IEnumerable<TelegramGroup> groups,
        string text,
        string? parseMode = null,
        bool disableWebPagePreview = false,
        bool silent = false,
        CancellationToken ct = default)
    {
        var results = new List<GroupSendResult>();
        foreach (var group in groups)
            results.Add(await SendToGroupAsync(group, text, parseMode, disableWebPagePreview, silent, ct));
        return results;
    }

    /// <summary>Telegram's own "description" field, which says far more than the status code.</summary>
    private static string DescribeError(string body, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("description", out var d) && d.GetString() is { Length: > 0 } text)
                return text;
        }
        catch { }
        return $"Telegram returned HTTP {status}.";
    }

    public async Task StartPollingAsync(CancellationToken ct)
    {
        if (!IsConfigured) return;

        Log?.Invoke("Telegram polling started");
        long offset = 0;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);

        while (!linked.Token.IsCancellationRequested)
        {
            try
            {
                var url = $"{BaseUrl}/getUpdates?offset={offset}&timeout=30&allowed_updates=[\"message\"]";
                var response = await _http.GetStringAsync(url, linked.Token);
                using var doc = JsonDocument.Parse(response);

                if (doc.RootElement.TryGetProperty("result", out var results))
                {
                    foreach (var update in results.EnumerateArray())
                    {
                        offset = update.GetProperty("update_id").GetInt64() + 1;
                        await ProcessUpdateAsync(update, linked.Token);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (HttpRequestException) { await Task.Delay(5000, linked.Token); }
            catch (Exception ex)
            {
                Log?.Invoke($"Telegram poll error: {ex.Message}");
                await Task.Delay(5000, linked.Token);
            }
        }
    }

    private async Task ProcessUpdateAsync(JsonElement update, CancellationToken ct)
    {
        if (!update.TryGetProperty("message", out var msg)) return;
        if (!msg.TryGetProperty("chat", out var chat)) return;
        if (chat.GetProperty("id").GetInt64() != _config.ChatId) return;

        // Plain text — the original, fast path
        if (msg.TryGetProperty("text", out var textEl) && textEl.GetString() is { Length: > 0 } textMsg)
        {
            MessageReceived?.Invoke(textMsg);
            return;
        }

        var caption = msg.TryGetProperty("caption", out var capEl) ? capEl.GetString() : null;

        // Voice note or audio clip — transcribe, then treat the transcript as a typed message
        if (msg.TryGetProperty("voice", out var voice) || msg.TryGetProperty("audio", out voice))
        {
            await HandleVoiceAsync(voice, caption, ct);
            return;
        }

        // Photo — Telegram sends an array of sizes; the last is the largest
        if (msg.TryGetProperty("photo", out var photos)
            && photos.ValueKind == JsonValueKind.Array && photos.GetArrayLength() > 0)
        {
            var fileId = photos.EnumerateArray().Last().GetProperty("file_id").GetString();
            await HandleFileAsync(fileId, "image", null, caption, ct);
            return;
        }

        // Document / file attachment
        if (msg.TryGetProperty("document", out var document))
        {
            var fileId = document.GetProperty("file_id").GetString();
            var fileName = document.TryGetProperty("file_name", out var fn) ? fn.GetString() : null;
            await HandleFileAsync(fileId, "file", fileName, caption, ct);
        }
    }

    private async Task HandleVoiceAsync(JsonElement media, string? caption, CancellationToken ct)
    {
        var fileId = media.TryGetProperty("file_id", out var f) ? f.GetString() : null;
        if (string.IsNullOrEmpty(fileId)) return;

        if (!_config.TranscribeVoice || string.IsNullOrEmpty(_config.TranscriptionApiKey))
        {
            await SendTextAsync("🎤 Voice received, but transcription isn't configured. Set <code>telegram.transcriptionApiKey</code> in justwatch-config.json.");
            return;
        }

        string? localPath = null;
        try
        {
            localPath = await DownloadFileAsync(fileId, ct);
            if (localPath == null) { await SendTextAsync("Couldn't download the voice message."); return; }

            var transcript = await TranscribeAsync(localPath, ct);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                await SendTextAsync("Couldn't transcribe that voice message.");
                return;
            }

            // Echo what was heard so the user can catch transcription mistakes
            await SendTextAsync($"🎤 <i>{System.Net.WebUtility.HtmlEncode(transcript)}</i>");

            var combined = string.IsNullOrWhiteSpace(caption) ? transcript : $"{caption}\n{transcript}";
            MessageReceived?.Invoke(combined.Trim());
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Voice handling failed: {ex.Message}");
            await SendTextAsync($"Voice transcription failed: {ex.Message}");
        }
        finally
        {
            if (localPath != null) try { File.Delete(localPath); } catch { }
        }
    }

    private async Task HandleFileAsync(string? fileId, string kind, string? fileName, string? caption, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(fileId)) return;
        try
        {
            var localPath = await DownloadFileAsync(fileId, ct, fileName);
            if (localPath == null) { await SendTextAsync($"Couldn't download the {kind}."); return; }

            var note = kind == "image"
                ? $"Received an image, saved to: {localPath}"
                : $"Received a file '{fileName ?? Path.GetFileName(localPath)}', saved to: {localPath}";
            var message = string.IsNullOrWhiteSpace(caption) ? note : $"{caption}\n\n{note}";
            MessageReceived?.Invoke(message);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"{kind} handling failed: {ex.Message}");
            await SendTextAsync($"Couldn't process the {kind}: {ex.Message}");
        }
    }

    // Resolves a Telegram file_id to a local file, downloading it into a temp incoming folder.
    private async Task<string?> DownloadFileAsync(string fileId, CancellationToken ct, string? preferredName = null)
    {
        var infoJson = await _http.GetStringAsync($"{BaseUrl}/getFile?file_id={Uri.EscapeDataString(fileId)}", ct);
        using var infoDoc = JsonDocument.Parse(infoJson);
        if (!infoDoc.RootElement.TryGetProperty("result", out var result)) return null;
        if (!result.TryGetProperty("file_path", out var fp) || fp.GetString() is not { Length: > 0 } remotePath)
            return null;

        var bytes = await _http.GetByteArrayAsync($"{FileBaseUrl}/{remotePath}", ct);

        var dir = Path.Combine(Path.GetTempPath(), "JustWatch.Incoming");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var name = preferredName is { Length: > 0 }
            ? $"{Path.GetFileNameWithoutExtension(preferredName)}_{stamp}{Path.GetExtension(preferredName)}"
            : $"{stamp}_{Path.GetFileName(remotePath)}";
        var local = Path.Combine(dir, name);
        await File.WriteAllBytesAsync(local, bytes, ct);
        return local;
    }

    private async Task<string?> TranscribeAsync(string filePath, CancellationToken ct)
    {
        var endpoint = $"{_config.TranscriptionBaseUrl.TrimEnd('/')}/audio/transcriptions";
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(await File.ReadAllBytesAsync(filePath, ct));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", Path.GetFileName(filePath));
        form.Add(new StringContent(_config.TranscriptionModel), "model");

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.TranscriptionApiKey);

        var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            Log?.Invoke($"Transcription failed ({(int)resp.StatusCode}): {body}");
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
    }

    public RecordingQuality GetRecordingQuality() =>
        RecordingQuality.ForTelegram(_config);

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _http.Dispose();
        _uploadHttp.Dispose();
    }
}
