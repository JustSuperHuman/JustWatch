using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using JustWatch.Core.Models;

namespace JustWatch.Core.Services;

/// <summary>
/// Lightweight HttpListener server that exposes the Agent window in a browser.
/// It subscribes to the shared <see cref="ChatSession"/> events, streams them to
/// connected browsers via Server-Sent Events, and routes chat/stop requests back
/// into the same session — so the web UI is a live mirror of the WPF Agent window.
/// </summary>
public sealed partial class WebAgentServer : IDisposable
{
    private const int BufferCap = 4000; // replayed to clients that connect mid-session

    private readonly ChatSession _chat;
    private readonly JustWatchConfig _config;
    private readonly ProcessManager? _processes;
    private readonly NotificationCenter? _notifications;
    private readonly CommandCatalog? _commands;
    private readonly TelegramService? _telegram;
    private readonly string _html;

    private readonly object _lock = new();
    private readonly List<SseClient> _clients = [];
    private readonly List<(long Id, string Json)> _buffer = [];
    private long _nextId;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public string? BoundUrl { get; private set; }
    public event Action<string>? Log;

    private static readonly JsonSerializerOptions JsonOpts = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public WebAgentServer(ChatSession chat, JustWatchConfig config, ProcessManager? processes = null,
        NotificationCenter? notifications = null, CommandCatalog? commands = null,
        TelegramService? telegram = null)
    {
        _chat = chat;
        _config = config;
        _processes = processes;
        _notifications = notifications;
        _commands = commands;
        _telegram = telegram;
        if (processes != null)
        {
            _discovery = new ProjectDiscovery(processes);
            processes.OutputReceived += ObserveProjectOutput;
        }
        _html = WebAgentHtml.Generate().Replace("JustWatch", WebUtility.HtmlEncode(config.ProjectName));
        if (!config.Android.Enabled)
            _html = _html.Replace("data-view=\"emulators\"", "style=\"display:none\" data-view=\"emulators\"");

        // Live-only (not buffered): a fresh page load shouldn't replay old toasts.
        // History is available at GET /api/notifications.
        if (_notifications != null)
            _notifications.NotificationAdded += n => Broadcast(new
            {
                t = "notify",
                id = n.Id,
                level = n.Level,
                title = n.Title,
                message = n.Message,
                source = n.Source,
                timestamp = n.Timestamp.ToString("o")
            }, buffer: false);

        _chat.AgentChunk += (ch, text) => Broadcast(new { t = "chunk", channel = ch, text });
        _chat.AgentStatus += (ch, status, _) => Broadcast(new { t = "status", channel = ch, status });
        _chat.TaskChanged += task => Broadcast(new
        {
            t = "task",
            id = task.Id,
            name = task.Name,
            type = task.Type.ToString().ToLowerInvariant(),
            status = task.Status.ToString().ToLowerInvariant(),
            channel = task.Channel
        });
        _chat.ConvoMessage += (text, type, filePath) => Broadcast(new { t = "convo", text, ctype = type, filePath });
        _chat.SessionCompleted += () => Broadcast(new { t = "done" });
    }

    public void Start()
    {
        if (!_config.Web.Enabled) return;

        _cts = new CancellationTokenSource();
        var port = _config.Web.Port;
        var host = _config.Web.Host;
        var listenHost = host is "+" or "0.0.0.0" or "*" ? "+" : host;

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{listenHost}:{port}/");
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex) when (listenHost == "+")
        {
            // Binding all interfaces usually needs an admin URL ACL — fall back to localhost.
            Log?.Invoke($"Could not bind http://+:{port}/ ({ex.Message}); falling back to localhost.");
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            listenHost = "localhost";
            _listener.Start();
        }

        BoundUrl = $"http://{(listenHost == "+" ? "localhost" : listenHost)}:{port}/";
        Log?.Invoke($"Web agent UI listening on {BoundUrl}");
        _discovery?.Start();

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { break; }

            _ = Task.Run(() => HandleRequestAsync(ctx), ct);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";

            if (path.Equals("/favicon.svg", StringComparison.OrdinalIgnoreCase))
            {
                WriteText(ctx, WebWorkspace.Read("justwatch.svg"), "image/svg+xml");
                return;
            }

