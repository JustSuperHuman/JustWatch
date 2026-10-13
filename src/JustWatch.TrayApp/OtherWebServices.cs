using System.Diagnostics;
using System.IO;
using System.Text.Json;
using JustWatch.Core.Services;

namespace JustWatch.TrayApp;

public sealed record OtherWebService
{
    public int Port { get; init; }
    public int Pid { get; init; }
    public ulong StartTime { get; init; }
    public string Url { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string? ProjectRoot { get; init; }
    public string? Cwd { get; init; }
    public string Framework { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public OtherLaunchRecipe? Launch { get; init; }

    public string Key => OtherWebServices.KeyFor(this);
}

public sealed record OtherLaunchRecipe
{
    public string Label { get; init; } = "";
    public string Program { get; init; } = "";
    public string[] Args { get; init; } = [];
    public string Cwd { get; init; } = "";
}

public sealed record OtherWebServiceRow(OtherWebService Service, bool IsPinned, bool IsRunning, string? PinKey = null);

public sealed class OtherWebServices
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _pinsPath;
    private readonly string _justPortsExecutable;
    private readonly TimeSpan _startupTimeout;
    private readonly Dictionary<string, OtherWebService> _pins = new(StringComparer.OrdinalIgnoreCase);

    public OtherWebServices(string? pinsPath = null, string justPortsExecutable = "justports.exe",
        TimeSpan? startupTimeout = null)
    {
        _pinsPath = pinsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JustWatch", "other-services.json");
        _justPortsExecutable = justPortsExecutable;
        _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(25);
        LoadPins();
    }

    public static string KeyFor(OtherWebService service)
    {
        var owner = service.ProjectRoot ?? service.Cwd ?? service.ProcessName;
        return $"{owner.Replace('\\', '/').TrimEnd('/').ToLowerInvariant()}|{service.Port}";
    }

    public static IReadOnlyList<OtherWebService> ParseSnapshot(string json) =>
        (JsonSerializer.Deserialize<List<OtherWebService>>(json, JsonOptions) ?? [])
            .Where(IsValid).ToArray();

    public IReadOnlyList<OtherWebServiceRow> Merge(
        IReadOnlyList<OtherWebService> live, IReadOnlySet<int> managedPorts, int selfPid)
    {
        var available = live.Where(service => !managedPorts.Contains(service.Port) && service.Pid != selfPid)
            .GroupBy(service => service.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(service => service.Key, StringComparer.OrdinalIgnoreCase);
        var rows = new List<OtherWebServiceRow>();
        var unmatched = new List<OtherWebService>();
        foreach (var pin in _pins.Values.ToArray())
        {
            if (managedPorts.Contains(pin.Port)) continue;
            if (available.Remove(pin.Key, out var active))
                rows.Add(new OtherWebServiceRow(active with { Launch = active.Launch ?? pin.Launch }, true, true, pin.Key));
            else unmatched.Add(pin);
        }

        // A development server may select a different port on its next launch.
        // Only follow it when both the saved pin and the live listener identify a
        // unique process in the same project. Never guess between sibling ports.
        foreach (var pin in unmatched)
        {
            var candidates = available.Values.Where(service => SameIdentity(pin, service)).ToArray();
            if (candidates.Length == 1 && unmatched.Count(other => SameIdentity(other, candidates[0])) == 1)
            {
                var active = candidates[0];
                available.Remove(active.Key);
                var pinKey = RebindPin(pin, active);
                rows.Add(new OtherWebServiceRow(active with { Launch = active.Launch ?? pin.Launch }, true, true, pinKey));
            }
            else rows.Add(new OtherWebServiceRow(pin, true, false, pin.Key));
        }
        rows.AddRange(available.Values.Select(service => new OtherWebServiceRow(service, false, true)));
        return rows.OrderByDescending(row => row.IsPinned)
            .ThenBy(row => row.Service.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Service.Port).ToArray();
    }

    public void Pin(OtherWebService service)
    {
        if (!IsValid(service)) throw new ArgumentException("This listener has no valid local web address.");
        if (!_pins.ContainsKey(service.Key) && _pins.Count >= 40)
            throw new InvalidOperationException("You can pin up to 40 other services.");
        _pins.TryGetValue(service.Key, out var previous);
        _pins[service.Key] = service with { Pid = 0, StartTime = 0 };
        try { SavePins(); }
        catch
        {
            if (previous == null) _pins.Remove(service.Key);
            else _pins[service.Key] = previous;
            throw;
        }
    }

    public void Unpin(OtherWebService service)
    {
        Unpin(service.Key);
    }

    public void Unpin(string key)
    {
        if (!_pins.Remove(key, out var previous)) return;
        try { SavePins(); }
        catch { _pins[key] = previous; throw; }
    }

    private string RebindPin(OtherWebService previous, OtherWebService active)
    {
        if (string.Equals(previous.Key, active.Key, StringComparison.OrdinalIgnoreCase)
            || _pins.ContainsKey(active.Key)) return previous.Key;
        _pins.Remove(previous.Key);
        _pins[active.Key] = active with { Pid = 0, StartTime = 0, Launch = active.Launch ?? previous.Launch };
        try { SavePins(); return active.Key; }
        catch (Exception ex)
        {
            _pins.Remove(active.Key);
            _pins[previous.Key] = previous;
            Debug.WriteLine($"[OtherWebServices] Could not update changed port: {ex.Message}");
            return previous.Key;
        }
    }

    private static bool SameIdentity(OtherWebService a, OtherWebService b)
    {
        var aRoot = a.ProjectRoot ?? a.Cwd;
        var bRoot = b.ProjectRoot ?? b.Cwd;
        return !string.IsNullOrWhiteSpace(aRoot) && !string.IsNullOrWhiteSpace(bRoot)
            && !string.IsNullOrWhiteSpace(a.ProcessName)
            && string.Equals(NormalizePath(aRoot), NormalizePath(bRoot), StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimEnd('/');

    public async Task<IReadOnlyList<OtherWebService>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var output = await RunJustPortsAsync(["--json"], cancellationToken);
        return ParseSnapshot(output);
    }

    public async Task StopAsync(OtherWebService service, CancellationToken cancellationToken = default)
    {
        var current = FindCurrent(service, await ScanAsync(cancellationToken))
            ?? throw new InvalidOperationException("This listener is no longer running. Refresh the list.");
        await StopCurrentAsync(current, cancellationToken);
    }

    public async Task StartAsync(OtherWebService service)
    {
        var launch = RequireLaunch(service);
        var existing = FindCurrent(service, await ScanAsync());
        if (existing != null)
            throw new InvalidOperationException($"Already running on port {existing.Port}. Refresh the list.");
        if ((await Task.Run(() => PortGuard.FindListeners(service.Port))).Count > 0)
            throw new InvalidOperationException($"Port {service.Port} is already in use. Refresh the list first.");

        await StartAndVerifyAsync(service, launch);
    }

    private static OtherLaunchRecipe RequireLaunch(OtherWebService service)
    {
        var launch = service.Launch;
        if (launch == null || string.IsNullOrWhiteSpace(launch.Program)
            || string.IsNullOrWhiteSpace(launch.Cwd) || !Directory.Exists(launch.Cwd))
            throw new InvalidOperationException("No usable start command was detected for this service.");
        return launch;
    }

    private async Task StartAndVerifyAsync(OtherWebService service, OtherLaunchRecipe launch)
    {
        var info = new ProcessStartInfo(ResolveLaunchProgram(launch.Program))
        {
            WorkingDirectory = launch.Cwd,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var arg in launch.Args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start {launch.Label}.");
        var deadline = DateTimeOffset.UtcNow + _startupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var active = FindCurrent(service, await ScanAsync());
            if (active != null) return;
            if (process.HasExited && process.ExitCode != 0)
                throw new InvalidOperationException($"{launch.Label} exited with code {process.ExitCode} before a listener appeared.");
            await Task.Delay(350);
        }
        throw new TimeoutException($"{launch.Label} started, but no matching web listener appeared within {_startupTimeout.TotalSeconds:0} seconds.");
    }

    private static string ResolveLaunchProgram(string program)
    {
        if (!OperatingSystem.IsWindows() || Path.HasExtension(program)
            || program.Contains('\\') || program.Contains('/')) return program;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
            {
                var candidate = Path.Combine(directory.Trim(' ', '"'), program + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return program;
    }

    public async Task RestartAsync(OtherWebService service)
    {
        var launch = RequireLaunch(service);
        var current = FindCurrent(service, await ScanAsync())
            ?? throw new InvalidOperationException("This listener is no longer running. Refresh the list.");
        await StopCurrentAsync(current, CancellationToken.None);
        await StartAndVerifyAsync(current, launch);
    }

    private async Task StopCurrentAsync(OtherWebService current, CancellationToken cancellationToken)
    {
        if (current.Pid <= 4 || current.StartTime == 0)
            throw new InvalidOperationException("This listener has no safe process identity to stop.");
        await RunJustPortsAsync(["--stop", current.Port.ToString(), current.Pid.ToString(),
            current.StartTime.ToString()], cancellationToken);
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var stillOwned = (await Task.Run(() => PortGuard.FindListeners(current.Port), cancellationToken))
                .Any(holder => holder.Pid == current.Pid);
            if (!stillOwned) break;
            if (attempt == 39)
                throw new TimeoutException($"Port {current.Port} is still held by PID {current.Pid}.");
            await Task.Delay(100, cancellationToken);
        }
        // Give any parent supervisor a moment to reveal an automatic restart.
        await Task.Delay(400, cancellationToken);
        var replacement = FindCurrent(current, await ScanAsync(cancellationToken));
        if (replacement != null)
            throw new InvalidOperationException($"The service restarted automatically on port {replacement.Port}; it is still running.");
    }

    private static OtherWebService? FindCurrent(OtherWebService service, IReadOnlyList<OtherWebService> live)
    {
        if (service.Pid > 4 && service.StartTime > 0)
        {
            var exact = live.FirstOrDefault(candidate => candidate.Port == service.Port
                && candidate.Pid == service.Pid && candidate.StartTime == service.StartTime);
            if (exact != null) return exact;
        }
        var onPort = live.Where(candidate => candidate.Key == service.Key
            && (string.IsNullOrWhiteSpace(service.ProcessName)
                || string.Equals(candidate.ProcessName, service.ProcessName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (onPort.Length == 1) return onPort[0];
        var moved = live.Where(candidate => SameIdentity(service, candidate)).ToArray();
        return moved.Length == 1 ? moved[0] : null;
    }

    private async Task<string> RunJustPortsAsync(string[] args, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var info = new ProcessStartInfo(_justPortsExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Could not start JustPorts.");
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? "JustPorts could not complete the action."
                    : error.Trim());
            return output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("JustPorts took too long to respond.");
        }
    }

    private void LoadPins()
    {
        try
        {
            if (!File.Exists(_pinsPath)) return;
            var pins = JsonSerializer.Deserialize<List<OtherWebService>>(
                File.ReadAllText(_pinsPath), JsonOptions) ?? [];
            foreach (var pin in pins.Where(IsValid).Take(40))
                _pins[pin.Key] = pin with { Pid = 0, StartTime = 0 };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OtherWebServices] Could not load pins: {ex.Message}");
        }
    }

    private void SavePins()
    {
        var directory = Path.GetDirectoryName(_pinsPath)!;
        Directory.CreateDirectory(directory);
        var temp = _pinsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(_pins.Values.ToArray(), JsonOptions));
            File.Move(temp, _pinsPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static bool IsValid(OtherWebService service) =>
        service.Port is > 0 and <= 65535
        && Uri.TryCreate(service.Url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && uri.Port == service.Port;
}
