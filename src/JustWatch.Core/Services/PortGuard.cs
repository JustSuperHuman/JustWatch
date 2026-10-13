using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace JustWatch.Core.Services;

/// <summary>
/// Finds — and optionally evicts — whatever already holds a TCP port before we try
/// to bind it. A dev server started outside the JustWatch (or one that outlived the
/// JustWatch's job object) keeps listening on its port, and every start we attempt
/// then dies on EADDRINUSE. Worse, some servers report that failure as an unhandled
/// rejection and exit with code 0, so the JustWatch sees a "clean" exit and retries
/// forever. Reclaiming the port up front is what makes a start deterministic.
/// </summary>
public static class PortGuard
{
    /// <param name="Blocking">
    /// True when this listener actually stands in the way of a dev server binding the
    /// port — i.e. it holds the wildcard address or loopback. A listener bound to one
    /// specific interface address (Tailscale's 100.x proxy on 8081, a LAN IP) coexists
    /// with our servers and must never be killed.
    /// </param>
    public record PortHolder(int Pid, string ProcessName, string Address, bool Blocking);

    /// <summary>
    /// Processes we will never kill to free a port, no matter what the TCP table
    /// says. A dev port can legitimately fall inside a range reserved by Hyper-V /
    /// WinNAT (held by a system process), and killing one of these would take the
    /// machine down rather than fix a dev server.
    /// </summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression",
        "svchost", "services", "lsass", "wininit", "csrss", "smss", "winlogon",
        "MsMpEng", "explorer", "dwm", "vmms", "vmmem", "vmcompute", "wslservice",
        "tailscaled",
    };

    /// <summary>Every process listening on <paramref name="port"/> (IPv4 and IPv6).</summary>
    public static IReadOnlyList<PortHolder> FindListeners(int port)
    {
        if (!OperatingSystem.IsWindows() || port <= 0) return [];

        var holders = new List<PortHolder>();
        var seen = new HashSet<(int, string)>();

        foreach (var (pid, address, blocking) in EnumerateListeners(AF_INET, port)
            .Concat(EnumerateListeners(AF_INET6, port)))
        {
            var name = SafeProcessName(pid);
            if (seen.Add((pid, address)))
                holders.Add(new PortHolder(pid, name, address, blocking));
        }

        return holders;
    }

    /// <summary>
    /// Only the listeners that would actually block a bind — see
    /// <see cref="PortHolder.Blocking"/>. This is what the start path acts on.
    /// </summary>
    public static IReadOnlyList<PortHolder> FindBlocking(int port) =>
        FindListeners(port).Where(h => h.Blocking).ToList();

    public static bool IsInUse(int port) => FindBlocking(port).Count > 0;

    /// <summary>
    /// Kill whatever is blocking <paramref name="port"/> and wait for the OS to
    /// release it. Returns true when the port is free by the time we give up waiting.
    /// Never kills this process or anything in <see cref="Protected"/>.
    /// </summary>
    public static bool TryReclaim(int port, Action<string> log, TimeSpan timeout)
    {
        var holders = FindBlocking(port);
        if (holders.Count == 0) return true;

        var self = Environment.ProcessId;

        foreach (var holder in holders)
        {
            if (holder.Pid <= 4 || holder.Pid == self)
            {
                log($"Port {port} is held by PID {holder.Pid} ({holder.ProcessName}) — refusing to kill it.");
                continue;
            }

            if (Protected.Contains(holder.ProcessName))
            {
                log($"Port {port} is held by protected process {holder.ProcessName} (PID {holder.Pid}) — leaving it alone.");
                continue;
            }

            try
            {
                using var proc = Process.GetProcessById(holder.Pid);
                log($"Port {port} already held by {holder.ProcessName} (PID {holder.Pid}) on {holder.Address} — killing it to reclaim the port.");
                proc.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
                // Already gone between the table read and the kill — that's a win.
            }
            catch (Exception ex)
            {
                log($"Could not kill {holder.ProcessName} (PID {holder.Pid}) holding port {port}: {ex.Message}");
            }
        }

        // The listener socket can outlive the process by a few hundred ms.
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsInUse(port)) return true;
            Thread.Sleep(100);
        }

        return !IsInUse(port);
    }

    public static string Describe(IReadOnlyList<PortHolder> holders) =>
        string.Join(", ", holders.Select(h => $"{h.ProcessName} (PID {h.Pid}) on {h.Address}"));

    private static string SafeProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }

    private static IEnumerable<(int Pid, string Address, bool Blocking)> EnumerateListeners(int family, int port)
    {
        var size = 0;
        // First call sizes the buffer; ERROR_INSUFFICIENT_BUFFER is expected here.
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (size <= 0) yield break;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
                yield break;

            var count = Marshal.ReadInt32(buffer);
            var rowPtr = buffer + 4;
            var rowSize = family == AF_INET
                ? Marshal.SizeOf<MIB_TCPROW_OWNER_PID>()
                : Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();

            for (var i = 0; i < count; i++, rowPtr += rowSize)
            {
                int rowPort, pid;
                IPAddress ip;

                if (family == AF_INET)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    rowPort = NetworkPort(row.localPort);
                    pid = (int)row.owningPid;
                    ip = new IPAddress(BitConverter.GetBytes(row.localAddr));
                }
                else
                {
                    var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(rowPtr);
                    rowPort = NetworkPort(row.localPort);
                    pid = (int)row.owningPid;
                    ip = new IPAddress(row.localAddr);
                }

                if (rowPort != port) continue;

                var display = family == AF_INET ? $"{ip}:{port}" : $"[{ip}]:{port}";
                yield return (pid, display, IsBlockingAddress(ip));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// A listener blocks us only if it holds the wildcard address (0.0.0.0 / ::) or
    /// loopback — the addresses our dev servers bind. Anything pinned to a single
    /// routable interface (Tailscale's 100.x proxy on 8081, a LAN IP) has always
    /// coexisted with them, so it is not a conflict and never a kill target.
    /// </summary>
    private static bool IsBlockingAddress(IPAddress ip) =>
        ip.Equals(IPAddress.Any)
        || ip.Equals(IPAddress.IPv6Any)
        || IPAddress.IsLoopback(ip);

    /// <summary>dwLocalPort holds the port in network byte order in its low two bytes.</summary>
    private static int NetworkPort(uint raw)
    {
        var bytes = BitConverter.GetBytes(raw);
        return (bytes[0] << 8) | bytes[1];
    }

    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] remoteAddr;
        public uint remoteScopeId;
        public uint remotePort;
        public uint state;
        public uint owningPid;
    }
}