            // Agent discovery: docs, installable skill and an endpoint index.
            if (path.Equals("/llms.txt", StringComparison.OrdinalIgnoreCase) || path.Equals("/AGENTS.md", StringComparison.OrdinalIgnoreCase))
            {
                WriteText(ctx, WebWorkspace.Read("llms.txt"), "text/markdown; charset=utf-8");
                return;
            }
            if (path.Equals("/skill", StringComparison.OrdinalIgnoreCase) || path.Equals("/SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                WriteText(ctx, WebWorkspace.Read("SKILL.md"), "text/markdown; charset=utf-8");
                return;
            }
            if (path.Equals("/api", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(ctx, 200, ApiIndexJson());
                return;
            }

            if (path.Equals("/projects", StringComparison.OrdinalIgnoreCase))
            {
                WriteText(ctx, ProjectLibraryHtml.Generate(), "text/html; charset=utf-8");
                return;
            }
            if (path.Equals("/api/projects", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/projects/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleProjectsAsync(ctx, path);
                return;
            }

            if (path.Equals("/services", StringComparison.OrdinalIgnoreCase))
            {
                WriteText(ctx, ServiceManagerHtml.Generate(), "text/html; charset=utf-8");
                return;
            }
            if (path.Equals("/api/services/configuration", StringComparison.OrdinalIgnoreCase))
            {
                await HandleServiceConfigurationAsync(ctx);
                return;
            }

            // Service control API (no auth): status + start/stop/restart for each dev process.
            if (path.Equals("/api/services", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/services/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleServicesAsync(ctx, path);
                return;
            }

            if (path.Equals("/api/commands", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/commands/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleCommandsAsync(ctx, path);
                return;
            }

            // Android utility API (no auth): emulator launchers + app restart.
            if (path.Equals("/api/android", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/android/", StringComparison.OrdinalIgnoreCase))
            {
                if (!_config.Android.Enabled) { WriteJson(ctx, 404, "{\"error\":\"Android integration is disabled\"}"); return; }
                await HandleAndroidAsync(ctx, path);
                return;
            }

            // Telegram status relay (no auth): POST /api/telegram/messages, GET /api/telegram/groups
            if (path.Equals("/api/telegram", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/telegram/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleTelegramAsync(ctx, path);
                return;
            }

            // Notification history (no auth): GET /api/notifications[?since=<id>]
            if (path.Equals("/api/notifications", StringComparison.OrdinalIgnoreCase))
            {
                HandleNotifications(ctx);
                return;
            }

            switch (path)
            {
                case "/":
                case "/index.html":
                    WriteText(ctx, _html, "text/html; charset=utf-8");
                    break;
                case "/events":
                    await HandleSseAsync(ctx);
                    break;
                case "/chat":
                    HandleChat(ctx);
                    break;
                case "/stop":
                    HandleStop(ctx, isTask: false);
                    break;
                case "/stop-task":
                    HandleStop(ctx, isTask: true);
                    break;
                case "/video":
                    HandleVideo(ctx);
                    break;
                default:
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Request error: {ex.Message}");
            try { ctx.Response.Abort(); } catch { }
        }
    }

    // --- SSE ---

    private sealed class SseClient
    {
        public BlockingCollection<(long Id, string Json)> Queue { get; } = new();
    }

    private async Task HandleSseAsync(HttpListenerContext ctx)
    {
        var resp = ctx.Response;
        resp.StatusCode = 200;
        resp.ContentType = "text/event-stream";
        resp.Headers["Cache-Control"] = "no-cache";
        resp.Headers["X-Accel-Buffering"] = "no";
        resp.KeepAlive = true;
        resp.SendChunked = true;

        long lastEventId = 0;
        var lastIdHeader = ctx.Request.Headers["Last-Event-ID"];
        if (!string.IsNullOrEmpty(lastIdHeader)) long.TryParse(lastIdHeader, out lastEventId);

        var client = new SseClient();
        List<(long Id, string Json)> replay;
        lock (_lock)
        {
            _clients.Add(client);
            replay = _buffer.Where(e => e.Id > lastEventId).ToList();
        }

        var stream = resp.OutputStream;
        try
        {
            await WriteSse(stream, "retry: 3000\n\n");
            foreach (var (id, json) in replay)
                await WriteSse(stream, $"id: {id}\ndata: {json}\n\n");

            while (true)
            {
                if (client.Queue.TryTake(out var item, 15000))
                    await WriteSse(stream, $"id: {item.Id}\ndata: {item.Json}\n\n");
                else
                    await WriteSse(stream, ": ping\n\n"); // heartbeat
            }
        }
        catch
        {
            // client disconnected
        }
        finally
        {
            lock (_lock) { _clients.Remove(client); }
            client.Queue.Dispose();
            try { resp.Close(); } catch { }
        }
    }

    private static async Task WriteSse(System.IO.Stream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private void Broadcast(object evt, bool buffer = true)
    {
        var json = JsonSerializer.Serialize(evt, JsonOpts);
        lock (_lock)
        {
            var id = ++_nextId;
            if (buffer)
            {
                _buffer.Add((id, json));
                if (_buffer.Count > BufferCap)
                    _buffer.RemoveRange(0, _buffer.Count - BufferCap);
            }
            foreach (var c in _clients)
                try { c.Queue.Add((id, json)); } catch { }
        }
    }

    // --- POST handlers ---

    private void HandleChat(HttpListenerContext ctx)
    {
        var body = ReadBody(ctx);
        var message = GetJsonString(body, "message");
        if (string.IsNullOrWhiteSpace(message))
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }

        // Echo to all connected browsers so every client stays in sync.
        Broadcast(new { t = "user", text = message });

        _ = Task.Run(async () =>
        {
            try
            {
                if (_chat.IsProcessing)
                    _chat.SendNewTask(message, _config.ProjectPath);
                else
                    await _chat.SendAsync(message, _config.ProjectPath);
            }
            catch (Exception ex)
            {
                Broadcast(new { t = "convo", text = $"Error: {ex.Message}", ctype = "error", filePath = (string?)null });
            }
        });

        WriteText(ctx, "{\"ok\":true}", "application/json");
    }

    private void HandleStop(HttpListenerContext ctx, bool isTask)
    {
        var body = ReadBody(ctx);
        if (isTask)
        {
            var taskId = GetJsonString(body, "taskId");
            if (!string.IsNullOrEmpty(taskId)) _chat.StopTask(taskId);
        }
        else
        {
            var channel = GetJsonString(body, "channel");
            if (!string.IsNullOrEmpty(channel)) _chat.StopChannel(channel);
        }
        WriteText(ctx, "{\"ok\":true}", "application/json");
    }

    private void HandleVideo(HttpListenerContext ctx)
    {
        var path = GetQueryValue(ctx.Request.Url?.Query, "path");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        ctx.Response.ContentType = ext switch
        {
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".mov" => "video/quicktime",
            _ => "application/octet-stream"
        };
        try
        {
            using var fs = File.OpenRead(path);
            ctx.Response.ContentLength64 = fs.Length;
            fs.CopyTo(ctx.Response.OutputStream);
        }
        catch { }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    private void HandleNotifications(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod == "OPTIONS")
        {
            AddCors(ctx.Response);
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }

        if (_notifications is null)
        {
            WriteJson(ctx, 503, "{\"error\":\"notifications unavailable\"}");
            return;
        }

        if (!IsMethod(ctx, "GET"))
        {
            WriteMethodNotAllowed(ctx);
            return;
        }

        long.TryParse(GetQueryValue(ctx.Request.Url?.Query, "since"), out var since);
        var items = _notifications.GetRecent(since).Select(n => new
        {
            id = n.Id,
            timestamp = n.Timestamp.ToString("o"),
            level = n.Level,
            title = n.Title,
            message = n.Message,
            source = n.Source
        }).ToArray();
        WriteJson(ctx, 200, JsonSerializer.Serialize(new { notifications = items }, JsonOpts));
    }

    private async Task HandleServiceConfigurationAsync(HttpListenerContext ctx)
    {
        // Configuration can execute arbitrary commands and contain environment secrets.
        // Restrict it to local, same-origin callers; do not use the legacy wildcard CORS helper.
        void Reply(int status, object value)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, ServiceConfiguration.JsonOptions));
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        }
        var origin = ctx.Request.Headers["Origin"];
        if (!ctx.Request.IsLocal || ctx.Request.Url?.IsLoopback != true || (origin != null && origin != ctx.Request.Url!.GetLeftPart(UriPartial.Authority)))
        {
            Reply(403, new { error = "Service configuration is available only from this computer and the JustWatch origin." });
            return;
        }
        if (_processes == null) { Reply(503, new { error = "Process manager unavailable." }); return; }
        try
        {
            if (IsMethod(ctx, "PUT"))
            {
                if (ctx.Request.ContentLength64 > 1024 * 1024 || ctx.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
                { Reply(400, new { error = "Send application/json, at most 1 MB." }); return; }
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = new StringBuilder();
                var chunk = new char[4096];
                int count;
                while ((count = await reader.ReadAsync(chunk)) > 0)
                {
                    body.Append(chunk, 0, count);
                    if (body.Length > 1024 * 1024) { Reply(413, new { error = "Configuration is too large." }); return; }
                }
                var request = JsonSerializer.Deserialize<ServiceConfigurationRequest>(body.ToString(), ServiceConfiguration.JsonOptions);
                if (request?.Services == null || request.Revision == null) { Reply(400, new { error = "services and revision are required." }); return; }
                await _processes.ApplyConfigurationAsync(request.Services, request.Revision);
            }
            else if (!IsMethod(ctx, "GET")) { Reply(405, new { error = "Use GET or PUT." }); return; }
            var services = _processes.GetProcessConfigs();
            Reply(200, new { projectName = _config.ProjectName, projectPath = _config.ProjectPath,
                revision = ServiceConfiguration.Revision(services), services });
        }
        catch (JsonException ex) { Reply(400, new { error = ex.Message }); }
        catch (ArgumentException ex) { Reply(400, new { error = ex.Message }); }
        catch (InvalidOperationException ex) { Reply(409, new { error = ex.Message }); }
        catch (Exception ex) { Reply(500, new { error = ex.Message }); }
    }

    private string ApiIndexJson() => JsonSerializer.Serialize(new
    {
        name = "JustWatch",
        version = UpdateService.CurrentVersion.ToString(3),
        description = "Keeps local dev servers running. Start, stop, restart and read logs of services through this API.",
        docs = "/llms.txt",
        skill = "/skill",
        repository = "https://github.com/JustSuperHuman/JustWatch",
        configPath = _config.ConfigPath,
        notes = new[]
        {
            "GET works for every action; a body-less POST needs Content-Length: 0.",
            "Confirm each action from the returned snapshot (status, running, pid).",
            "Ask the user before removing services."
        },
        endpoints = new object[]
        {
            new { method = "GET", path = "/api/services", does = "Active or failed services. ?scope=all includes stopped; ?project=<name> filters." },
            new { method = "GET", path = "/api/services/{id}", does = "One service snapshot." },
            new { method = "GET", path = "/api/services/{id}/start", does = "Start a service." },
            new { method = "GET", path = "/api/services/{id}/stop", does = "Stop a service." },
            new { method = "GET", path = "/api/services/{id}/restart", does = "Restart a service." },
            new { method = "GET", path = "/api/services/{id}/logs?lines=100", does = "Recent output as plain text." },
            new { method = "GET", path = "/api/services/{id}/logs/stream", does = "Live output (Server-Sent Events)." },
            new { method = "GET", path = "/api/services/configuration", does = "Full service list plus revision (local only)." },
            new { method = "PUT", path = "/api/services/configuration", does = "Replace the service list: { revision, services }. Applied live." },
            new { method = "GET", path = "/api/notifications", does = "Recent status-change events. ?since=<id>." },
            new { method = "GET", path = "/api/projects", does = "Projects found in the configured watch folders." },
            new { method = "POST", path = "/api/telegram/messages", does = "Send a Telegram message if configured: { text, group }." }
        }
    }, new JsonSerializerOptions { WriteIndented = true });

    private sealed class ServiceConfigurationRequest
    {
        public List<ProcessConfig>? Services { get; set; }
        public string? Revision { get; set; }
    }

    // --- service control API ---

    /// <summary>
    /// Routes for managing the dev processes (api, web, mobile, funnel). No auth.
    ///   GET  /api/services            → compact active status; scope=all discovers stopped services
    ///   GET  /api/services/{id}       → status of one service
    ///   POST /api/services/{id}/start   | /stop | /restart
    /// </summary>
    private async Task HandleServicesAsync(HttpListenerContext ctx, string path)
    {
        if (ctx.Request.HttpMethod == "OPTIONS")
        {
            AddCors(ctx.Response);
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }

        if (_processes is null)
        {
            WriteJson(ctx, 503, "{\"error\":\"process manager unavailable\"}");
            return;
        }

        // segments: ["api", "services", "{id}", "{action}"]
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // GET /api/services defaults to compact active status for agent polling.
        if (segments.Length == 2)
        {
            if (!IsMethod(ctx, "GET")) { WriteMethodNotAllowed(ctx); return; }
            var scope = ctx.Request.QueryString["scope"] ?? "active";
            var detail = ctx.Request.QueryString["detail"] ?? "compact";
            var project = ctx.Request.QueryString["project"];
            if (scope is not ("active" or "all") || detail is not ("compact" or "full"))
            { WriteJson(ctx, 400, "{\"error\":\"Use scope=active|all and detail=compact|full\"}"); return; }
            var states = _processes.GetServiceStates(scope == "all", project);
            var services = states.Select(s => detail == "full" ? Snapshot(s) : CompactSnapshot(s)).ToArray();
            ctx.Response.Headers["Cache-Control"] = "no-store";
            WriteJson(ctx, 200, JsonSerializer.Serialize(new { services }, JsonOpts));
            return;
        }

        var pc = _processes.GetServiceState(segments[2]);
        if (pc is null)
        {
            WriteJson(ctx, 404, "{\"error\":\"unknown service\"}");
            return;
        }

        // GET /api/services/{id} → one
        if (segments.Length == 3)
        {
            if (!IsMethod(ctx, "GET")) { WriteMethodNotAllowed(ctx); return; }
            WriteJson(ctx, 200, JsonSerializer.Serialize(Snapshot(_processes.GetServiceState(pc.Id) ?? pc), JsonOpts));
            return;
        }

        // GET /api/services/{id}/logs[?lines=N] → buffered output dump
        // GET /api/services/{id}/logs/stream   → live SSE stream (backlog + new output)
        if (segments[3].Equals("logs", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsMethod(ctx, "GET")) { WriteMethodNotAllowed(ctx); return; }
            if (segments.Length > 5 || (segments.Length == 5 && !segments[4].Equals("stream", StringComparison.OrdinalIgnoreCase)))
            { WriteJson(ctx, 404, "{\"error\":\"unknown route\"}"); return; }
            if (segments.Length >= 5 && segments[4].Equals("stream", StringComparison.OrdinalIgnoreCase))
            {
                await HandleServiceLogStreamAsync(ctx, pc.Id);
                return;
            }
            HandleServiceLogs(ctx, pc.Id);
            return;
        }

        // POST /api/services/{id}/{action}
        if (segments.Length != 4) { WriteJson(ctx, 404, "{\"error\":\"unknown route\"}"); return; }
        if (!IsMethod(ctx, "GET") && !IsMethod(ctx, "POST")) { WriteMethodNotAllowed(ctx); return; }
        switch (segments[3].ToLowerInvariant())
        {
            case "start":
                _processes.Start(pc.Id);
                break;
            case "stop":
                await _processes.StopAsync(pc.Id);
                break;
            case "restart":
                await _processes.RestartAsync(pc.Id);
                break;
            default:
                WriteJson(ctx, 404, "{\"error\":\"unknown action\"}");
                return;
        }

        WriteJson(ctx, 200, JsonSerializer.Serialize(Snapshot(_processes.GetServiceState(pc.Id) ?? pc), JsonOpts));
    }

    private void HandleServiceLogs(HttpListenerContext ctx, string serviceId)
    {
        var text = _processes!.GetBufferedOutput(serviceId);

        var linesParam = ctx.Request.QueryString["lines"];
        if (int.TryParse(linesParam, out var lines) && lines > 0)
        {
            var all = text.Split('\n');
            if (all.Length > lines)
                text = string.Join('\n', all[^lines..]);
        }

        AddCors(ctx.Response);
        WriteText(ctx, text, "text/plain; charset=utf-8");
    }

    private async Task HandleServiceLogStreamAsync(HttpListenerContext ctx, string serviceId)
    {
        var resp = ctx.Response;
        resp.StatusCode = 200;
        resp.ContentType = "text/event-stream";
        resp.Headers["Cache-Control"] = "no-cache";
        resp.Headers["X-Accel-Buffering"] = "no";
        AddCors(resp);
        resp.KeepAlive = true;
        resp.SendChunked = true;

        using var queue = new BlockingCollection<string>();
        Action<string, string> onOutput = (id, text) =>
        {
            if (id.Equals(serviceId, StringComparison.OrdinalIgnoreCase))
                try { queue.Add(text); } catch { /* stream closing */ }
        };
        _processes!.OutputReceived += onOutput;

        var stream = resp.OutputStream;
        try
        {
            await WriteSse(stream, "retry: 3000\n\n");

            // Backlog first, so a fresh consumer sees recent history.
            var backlog = _processes.GetBufferedOutput(serviceId);
            if (backlog.Length > 0)
                await WriteSse(stream, $"event: backlog\ndata: {JsonSerializer.Serialize(backlog, JsonOpts)}\n\n");

            while (true)
            {
                if (queue.TryTake(out var chunk, 15000))
                    await WriteSse(stream, $"data: {JsonSerializer.Serialize(chunk, JsonOpts)}\n\n");
                else
                    await WriteSse(stream, ": ping\n\n"); // heartbeat
            }
        }
        catch
        {
            // client disconnected
        }
        finally
        {
            _processes.OutputReceived -= onOutput;
            try { resp.Close(); } catch { }
        }
    }

    private async Task HandleCommandsAsync(HttpListenerContext ctx, string path)
    {
        if (ctx.Request.HttpMethod == "OPTIONS")
        {
            AddCors(ctx.Response);
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }

        if (_commands is null)
        {
            WriteJson(ctx, 503, "{\"error\":\"commands unavailable\"}");
            return;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 2)
        {
            if (!IsMethod(ctx, "GET"))
            {
                WriteMethodNotAllowed(ctx);
                return;
            }

            var rescan = GetQueryValue(ctx.Request.Url?.Query, "rescan") == "1";
            var packages = await _commands.GetPackagesAsync(rescan, CancellationToken.None);
            WriteJson(ctx, 200, JsonSerializer.Serialize(new
            {
                packages = packages.Select(CommandPackageSnapshot).ToArray()
            }, JsonOpts));
            return;
        }

        if (segments.Length == 3 && segments[2].Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsMethod(ctx, "POST"))
            {
                WriteMethodNotAllowed(ctx);
                return;
            }

            var body = ReadBody(ctx);
            var dir = GetJsonString(body, "dir");
            var script = GetJsonString(body, "script");
            string? error = null;
            if (dir is null || script is null || !_commands.TryRun(dir, script, out error))
            {
                WriteJson(ctx, 400, JsonSerializer.Serialize(new { error = error ?? "Could not run command." }, JsonOpts));
                return;
            }

            WriteJson(ctx, 200, "{\"ok\":true}");
            return;
        }

        WriteJson(ctx, 404, "{\"error\":\"unknown commands route\"}");
    }

    /// <summary>
    /// Routes for Android emulator/app helpers. No auth.
    ///   GET    /api/android
    ///   GET    /api/android/emulators
    ///   GET    /api/android/running-emulators
    ///   GET    /api/android/dashboard                         (stats + tuning)
    ///   POST   /api/android/emulators/clone
    ///   POST   /api/android/emulators/start-all
    ///   POST   /api/android/emulators/restart-all
    ///   POST   /api/android/emulators/scorched-update
    ///   POST   /api/android/emulators/{id}/launch | /stop | /restart-app
    ///   GET    /api/android/emulators/{id}/config
    ///   POST   /api/android/emulators/{id}/config            (ramSizeMb, cores, gpuMode)
    ///   DELETE /api/android/emulators/{id}
    ///   GET    /api/android/emulator-launchers              (legacy)
    ///   POST   /api/android/emulator-launchers/{id}/launch  (legacy)
    ///   POST   /api/android/launch-emulator/{id}            (legacy)
    ///   POST   /api/android/restart-app
    /// </summary>
    private async Task HandleAndroidAsync(HttpListenerContext ctx, string path)
    {
        if (ctx.Request.HttpMethod == "OPTIONS")
        {
            AddCors(ctx.Response);
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }

        if (_processes is null)
        {
            WriteJson(ctx, 503, "{\"error\":\"process manager unavailable\"}");
            return;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            if (segments.Length == 2)
            {
                if (!IsMethod(ctx, "GET"))
                {
                    WriteMethodNotAllowed(ctx);
                    return;
                }

                var (emulators, emulatorError) = await TryListAndroidEmulatorsAsync();
                var (runningEmulators, runningError) = await TryListAndroidRunningEmulatorsAsync();
                WriteJson(ctx, 200, JsonSerializer.Serialize(new
                {
                    packageName = _config.Android.PackageName,
                    emulators = emulators.Select(EmulatorSnapshot).ToArray(),
                    emulatorError,
                    runningEmulators,
                    runningError,
                    launchers = _processes.GetAndroidEmulators().Select(LegacyLauncherSnapshot).ToArray()
                }, JsonOpts));
                return;
            }

            if (segments.Length < 3)
            {
                WriteJson(ctx, 404, "{\"error\":\"unknown android route\"}");
                return;
            }

            var resource = segments[2].ToLowerInvariant();

            if (segments.Length == 3 && resource == "emulators")
            {
                if (!IsMethod(ctx, "GET"))
                {
                    WriteMethodNotAllowed(ctx);
                    return;
                }

                var emulators = await _processes.GetAndroidEmulatorsAsync(CancellationToken.None);
                WriteJson(ctx, 200, JsonSerializer.Serialize(new
                {
                    emulators = emulators.Select(EmulatorSnapshot).ToArray()
                }, JsonOpts));
                return;
            }

            if (segments.Length == 3 && resource == "running-emulators")
            {
                if (!IsMethod(ctx, "GET"))
                {
                    WriteMethodNotAllowed(ctx);
                    return;
                }

                var runningEmulators = await _processes.ListAndroidEmulatorsAsync(CancellationToken.None);
                WriteJson(ctx, 200, JsonSerializer.Serialize(new { runningEmulators }, JsonOpts));
                return;
            }

            if (segments.Length == 3 && resource == "dashboard")
            {
                if (!IsMethod(ctx, "GET"))
                {
                    WriteMethodNotAllowed(ctx);
                    return;
                }

                var dashboard = await _processes.GetAndroidDashboardAsync(CancellationToken.None);
                WriteJson(ctx, 200, JsonSerializer.Serialize(DashboardSnapshot(dashboard), JsonOpts));
                return;
            }

            if (segments.Length == 3 && (resource == "emulator-launchers" || resource == "launchers"))
            {
                if (!IsMethod(ctx, "GET"))
                {
                    WriteMethodNotAllowed(ctx);
                    return;
                }

                var launchers = _processes.GetAndroidEmulators().Select(LegacyLauncherSnapshot).ToArray();
                WriteJson(ctx, 200, JsonSerializer.Serialize(new { launchers }, JsonOpts));
                return;
            }

            if (segments.Length == 3 && resource == "restart-app")
            {
                if (!IsMethod(ctx, "POST"))
                {
                    WriteMethodNotAllowed(ctx);
                    return;
                }

                var emulators = await _processes.RestartJustGainsAppAsync(CancellationToken.None);
                WriteJson(ctx, 200, JsonSerializer.Serialize(new
                {
                    ok = true,
                    message = "JustGains app restarted.",
                    emulators
                }, JsonOpts));
                return;
            }

            if (resource == "emulators" && segments.Length == 4 && segments[3].Equals("clone", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsMethod(ctx, "POST"))
                {
                    WriteMethodNotAllowed(ctx);
                    return;
                }

                var emulator = await _processes.CloneAndroidEmulatorAsync(CancellationToken.None);
                WriteJson(ctx, 200, JsonSerializer.Serialize(new
                {
                    ok = true,
                    message = "Android emulator cloned.",
                    emulator = EmulatorSnapshot(emulator)
                }, JsonOpts));
                return;
            }

            if (resource == "emulators" && segments.Length == 4 && segments[3].Equals("start-all", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsMethod(ctx, "POST"))
                {
                    WriteMethodNotAllowed(ctx);
                    return;
                }

                var emulators = await _processes.StartAllAndroidEmulatorsAsync(CancellationToken.None);
                WriteJson(ctx, 200, JsonSerializer.Serialize(new
                {
                    ok = true,
                    message = "Android emulators started.",
                    emulators = emulators.Select(EmulatorSnapshot).ToArray()
                }, JsonOpts));
                return;
            }

            if (resource == "emulators" && segments.Length == 4 && segments[3].Equals("restart-all", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsMethod(ctx, "POST"))
                {
                    WriteMethodNotAllowed(ctx);
                    return;
                }

                var emulators = await _processes.RestartAllAndroidEmulatorsAsync(CancellationToken.None);
                WriteJson(ctx, 200, JsonSerializer.Serialize(new
                {
                    ok = true,
                    message = "Android emulators restarted.",
                    emulators = emulators.Select(EmulatorSnapshot).ToArray()
                }, JsonOpts));
                return;
            }

            if (resource == "emulators" && segments.Length == 4 && segments[3].Equals("scorched-update", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsMethod(ctx, "POST"))
                {
                    WriteMethodNotAllowed(ctx);
                    return;
                }

                await _processes.ScorchedAndroidUpdateAsync(CancellationToken.None);
                var emulators = await _processes.GetAndroidEmulatorsAsync(CancellationToken.None);
                WriteJson(ctx, 200, JsonSerializer.Serialize(new
                {
                    ok = true,
                    message = "Scorched update complete.",
                    emulators = emulators.Select(EmulatorSnapshot).ToArray()
                }, JsonOpts));
                return;
            }

            if (resource == "launch-emulator")
            {
                if (!IsMethod(ctx, "POST"))
                {
                    WriteMethodNotAllowed(ctx);
                    return;
                }

                var launcherId = segments.Length >= 4
                    ? Uri.UnescapeDataString(segments[3])
                    : GetJsonString(ReadBody(ctx), "id");
                await LaunchAndroidEmulatorAsync(ctx, launcherId);
                return;
            }

            if (resource == "emulators" && segments.Length == 4 && IsMethod(ctx, "DELETE"))
            {
                var emulatorId = Uri.UnescapeDataString(segments[3]);
                await _processes.DeleteAndroidEmulatorAsync(emulatorId, CancellationToken.None);
                WriteJson(ctx, 200, JsonSerializer.Serialize(new
                {
                    ok = true,
                    message = "Android emulator clone deleted.",
                    emulatorId
                }, JsonOpts));
                return;
            }

            if (resource == "emulators" && segments.Length == 5
                && segments[4].Equals("config", StringComparison.OrdinalIgnoreCase))
            {
                var emulatorId = Uri.UnescapeDataString(segments[3]);

                if (IsMethod(ctx, "GET"))
                {
                    var config = _processes.GetAndroidEmulatorConfig(emulatorId);
                    WriteJson(ctx, 200, JsonSerializer.Serialize(ConfigSnapshot(config), JsonOpts));
                    return;
                }

                if (IsMethod(ctx, "POST") || IsMethod(ctx, "PUT"))
                {
                    var body = ReadBody(ctx);
                    var config = _processes.UpdateAndroidEmulatorConfig(
                        emulatorId,
                        GetJsonInt(body, "ramSizeMb"),
                        GetJsonInt(body, "cores"),
                        GetJsonString(body, "gpuMode"));
                    WriteJson(ctx, 200, JsonSerializer.Serialize(new
                    {
                        ok = true,
                        message = "Emulator tuning saved (applies on next MuMu restart).",
                        emulator = ConfigSnapshot(config)
                    }, JsonOpts));
                    return;
                }

                WriteMethodNotAllowed(ctx);
                return;
            }

            if (resource == "emulators" && segments.Length == 5)
            {
                if (!IsMethod(ctx, "POST"))
                {
                    WriteMethodNotAllowed(ctx);
                    return;
                }

                var emulatorId = Uri.UnescapeDataString(segments[3]);
                switch (segments[4].ToLowerInvariant())
                {
                    case "launch":
                        await LaunchAndroidEmulatorAsync(ctx, emulatorId);
                        return;
                    case "stop":
                        await _processes.StopAndroidEmulatorAsync(emulatorId, CancellationToken.None);
                        WriteJson(ctx, 200, JsonSerializer.Serialize(new
                        {
                            ok = true,
                            message = "Android emulator stopped.",
                            emulatorId
                        }, JsonOpts));
                        return;
                    case "restart-app":
                        var emulators = await _processes.RestartJustGainsAppAsync(emulatorId, CancellationToken.None);
                        WriteJson(ctx, 200, JsonSerializer.Serialize(new
                        {
                            ok = true,
                            message = "JustGains app restarted.",
                            emulatorId,
                            emulators
                        }, JsonOpts));
                        return;
                    default:
                        WriteJson(ctx, 404, "{\"error\":\"unknown android emulator action\"}");
                        return;
                }
            }

            if ((resource == "emulator-launchers" || resource == "launchers")
                && segments.Length == 5
                && segments[4].Equals("launch", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsMethod(ctx, "POST"))
                {
                    WriteMethodNotAllowed(ctx);
                    return;
                }

                await LaunchAndroidEmulatorAsync(ctx, Uri.UnescapeDataString(segments[3]));
                return;
            }

            WriteJson(ctx, 404, "{\"error\":\"unknown android route\"}");
        }
        catch (KeyNotFoundException ex)
        {
            WriteJson(ctx, 404, JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts));
        }
        catch (FileNotFoundException ex)
        {
            WriteJson(ctx, 404, JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts));
        }
        catch (InvalidOperationException ex)
        {
            WriteJson(ctx, 409, JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts));
        }
        catch (Exception ex)
        {
            WriteJson(ctx, 500, JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts));
        }
    }

