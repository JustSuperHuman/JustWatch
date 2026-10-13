using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using JustWatch.Core.Models;

namespace JustWatch.Core.Services;

/// <summary>MuMu instance lifecycle, ADB connectivity, tuning, and JustGains app recovery.</summary>
public sealed partial class AndroidReloader
{
    private readonly AndroidConfig _config;
    private readonly MuMuClient _mumu;
    private readonly SemaphoreSlim _mutations = new(1, 1);
    private readonly SemaphoreSlim _connections = new(1, 1);
    private readonly ConcurrentDictionary<string, EmulatorCommandActivity> _activity = new(StringComparer.OrdinalIgnoreCase);
    public event Action<string>? Log;

    public AndroidReloader(AndroidConfig config) { _config = config; _mumu = new(config); }
    public string ManagerPath => _mumu.ManagerPath;

    private bool IsManaged(MuMuInstance e) => e.Index == _config.CoreInstanceIndex ||
        Regex.IsMatch(e.Name, $"^{Regex.Escape(_config.EmulatorNamePrefix)}-\\d+$", RegexOptions.IgnoreCase);

    private async Task<List<MuMuInstance>> DiscoverAsync(CancellationToken ct) =>
        (await _mumu.ListAsync(ct)).Where(IsManaged).ToList();

    private async Task<MuMuInstance> ResolveAsync(string id, CancellationToken ct)
    {
        var all = await DiscoverAsync(ct);
        var matches = all.Where(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
            e.Name.Equals(id, StringComparison.OrdinalIgnoreCase) || e.Index.ToString() == id ||
            $"JustGains-{e.Index + 1}".Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1) throw new InvalidOperationException($"Unknown or ambiguous MuMu instance '{id}'. Use its mumu-N id.");
        return matches[0];
    }

    // Synchronous callers are legacy UI/API entry points; run off their synchronization context.
    public IReadOnlyList<AndroidEmulatorInfo> GetEmulators() => Task.Run(async () =>
        (IReadOnlyList<AndroidEmulatorInfo>)(await DiscoverAsync(CancellationToken.None)).Select(e => ToInfo(e)).ToArray()).GetAwaiter().GetResult();

    public async Task<IReadOnlyList<AndroidEmulatorInfo>> GetEmulatorsAsync(CancellationToken ct)
    {
        var instances = await DiscoverAsync(ct);
        var running = await ConnectAsync(instances, ct);
        var idle = await GetDeviceIdleBySerialAsync(running, ct);
        return instances.Select(e =>
        {
            var runtime = running.FirstOrDefault(r => r.AvdName == e.Id);
            var info = ToInfo(e, runtime?.Serial);
            if (runtime != null && idle.TryGetValue(runtime.Serial, out var value))
            { info.IdleTime = value.IdleTime; info.IdleSeconds = value.IdleSeconds; }
            return info;
        }).ToArray();
    }

    private async Task<List<RunningEmulator>> ConnectAsync(IEnumerable<MuMuInstance> instances, CancellationToken ct)
    {
        await _connections.WaitAsync(ct);
        try
        {
            var candidates = instances.Where(e => e.Started && e.AndroidReady && e.Serial != null).ToArray();
            if (candidates.Length == 0) return [];
            var attached = ParseDevices(await RunAdbAsync(null, ["devices"], ct));
            foreach (var instance in candidates.Where(e => !attached.Contains(e.Serial!)))
            {
                // An offline TCP transport can survive a MuMu restart. Reconnect just
                // this endpoint, never kill the shared ADB server or other devices.
                await DisconnectAsync(instance.Serial!, ct);
                var result = await RunAdbAsync(null, ["connect", instance.Serial!], ct);
                if (result.Contains("cannot connect", StringComparison.OrdinalIgnoreCase) || result.Contains("failed", StringComparison.OrdinalIgnoreCase))
                    Log?.Invoke($"MuMu {instance.Name}: {result}");
            }
            attached = ParseDevices(await RunAdbAsync(null, ["devices"], ct));
            return candidates.Where(e => attached.Contains(e.Serial!)).Select(e => new RunningEmulator(e.Serial!, e.Id)).ToList();
        }
        finally { _connections.Release(); }
    }

