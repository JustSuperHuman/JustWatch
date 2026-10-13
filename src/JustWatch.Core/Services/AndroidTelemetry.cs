using System.Diagnostics;
using System.Text.RegularExpressions;
namespace JustWatch.Core.Services;

public sealed partial class AndroidReloader
{
    private async Task<GuestMemory> GetGuestMemoryAsync(string serial, CancellationToken ct)
    {
        long? appMb = null;
        if (!string.IsNullOrWhiteSpace(_config.PackageName))
        {
            try
            {
                var meminfo = await RunAdbAsync(serial, ["shell", "dumpsys", "meminfo", _config.PackageName], ct);
                appMb = ParseDumpsysTotalPssMb(meminfo);
            }
            catch { /* app not running / not installed */ }
        }

        long? totalMb = null, usedMb = null;
        try
        {
            var meminfo = await RunAdbAsync(serial, ["shell", "cat", "/proc/meminfo"], ct);
            var (total, available) = ParseProcMeminfoMb(meminfo);
            totalMb = total;
            if (total is long t && available is long a)
                usedMb = Math.Max(0, t - a);
        }
        catch { }

        return new GuestMemory(appMb, usedMb, totalMb);
    }

    private async Task<Dictionary<string, DeviceIdle>> GetDeviceIdleBySerialAsync(
        IEnumerable<RunningEmulator> running,
        CancellationToken ct)
    {
        var emulators = running.ToList();
        if (emulators.Count == 0)
            return new Dictionary<string, DeviceIdle>(StringComparer.OrdinalIgnoreCase);

        var results = await Task.WhenAll(emulators.Select(async r =>
            (r.Serial, Idle: await GetDeviceIdleAsync(r.Serial, ct))));

        return results.ToDictionary(r => r.Serial, r => r.Idle, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<DeviceIdle> GetDeviceIdleAsync(string serial, CancellationToken ct)
    {
        try
        {
            var power = await RunAdbAsync(serial, ["shell", "dumpsys", "power"], ct);
            return ParseDeviceIdle(power);
        }
        catch
        {
            return default;
        }
    }

    private static DeviceIdle ParseDeviceIdle(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            if (line.Contains("lastUserActivityTimeNoChangeLights", StringComparison.OrdinalIgnoreCase))
                continue;

            var match = Regex.Match(
                line,
                @"\blastUserActivityTime(?:\([^)]*\))?=\d+\s+\((\d+)\s*ms ago\)",
                RegexOptions.IgnoreCase);
            if (match.Success && double.TryParse(match.Groups[1].Value, out var ms))
                return DeviceIdle.FromTimeSpan(TimeSpan.FromMilliseconds(ms));
        }

        foreach (var line in output.Split('\n'))
        {
            var match = Regex.Match(
                line,
                @"\bmLastInteractivePowerHintTime=\d+\s+\((\d+)\s*ms ago\)",
                RegexOptions.IgnoreCase);
            if (match.Success && double.TryParse(match.Groups[1].Value, out var ms))
                return DeviceIdle.FromTimeSpan(TimeSpan.FromMilliseconds(ms));
        }

        return default;
    }

    private static long? ParseDumpsysTotalPssMb(string output)
    {
        // The per-process summary table and the App Summary both carry a "TOTAL"
        // line whose first number is total PSS in KB. Skip the "TOTAL SWAP" line.
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("TOTAL", StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.StartsWith("TOTAL SWAP", StringComparison.OrdinalIgnoreCase)) continue;

            var match = Regex.Match(trimmed, "(\\d+)");
            if (match.Success && long.TryParse(match.Value, out var kb))
                return kb / 1024;
        }
        return null;
    }

    private static (long? TotalMb, long? AvailableMb) ParseProcMeminfoMb(string output)
    {
        long? total = null, available = null, free = null, buffers = null, cached = null;
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split([':', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], out var kb)) continue;