    private async Task LaunchAndroidEmulatorAsync(HttpListenerContext ctx, string? emulatorId)
    {
        emulatorId = ResolveAndroidEmulatorId(emulatorId);
        await _processes!.LaunchAndroidEmulatorAsync(emulatorId, CancellationToken.None);
        WriteJson(ctx, 200, JsonSerializer.Serialize(new
        {
            ok = true,
            message = "Android emulator started.",
            emulatorId
        }, JsonOpts));
    }

    private string ResolveAndroidEmulatorId(string? emulatorId)
    {
        if (!string.IsNullOrWhiteSpace(emulatorId))
            return emulatorId.Trim();

        var emulators = _processes!.GetAndroidEmulators();
        if (emulators.Count == 1)
            return emulators[0].Id;

        throw new InvalidOperationException("Android emulator id is required.");
    }

    private async Task<(IReadOnlyList<AndroidEmulatorInfo> Emulators, string? Error)> TryListAndroidEmulatorsAsync()
    {
        try
        {
            return (await _processes!.GetAndroidEmulatorsAsync(CancellationToken.None), null);
        }
        catch (Exception ex)
        {
            return (Array.Empty<AndroidEmulatorInfo>(), ex.Message);
        }
    }

    private async Task<(IReadOnlyList<string> Emulators, string? Error)> TryListAndroidRunningEmulatorsAsync()
    {
        try
        {
            return (await _processes!.ListAndroidEmulatorsAsync(CancellationToken.None), null);
        }
        catch (Exception ex)
        {
            return (Array.Empty<string>(), ex.Message);
        }
    }

