using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;
using JustWatch.Core.Models;

namespace JustWatch.Core.Services;

internal sealed record MuMuInstance(int Index, string Name, bool Started, bool AndroidReady, string? Serial, int? Pid)
{
    public string Id => $"mumu-{Index}";
}

/// <summary>Uses MuMu's supported CLI; never edits virtual machine files.</summary>
internal sealed class MuMuClient(AndroidConfig config)
{
    private string? _managerPath;
    public string ManagerPath => _managerPath ??= ResolveManagerPath(config.MuMuManagerPath);

    public async Task<IReadOnlyList<MuMuInstance>> ListAsync(CancellationToken ct) =>
        ParseInstances(await RunAsync(["info", "-v", "all"], ct));

    internal static IReadOnlyList<MuMuInstance> ParseInstances(string output)
    {
        using var doc = JsonDocument.Parse(output);
        CheckErrors(doc.RootElement);
        var result = new List<MuMuInstance>();
        var root = doc.RootElement;
        var entries = root.TryGetProperty("index", out _) ? new[] { root } :
            root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object).Select(p => p.Value);
        foreach (var item in entries)
        {
            if (!item.TryGetProperty("index", out var index) || !int.TryParse(index.ToString(), out var n)) continue;
            var started = item.TryGetProperty("is_process_started", out var s) && s.ValueKind == JsonValueKind.True;
            var ready = item.TryGetProperty("is_android_started", out var a) && a.ValueKind == JsonValueKind.True;
            var port = item.TryGetProperty("adb_port", out var p) && int.TryParse(p.ToString(), out var portValue) ? portValue : 0;
            var host = item.TryGetProperty("adb_host_ip", out var h) ? h.GetString() : null;
            // Only local MuMu devices are managed. Never connect to an arbitrary remote ADB host.
            var serial = port is > 0 and <= 65535 && (host is "127.0.0.1" or "localhost" or "::1")
                ? $"{(host == "::1" ? "[::1]" : "127.0.0.1")}:{port}" : null;
            var pid = item.TryGetProperty("pid", out var id) && int.TryParse(id.ToString(), out var pidValue) && pidValue > 0 ? (int?)pidValue : null;
            result.Add(new(n, item.GetProperty("name").GetString() ?? $"MuMu {n}", started, ready, serial, pid));
        }
        return result.OrderBy(i => i.Index).ToArray();
    }

    public async Task<Dictionary<string, string>> SettingsAsync(int index, CancellationToken ct)
    {
        var output = await RunAsync(["setting", "-v", index.ToString(), "-k", "performance_mode",
            "-k", "performance_cpu.custom", "-k", "performance_cpu.high", "-k", "performance_cpu.middle", "-k", "performance_cpu.low",
            "-k", "performance_mem.custom", "-k", "performance_mem.high", "-k", "performance_mem.middle", "-k", "performance_mem.low",
            "-k", "renderer_mode"], ct);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(output) ?? [];
    }

    public async Task<string> RunAsync(string[] args, CancellationToken ct, int timeoutSeconds = 30)
    {
        var output = await RunProcessAsync(ManagerPath, args, ct, timeoutSeconds);
        using var doc = JsonDocument.Parse(output);
        CheckErrors(doc.RootElement);
        return output;
    }

    internal static void CheckErrors(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        if ((value.TryGetProperty("error_code", out var code) || value.TryGetProperty("errcode", out code)) && code.ToString() != "0")
            throw new InvalidOperationException($"MuMu error {code}: {value}");
        foreach (var child in value.EnumerateObject())
            if (child.Value.ValueKind == JsonValueKind.Object) CheckErrors(child.Value);
    }

    internal static async Task<string> RunProcessAsync(string file, IEnumerable<string> args, CancellationToken ct, int timeoutSeconds = 20, bool failOnErrorOutput = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(file)} failed ({process.ExitCode}): {error} {output}");
            if (failOnErrorOutput && error.Contains("Error:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(error.Trim());
            return output.Trim();
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(file)} timed out after {timeoutSeconds}s.");
        }
    }

    internal static string ResolveManagerPath(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Environment.ExpandEnvironmentVariables(configured);
            if (File.Exists(path)) return Path.GetFullPath(path);
            throw new FileNotFoundException("Configured MuMuManager.exe was not found.", path);
        }
        var roots = new List<string>();
        foreach (var process in Process.GetProcessesByName("MuMuNxMain"))
        {
            using (process)
            try { if (process.MainModule?.FileName is string file) roots.Add(Path.GetDirectoryName(file)!); } catch { }
        }
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var keyPath in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var key = hive.OpenSubKey(keyPath);
            if (key == null) continue;
            foreach (var name in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(name);
                if (app?.GetValue("DisplayName") is string display && display.Contains("MuMu", StringComparison.OrdinalIgnoreCase)
                    && app.GetValue("InstallLocation") is string location) roots.Add(location);
            }
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Netease", "MuMuPlayer"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Netease", "MuMuPlayer-12.0"));
        foreach (var root in roots)
        foreach (var relative in new[] { "MuMuManager.exe", @"nx_main\MuMuManager.exe", @"shell\MuMuManager.exe" })
            if (File.Exists(Path.Combine(root, relative))) return Path.Combine(root, relative);
        throw new FileNotFoundException("MuMu Player was not found. Set android.muMuManagerPath to MuMuManager.exe.");
    }
}