            switch (parts[0])
            {
                case "MemTotal": total = kb / 1024; break;
                case "MemAvailable": available = kb / 1024; break;
                case "MemFree": free = kb / 1024; break;
                case "Buffers": buffers = kb / 1024; break;
                case "Cached": cached = kb / 1024; break;
            }
        }

        available ??= (free ?? 0) + (buffers ?? 0) + (cached ?? 0);
        return (total, available);
    }

    private static async Task<Dictionary<int, ProcessSample>> SampleProcessesAsync(
        IReadOnlyCollection<int> pids, CancellationToken ct)
    {
        var result = new Dictionary<int, ProcessSample>();
        if (pids.Count == 0) return result;

        var first = new Dictionary<int, TimeSpan>();
        foreach (var pid in pids)
        {
            try { first[pid] = Process.GetProcessById(pid).TotalProcessorTime; }
            catch { }
        }

        var sw = Stopwatch.StartNew();
        await Task.Delay(500, ct);
        sw.Stop();

        var cores = Math.Max(1, Environment.ProcessorCount);
        foreach (var pid in pids)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                proc.Refresh();
                double? cpu = null;
                if (first.TryGetValue(pid, out var t0))
                {
                    var deltaMs = (proc.TotalProcessorTime - t0).TotalMilliseconds;
                    var elapsed = Math.Max(1, sw.Elapsed.TotalMilliseconds);
                    cpu = Math.Round(Math.Clamp(deltaMs / (elapsed * cores) * 100.0, 0, 100), 1);
                }
                result[pid] = new ProcessSample(cpu, proc.WorkingSet64);
            }
            catch { }
        }

        return result;
    }

    private void RecordActivity(string avdName, string command)
    {
        if (string.IsNullOrWhiteSpace(avdName) || string.IsNullOrWhiteSpace(command))
            return;

        _activity[avdName] = new EmulatorCommandActivity(command, DateTimeOffset.UtcNow);
    }

    private EmulatorActivityNotes GetActivityNotes(string avdName)
    {
        if (!_activity.TryGetValue(avdName, out var activity))
            return new EmulatorActivityNotes(null, null, null, null);

        var idle = DateTimeOffset.UtcNow - activity.LastCommandAt;
        if (idle < TimeSpan.Zero)
            idle = TimeSpan.Zero;

        return new EmulatorActivityNotes(
            activity.LastCommand,
            activity.LastCommandAt,
            FormatIdleTime(idle),
            Math.Round(idle.TotalSeconds, 1));
    }

    private string FormatAdbCommand(string? serial, IEnumerable<string> args)
    {
        var commandArgs = serial is null
            ? args
            : new[] { "-s", serial }.Concat(args);
        return FormatCommand(_config.AdbPath, commandArgs);
    }

    private static string FormatCommand(string command, IEnumerable<string> args) =>
        string.Join(" ", new[] { command }.Concat(args).Select(QuoteCommandPart));

    private static string QuoteCommandPart(string part)
    {
        if (string.IsNullOrEmpty(part))
            return "\"\"";

        return part.Any(char.IsWhiteSpace) || part.Contains('"')
            ? $"\"{part.Replace("\"", "\\\"")}\""
            : part;
    }

    private static string FormatConfigUpdateCommand(int? ramMb, int? cores, string? gpuMode)
    {
        var changes = new List<string>();
        if (ramMb is not null) changes.Add($"ramSizeMb={ramMb}");
        if (cores is not null) changes.Add($"cores={cores}");
        if (!string.IsNullOrWhiteSpace(gpuMode)) changes.Add($"gpuMode={gpuMode.Trim()}");
        return changes.Count == 0
            ? "update emulator config"
            : $"update emulator config ({string.Join(", ", changes)})";
    }

    private static string FormatIdleTime(TimeSpan time)
    {
        var wholeDays = (int)Math.Floor(time.TotalDays);
        return wholeDays > 0
            ? $"{wholeDays}.{time:hh\\:mm\\:ss}"
            : time.ToString(@"hh\:mm\:ss");
    }

    private readonly record struct ProcessSample(double? CpuPercent, long MemoryBytes);

    private readonly record struct GuestMemory(long? AppMb, long? UsedMb, long? TotalMb);

    private readonly record struct DeviceIdle(string? IdleTime, double? IdleSeconds)
    {
        public static DeviceIdle FromTimeSpan(TimeSpan idle) =>
            new(FormatIdleTime(idle), Math.Round(idle.TotalSeconds, 1));
    }

    private readonly record struct EmulatorCommandActivity(string LastCommand, DateTimeOffset LastCommandAt);

    private readonly record struct EmulatorActivityNotes(
        string? LastCommand,
        DateTimeOffset? LastCommandAt,
        string? CommandIdleTime,
        double? CommandIdleSeconds);
}
