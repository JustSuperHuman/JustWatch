namespace JustWatch.Core.Models;

/// <summary>
/// Host machine resources used to bound emulator RAM/CPU tuning and to show how
/// much headroom the running emulators are consuming.
/// </summary>
public sealed class AndroidHostInfo
{
    public int LogicalCores { get; init; }
    public long TotalRamMb { get; init; }
    public long AvailableRamMb { get; init; }

    // Convenience: physical RAM in use across the whole machine (toolbar context only).
    public long UsedRamMb => Math.Max(0, TotalRamMb - AvailableRamMb);
}

/// <summary>
/// A single managed MuMu instance with its current runtime stats (when running) and its
/// persisted MuMu tuning (RAM / CPU cores / GPU mode).
/// </summary>
public sealed class AndroidEmulatorView
{
    public string Id { get; init; } = string.Empty;
    public string AvdName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Index { get; init; }
    public bool IsCore { get; init; }
    public bool IsClone { get; init; }

    // Runtime (null/false when stopped).
    public bool IsRunning { get; init; }
    public string? Serial { get; init; }
    public int? Pid { get; init; }
    public double? CpuPercent { get; init; }

    // Host footprint: how much of the machine's RAM this emulator process consumes.
    public long? MemoryMb { get; init; }

    // Guest memory, read inside the emulator via adb:
    //  AppMemoryMb     = the JustGains app's own usage (PSS) — how much the app itself uses.
    //  GuestUsed/Total = the emulator's RAM in use / total, as Android sees it.
    public long? AppMemoryMb { get; init; }
    public long? GuestUsedRamMb { get; init; }
    public long? GuestTotalRamMb { get; init; }

    // Persisted MuMu tuning. 0 means "unset / emulator default".
    public int RamSizeMb { get; init; }
    public int Cores { get; init; }
    public string GpuMode { get; init; } = "vk";

    // Last explicit command JustWatch ran for this emulator and how long ago.
    public string? LastCommand { get; init; }
    public DateTimeOffset? LastCommandAt { get; init; }
    public string? IdleTime { get; init; }
    public double? IdleSeconds { get; init; }
    public string? CommandIdleTime { get; init; }
    public double? CommandIdleSeconds { get; init; }
}

/// <summary>
/// The emulator management screen payload: host info + every managed MuMu instance. Error
/// is set when the running-emulator probe (adb) failed but configs were still read.
/// </summary>
public sealed class AndroidDashboard
{
    public AndroidHostInfo Host { get; init; } = new();
    public IReadOnlyList<AndroidEmulatorView> Emulators { get; init; } = [];
    public string? Error { get; init; }
}

/// <summary>
/// The editable subset of a MuMu instance's settings surfaced by the tuning UI.
/// </summary>
public sealed class AndroidEmulatorConfig
{
    public string Id { get; init; } = string.Empty;
    public string AvdName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int RamSizeMb { get; init; }
    public int Cores { get; init; }
    public string GpuMode { get; init; } = "vk";
}
