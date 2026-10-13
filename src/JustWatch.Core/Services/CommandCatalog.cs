using System.Diagnostics;
using System.Text.Json;
using JustWatch.Core.Models;

namespace JustWatch.Core.Services;

public sealed class CommandPackage
{
    public string Root { get; init; } = string.Empty;
    public string Dir { get; init; } = string.Empty;
    public string RelativeDir { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public List<CommandScript> Scripts { get; init; } = [];
}

public sealed class CommandScript
{
    public string Name { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public string? Description { get; init; }
}

public sealed class CommandCatalog
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".next",
        ".expo",
        "node_modules",
        "dist",
        "build",
        "coverage",
        "out"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly JustWatchConfig _config;
    private readonly object _cacheLock = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private IReadOnlyList<CommandPackage>? _cachedPackages;
    private DateTimeOffset _scannedAt = DateTimeOffset.MinValue;

    public CommandCatalog(JustWatchConfig config)
    {
        _config = config;
    }

    public bool IsEnabled => _config.Commands.Enabled;

    public async Task<IReadOnlyList<CommandPackage>> GetPackagesAsync(
        bool forceRescan = false,
        CancellationToken ct = default)
    {
        if (!IsEnabled) return [];

        lock (_cacheLock)
        {
            if (!forceRescan && _cachedPackages is not null
                && DateTimeOffset.Now - _scannedAt < TimeSpan.FromSeconds(30))
            {
                return _cachedPackages;
            }
        }

        await _scanLock.WaitAsync(ct);
        try
        {
            lock (_cacheLock)
            {
                if (!forceRescan && _cachedPackages is not null
                    && DateTimeOffset.Now - _scannedAt < TimeSpan.FromSeconds(30))
                {
                    return _cachedPackages;
                }
            }

            var packages = await Task.Run(() => Scan(ct), ct);
            lock (_cacheLock)
            {
                _cachedPackages = packages;
                _scannedAt = DateTimeOffset.Now;
                return _cachedPackages;
            }
        }
        finally
        {
            _scanLock.Release();
        }
    }

    public bool TryRun(string packageDir, string scriptName, out string? error)
    {
        error = null;

        if (!IsEnabled)
        {
            error = "Commands are disabled.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(packageDir) || string.IsNullOrWhiteSpace(scriptName))
        {
            error = "Package directory and script are required.";
            return false;
        }

        if (scriptName.Contains('"') || scriptName.Contains('\r') || scriptName.Contains('\n'))
        {
            error = "Invalid script name.";
            return false;
        }

        CommandPackage? package;
        CommandScript? script;
        lock (_cacheLock)
        {
            if (_cachedPackages is null)
            {
                error = "Command catalog has not been scanned yet.";
                return false;
            }

            package = _cachedPackages.FirstOrDefault(p =>
                p.Dir.Equals(packageDir, StringComparison.OrdinalIgnoreCase));
            script = package?.Scripts.FirstOrDefault(s =>
                s.Name.Equals(scriptName, StringComparison.Ordinal));
        }

        if (package is null || script is null)
        {
            error = "Unknown command.";
            return false;
        }

        try
        {
            var title = SanitizeTitle($"{package.DisplayName} - {script.Name}");
            var args = $"/k \"title {title} && bun run {script.Name}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", args)
            {
                WorkingDirectory = package.Dir,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private IReadOnlyList<CommandPackage> Scan(CancellationToken ct)
    {
        var roots = _config.Commands.Roots.Count > 0
            ? _config.Commands.Roots
            : [_config.ProjectPath];
        var maxDepth = Math.Max(0, _config.Commands.MaxDepth);
        var packages = new List<CommandPackage>();

        foreach (var configuredRoot in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(configuredRoot)) continue;

            string root;
            try { root = Path.GetFullPath(configuredRoot); }
            catch { continue; }

            if (!Directory.Exists(root)) continue;
            Visit(root, root, 0, maxDepth, packages, ct);
        }

        return packages
            .OrderBy(p => p.RelativeDir, StringComparer.Ordinal)
            .ToArray();
    }

    private static void Visit(
        string root,
        string currentDir,
        int depth,
        int maxDepth,
        List<CommandPackage> packages,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        DirectoryInfo directory;
        try { directory = new DirectoryInfo(currentDir); }
        catch { return; }

        FileInfo[] files;
        DirectoryInfo[] directories;
        try
        {
            files = directory.GetFiles();
            directories = directory.GetDirectories();
        }
        catch
        {
            return;
        }

        var packageJson = files.FirstOrDefault(f =>
            f.Name.Equals("package.json", StringComparison.Ordinal));
        if (packageJson is not null && LoadPackage(root, packageJson.FullName) is { } package)
            packages.Add(package);

        if (depth >= maxDepth) return;

        foreach (var child in directories)
        {
            // Dot-directories (.git, .claude worktrees, .expo, …) are tooling
            // internals — their package.json copies would duplicate the menu.
            if (child.Name.StartsWith('.') || SkippedDirectories.Contains(child.Name)) continue;
            Visit(root, child.FullName, depth + 1, maxDepth, packages, ct);
        }
    }

    private static CommandPackage? LoadPackage(string root, string packageJsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            var rootElement = doc.RootElement;
            if (!rootElement.TryGetProperty("scripts", out var scriptsElement)
                || scriptsElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var metadata = LoadMetadata(Path.GetDirectoryName(packageJsonPath)!);
            var scripts = scriptsElement.EnumerateObject()
                .Where(p => p.Name != "it")
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => new CommandScript
                {
                    Name = p.Name,
                    Command = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? string.Empty : p.Value.ToString(),
                    Description = metadata.GetScriptDescription(p.Name)
                })
                .ToList();

            if (scripts.Count == 0) return null;

            var dir = Path.GetDirectoryName(packageJsonPath)!;
            var relativeDir = Path.GetRelativePath(root, dir);
            if (string.IsNullOrWhiteSpace(relativeDir)) relativeDir = ".";
            if (relativeDir == string.Empty) relativeDir = ".";

            var displayName = rootElement.TryGetProperty("name", out var nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()?.Trim()
                    : null;

            return new CommandPackage
            {
                Root = root,
                Dir = dir,
                RelativeDir = relativeDir,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? relativeDir : displayName,
                Description = metadata.Description,
                Scripts = scripts
            };
        }
        catch
        {
            return null;
        }
    }

    private static CommandMetadata LoadMetadata(string dir)
    {
        try
        {
            var path = Path.Combine(dir, "package.doit.json");
            if (!File.Exists(path)) return new CommandMetadata();
            return JsonSerializer.Deserialize<CommandMetadata>(File.ReadAllText(path), JsonOptions)
                   ?? new CommandMetadata();
        }
        catch
        {
            return new CommandMetadata();
        }
    }

    private static string SanitizeTitle(string title)
    {
        var chars = title.Where(c => c is not ('&' or '|' or '<' or '>' or '^' or '%' or '"' or '(' or ')'));
        var sanitized = new string(chars.ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Command" : sanitized;
    }

    private sealed class CommandMetadata
    {
        public string? Description { get; set; }
        public Dictionary<string, CommandScriptMetadata> Scripts { get; set; } = new();

        public string? GetScriptDescription(string scriptName)
        {
            if (!Scripts.TryGetValue(scriptName, out var metadata)) return null;
            var description = metadata.Description?.Trim();
            return string.IsNullOrWhiteSpace(description) ? null : description;
        }
    }

    private sealed class CommandScriptMetadata
    {
        public string? Description { get; set; }
    }
}
