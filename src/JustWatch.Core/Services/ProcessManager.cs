using System.Diagnostics;
using System.Collections.Concurrent;
using JustWatch.Core.Models;

namespace JustWatch.Core.Services;

public class ProcessManager : IDisposable
{
    private readonly JustWatchConfig _config;
    private readonly ConcurrentDictionary<string, ProcessRunner> _runners = new();
    private readonly ConcurrentDictionary<string, FailureDetector> _detectors = new();
    private readonly ConcurrentDictionary<string, CircularBuffer<string>> _outputBuffers = new();
    private readonly AgentRecovery _agentRecovery;
    private readonly AndroidReloader _androidReloader;
    private readonly SemaphoreSlim _recoveryLock = new(1, 1);
    private int _androidReloading;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<string, long> _epochs = new();
    public event Action? ConfigurationChanged;
    private bool _disposed;

    public event Action<string, string>? OutputReceived;
    public event Action<string, ProcessStatus>? StatusChanged;

    public JustWatchConfig Config => _config;
    private string AndroidOutputId => _config.Processes.FirstOrDefault(p => p.ReloadAndroidOnRestart)?.Id ?? "__android__";

    public ProcessManager(JustWatchConfig config)
    {
        ServiceConfiguration.Validate(config.Processes);
        _config = config;
        _agentRecovery = new AgentRecovery(config.Agent);
        _agentRecovery.OutputReceived += text =>
            OutputReceived?.Invoke("__agent__", text);

        _androidReloader = new AndroidReloader(config.Android);
        _androidReloader.Log += msg =>
            OutputReceived?.Invoke(AndroidOutputId, $"\r\n\x1b[36m[JUSTWATCH] {msg}\x1b[0m\r\n");

        foreach (var pc in config.Processes) AddRunner(pc);
    }

    private void AddRunner(ProcessConfig pc)
    {
        var runtime = ServiceConfiguration.Clone([pc])[0];
        runtime.WorkingDirectory = string.IsNullOrWhiteSpace(pc.WorkingDirectory)
            ? _config.ProjectPath : Path.GetFullPath(pc.WorkingDirectory, _config.ProjectPath);
        var runner = new ProcessRunner(runtime);
        runner.OutputReceived += OnRunnerOutput;
        runner.ProcessExited += (id, code) => { if (_runners.TryGetValue(id, out var current) && ReferenceEquals(current, runner)) OnRunnerExited(id, code); };
        runner.RestartRequested += (id, pattern) => { if (_runners.TryGetValue(id, out var current) && ReferenceEquals(current, runner)) OnRunnerRestartRequested(id, pattern); };
        _runners[pc.Id] = runner;
        _detectors[pc.Id] = new FailureDetector(pc.CrashThreshold, pc.CrashWindowSeconds);
        _outputBuffers[pc.Id] = new CircularBuffer<string>(_config.Agent.OutputBufferLines);
        _epochs.AddOrUpdate(pc.Id, 1, (_, value) => value + 1);
    }

    public IReadOnlyList<ProcessConfig> GetProcessConfigs() => ServiceConfiguration.Clone(_config.Processes);
    public string ConfigurationRevision => ServiceConfiguration.Revision(_config.Processes);

