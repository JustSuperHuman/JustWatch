using System.Text.Json;
using System.Text.RegularExpressions;

namespace JustWatch.Core.Services;

/// <summary>Conservative static discovery: script names and dependencies alone do not prove an HTTP service.</summary>
internal sealed class WebServiceDetector(string projectDirectory, JsonElement package)
{
    private readonly Dictionary<string, bool> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _sources = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex Tokens = new("\"[^\"]*\"|'[^']*'|&&|\\|\\||[;|]|[^\\s;&|]+", RegexOptions.Compiled);
    private static readonly Regex SourcePath = new(@"\.(?:[cm]?[jt]sx?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool IsWebScript(string name) => IsWebScript(projectDirectory, package, name, new HashSet<string>(), 0);

    private bool IsWebScript(string directory, JsonElement manifest, string name, HashSet<string> chain, int depth)
    {
        if (depth > 8 || !Regex.IsMatch(name, @"^[a-zA-Z0-9][a-zA-Z0-9:_-]{0,99}$") ||
            Regex.IsMatch(name, @"^(?:pre(?!view)|post)|^(?:build|test|smoke|e2e|bench|benchmark|lint|check|typecheck|validate|audit|deploy|install|publish|release|format|migrate|seed|clean)(?:$|[:_-])", RegexOptions.IgnoreCase)) return false;
        var key = directory + "|" + name;
        if (_results.TryGetValue(key, out var cached)) return cached;
        if (!chain.Add(key)) return false;
        try
        {
            if (manifest.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object &&
                scripts.TryGetProperty(name, out var script) && script.ValueKind == JsonValueKind.String)
                return _results[key] = IsWebCommand(directory, manifest, script.GetString()!, chain, depth + 1);
            return false;
        }
        finally { chain.Remove(key); }
    }

    private bool IsWebCommand(string directory, JsonElement manifest, string command, HashSet<string> chain, int depth)
    {
        if (depth > 8 || command.Length > 16384) return false;
        var segment = new List<string>();
        foreach (Match match in Tokens.Matches(command))
        {
            if (match.Value is "&&" or "||" or ";" or "|")
            {
                if (IsWebSegment(directory, manifest, segment, chain, depth)) return true;
                // Resolve common workspace wrappers without executing their shell commands.
                if (segment.Count == 2 && segment[0] == "cd")
                {
                    var next = Path.GetFullPath(segment[1], directory);
                    if (!ProjectDiscovery.IsWithin(next, projectDirectory)) return false;
                    directory = next;
                    var json = ReadSource(Path.Combine(directory, "package.json"));
                    if (json != null) { try { using var doc = JsonDocument.Parse(json); manifest = doc.RootElement.Clone(); } catch (JsonException) { return false; } }
                }
                segment.Clear();
            }
            else segment.Add(match.Value.Trim('"', '\''));
        }
        return IsWebSegment(directory, manifest, segment, chain, depth);
    }

    private bool IsWebSegment(string directory, JsonElement manifest, List<string> args, HashSet<string> chain, int depth)
    {
        if (args.Count == 0 || depth > 8) return false;
        var i = 0;
        while (i < args.Count && (Regex.IsMatch(args[i], @"^[A-Za-z_][A-Za-z0-9_]*=") || args[i] is "cross-env" or "cross-env-shell" or "env")) i++;
        if (i == args.Count) return false;
        args = args.Skip(i).ToList();
        var exe = Path.GetFileNameWithoutExtension(args[0]).ToLowerInvariant();
        var tail = args.Skip(1).ToArray();
        if (tail.Any(a => a is "--help" or "-h" or "--version" or "-v")) return false;
        var first = tail.FirstOrDefault() ?? "";
        bool Subcommand(params string[] allowed) => allowed.Contains(first);
        bool DefaultOr(params string[] allowed) => first == "" || first.StartsWith('-') || allowed.Contains(first);

        if (exe is "npx" or "bunx") return IsWebSegment(directory, manifest, tail.Where(a => a is not "--yes" and not "-y").ToList(), chain, depth + 1);
        if (exe is "npm" or "pnpm" or "yarn" or "bun")
        {
            var folderIndex = args.FindIndex(a => a is "--prefix" or "--cwd" or "-C" or "--dir");
            if (folderIndex >= 0 && folderIndex + 1 < args.Count)
            {
                directory = Path.GetFullPath(args[folderIndex + 1], directory);
                if (!ProjectDiscovery.IsWithin(directory, projectDirectory)) return false;
                args.RemoveRange(folderIndex, 2);
                var json = ReadSource(Path.Combine(directory, "package.json"));
                if (json == null) return false;
                try { using var doc = JsonDocument.Parse(json); return IsWebSegment(directory, doc.RootElement, args, chain, depth + 1); }
                catch (JsonException) { return false; }
            }
            if (Subcommand("exec", "dlx")) return IsWebSegment(directory, manifest, tail.Skip(1).Where(a => a != "--").ToList(), chain, depth + 1);
            // Filtered/recursive monorepo tasks are represented by their individual discovered apps.
            if (tail.Any(a => a is "--filter" or "--workspace" or "--workspaces" or "-w" or "-r" or "--recursive")) return false;
            var runIndex = Array.IndexOf(tail, "run");
            var script = runIndex >= 0 ? tail.ElementAtOrDefault(runIndex + 1) : first;
            if (script != null && !SourcePath.IsMatch(script) && !script.StartsWith('-') && IsWebScript(directory, manifest, script, chain, depth + 1)) return true;
            if (exe != "bun") return false;
        }

        if (exe == "next") return Subcommand("dev", "start");
        if (exe is "astro" or "nuxt" or "nuxi") return Subcommand("dev", "start", "preview");
        if (exe == "vite") return DefaultOr("dev", "serve", "preview");
        if (exe == "vitepress") return !Subcommand("build", "init") && (DefaultOr("dev", "preview") || !first.Contains(':'));
        if (exe == "webpack") return Subcommand("serve");
        if (exe is "webpack-dev-server" or "http-server" or "live-server" or "serve" or "start-storybook" or "remix-serve") return true;
        if (exe == "react-scripts") return Subcommand("start");
        if (exe == "vue-cli-service") return Subcommand("serve");
        if (exe == "ng") return Subcommand("serve");
        if (exe == "remix") return Subcommand("dev", "vite:dev");
        if (exe == "react-router") return Subcommand("dev", "serve");
        if (exe == "storybook") return Subcommand("dev");
        if (exe == "docusaurus") return Subcommand("start", "serve");
        if (exe is "vercel" or "netlify") return Subcommand("dev");
        if (exe == "wrangler") return Subcommand("dev") || (first == "pages" && tail.ElementAtOrDefault(1) == "dev");
        if (exe == "expo") return Subcommand("start") && tail.Contains("--web") && !tail.Any(a => a is "--ios" or "--android");
        if (exe == "fastify") return Subcommand("start");
        if (exe == "nest") return Subcommand("start");
        if (exe == "nx") return Regex.IsMatch(first, @"^(?:[\w-]+:)?(?:serve|dev|start)$") &&
            manifest.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object && deps.TryGetProperty("next", out _);
        if (exe == "python" || exe == "python3") return tail.Contains("http.server") || (tail.Contains("manage.py") && tail.Contains("runserver"));
        if (exe == "php") return tail.Contains("-S") || (first == "artisan" && tail.Contains("serve"));

        if (exe is not ("node" or "bun" or "tsx" or "ts-node" or "ts-node-dev" or "nodemon" or "deno")) return false;
        var execIndex = Array.IndexOf(tail, "--exec");
        if (exe == "nodemon" && execIndex >= 0 && execIndex + 1 < tail.Length)
            return IsWebCommand(directory, manifest, string.Join(' ', tail.Skip(execIndex + 1)), chain, depth + 1);
        // Inspect the actual entrypoint (not preload modules), with bounded local import traversal.
        for (var j = 0; j < tail.Length; j++)
        {
            if (tail[j] is "--import" or "--require" or "-r" or "--loader" or "--watch" or "--ignore" or "--ext")
            { if (tail[j] is not "--watch" || exe == "nodemon") j++; continue; }
            if (tail[j].StartsWith('-') || !SourcePath.IsMatch(tail[j])) continue;
            // Scripts exported as CLIs need an explicit server mode; a CLI may import unused server helpers.
            if (manifest.TryGetProperty("bin", out _) && !tail.Skip(j + 1).Any(a => a is "serve" or "server" or "web" or "dev")) return false;
            // An explicit non-server mode must not inherit an HTTP helper imported by a CLI dispatcher.
            var mode = tail.ElementAtOrDefault(j + 1);
            if (mode != null && !mode.StartsWith('-') && mode is not ("serve" or "server" or "web" or "dev" or "start" or "preview")) return false;
            var cli = tail[j].Replace('\\', '/');
            if (cli.Contains("/node_modules/", StringComparison.Ordinal))
            {
                var known = new[] { ("/vite/bin/vite.js", "vite"), ("/next/dist/bin/next", "next"), ("/wrangler/bin/wrangler.js", "wrangler"), ("/astro/astro.js", "astro") }
                    .FirstOrDefault(p => cli.EndsWith(p.Item1, StringComparison.Ordinal));
                return known.Item2 != null && IsWebSegment(directory, manifest, new[] { known.Item2 }.Concat(tail.Skip(j + 1)).ToList(), chain, depth + 1);
            }
            var path = ResolveSource(directory, tail[j]);
            var hasLibrary = false; var hasListener = false;
            return path != null && HasHttpEntrypoint(path, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, ref hasLibrary, ref hasListener);
        }
        return false;
    }

    private string? ReadSource(string path)
    {
        if (_sources.TryGetValue(path, out var cached)) return cached;
        if (_sources.Count >= 80 || !ProjectDiscovery.IsWithin(path, projectDirectory) || path.Split(Path.DirectorySeparatorChar).Any(p => p is "node_modules" or ".git")) return null;
        try
        {
            if (!File.Exists(path)) return _sources[path] = null;
            for (var current = path; current != null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return _sources[path] = null;
            using var stream = File.OpenRead(path);
            if (stream.Length > 512 * 1024) return _sources[path] = null;
            var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
            return _sources[path] = System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return _sources[path] = null; }
    }

    private string? ResolveSource(string directory, string value)
    {
        if (value.Contains(':') || value.Contains('$')) return null;
        var path = Path.GetFullPath(value, directory);
        var candidates = new List<string> { path };
        if (SourcePath.IsMatch(path))
        {
            var stem = Path.ChangeExtension(path, null);
            candidates.AddRange(new[] { ".ts", ".tsx", ".js", ".mjs", ".cjs" }.Select(ext => stem + ext));
        }
        else candidates.AddRange(new[] { ".ts", ".js", ".mjs", "/index.ts", "/index.js" }.Select(ext => path + ext));
        // A production start script can point at output that has not been built yet.
        candidates.AddRange(candidates.ToArray().Where(p => p.Contains(Path.DirectorySeparatorChar + "dist" + Path.DirectorySeparatorChar))
            .Select(p => p.Replace(Path.DirectorySeparatorChar + "dist" + Path.DirectorySeparatorChar, Path.DirectorySeparatorChar + "src" + Path.DirectorySeparatorChar)));
        return candidates.FirstOrDefault(p => ReadSource(p) != null);
    }

    private bool HasHttpEntrypoint(string path, HashSet<string> visited, int depth, ref bool hasLibrary, ref bool hasListener)
    {
        if (depth > 5 || visited.Count >= 40 || !visited.Add(path)) return false;
        var source = ReadSource(path);
        if (source == null) return false;
        source = Regex.Replace(source, @"/\*[\s\S]*?\*/|(?m:^\s*//.*$)", "");
        if (Regex.IsMatch(source, @"\b(?:Bun|Deno)\s*\.\s*serve\s*\(")) return true;
        var httpLibrary = Regex.IsMatch(source, "[\"'](?:node:)?https?[\"']|[\"'](?:express|fastify|koa|hapi|@hapi/hapi|@hono/node-server|elysia|polka)[\"']");
        hasLibrary |= httpLibrary;
        hasListener |= Regex.IsMatch(source, @"\.\s*(?:listen|start)\s*\(") ||
            source.Contains("@hono/node-server") && Regex.IsMatch(source, @"\bserve\s*\(");
        if (hasLibrary && hasListener) return true;
        // Follow relative imports and explicit wrapper entrypoint paths only, never an entire source tree.
        var references = Regex.Matches(source, "(?:from\\s*|import\\s*\\(?|require\\s*\\()[\"'](?<path>\\.[^\"']+)[\"']|[\"'](?<path>[^\"'\\r\\n ]+\\.[cm]?[jt]sx?)[\"']");
        foreach (Match reference in references)
        {
            var value = reference.Groups["path"].Value;
            var resolved = ResolveSource(Path.GetDirectoryName(path)!, value) ?? ResolveSource(projectDirectory, value);
            if (resolved != null && HasHttpEntrypoint(resolved, visited, depth + 1, ref hasLibrary, ref hasListener)) return true;
        }
        return false;
    }
}