    private static object CompactSnapshot(ServiceState state) => new
    {
        id = state.Id,
        name = state.Name,
        project = state.Project,
        status = state.Status.ToString().ToLowerInvariant(),
        running = state.Status == ProcessStatus.Running,
        pid = state.Pid,
        port = state.Port
    };

    private static object Snapshot(ServiceState state) => new
    {
        id = state.Id,
        name = state.Name,
        project = state.Project,
        description = state.Description,
        group = state.Group,
        autoStart = state.AutoStart,
        showInTray = state.ShowInTray,
        hideFromTray = state.HideFromTray,
        showTrayDot = state.ShowTrayDot,
        trayColor = state.TrayColor,
        status = state.Status.ToString().ToLowerInvariant(),
        running = state.Status == ProcessStatus.Running,
        pid = state.Pid,
        port = state.Port,
        url = state.Url,
        startedAt = state.StartedAt?.ToString("o")
    };

    private static object CommandPackageSnapshot(CommandPackage package) => new
    {
        dir = package.Dir,
        root = package.Root,
        relativeDir = package.RelativeDir,
        displayName = package.DisplayName,
        description = package.Description,
        scripts = package.Scripts.Select(CommandScriptSnapshot).ToArray()
    };

    private static object CommandScriptSnapshot(CommandScript script) => new
    {
        name = script.Name,
        command = script.Command,
        description = script.Description
    };

