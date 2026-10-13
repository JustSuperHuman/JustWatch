using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JustWatch.Core.Models;

namespace JustWatch.Core.Services;

public sealed partial class WebAgentServer
{
    private readonly ProjectDiscovery? _discovery;
    private readonly ConcurrentDictionary<string, (int? Pid, string Tail, string? Url)> _projectOutput = new();
    private static readonly Regex Ansi = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex LocalUrl = new(@"https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\]):\d{1,5}(?:/[^\s\x1b<>""']*)?", RegexOptions.Compiled);

    private void ObserveProjectOutput(string id, string text)
    {
        if (_processes == null) return;
        var pid = _processes.GetPid(id);
        _projectOutput.AddOrUpdate(id, _ => Parse("", null), (_, previous) => Parse(previous.Pid == pid ? previous.Tail : "", previous.Pid == pid ? previous.Url : null));
        (int?, string, string?) Parse(string tail, string? url)
        {
            var clean = Ansi.Replace(tail + text, "");
            var matches = LocalUrl.Matches(clean);
            if (matches.Count > 0 && Uri.TryCreate(matches[^1].Value, UriKind.Absolute, out var uri) && uri.Port is > 0 and <= 65535)
                url = new UriBuilder(uri) { Host = uri.Host == "0.0.0.0" ? "localhost" : uri.Host }.Uri.AbsoluteUri;
            return (pid, clean.Length > 512 ? clean[^512..] : clean, url);
        }
    }

