using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JustWatch.Core.Models;

namespace JustWatch.Core.Services;

/// <summary>
/// Status-update relay: posts messages and files to any configured Telegram
/// group, so scripts and agents can announce a build/deploy/result without holding
/// the bot token themselves.
///
///   GET  /api/telegram/groups    → the configured destinations
///   POST /api/telegram/messages  → send text (JSON body, or plain query params)
///   POST /api/telegram/files     → send a photo/video/audio/document (upload, local path or URL)
///
/// Groups live in config/justwatch-config.json under telegram.groups; adding one
/// there is all it takes to make it addressable.
/// </summary>
public sealed partial class WebAgentServer
{
    private async Task HandleTelegramAsync(HttpListenerContext ctx, string path)
    {
        if (ctx.Request.HttpMethod == "OPTIONS")
        {
            AddCors(ctx.Response);
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }

        if (_telegram is null)
        {
            WriteJson(ctx, 503, "{\"error\":\"Telegram is not configured. Set telegram.botToken and telegram.groups in justwatch-config.json.\"}");
            return;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries); // ["api","telegram",...]

        if (segments.Length == 3 && segments[2].Equals("groups", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsMethod(ctx, "GET")) { WriteMethodNotAllowed(ctx); return; }
            var defaultId = _telegram.DefaultGroup?.Id;
            var groups = _telegram.Groups.Select(g => new
            {
                id = g.Id,
                name = string.IsNullOrWhiteSpace(g.Name) ? g.Id : g.Name,
                chatId = g.ChatId,
                threadId = g.ThreadId == 0 ? (int?)null : g.ThreadId,
                isDefault = g.Id.Equals(defaultId, StringComparison.OrdinalIgnoreCase),
                excludeFromAll = g.ExcludeFromAll
            }).ToArray();
            WriteJson(ctx, 200, JsonSerializer.Serialize(new { groups }, JsonOpts));
            return;
        }

        if (segments.Length == 3 && segments[2].Equals("files", StringComparison.OrdinalIgnoreCase))
        {
            await HandleTelegramFilesAsync(ctx);
            return;
        }

        if (segments.Length != 3 || !segments[2].Equals("messages", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(ctx, 404, "{\"error\":\"unknown route\"}");
            return;
        }

        if (!IsMethod(ctx, "POST")) { WriteMethodNotAllowed(ctx); return; }

        var body = ReadBody(ctx);
        var query = ctx.Request.QueryString;

        // Body wins, query string is the curl-friendly shorthand.
        var text = GetJsonString(body, "text") ?? GetJsonString(body, "message") ?? query["text"] ?? query["message"];
        if (string.IsNullOrWhiteSpace(text))
        {
            WriteJson(ctx, 400, "{\"error\":\"'text' is required.\"}");
            return;
        }

        if (!TryResolveTelegramGroups(ctx, ReadGroupSelectors(body, query), out var groupsToSend)) return;

        var parseMode = GetJsonString(body, "parseMode") ?? query["parseMode"];
        if (parseMode is not null && parseMode.Equals("none", StringComparison.OrdinalIgnoreCase)) parseMode = null;

        var results = await _telegram.SendToGroupsAsync(
            groupsToSend,
            text,
            parseMode,
            disableWebPagePreview: ReadBool(body, query, "disableWebPagePreview") ?? false,
            silent: ReadBool(body, query, "silent") ?? false);

        WriteTelegramResults(ctx, results);
    }

    /// <summary>
    /// POST /api/telegram/files — one file (photo, video, audio, document, …) to one
    /// or more groups. Built to accept whatever a script or agent throws at it:
    ///   multipart      curl -F file=@shot.png            (or -F path=J:\x\shot.png — no escaping)
    ///   form fields    curl --data-urlencode "path=J:\x\shot.png" -d group=me
    ///   JSON           {"path":"J:/x/shot.png"} or {"url":"https://…"} — Content-Type optional,
    ///                  and mangled Windows backslashes are repaired rather than rejected
    ///   raw body       curl --data-binary @shot.png "…?name=shot.png"
    /// Over 50 MB goes to the storage bucket and is posted as a link (see TelegramService.Files).
    /// </summary>
    private async Task HandleTelegramFilesAsync(HttpListenerContext ctx)
    {
        if (!IsMethod(ctx, "POST")) { WriteMethodNotAllowed(ctx); return; }

        // Uploads past Telegram's 50 MB only make sense when storage can host them.
        // Paths have no cap: the file is streamed from disk, never buffered here.
        var bodyLimit = _telegram!.StorageConfigured ? MaxTelegramUploadBody : TelegramService.MaxUploadBytes + 1024 * 1024;
        var tooLarge = _telegram.StorageConfigured
            ? $"{{\"error\":\"Uploads are capped at {MaxTelegramUploadBody / 1024 / 1024} MB. For bigger files send {{\\\"path\\\"}} instead.\"}}"
            : "{\"error\":\"Over Telegram's 50 MB limit. Configure telegram.storage in justwatch-config.json to send big files as links.\"}";
        if (ctx.Request.ContentLength64 > bodyLimit) { WriteJson(ctx, 413, tooLarge); return; }

        var raw = await ReadBodyBytesAsync(ctx, bodyLimit);
        if (raw is null) { WriteJson(ctx, 413, tooLarge); return; }

        // Every option lands in one bag regardless of how it was sent; query params are the base.
        var fields = new NameValueCollection(ctx.Request.QueryString);
        var contentType = ctx.Request.ContentType ?? string.Empty;
        TelegramService.OutgoingFile? file = null;

        if (contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            var boundary = Regex.Match(contentType, "boundary=(?:\"([^\"]+)\"|([^;\\s]+))");
            if (!boundary.Success) { WriteJson(ctx, 400, "{\"error\":\"multipart body has no boundary.\"}"); return; }

            foreach (var part in ParseMultipart(raw, boundary.Groups[1].Success ? boundary.Groups[1].Value : boundary.Groups[2].Value))
            {
                if (part.FileName is not null)
                    file ??= new TelegramService.OutgoingFile(part.FileName, part.Data, ContentType: part.ContentType);
                else
                    fields.Add(part.Name, Encoding.UTF8.GetString(part.Data));
            }
        }
        else if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) || LooksLikeJson(raw))
        {
            // curl -d sends JSON as form-urlencoded unless told otherwise, so sniff the body too.
            using var doc = ParseJsonForgiving(raw);
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                WriteJson(ctx, 400, JsonSerializer.Serialize(new
                {
                    error = @"Body is not valid JSON. Easiest for local files: curl -F path=J:\x\file.mp4 -F group=me …/api/telegram/files"
                }, JsonOpts));
                return;
            }
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.Array:
                        foreach (var item in prop.Value.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.String) fields.Add(prop.Name, item.GetString());
                        break;
                    case JsonValueKind.String:
                        fields.Set(prop.Name, prop.Value.GetString());
                        break;
                    case JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number:
                        fields.Set(prop.Name, prop.Value.GetRawText());
                        break;
                }
            }
        }
        else if (contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) && LooksLikeFormFields(raw))
        {
            // Also curl's default for --data-binary, so only genuine key=value text counts as fields.
            var form = System.Web.HttpUtility.ParseQueryString(Encoding.UTF8.GetString(raw));
            foreach (string? key in form.Keys)
                if (key is not null)
                    foreach (var value in form.GetValues(key) ?? []) fields.Add(key, value);
        }
        else if (raw.Length > 0)
        {
            var name = fields["name"] ?? "file" + ExtensionForContentType(contentType);
            file = new TelegramService.OutgoingFile(name, raw, ContentType: contentType);
        }

        if (file is null && fields["url"] is { Length: > 0 } url)
        {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            { WriteJson(ctx, 400, "{\"error\":\"'url' must be an absolute http(s) URL.\"}"); return; }
            var name = fields["name"] ?? Path.GetFileName(uri.AbsolutePath);
            file = new TelegramService.OutgoingFile(string.IsNullOrEmpty(name) ? "file" : name, Url: uri.AbsoluteUri);
        }
        else if (file is null && fields["path"] is { Length: > 0 } rawPath)
        {
            // Reading an arbitrary local file is for local scripts and agents; a browser
            // page (which always sends Origin) must not be able to pick files off disk.
            if (ctx.Request.Headers["Origin"] is not null)
            { WriteJson(ctx, 403, "{\"error\":\"'path' is only accepted from local scripts, not browser pages. Upload the file instead.\"}"); return; }
            var path = ResolveLocalPath(rawPath);
            if (path is null) { WriteJson(ctx, 404, JsonSerializer.Serialize(new { error = $"File not found: {rawPath}" }, JsonOpts)); return; }
            file = new TelegramService.OutgoingFile(fields["name"] ?? Path.GetFileName(path), LocalPath: path);
        }

        if (file is null)
        {
            WriteJson(ctx, 400, "{\"error\":\"No file. Send multipart field 'file', a 'path' or 'url' (JSON or form field), or a raw body with ?name=.\"}");
            return;
        }
        if (file.Url is null && file.Length == 0) { WriteJson(ctx, 400, "{\"error\":\"File is empty.\"}"); return; }


        var type = fields["type"]?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(type) || type == "auto") type = TelegramService.DetectMediaType(file.FileName);
        if (!TelegramService.MediaTypes.Contains(type))
        {
            WriteJson(ctx, 400, JsonSerializer.Serialize(new { error = $"Unknown type '{type}'. Use auto, {string.Join(", ", TelegramService.MediaTypes)}." }, JsonOpts));
            return;
        }

        if (!TryResolveTelegramGroups(ctx, ReadGroupSelectors(string.Empty, fields), out var groupsToSend)) return;

        var parseMode = fields["parseMode"];
        if (parseMode is not null && parseMode.Equals("none", StringComparison.OrdinalIgnoreCase)) parseMode = null;

        var results = await _telegram!.SendFileToGroupsAsync(
            groupsToSend,
            file,
            type,
            caption: fields["caption"] ?? fields["text"],
            parseMode,
            silent: ReadBool(string.Empty, fields, "silent") ?? false);

        WriteTelegramResults(ctx, results);
    }

    /// <summary>Resolves group selectors, answering 400/503 itself when that fails.</summary>
    private bool TryResolveTelegramGroups(HttpListenerContext ctx, List<string> requested, out IReadOnlyList<TelegramGroup> groups)
    {
        groups = _telegram!.ResolveGroups(requested, out var unknown);

        if (unknown.Count > 0)
        {
            var known = string.Join(", ", _telegram.Groups.Select(g => g.Id));
            WriteJson(ctx, 400, JsonSerializer.Serialize(new
            {
                error = $"Unknown group(s): {string.Join(", ", unknown)}.",
                knownGroups = known
            }, JsonOpts));
            return false;
        }

        if (groups.Count == 0)
        {
            WriteJson(ctx, 503, "{\"error\":\"No Telegram groups configured. Add entries under telegram.groups in justwatch-config.json.\"}");
            return false;
        }

        return true;
    }

    private static void WriteTelegramResults(HttpListenerContext ctx, IReadOnlyList<TelegramService.GroupSendResult> results)
    {
        var sent = results.Where(r => r.Ok).Select(r => new { group = r.Group, messageId = r.MessageId, type = r.SentAs, url = r.Url }).ToArray();
        var failed = results.Where(r => !r.Ok).Select(r => new { group = r.Group, error = r.Error }).ToArray();

        // 200 all sent · 207 some sent · 502 Telegram rejected every destination.
        var status = failed.Length == 0 ? 200 : sent.Length > 0 ? 207 : 502;
        WriteJson(ctx, status, JsonSerializer.Serialize(new { ok = failed.Length == 0, sent, failed },
            new JsonSerializerOptions(JsonOpts) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
    }

    /// <summary>Largest upload body buffered in memory. Local files should come as 'path', which streams.</summary>
    private const long MaxTelegramUploadBody = 512L * 1024 * 1024;

    private static bool LooksLikeJson(byte[] raw)
    {
        foreach (var b in raw)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return b == (byte)'{';
        }
        return false;
    }

    /// <summary>key=value&amp;… text, as opposed to a file curl sent with its default form content type.</summary>
    private static bool LooksLikeFormFields(byte[] raw) =>
        raw.Length is > 0 and < 64 * 1024 &&
        !raw.Any(b => b < 0x20 && b is not (byte)'\r' and not (byte)'\n' and not (byte)'\t') &&
        Regex.IsMatch(Encoding.UTF8.GetString(raw), @"^\s*[A-Za-z][\w]*=");

    /// <summary>
    /// Parses JSON, repairing the classic mistake of unescaped Windows paths
    /// ({"path":"J:\ads\x.mp4"}, often after a shell collapsed the "\\"): any
    /// backslash that doesn't form a valid escape is taken literally.
    /// </summary>
    private static JsonDocument? ParseJsonForgiving(byte[] raw)
    {
        try { return JsonDocument.Parse(raw); }
        catch (JsonException) { }

        var text = Encoding.UTF8.GetString(raw);
        var fixedText = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\') { fixedText.Append(text[i]); continue; }

            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            var validEscape = next is '"' or '\\' or '/' ||
                (next == 'u' && i + 5 < text.Length && Regex.IsMatch(text.Substring(i + 2, 4), "^[0-9a-fA-F]{4}$"));
            if (validEscape) { fixedText.Append(text[i]).Append(next); i++; }
            else fixedText.Append(@"\\"); // \a, \x, and also \n \t etc. — in a broken body these are path separators
        }

        try { return JsonDocument.Parse(fixedText.ToString()); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Finds the file a caller meant, however they wrote the path: J:\a\b, J:/a/b,
    /// Git Bash /j/a/b, ~/…, wrapped in quotes, or with \n/\t that strict JSON
    /// turned into control characters (J:\ads\new.mp4). Null when nothing exists.
    /// </summary>
    private static string? ResolveLocalPath(string input)
    {
        var p = input.Trim().Trim('"', '\'').Trim();
        p = p.Replace("\t", @"\t").Replace("\n", @"\n").Replace("\r", @"\r").Replace("\f", @"\f").Replace("\b", @"\b");

        var bash = Regex.Match(p, @"^/(?:mnt/)?([a-zA-Z])(/.*)?$");
        if (bash.Success) p = $"{char.ToUpperInvariant(bash.Groups[1].Value[0])}:{(bash.Groups[2].Success ? bash.Groups[2].Value : "/")}";
        if (p.StartsWith("~/") || p.StartsWith(@"~\"))
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[2..]);

        try
        {
            var full = Path.GetFullPath(p);
            return File.Exists(full) ? full : null;
        }
        catch { return null; }
    }

    /// <summary>Reads the request body, or null once it passes <paramref name="limit"/> bytes.</summary>
    private static async Task<byte[]?> ReadBodyBytesAsync(HttpListenerContext ctx, long limit)
    {
        // Known length: one exact buffer instead of a growing stream plus a copy.
        if (ctx.Request.ContentLength64 is > 0 and var length && length <= limit)
        {
            var exact = new byte[length];
            try { await ctx.Request.InputStream.ReadExactlyAsync(exact); }
            catch (EndOfStreamException) { return null; }
            return exact;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await ctx.Request.InputStream.ReadAsync(chunk)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit) return null;
        }
        return buffer.ToArray();
    }

    private sealed record MultipartPart(string Name, string? FileName, string? ContentType, byte[] Data);

    /// <summary>Minimal multipart/form-data reader — HttpListener has none built in.</summary>
    private static List<MultipartPart> ParseMultipart(byte[] body, string boundary)
    {
        var parts = new List<MultipartPart>();
        var first = Encoding.ASCII.GetBytes("--" + boundary);
        var delimiter = Encoding.ASCII.GetBytes("\r\n--" + boundary);
        var headerBreak = "\r\n\r\n"u8.ToArray();

        var start = body.AsSpan().IndexOf(first);
        if (start < 0) return parts;
        var pos = start + first.Length;

        // After each delimiter: "--" closes the body, otherwise CRLF then the next part.
        while (pos + 2 <= body.Length && !(body[pos] == '-' && body[pos + 1] == '-'))
        {
            pos += 2;
            var next = body.AsSpan(pos).IndexOf(delimiter);
            if (next < 0) break;
            var end = pos + next;

            var headerLen = body.AsSpan(pos, end - pos).IndexOf(headerBreak);
            if (headerLen >= 0)
            {
                var headers = Encoding.UTF8.GetString(body, pos, headerLen);
                var disposition = Regex.Match(headers, @"^Content-Disposition:(.*)$", RegexOptions.IgnoreCase | RegexOptions.Multiline).Groups[1].Value;
                var name = Regex.Match(disposition, "\\bname=\"([^\"]*)\"").Groups[1].Value;
                var fileName = Regex.Match(disposition, "\\bfilename=\"([^\"]*)\"");
                var type = Regex.Match(headers, @"^Content-Type:\s*([^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);

                parts.Add(new MultipartPart(
                    name,
                    fileName.Success ? Path.GetFileName(fileName.Groups[1].Value) : null,
                    type.Success ? type.Groups[1].Value.Trim() : null,
                    body[(pos + headerLen + headerBreak.Length)..end]));
            }

            pos = end + delimiter.Length;
        }

        return parts;
    }

    /// <summary>Extension for a nameless raw upload, so the media type can still be detected.</summary>
    private static string ExtensionForContentType(string contentType) =>
        contentType.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "video/mp4" => ".mp4",
            "video/quicktime" => ".mov",
            "audio/mpeg" => ".mp3",
            "audio/ogg" => ".ogg",
            "application/pdf" => ".pdf",
            "application/zip" => ".zip",
            "text/plain" => ".txt",
            _ => ".bin"
        };

    /// <summary>
    /// Target groups, however the caller chose to say it: {"group":"events"},
    /// {"groups":["events","ops"]}, {"group":"all"} or ?group=events&amp;group=ops.
    /// Nothing at all means the default group.
    /// </summary>
    private static List<string> ReadGroupSelectors(string body, System.Collections.Specialized.NameValueCollection query)
    {
        var ids = new List<string>();

        if (!string.IsNullOrEmpty(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var prop in new[] { "group", "groups" })
                {
                    if (!doc.RootElement.TryGetProperty(prop, out var el)) continue;
                    if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } one)
                        ids.Add(one);
                    else if (el.ValueKind == JsonValueKind.Array)
                        ids.AddRange(el.EnumerateArray()
                            .Where(v => v.ValueKind == JsonValueKind.String)
                            .Select(v => v.GetString()!)
                            .Where(v => !string.IsNullOrWhiteSpace(v)));
                }
            }
            catch { /* not JSON — fall through to the query string */ }
        }

        if (ids.Count == 0)
            foreach (var key in new[] { "group", "groups" })
                if (query[key] is { Length: > 0 } raw)
                    ids.AddRange(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool? ReadBool(string body, System.Collections.Specialized.NameValueCollection query, string name)
    {
        if (!string.IsNullOrEmpty(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty(name, out var el))
                {
                    if (el.ValueKind == JsonValueKind.True) return true;
                    if (el.ValueKind == JsonValueKind.False) return false;
                    if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var parsed)) return parsed;
                }
            }
            catch { }
        }

        var raw = query[name];
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.Equals("1", StringComparison.Ordinal)) return true;
        if (raw.Equals("0", StringComparison.Ordinal)) return false;
        return bool.TryParse(raw, out var q) ? q : null;
    }
}