    private static object DashboardSnapshot(AndroidDashboard dashboard) => new
    {
        host = new
        {
            logicalCores = dashboard.Host.LogicalCores,
            totalRamMb = dashboard.Host.TotalRamMb,
            availableRamMb = dashboard.Host.AvailableRamMb,
            usedRamMb = dashboard.Host.UsedRamMb
        },
        error = dashboard.Error,
        emulators = dashboard.Emulators.Select(EmulatorViewSnapshot).ToArray()
    };

    private static object EmulatorViewSnapshot(AndroidEmulatorView e) => new
    {
        id = e.Id,
        avdName = e.AvdName,
        provider = "mumu",
        isReady = !string.IsNullOrWhiteSpace(e.Serial),
        name = e.Name,
        index = e.Index,
        isCore = e.IsCore,
        isClone = e.IsClone,
        isRunning = e.IsRunning,
        serial = e.Serial,
        pid = e.Pid,
        cpuPercent = e.CpuPercent,
        memoryMb = e.MemoryMb,
        appMemoryMb = e.AppMemoryMb,
        guestUsedRamMb = e.GuestUsedRamMb,
        guestTotalRamMb = e.GuestTotalRamMb,
        ramSizeMb = e.RamSizeMb,
        cores = e.Cores,
        gpuMode = e.GpuMode,
        lastCommand = e.LastCommand,
        lastCommandAt = e.LastCommandAt?.ToString("o"),
        idleTime = e.IdleTime,
        idleSeconds = e.IdleSeconds,
        commandIdleTime = e.CommandIdleTime,
        commandIdleSeconds = e.CommandIdleSeconds
    };