    private object ProjectLibrarySnapshot()
    {
        var scan = _discovery!.Scan;
        var roots = _config.WatchFolders.ToArray();
        var services = _processes!.GetProcessConfigs();
        return new
        {
            scanning = _discovery.IsScanning, visitedDirectories = _discovery.VisitedDirectories,
            completedAt = scan.CompletedAt, error = scan.Error,
            folders = roots.Select(root => scan.Folders.FirstOrDefault(f => f.Path.Equals(root, StringComparison.OrdinalIgnoreCase)) ?? new FolderScan(root, 0, 0, null)),
            projects = scan.Projects.Where(p => p.Scripts.Length > 0 && roots.Any(root => ProjectDiscovery.IsWithin(p.Directory, root)))
                .OrderByDescending(p => p.Icon != null).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Directory).Select(p => new
            {
                p.Id, p.Name, directory = p.Directory, p.Framework, p.PackageManager,
                iconUrl = p.Icon == null ? null : $"/api/projects/{p.Id}/icon?v={p.Icon.Version}",
                scripts = p.Scripts.Select(script =>
                {
                    var service = _discovery.FindService(p, script.Name, services);
                    var state = service == null ? null : _processes.GetServiceState(service.Id);
                    var url = state?.Url;
                    if (service != null && _projectOutput.TryGetValue(service.Id, out var output) && output.Pid == state?.Pid && state?.Pid != null)
                        url = output.Url ?? url;
                    return new { script.Name, script.Command, serviceId = service?.Id,
                        status = state?.Status.ToString().ToLowerInvariant() ?? "stopped", pid = state?.Pid,
                        url = state?.Status == ProcessStatus.Running ? url : null };
                })
            })
        };
    }

    private static void ProjectReply(HttpListenerContext ctx, int code, object value)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, ServiceConfiguration.JsonOptions));
        ctx.Response.StatusCode = code; ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = bytes.Length; ctx.Response.OutputStream.Write(bytes); ctx.Response.Close();
    }

    private async Task HandleProjectsAsync(HttpListenerContext ctx, string path)
    {
        var origin = ctx.Request.Headers["Origin"];
        if (!ctx.Request.IsLocal || ctx.Request.Url?.IsLoopback != true ||
            (origin != null && origin != ctx.Request.Url.GetLeftPart(UriPartial.Authority)) || ctx.Request.Headers["Sec-Fetch-Site"] == "cross-site")
        { ProjectReply(ctx, 403, new { error = "Projects are available only on this computer from the JustWatch origin." }); return; }
        if (_discovery == null || _processes == null) { ProjectReply(ctx, 503, new { error = "Project discovery is unavailable." }); return; }
        try
        {
            var parts = path.Trim('/').Split('/');
            if (parts.Length == 2)
            {
                if (!IsMethod(ctx, "GET")) { ProjectReply(ctx, 405, new { error = "Use GET." }); return; }
                ProjectReply(ctx, 200, ProjectLibrarySnapshot()); return;
            }
            if (parts.Length == 4 && parts[3] == "icon" && IsMethod(ctx, "GET"))
            {
                var icon = _discovery.GetProject(parts[2]).Icon;
                if (icon == null) { ProjectReply(ctx, 404, new { error = "No project icon." }); return; }
                ctx.Response.ContentType = icon.ContentType;
                ctx.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'; style-src 'unsafe-inline'";
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                ctx.Response.Headers["Cache-Control"] = "private, max-age=120";
                ctx.Response.ContentLength64 = icon.Bytes.Length; ctx.Response.OutputStream.Write(icon.Bytes); ctx.Response.Close(); return;
            }
            if (parts.Length == 3 && parts[2] == "scan" && IsMethod(ctx, "POST"))
            { _ = _discovery.RequestScan(); ProjectReply(ctx, 202, new { scanning = true }); return; }
            if (parts.Length == 4 && parts[3] == "logs" && IsMethod(ctx, "GET"))
            {
                var project = _discovery.GetProject(parts[2]);
                var script = ctx.Request.QueryString["script"] ?? "";
                var service = _discovery.FindService(project, script);
                ProjectReply(ctx, 200, new { output = service == null ? "Start this script to see its output here." : Ansi.Replace(_processes.GetBufferedOutput(service.Id), "") }); return;
            }
            if (parts.Length == 4 && parts[3] == "delete-preview" && IsMethod(ctx, "GET"))
            { ProjectReply(ctx, 200, _discovery.PreviewDeletion(parts[2])); return; }
            if (!IsMethod(ctx, "POST") && !IsMethod(ctx, "DELETE")) { ProjectReply(ctx, 405, new { error = "Use POST or DELETE." }); return; }
            if (ctx.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true || ctx.Request.ContentLength64 > 8192)
            { ProjectReply(ctx, 400, new { error = "Send application/json, at most 8 KB." }); return; }
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var buffer = new char[8193]; var length = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            if (length > 8192) { ProjectReply(ctx, 413, new { error = "Request is too large." }); return; }
            using var body = JsonDocument.Parse(new string(buffer, 0, length));
            string Field(string key) => body.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new ArgumentException($"{key} is required.");
            if (parts.Length == 3 && parts[2] == "folders")
            {
                await _processes.UpdateWatchFolderAsync(Field("path"), IsMethod(ctx, "DELETE"));
                _ = _discovery.RequestScan(changed: true);
                ProjectReply(ctx, 200, ProjectLibrarySnapshot()); return;
            }
            if (parts.Length == 3 && IsMethod(ctx, "DELETE"))
            {
                await _discovery.DeleteProjectAsync(parts[2], Field("token"), Field("confirmation"));
                ProjectReply(ctx, 200, ProjectLibrarySnapshot()); return;
            }
            if (parts.Length == 4 && IsMethod(ctx, "POST"))
            {
                var script = Field("script");
                var project = _discovery.GetProject(parts[2]);
                if (parts[3] == "start") await _discovery.StartProjectAsync(project.Id, script);
                else if (parts[3] == "stop")
                {
                    var service = _discovery.FindService(project, script);
                    if (service != null) await _processes.StopAsync(service.Id);
                }
                else { ProjectReply(ctx, 404, new { error = "Unknown project action." }); return; }
                ProjectReply(ctx, 200, ProjectLibrarySnapshot()); return;
            }
            ProjectReply(ctx, 404, new { error = "Unknown project route." });
        }
        catch (KeyNotFoundException ex) { ProjectReply(ctx, 404, new { error = ex.Message }); }
        catch (JsonException) { ProjectReply(ctx, 400, new { error = "Invalid JSON request or package.json. Rescan after correcting it." }); }
        catch (ArgumentException ex) { ProjectReply(ctx, 400, new { error = ex.Message }); }
        catch (IOException ex) { ProjectReply(ctx, 409, new { error = ex.Message }); }
        catch (UnauthorizedAccessException ex) { ProjectReply(ctx, 409, new { error = ex.Message }); }
        catch (InvalidOperationException ex) { ProjectReply(ctx, 409, new { error = ex.Message }); }
        catch (Exception ex) { ProjectReply(ctx, 500, new { error = ex.Message }); }
    }
}