    public async Task UpdateWatchFolderAsync(string path, bool remove)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Enter an absolute folder path, such as F:\\ or F:\\Projects.");
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        if (!remove && !Directory.Exists(path)) throw new ArgumentException("That folder is unavailable. Check the path and try again.");
        await _lifecycle.WaitAsync();
        try
        {
            var folders = _config.WatchFolders.ToList();
            folders.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (!remove) folders.Add(path);
            if (folders.Count > 30) throw new ArgumentException("You can watch up to 30 folders.");
            if (_config.ConfigPath == null) throw new InvalidOperationException("No configuration file is available to save.");
            ServiceConfiguration.SaveWatchFolders(_config.ConfigPath, folders, _config.WatchFolders);
            _config.WatchFolders = folders;
        }
        finally { _lifecycle.Release(); }
    }

    public IReadOnlyList<ServiceState> GetServiceStates(bool includeStopped = false, string? project = null) =>
        _config.Processes
            .Where(p => project == null || p.ProjectLabel.Equals(project.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(ToServiceState)
            .Where(s => includeStopped || ServiceVisibility.IsActive(s.Status))
            .ToArray();

    public ServiceState? GetServiceState(string id)
    {
        var pc = _config.Processes.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return pc == null ? null : ToServiceState(pc);
    }

    private ServiceState ToServiceState(ProcessConfig pc) => new(
        pc.Id, pc.Name, pc.ProjectLabel, pc.Description, pc.Group, pc.AutoStart,
        pc.ShowInTray, pc.ShowTrayDot, pc.TrayColor, GetStatus(pc.Id), GetPid(pc.Id),
        pc.Port, pc.EffectiveUrl, GetStartedAt(pc.Id), pc.HideFromTray);

    private static string RuntimeRevision(ProcessConfig pc)
    {
        var copy = ServiceConfiguration.Clone([pc])[0];
        copy.Name = copy.Description = copy.Project = copy.Group = copy.Url = "";
        copy.HideFromTray = copy.ShowTrayDot = copy.ShowInTray = copy.AutoStart = false;
        copy.TrayColor = "#000000";
        return ServiceConfiguration.Revision([copy]);
    }

    public async Task ApplyConfigurationAsync(List<ProcessConfig> services, string revision)
    {
        services = ServiceConfiguration.Clone(services);
        ServiceConfiguration.Validate(services);
        await _lifecycle.WaitAsync();
        try
        {
            if (revision != ConfigurationRevision) throw new InvalidOperationException("Configuration changed. Reload before saving.");
            if (_config.ConfigPath == null) throw new InvalidOperationException("No configuration file is available to save.");
            foreach (var pc in services)
                _ = Path.GetFullPath(string.IsNullOrWhiteSpace(pc.WorkingDirectory) ? _config.ProjectPath : pc.WorkingDirectory, _config.ProjectPath);
            ServiceConfiguration.CheckDiskRevision(_config.ConfigPath, revision);
            var old = _config.Processes;
            var affected = old.Where(pc =>
            {
                var replacement = services.FirstOrDefault(p => p.Id == pc.Id);
                return replacement == null || RuntimeRevision(pc) != RuntimeRevision(replacement);
            }).ToList();
            var restart = affected.Where(pc => GetStatus(pc.Id) is ProcessStatus.Running or ProcessStatus.Starting or ProcessStatus.RecoveringWithAgent)
                .Select(pc => pc.Id).ToHashSet();
            try
            {
                foreach (var pc in affected) await StopCoreAsync(pc.Id);
                ServiceConfiguration.Save(_config.ConfigPath, services, revision);
            }
            catch
            {
                // A failed stop or save must leave the old catalog on disk and in memory.
                foreach (var id in restart) StartCore(id);
                throw;
            }
            foreach (var pc in affected)
            {
                if (_runners.TryRemove(pc.Id, out var runner)) runner.Dispose();
                _detectors.TryRemove(pc.Id, out _);
                _outputBuffers.TryRemove(pc.Id, out _);
            }
            _config.Processes = services;
            foreach (var pc in services)
            {
                if (_runners.ContainsKey(pc.Id)) continue;
                AddRunner(pc);
                if (restart.Contains(pc.Id) || (!old.Any(p => p.Id == pc.Id) && pc.AutoStart)) StartCore(pc.Id);
            }
        }
        finally { _lifecycle.Release(); }
        ConfigurationChanged?.Invoke();
    }

    public ProcessStatus GetStatus(string id) =>
        _runners.TryGetValue(id, out var r) ? r.Status : ProcessStatus.Stopped;

    internal async Task DeleteProjectFilesAsync(string directory, string revision, Action validate, Action deleteFiles)
    {
        await _lifecycle.WaitAsync();
        var changed = false;
        try
        {
            validate();
            if (revision != ConfigurationRevision) throw new InvalidOperationException("Services changed. Review the deletion again.");
            if (_config.ConfigPath == null) throw new InvalidOperationException("No configuration file is available to save.");
            ServiceConfiguration.CheckDiskRevision(_config.ConfigPath, revision);
            var affected = _config.Processes.Where(pc => ProjectDiscovery.IsWithin(
                Path.GetFullPath(string.IsNullOrWhiteSpace(pc.WorkingDirectory) ? _config.ProjectPath : pc.WorkingDirectory, _config.ProjectPath), directory)).ToArray();
            foreach (var pc in affected) await StopCoreAsync(pc.Id);
            validate();
            // Persist removal before touching files so a restart cannot relaunch a deleted project.
            var remaining = _config.Processes.Where(p => !affected.Any(a => a.Id == p.Id)).ToList();
            ServiceConfiguration.Save(_config.ConfigPath, remaining, revision);
            _config.Processes = remaining;
            foreach (var pc in affected)
            {
                if (_runners.TryRemove(pc.Id, out var runner)) runner.Dispose();
                _detectors.TryRemove(pc.Id, out _);
                _outputBuffers.TryRemove(pc.Id, out _);
            }
            changed = true;
            await Task.Run(deleteFiles);
        }
        finally
        {
            _lifecycle.Release();
            if (changed) ConfigurationChanged?.Invoke();
        }
    }

    public int? GetPid(string id) =>
        _runners.TryGetValue(id, out var r) ? r.Pid : null;

    public DateTime? GetStartedAt(string id) =>
        _runners.TryGetValue(id, out var r) ? r.StartedAt : null;

    public IReadOnlyList<AndroidEmulatorInfo> GetAndroidEmulators() =>
        _androidReloader.GetEmulators();

    public Task<IReadOnlyList<AndroidEmulatorInfo>> GetAndroidEmulatorsAsync(CancellationToken ct = default) =>
        _androidReloader.GetEmulatorsAsync(ct);

    public Task<IReadOnlyList<string>> ListAndroidEmulatorsAsync(CancellationToken ct = default) =>
        _androidReloader.ListEmulatorsAsync(ct);

    public string GetBufferedOutput(string id) =>
        _outputBuffers.TryGetValue(id, out var buf)
            ? string.Join("", buf.ToArray())
            : string.Empty;

    public void Start(string id)
    {
        _lifecycle.Wait();
        try { StartCore(id); }
        finally { _lifecycle.Release(); }
    }

    private void StartCore(string id)
    {
        if (_disposed || !_runners.TryGetValue(id, out var runner)) return;
        _epochs.AddOrUpdate(id, 1, (_, value) => value + 1);
        runner.Start();
        StatusChanged?.Invoke(id, runner.Status);
    }

    public async Task StopAsync(string id)
    {
        await _lifecycle.WaitAsync();
        try { await StopCoreAsync(id); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync(string id)
    {
        _epochs.AddOrUpdate(id, 1, (_, value) => value + 1);
        if (!_runners.TryGetValue(id, out var runner)) return;
        await runner.StopAsync();
        StatusChanged?.Invoke(id, runner.Status);
    }

    public async Task RestartAsync(string id)
    {
        await _lifecycle.WaitAsync();
        try
        {
            await StopCoreAsync(id);
            await Task.Delay(500);
            StartCore(id);
            MaybeReloadAndroid(id);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task RestartIfCurrentAsync(string id, long epoch)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed || _epochs.GetValueOrDefault(id) != epoch) return;
            StartCore(id);
            MaybeReloadAndroid(id);
        }
        finally { _lifecycle.Release(); }
    }

    public Task LaunchAndroidEmulatorAsync(string id, CancellationToken ct = default) =>
        _androidReloader.LaunchEmulatorAsync(id, ct);

    public Task<IReadOnlyList<AndroidEmulatorInfo>> StartAllAndroidEmulatorsAsync(CancellationToken ct = default) =>
        _androidReloader.StartAllEmulatorsAsync(ct);

    public Task<IReadOnlyList<AndroidEmulatorInfo>> RestartAllAndroidEmulatorsAsync(CancellationToken ct = default) =>
        _androidReloader.RestartAllEmulatorsAsync(ct);

    public Task<AndroidEmulatorInfo> CloneAndroidEmulatorAsync(CancellationToken ct = default) =>
        _androidReloader.CloneCoreEmulatorAsync(ct);

    public Task<AndroidDashboard> GetAndroidDashboardAsync(CancellationToken ct = default) =>
        _androidReloader.GetDashboardAsync(ct);

    public AndroidEmulatorConfig GetAndroidEmulatorConfig(string id) =>
        _androidReloader.GetEmulatorConfig(id);

    public AndroidEmulatorConfig UpdateAndroidEmulatorConfig(string id, int? ramMb, int? cores, string? gpuMode) =>
        _androidReloader.UpdateEmulatorConfig(id, ramMb, cores, gpuMode);

    public Task DeleteAndroidEmulatorAsync(string id, CancellationToken ct = default) =>
        _androidReloader.DeleteEmulatorAsync(id, ct);

    public Task StopAndroidEmulatorAsync(string id, CancellationToken ct = default) =>
        _androidReloader.StopEmulatorAsync(id, ct);

    public Task<IReadOnlyList<string>> RestartJustGainsAppAsync(CancellationToken ct = default) =>
        _androidReloader.RestartAppAsync(ct);

    public Task<IReadOnlyList<string>> RestartJustGainsAppAsync(string id, CancellationToken ct = default) =>
        _androidReloader.RestartAppAsync(id, ct);

    public Task InstallJustGainsAppOnAllAndroidEmulatorsAsync(CancellationToken ct = default) =>
        RunLoggedCommandAsync(
            "bun",
            ["run", "mobile:android:all"],
            _config.ProjectPath,
            AndroidOutputId,
            "build and install JustGains on all Android emulators",
            ct);

    public async Task ScorchedAndroidUpdateAsync(CancellationToken ct = default)
    {
        OutputReceived?.Invoke(AndroidOutputId,
            "\r\n\x1b[36m[JUSTWATCH] Scorched Update: starting all Android emulators...\x1b[0m\r\n");

        await _androidReloader.StartAllEmulatorsAsync(ct);
        await _androidReloader.WaitForAllManagedEmulatorsAsync(TimeSpan.FromMinutes(4), ct);

        OutputReceived?.Invoke(AndroidOutputId,
            "\r\n\x1b[36m[JUSTWATCH] Scorched Update: rebuilding and installing JustGains on all Android emulators...\x1b[0m\r\n");

        await InstallJustGainsAppOnAllAndroidEmulatorsAsync(ct);

        OutputReceived?.Invoke(AndroidOutputId,
            "\r\n\x1b[32m[JUSTWATCH] Scorched Update complete.\x1b[0m\r\n");
    }

    /// <summary>
    /// After the mobile dev server restarts, reload the JustGains app on any running
    /// emulator so it reconnects to the fresh Metro bundler. Fire-and-forget; guarded
    /// so overlapping restarts don't stack reloads.
    /// </summary>
    private void MaybeReloadAndroid(string id)
    {
        if (!_config.Android.Enabled || !_config.Processes.Any(p => p.Id == id && p.ReloadAndroidOnRestart)) return;
        if (Interlocked.CompareExchange(ref _androidReloading, 1, 0) != 0) return;

        var pc = _config.Processes.FirstOrDefault(p => p.Id == id);
        var metroPort = pc?.Port ?? 8081;

        _ = Task.Run(async () =>
        {
            try { await _androidReloader.ReloadAsync(metroPort, CancellationToken.None); }
            finally { Interlocked.Exchange(ref _androidReloading, 0); }
        });
    }

    public void StartAll()
    {
        foreach (var pc in _config.Processes.Where(p => p.AutoStart))
            Start(pc.Id);
    }

    private async Task RunLoggedCommandAsync(
        string command,
        string[] args,
        string workingDirectory,
        string outputChannel,
        string label,
        CancellationToken ct)
    {
        var printable = $"{command} {string.Join(" ", args)}";
        OutputReceived?.Invoke(outputChannel, $"\r\n\x1b[36m[JUSTWATCH] Running {label}: {printable}\x1b[0m\r\n");

        var psi = new ProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        using var outputDrain = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stdoutTask = PipeProcessOutputAsync(proc.StandardOutput, outputChannel, outputDrain.Token);
        var stderrTask = PipeProcessOutputAsync(proc.StandardError, outputChannel, outputDrain.Token);

        try { await proc.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        // Native Windows build tools can leave descendants holding inherited
        // pipes after the command has exited. Bound the final drain, not the build.
        outputDrain.CancelAfter(TimeSpan.FromSeconds(2));
        try { await Task.WhenAll(stdoutTask, stderrTask); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{label} failed with exit code {proc.ExitCode}.");

        OutputReceived?.Invoke(outputChannel, $"\r\n\x1b[32m[JUSTWATCH] Finished {label}.\x1b[0m\r\n");
    }

    private async Task PipeProcessOutputAsync(StreamReader reader, string outputChannel, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) return;
            OutputReceived?.Invoke(outputChannel, line + "\r\n");
        }
    }

    public async Task StopAllAsync()
    {
        var tasks = _runners.Values
            .Where(r => r.Status is ProcessStatus.Running or ProcessStatus.Starting
                or ProcessStatus.Crashed or ProcessStatus.RecoveringWithAgent)
            .Select(r => StopAsync(r.Id));
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Restarts the currently active services: captures which processes are running,
    /// stops them, then starts those same ones back up. If nothing is running, falls
    /// back to starting the configured auto-start set.
    /// </summary>
    public async Task RestartAllAsync()
    {
        var runningIds = _runners.Values
            .Where(r => r.Status is ProcessStatus.Running or ProcessStatus.Starting
                or ProcessStatus.Crashed or ProcessStatus.RecoveringWithAgent)
            .Select(r => r.Id)
            .ToList();

        if (runningIds.Count == 0)
        {
            StartAll();
            return;
        }

        await Task.WhenAll(runningIds.Select(StopAsync));
        await Task.Delay(500);

        foreach (var id in runningIds)
            Start(id);

        foreach (var id in runningIds) MaybeReloadAndroid(id);
    }

    public void WriteInput(string id, string data)
    {
        if (_runners.TryGetValue(id, out var runner))
            runner.WriteInput(data);
    }

    private void OnRunnerOutput(string id, string data)
    {
        if (_outputBuffers.TryGetValue(id, out var buf))
            buf.Add(data);
        OutputReceived?.Invoke(id, data);
    }

    private async void OnRunnerRestartRequested(string id, string pattern)
    {
        var epoch = _epochs.GetValueOrDefault(id);
        try
        {
            await _lifecycle.WaitAsync();
            try
            {
                if (_disposed || _epochs.GetValueOrDefault(id) != epoch) return;
                await StopCoreAsync(id);
                epoch = _epochs.GetValueOrDefault(id);
            }
            finally { _lifecycle.Release(); }
            await Task.Delay(2000);
            await RestartIfCurrentAsync(id, epoch);
        }
        catch (Exception ex) { OutputReceived?.Invoke(id, $"[JUSTWATCH] Restart failed: {ex.Message}\r\n"); }
    }

    private async void OnRunnerExited(string id, int exitCode)
    {
        if (!_runners.TryGetValue(id, out var runner)) return;

        try
        {
            var epoch = _epochs.GetValueOrDefault(id);
            var status = runner.Status;
            StatusChanged?.Invoke(id, status);

            if (status != ProcessStatus.Crashed) return;

            if (!_detectors.TryGetValue(id, out var detector)) return;
            var thresholdReached = detector.RecordCrash();
            var pc = _config.Processes.FirstOrDefault(p => p.Id == id);
            if (pc == null || !pc.AutoRestart || _disposed) return;

            if (thresholdReached && pc.AgentRecovery)
            {
                await RunAgentRecovery(id, pc, detector, epoch);
            }
            else
            {
                var delay = Math.Min(2000 * detector.RecentCrashCount, 15000);
                OutputReceived?.Invoke(id,
                    $"\r\n\x1b[36m[JUSTWATCH] Restarting {pc.Name} in {delay / 1000}s...\x1b[0m\r\n");
                await Task.Delay(delay);
                await RestartIfCurrentAsync(id, epoch);
            }
        }
        catch (Exception ex) { OutputReceived?.Invoke(id, $"[JUSTWATCH] Recovery failed: {ex.Message}\r\n"); }
    }

    private async Task RunAgentRecovery(string id, ProcessConfig pc, FailureDetector detector, long epoch)
    {
        if (!await _recoveryLock.WaitAsync(0)) return;

        try
        {
            StatusChanged?.Invoke(id, ProcessStatus.RecoveringWithAgent);
            OutputReceived?.Invoke(id,
                $"\r\n\x1b[35m[JUSTWATCH] Crash threshold reached for {pc.Name}. Running agent recovery...\x1b[0m\r\n");

            if (_disposed || _epochs.GetValueOrDefault(id) != epoch || !_outputBuffers.TryGetValue(id, out var output) || !_runners.TryGetValue(id, out var runner)) return;
            var recentOutput = output.ToArray();
            var success = await _agentRecovery.AttemptRecoveryAsync(
                id, recentOutput, runner.Config.WorkingDirectory, CancellationToken.None);

            detector.Reset();

            OutputReceived?.Invoke(id, success
                ? "\r\n\x1b[32m[JUSTWATCH] Agent recovery completed. Restarting...\x1b[0m\r\n"
                : "\r\n\x1b[33m[JUSTWATCH] Agent recovery finished. Restarting...\x1b[0m\r\n");

            await Task.Delay(2000);
            await RestartIfCurrentAsync(id, epoch);
        }
        finally
        {
            _recoveryLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var runner in _runners.Values)
            runner.Dispose();
        // Delayed callbacks check _disposed before restarting. Do not dispose their gates.
    }
}