    private static object ConfigSnapshot(AndroidEmulatorConfig c) => new
    {
        id = c.Id,
        avdName = c.AvdName,
        name = c.Name,
        ramSizeMb = c.RamSizeMb,
        cores = c.Cores,
        gpuMode = c.GpuMode
    };

    private static object EmulatorSnapshot(AndroidEmulatorInfo emulator) => new
    {
        id = emulator.Id,
        avdName = emulator.AvdName,
        provider = "mumu",
        isReady = emulator.IsReady,
        name = emulator.Name,
        index = emulator.Index,
        isCore = emulator.IsCore,
        isClone = emulator.IsClone,
        isRunning = emulator.IsRunning,
        serial = emulator.Serial,
        lastCommand = emulator.LastCommand,
        lastCommandAt = emulator.LastCommandAt?.ToString("o"),
        idleTime = emulator.IdleTime,
        idleSeconds = emulator.IdleSeconds,
        commandIdleTime = emulator.CommandIdleTime,
        commandIdleSeconds = emulator.CommandIdleSeconds
    };

    private object LegacyLauncherSnapshot(AndroidEmulatorInfo emulator) => new
    {
        id = emulator.Id,
        name = emulator.Name,
        command = MuMuClient.ResolveManagerPath(_config.Android.MuMuManagerPath),
        args = new[] { "control", "-v", emulator.Index.ToString(), "launch" },
        workingDirectory = (string?)null,
        lastCommand = emulator.LastCommand,
        lastCommandAt = emulator.LastCommandAt?.ToString("o"),
        idleTime = emulator.IdleTime,
        idleSeconds = emulator.IdleSeconds,
        commandIdleTime = emulator.CommandIdleTime,
        commandIdleSeconds = emulator.CommandIdleSeconds
    };

