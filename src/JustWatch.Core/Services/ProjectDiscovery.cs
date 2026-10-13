using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JustWatch.Core.Models;

namespace JustWatch.Core.Services;

public sealed record ProjectScript(string Name, string Command);
public sealed record ProjectIcon(byte[] Bytes, string ContentType, string Version);
public sealed record DiscoveredProject(string Id, string Name, string Directory, string Framework,
    string PackageManager, ProjectScript[] Scripts, ProjectIcon? Icon);
public sealed record FolderScan(string Path, int ProjectCount, int Skipped, string? Error);
public sealed record DiscoveryScan(DiscoveredProject[] Projects, FolderScan[] Folders, DateTime? CompletedAt, string? Error = null);

/// <summary>Bounded, read-only discovery. Periodic scans also recover from offline drives and new folders.</summary>
public sealed partial class ProjectDiscovery : IDisposable
{
    private readonly ProcessManager _manager;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _actions = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Timer _timer;
    private Task _scanTask = Task.CompletedTask;
    private volatile DiscoveryScan _scan = new([], [], null);
    private volatile bool _scanning;
    private int _visited;
    private bool _rescanRequested;
    private bool _disposed;
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".next", ".nuxt", ".output", ".cache", ".turbo", ".svelte-kit", ".expo",
        ".yarn", ".bun", "bun-cache", ".npm", ".npm-cache", ".pnpm", ".pnpm-store", "pnpm-store", "dist", "build", "out", "coverage", "vendor", "bin", "obj",
        "$RECYCLE.BIN", "System Volume Information", "Windows", "Program Files", "Program Files (x86)",
        "ProgramData", ".venv", "venv", "__pycache__", "tmp", ".tmp", "temp", ".temp"
    };
    private static readonly EnumerationOptions Enumeration = new()
    { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };

    public ProjectDiscovery(ProcessManager manager)
    {
        _manager = manager;
        _timer = new Timer(_ => RequestScan(), null, Timeout.Infinite, Timeout.Infinite);
    }
    public void Start() { RequestScan(); _timer.Change(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2)); }
    public bool IsScanning => _scanning;
    public int VisitedDirectories => Volatile.Read(ref _visited);
    public DiscoveryScan Scan => _scan;

    public Task RequestScan(bool changed = false)
    {
        lock (_gate)
        {
            if (_disposed || _deleting) return Task.CompletedTask;
            if (_scanning) { _rescanRequested |= changed; return _scanTask; }
            _scanning = true;
            _visited = 0;
            _scanTask = Task.Run(() =>
            {
                try
                {
                    do
                    {
                        lock (_gate) _rescanRequested = false;
                        var roots = _manager.Config.WatchFolders.ToArray();
                        var result = ScanFolders(roots, _stop.Token, count => Volatile.Write(ref _visited, count), partial =>
                        {
                            lock (_gate)
                            {
                                if (!roots.SequenceEqual(_manager.Config.WatchFolders)) return;
                                // Keep existing cards stable during refresh; remove vanished projects only at completion.
                                _scan = new(_scan.Projects.Concat(partial.Projects).GroupBy(p => p.Id).Select(g => g.Last())
                                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray(), partial.Folders, _scan.CompletedAt);
                            }
                        });
                        lock (_gate)
                        {
                            if (!roots.SequenceEqual(_manager.Config.WatchFolders)) { _rescanRequested = true; continue; }
                            _scan = result;
                            if (!_rescanRequested) { _scanning = false; return; }
                        }
                    } while (!_stop.IsCancellationRequested);
                }
                catch (OperationCanceledException) { lock (_gate) _scanning = false; }
                catch (Exception ex) { lock (_gate) { _scan = _scan with { Error = ex.Message }; _scanning = false; } }
            });
            return _scanTask;
        }
    }

    public static bool IsWithin(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool SafePath(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    public static DiscoveryScan ScanFolders(IEnumerable<string> paths, CancellationToken ct = default, Action<int>? progress = null, Action<DiscoveryScan>? partial = null)
    {
        var projects = new Dictionary<string, DiscoveredProject>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new List<FolderScan>();
        var total = 0;
        var publishClock = Stopwatch.StartNew();
        foreach (var raw in paths)
        {
            ct.ThrowIfCancellationRequested();
            var skipped = 0;
            string root;
            try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw)); }
            catch (Exception ex) when (ex is ArgumentException or IOException) { folders.Add(new(raw, 0, 0, "Invalid folder path.")); continue; }
            if (!Directory.Exists(root)) { folders.Add(new(root, 0, 0, "Folder unavailable. Reconnect the drive or update the path.")); continue; }
            var pending = new Stack<string>(); pending.Push(root);
            string? error = null;
            while (pending.TryPop(out var dir))
            {
                ct.ThrowIfCancellationRequested();
                if (!visited.Add(dir)) continue;
                if (++total > 200000 || projects.Count >= 10000) { error = "Scan limit reached. Watch a more specific folder to see all projects."; break; }
                if (total % 100 == 0)
                {
                    progress?.Invoke(total);
                    if (partial != null && publishClock.ElapsedMilliseconds >= 750)
                    {
                        partial(new(projects.Values.ToArray(), [.. folders, new(root, projects.Values.Count(p => IsWithin(p.Directory, root)), skipped, null)], null));
                        publishClock.Restart();
                    }
                }
                try
                {
                    // Enumeration skips links; check the current directory again in case it changed.
                    // Validate the root's ancestors once, and fully validate packages/icons before reading them.
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0 || (dir == root && !SafePath(dir))) { skipped++; continue; }
                    var package = Path.Combine(dir, "package.json");
                    if (File.Exists(package))
                    {
                        try { var project = ReadProject(dir); if (project.Scripts.Length > 0) projects[project.Id] = project; }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { skipped++; }
                    }
                    foreach (var child in Directory.EnumerateDirectories(dir, "*", Enumeration))
                        if (!Ignored.Contains(Path.GetFileName(child))) pending.Push(child);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { skipped++; if (dir.Equals(root, StringComparison.OrdinalIgnoreCase)) error = "Cannot read this folder. Check folder permissions."; }
            }
            folders.Add(new(root, projects.Values.Count(p => IsWithin(p.Directory, root)), skipped, error));
        }
        progress?.Invoke(total);
        // Overlapping roots are deliberately deduplicated; counts still describe each root.
        return new(projects.Values.OrderByDescending(p => p.Icon != null).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Directory).ToArray(),
            folders.Select(f => f with { ProjectCount = projects.Values.Count(p => IsWithin(p.Directory, f.Path)) }).ToArray(), DateTime.UtcNow);
    }

    public static DiscoveredProject ReadProject(string dir)
    {
        var package = Path.Combine(dir, "package.json");
        if (!SafePath(package)) throw new ArgumentException("Linked packages are skipped.");
        using var doc = JsonDocument.Parse(ReadSmallFile(package, 1024 * 1024));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Package must be an object.");
        string? GetString(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var name = GetString("name");
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(dir);
        name = name!.Length > 120 ? name[..120] : name;
        var scripts = new List<ProjectScript>();
        var detector = new WebServiceDetector(dir, root);
        if (root.TryGetProperty("scripts", out var scriptObject) && scriptObject.ValueKind == JsonValueKind.Object)
            foreach (var script in scriptObject.EnumerateObject())
                if (script.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(script.Value.GetString()) && detector.IsWebScript(script.Name))
                    scripts.Add(new(script.Name, script.Value.GetString()!));
        int Rank(string s) => s == "dev" ? 0 : s.StartsWith("dev:") ? 1 : s == "start" ? 2 : s == "serve" ? 3 : 4;
        var dependencies = new HashSet<string>();
        foreach (var key in new[] { "dependencies", "devDependencies" })
            if (root.TryGetProperty(key, out var deps) && deps.ValueKind == JsonValueKind.Object)
                foreach (var dep in deps.EnumerateObject()) dependencies.Add(dep.Name);
        var framework = new[] { ("next", "Next.js"), ("nuxt", "Nuxt"), ("astro", "Astro"), ("@sveltejs/kit", "SvelteKit"),
            ("expo", "Expo"), ("@angular/core", "Angular"), ("vite", "Vite"), ("react", "React"), ("vue", "Vue"),
            ("express", "Express"), ("@nestjs/core", "NestJS") }.FirstOrDefault(f => dependencies.Contains(f.Item1)).Item2 ?? "Node.js";
        var manager = DetectPackageManager(dir, GetString("packageManager"));
        var id = "project-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dir.ToUpperInvariant())))[..20].ToLowerInvariant();
        return new(id, name, dir, framework, manager, scripts.OrderBy(s => Rank(s.Name)).ThenBy(s => s.Name).ToArray(), FindIcon(dir));
    }

    private static byte[] ReadSmallFile(string path, int limit)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > limit) throw new IOException("File is too large.");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }

    private static string DetectPackageManager(string dir, string? declared)
    {
        string? Known(string? value) => value?.Split('@')[0] is "bun" or "pnpm" or "yarn" or "npm" ? value.Split('@')[0] : null;
        if (Known(declared) is { } own) return own;
        for (var current = dir; current != null; current = Path.GetDirectoryName(current))
        {
            if (current != dir)
                try
                {
                    var file = Path.Combine(current, "package.json");
                    if (File.Exists(file) && SafePath(file))
                    {
                        using var doc = JsonDocument.Parse(ReadSmallFile(file, 1024 * 1024));
                        if (doc.RootElement.TryGetProperty("packageManager", out var pm) && pm.ValueKind == JsonValueKind.String && Known(pm.GetString()) is { } parent) return parent;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
            foreach (var (file, pm) in new[] { ("bun.lock", "bun"), ("bun.lockb", "bun"), ("pnpm-lock.yaml", "pnpm"), ("yarn.lock", "yarn"), ("package-lock.json", "npm") })
                if (File.Exists(Path.Combine(current, file))) return pm;
        }
        return "npm";
    }

    private static ProjectIcon? FindIcon(string dir)
    {
        // Framework conventions first, then common icon subfolders. No external requests.
        var locations = new[] { "public", "static", "app", "src/app", "", "assets", "assets/images", "assets/icons", "src", "src/assets", "src/lib/assets", "public/icons", "static/icons", "public/images" };
        var names = new[] { "favicon.svg", "favicon.ico", "favicon.png", "favicon.webp", "icon.svg", "icon.png", "apple-touch-icon.png", "logo.svg", "logo.png" };
        foreach (var location in locations)
            foreach (var name in names)
            {
                var path = Path.Combine(dir, location, name);
                try
                {
                    if (!File.Exists(path) || !SafePath(path)) continue;
                    var bytes = ReadSmallFile(path, 512 * 1024);
                    if (bytes.Length == 0) continue;
                    var mime = Path.GetExtension(path) switch { ".svg" => "image/svg+xml", ".ico" => "image/x-icon", ".webp" => "image/webp", _ => "image/png" };
                    return new(bytes, mime, Convert.ToHexString(SHA256.HashData(bytes))[..12]);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        return null;
    }

    public DiscoveredProject GetProject(string id) => _scan.Projects.FirstOrDefault(p => p.Id == id &&
        _manager.Config.WatchFolders.Any(root => IsWithin(p.Directory, root))) ?? throw new KeyNotFoundException("Project no longer exists in a watched folder. Refresh the library.");

    public ProcessConfig? FindService(DiscoveredProject project, string script, IReadOnlyList<ProcessConfig>? services = null) =>
        (services ?? _manager.GetProcessConfigs()).FirstOrDefault(p =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(string.IsNullOrWhiteSpace(p.WorkingDirectory) ? _manager.Config.ProjectPath : p.WorkingDirectory, _manager.Config.ProjectPath))
                .Equals(project.Directory, StringComparison.OrdinalIgnoreCase) &&
            ((p.Args.Length == 2 && p.Args[0] == "run" && p.Args[1] == script && Path.GetFileNameWithoutExtension(p.Command) is "bun" or "npm" or "pnpm" or "yarn") ||
             p.Id == ServiceId(project.Id, script)));

    private static string ServiceId(string project, string script) => project + "-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)))[..10].ToLowerInvariant();

    public async Task<ProcessConfig> StartProjectAsync(string id, string script)
    {
        await _actions.WaitAsync();
        try
        {
            var project = GetProject(id);
            // Re-read before execution: removed or edited manifests must not launch stale scripts.
            project = ReadProject(project.Directory);
            if (!project.Scripts.Any(s => s.Name == script)) throw new ArgumentException("That dev script is no longer available. Rescan this project.");
            var services = _manager.GetProcessConfigs();
            var service = FindService(project, script, services);
            if (service == null)
            {
                service = CreateService(project, script);
                await _manager.ApplyConfigurationAsync([.. services, service], ServiceConfiguration.Revision(services));
            }
            _manager.Start(service.Id);
            return service;
        }
        finally { _actions.Release(); }
    }

    public static ProcessConfig CreateService(DiscoveredProject project, string script)
    {
        var command = project.PackageManager;
        string[] arguments = ["run", script];
        if (command != "bun")
        {
            // Windows package-manager shims are .cmd files. Execute the resolved JS entrypoint
            // with node so paths and script names never pass through shell interpolation.
            var shim = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(p => Path.Combine(p.Trim('"'), project.PackageManager + ".cmd")).FirstOrDefault(File.Exists);
            if (shim == null) throw new InvalidOperationException($"{command} is not installed or is not on JustWatch's PATH. Install it and restart JustWatch.");
            var home = Path.GetDirectoryName(shim)!;
            var candidates = command switch
            {
                "npm" => new[] { "node_modules/npm/bin/npm-cli.js" },
                "pnpm" => new[] { "node_modules/pnpm/bin/pnpm.cjs", "node_modules/corepack/dist/pnpm.js" },
                _ => new[] { "node_modules/yarn/bin/yarn.js", "node_modules/corepack/dist/yarn.js" }
            };
            var entry = candidates.Select(p => Path.Combine(home, p)).FirstOrDefault(File.Exists);
            if (entry == null)
            {
                // Standalone pnpm provides an executable instead of a Node entrypoint.
                var exe = Path.Combine(home, command + ".exe");
                if (!File.Exists(exe)) throw new InvalidOperationException($"Cannot locate the installed {command} entrypoint. Configure this project manually in Services.");
                command = exe;
            }
            else { command = "node"; arguments = [entry, "run", script]; }
        }
        return new ProcessConfig
        {
            Id = ServiceId(project.Id, script), Name = project.Name + " · " + script,
            Project = project.Name.Length > 80 ? project.Name[..80] : project.Name,
            Description = $"{project.PackageManager} run {script}", Group = "Discovered",
            Command = command, Args = arguments, WorkingDirectory = project.Directory,
            AutoStart = false, AutoRestart = false, AgentRecovery = false, ReclaimPort = false,
            ShowInTray = true, ShowTrayDot = false, RestartOutputPatterns = [], TrayColor = "#6875E8"
        };
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _timer.Dispose(); _stop.Cancel(); }
        // Let in-flight file reads unwind before disposing their cancellation source.
        _ = _scanTask.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}