    internal static HashSet<string> ParseDevices(string output) => output.Split('\n')
        .Select(line => line.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries))
        .Where(parts => parts.Length >= 2 && parts[1] == "device").Select(parts => parts[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<RunningEmulator>> ListRunningEmulatorsAsync(CancellationToken ct) =>
        await ConnectAsync(await DiscoverAsync(ct), ct);

    public async Task<IReadOnlyList<string>> ListEmulatorsAsync(CancellationToken ct) =>
        (await ListRunningEmulatorsAsync(ct)).Select(e => e.Serial).ToArray();

    public async Task LaunchEmulatorAsync(string id, CancellationToken ct)
    {
        await _mutations.WaitAsync(ct);
        try
        {
            var instance = await ResolveAsync(id, ct);
            if (!instance.Started) await ControlAsync(instance, "launch", ct);
            await WaitForReadyAsync([instance.Id], TimeSpan.FromMinutes(4), ct);
        }
        finally { _mutations.Release(); }
    }

    public async Task<IReadOnlyList<AndroidEmulatorInfo>> StartAllEmulatorsAsync(CancellationToken ct)
    {
        await _mutations.WaitAsync(ct);
        try
        {
            var all = await DiscoverAsync(ct);
            if (all.Count == 0) throw new InvalidOperationException("No managed MuMu instances found.");
            foreach (var e in all.Where(e => !e.Started)) await ControlAsync(e, "launch", ct);
            return await WaitForReadyAsync(all.Select(e => e.Id).ToArray(), TimeSpan.FromMinutes(4), ct);
        }
        finally { _mutations.Release(); }
    }

    public async Task<IReadOnlyList<AndroidEmulatorInfo>> WaitForAllManagedEmulatorsAsync(TimeSpan timeout, CancellationToken ct)
    {
        var all = await DiscoverAsync(ct);
        if (all.Count == 0) throw new InvalidOperationException("No managed MuMu instances found.");
        return await WaitForReadyAsync(all.Select(e => e.Id).ToArray(), timeout, ct);
    }

    private async Task<IReadOnlyList<AndroidEmulatorInfo>> WaitForReadyAsync(string[] ids, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var all = (await DiscoverAsync(deadline.Token)).Where(e => ids.Contains(e.Id)).ToArray();
                var connected = await ConnectAsync(all, deadline.Token);
                var ready = new List<AndroidEmulatorInfo>();
                foreach (var runtime in connected)
                {
                    if ((await RunAdbAsync(runtime.Serial, ["shell", "getprop", "sys.boot_completed"], deadline.Token)).Trim() == "1")
                    {
                        var port = _config.MetroPort > 0 ? _config.MetroPort : 8081;
                        await EnsureDevTunnelsAsync(runtime.AvdName, runtime.Serial, port, deadline.Token);
                        ready.Add(ToInfo(all.Single(e => e.Id == runtime.AvdName), runtime.Serial));
                    }
                }
                if (ready.Count == ids.Length) return ready;
                await Task.Delay(1000, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException($"MuMu did not finish booting and connecting within {timeout.TotalSeconds:0}s: {string.Join(", ", ids)}."); }
    }

    private async Task ControlAsync(MuMuInstance instance, string action, CancellationToken ct)
    {
        RecordActivity(instance.Id, $"MuMuManager control -v {instance.Index} {action}");
        Log?.Invoke($"MuMu {action}: {instance.Name}.");
        await _mumu.RunAsync(["control", "-v", instance.Index.ToString(), action], ct);
    }

    private async Task StopAsync(MuMuInstance instance, CancellationToken ct)
    {
        if (instance.Started) await ControlAsync(instance, "shutdown", ct);
        var until = DateTime.UtcNow.AddSeconds(60);
        while ((await DiscoverAsync(ct)).Any(e => e.Id == instance.Id && e.Started))
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException($"MuMu {instance.Name} did not shut down.");
            await Task.Delay(500, ct);
        }
        if (instance.Serial != null) await DisconnectAsync(instance.Serial, ct);
    }

    public async Task StopEmulatorAsync(string id, CancellationToken ct)
    {
        await _mutations.WaitAsync(ct);
        try { await StopAsync(await ResolveAsync(id, ct), ct); }
        finally { _mutations.Release(); }
    }

    public async Task<IReadOnlyList<AndroidEmulatorInfo>> RestartAllEmulatorsAsync(CancellationToken ct)
    {
        await _mutations.WaitAsync(ct);
        try
        {
            var running = (await DiscoverAsync(ct)).Where(e => e.Started).ToArray();
            foreach (var e in running) await StopAsync(e, ct);
            foreach (var e in running) await ControlAsync(e, "launch", ct);
            return await WaitForReadyAsync(running.Select(e => e.Id).ToArray(), TimeSpan.FromMinutes(4), ct);
        }
        finally { _mutations.Release(); }
    }

    public async Task<AndroidEmulatorInfo> CloneCoreEmulatorAsync(CancellationToken ct)
    {
        await _mutations.WaitAsync(ct);
        try
        {
            var before = await _mumu.ListAsync(ct);
            var core = before.SingleOrDefault(e => e.Index == _config.CoreInstanceIndex)
                ?? throw new InvalidOperationException("The configured core MuMu instance does not exist.");
            string output;
            try
            {
                if (core.Started) await StopAsync(core, ct);
                output = await _mumu.RunAsync(["clone", "-v", core.Index.ToString(), "-n", "1"], ct, 300);
                var created = (await _mumu.ListAsync(ct)).Where(e => before.All(b => b.Index != e.Index)).ToArray();
                if (created.Length != 1) throw new InvalidOperationException($"Could not identify the new MuMu clone: {output}");
                var clone = created[0];
                var number = 1;
                while (before.Any(e => e.Name.Equals($"{_config.EmulatorNamePrefix}-{number}", StringComparison.OrdinalIgnoreCase))) number++;
                var name = $"{_config.EmulatorNamePrefix}-{number}";
                await _mumu.RunAsync(["rename", "-v", clone.Index.ToString(), "-n", name], ct);
                RecordActivity(clone.Id, $"clone {core.Id}");
                return ToInfo(clone with { Name = name });
            }
            finally
            {
                // Restore even when the caller cancels or cloning fails.
                if (core.Started)
                {
                    await ControlAsync(core, "launch", CancellationToken.None);
                    await WaitForReadyAsync([core.Id], TimeSpan.FromMinutes(4), CancellationToken.None);
                }
            }

        }
        finally { _mutations.Release(); }
    }

    public async Task DeleteEmulatorAsync(string id, CancellationToken ct)
    {
        await _mutations.WaitAsync(ct);
        try
        {
            var instance = await ResolveAsync(id, ct);
            if (instance.Index == _config.CoreInstanceIndex) throw new InvalidOperationException("The core MuMu instance is protected and cannot be deleted.");
            await StopAsync(instance, ct);
            await _mumu.RunAsync(["delete", "-v", instance.Index.ToString()], ct, 120);
            var until = DateTime.UtcNow.AddSeconds(60);
            while ((await _mumu.ListAsync(ct)).Any(e => e.Index == instance.Index))
            {
                if (DateTime.UtcNow >= until) throw new TimeoutException("MuMu did not finish deleting the instance.");
                await Task.Delay(500, ct);
            }
            _activity.TryRemove(instance.Id, out _);
        }
        finally { _mutations.Release(); }
    }

    public AndroidEmulatorConfig GetEmulatorConfig(string id) => Task.Run(async () =>
        await ReadConfigAsync(await ResolveAsync(id, CancellationToken.None), CancellationToken.None)).GetAwaiter().GetResult();

    internal static AndroidEmulatorConfig ParseConfig(MuMuInstance e, IReadOnlyDictionary<string, string> settings)
    {
        var mode = settings.GetValueOrDefault("performance_mode", "middle");
        double.TryParse(settings.GetValueOrDefault($"performance_mem.{mode}"), CultureInfo.InvariantCulture, out var gb);
        int.TryParse(settings.GetValueOrDefault($"performance_cpu.{mode}"), out var cores);
        return new() { Id = e.Id, AvdName = e.Id, Name = e.Name, RamSizeMb = (int)Math.Round(gb * 1024),
            Cores = cores, GpuMode = settings.GetValueOrDefault("renderer_mode", "vk") };
    }

    private async Task<AndroidEmulatorConfig> ReadConfigAsync(MuMuInstance e, CancellationToken ct) =>
        ParseConfig(e, await _mumu.SettingsAsync(e.Index, ct));

    public AndroidEmulatorConfig UpdateEmulatorConfig(string id, int? ramMb, int? cores, string? gpuMode) =>
        Task.Run(async () =>
        {
            await _mutations.WaitAsync();
            try
            {
                var e = await ResolveAsync(id, CancellationToken.None);
                var existing = await ReadConfigAsync(e, CancellationToken.None);
                var ram = ramMb ?? existing.RamSizeMb;
                var cpu = cores ?? existing.Cores;
                var gpu = gpuMode ?? existing.GpuMode;
                var host = SystemInfo.GetHostInfo();
                if (ram < 768 || ram > Math.Min(16384, host.TotalRamMb)) throw new InvalidOperationException("MuMu RAM must be between 768 and 16384 MB and fit host memory.");
                if (cpu < 1 || cpu > Math.Min(32, host.LogicalCores)) throw new InvalidOperationException("MuMu CPU cores must be between 1 and 32 and fit the host.");
                if (gpu is not ("vk" or "dx")) throw new InvalidOperationException("MuMu renderer must be vk (Vulkan) or dx (DirectX).");
                await _mumu.RunAsync(["setting", "-v", e.Index.ToString(),
                    "-k", "performance_mode", "-val", "custom", "-k", "performance_mem.custom", "-val", (ram / 1024.0).ToString(CultureInfo.InvariantCulture),
                    "-k", "performance_cpu.custom", "-val", cpu.ToString(), "-k", "renderer_mode", "-val", gpu], CancellationToken.None);
                RecordActivity(e.Id, FormatConfigUpdateCommand(ramMb, cores, gpuMode));
                var saved = await ReadConfigAsync(e, CancellationToken.None);
                if (saved.RamSizeMb != ram || saved.Cores != cpu || saved.GpuMode != gpu) throw new InvalidOperationException("MuMu did not retain the requested tuning values.");
                return saved;
            }
            finally { _mutations.Release(); }
        }).GetAwaiter().GetResult();

    public async Task<AndroidDashboard> GetDashboardAsync(CancellationToken ct)
    {
        var instances = await DiscoverAsync(ct);
        var errors = new List<string>();
        IReadOnlyList<RunningEmulator> running = [];
        try { running = await ConnectAsync(instances, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(ex.Message); }
        var samples = await SampleProcessesAsync(instances.Where(e => e.Started && e.Pid != null).Select(e => e.Pid!.Value).ToArray(), ct);
        var views = new List<AndroidEmulatorView>();
        foreach (var e in instances)
        {
            AndroidEmulatorConfig cfg = new();
            try { cfg = await ReadConfigAsync(e, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add($"{e.Name}: {ex.Message}"); }
            var runtime = running.FirstOrDefault(r => r.AvdName == e.Id);
            var guest = runtime == null ? default : await GetGuestMemoryAsync(runtime.Serial, ct);
            var idle = runtime == null ? default : await GetDeviceIdleAsync(runtime.Serial, ct);
            var activity = GetActivityNotes(e.Id);
            var sample = e.Pid is int pid && samples.TryGetValue(pid, out var value) ? (ProcessSample?)value : null;
            views.Add(new() { Id = e.Id, AvdName = e.Id, Name = e.Name, Index = e.Index,
                IsCore = e.Index == _config.CoreInstanceIndex, IsClone = e.Index != _config.CoreInstanceIndex,
                IsRunning = e.Started, Serial = runtime?.Serial, Pid = e.Started ? e.Pid : null,
                CpuPercent = sample?.CpuPercent, MemoryMb = sample?.MemoryBytes / (1024 * 1024),
                AppMemoryMb = guest.AppMb, GuestUsedRamMb = guest.UsedMb, GuestTotalRamMb = guest.TotalMb,
                RamSizeMb = cfg.RamSizeMb, Cores = cfg.Cores, GpuMode = cfg.GpuMode,
                LastCommand = activity.LastCommand, LastCommandAt = activity.LastCommandAt,
                CommandIdleTime = activity.CommandIdleTime, CommandIdleSeconds = activity.CommandIdleSeconds,
                IdleTime = idle.IdleTime, IdleSeconds = idle.IdleSeconds });
        }
        return new() { Host = SystemInfo.GetHostInfo(), Emulators = views, Error = errors.Count == 0 ? null : string.Join("; ", errors) };
    }

    public async Task ReloadAsync(int metroPort, CancellationToken ct)
    {
        if (!_config.ReloadOnMobileRestart) return;
        try
        {
            var emulators = await ListRunningEmulatorsAsync(ct);
            if (emulators.Count == 0) return;
            var port = _config.MetroPort > 0 ? _config.MetroPort : metroPort;
            await WaitForMetroAsync(port, ct);
            foreach (var e in emulators)
            {
                try { await RelaunchAppAsync(e.Serial, e.AvdName, port, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Log?.Invoke($"MuMu reload failed for {e.Serial}: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Log?.Invoke($"MuMu reload failed: {ex.Message}"); }
    }

    public Task<IReadOnlyList<string>> RestartAppAsync(CancellationToken ct) => RestartAppAsync(null, ct);
    public async Task<IReadOnlyList<string>> RestartAppAsync(string? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.PackageName)) throw new InvalidOperationException("No Android package configured.");
        var target = id == null ? null : await ResolveAsync(id, ct);
        var running = (await ListRunningEmulatorsAsync(ct)).Where(e => target == null || e.AvdName == target.Id).ToArray();
        if (running.Length == 0) throw new InvalidOperationException("No matching MuMu device is connected.");
        var failures = new List<string>();
        foreach (var e in running)
        {
            try { await RestartAppOnSerialAsync(e.Serial, e.AvdName, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { failures.Add($"{e.Serial}: {ex.Message}"); }
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("; ", failures));
        return running.Select(e => e.Serial).ToArray();
    }

    private async Task<string> RunTrackedAdbAsync(string? id, string? serial, string[] args, CancellationToken ct)
    {
        if (id != null) RecordActivity(id, FormatAdbCommand(serial, args));
        return await RunAdbAsync(serial, args, ct);
    }
    private async Task DisconnectAsync(string serial, CancellationToken ct)
    {
        try { await RunAdbAsync(null, ["disconnect", serial], ct); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("no such device", StringComparison.OrdinalIgnoreCase))
        { /* Already disconnected, including a transport removed by MuMu shutdown. */ }
    }
    private async Task<string> RunAdbAsync(string? serial, string[] args, CancellationToken ct)
    {
        var output = await MuMuClient.RunProcessAsync(_config.AdbPath, serial == null ? args : new[] { "-s", serial }.Concat(args), ct, failOnErrorOutput: true);
        if (output.Contains("Error:", StringComparison.OrdinalIgnoreCase) || output.Contains("No activities found", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(output);
        return output;
    }

    private AndroidEmulatorInfo ToInfo(MuMuInstance e, string? serial = null)
    {
        var activity = GetActivityNotes(e.Id);
        return new() { Id = e.Id, AvdName = e.Id, Name = e.Name, Index = e.Index,
            IsCore = e.Index == _config.CoreInstanceIndex, IsClone = e.Index != _config.CoreInstanceIndex,
            IsRunning = e.Started, Serial = serial, LastCommand = activity.LastCommand, LastCommandAt = activity.LastCommandAt,
            CommandIdleTime = activity.CommandIdleTime, CommandIdleSeconds = activity.CommandIdleSeconds };
    }
    private sealed record RunningEmulator(string Serial, string? AvdName);
}