    private static bool IsMethod(HttpListenerContext ctx, string method) =>
        ctx.Request.HttpMethod.Equals(method, StringComparison.OrdinalIgnoreCase);

    private static void WriteMethodNotAllowed(HttpListenerContext ctx) =>
        WriteJson(ctx, 405, "{\"error\":\"method not allowed\"}");

    private static void AddCors(HttpListenerResponse resp)
    {
        resp.Headers["Access-Control-Allow-Origin"] = "*";
        resp.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
        resp.Headers["Access-Control-Allow-Headers"] = "Content-Type";
    }

    private static void WriteJson(HttpListenerContext ctx, int status, string json)
    {
        AddCors(ctx.Response);
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    // --- helpers ---

    private static string ReadBody(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        return reader.ReadToEnd();
    }

    private static string? GetQueryValue(string? query, string key)
    {
        if (string.IsNullOrEmpty(query)) return null;
        if (query.StartsWith('?')) query = query[1..];
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0) continue;
            if (pair[..idx] == key) return Uri.UnescapeDataString(pair[(idx + 1)..]);
        }
        return null;
    }

    private static string? GetJsonString(string body, string property)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(property, out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    private static int? GetJsonInt(string body, string property)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty(property, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
            return null;
        }
        catch { return null; }
    }

    private static void WriteText(HttpListenerContext ctx, string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        if (_processes != null) _processes.OutputReceived -= ObserveProjectOutput;
        _discovery?.Dispose();
        try { _cts?.Cancel(); } catch { }
        lock (_lock)
        {
            foreach (var c in _clients)
                try { c.Queue.CompleteAdding(); } catch { }
            _clients.Clear();
        }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _cts?.Dispose();
    }
}
