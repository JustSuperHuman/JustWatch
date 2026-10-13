using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace JustWatch.Core.Services;

public record WindowInfo(string Title, string ProcessName, int Pid);

public static class WindowFinder
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public static List<WindowInfo> GetAllVisible()
    {
        var results = new List<WindowInfo>();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var len = GetWindowTextLength(hWnd);
            if (len == 0) return true;

            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            GetWindowThreadProcessId(hWnd, out var pid);
            var processName = "";
            try { processName = Process.GetProcessById((int)pid).ProcessName; } catch { }

            results.Add(new WindowInfo(title, processName, (int)pid));
            return true;
        }, IntPtr.Zero);

        return results;
    }

    public static List<WindowInfo> FindForTestType(string testType)
    {
        var all = GetAllVisible();

        return testType switch
        {
            "web" => all.Where(w =>
                w.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
                w.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
                w.Title.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                w.Title.Contains("Chrome", StringComparison.Ordinal) ||
                w.Title.Contains("Edge", StringComparison.Ordinal)
            ).ToList(),

            "mobile" => all.Where(w =>
                w.ProcessName.Equals("MuMuNxDevice", StringComparison.OrdinalIgnoreCase) ||
                w.ProcessName.Equals("MuMuPlayer", StringComparison.OrdinalIgnoreCase)
            ).ToList(),

            _ => []
        };
    }

    public static bool WindowExists(string title)
    {
        return GetAllVisible().Any(w =>
            w.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
    }

    public static string FormatWindowList(List<WindowInfo> windows)
    {
        if (windows.Count == 0) return "(no matching windows found)";
        var sb = new StringBuilder();
        for (int i = 0; i < windows.Count; i++)
            sb.AppendLine($"  [{i + 1}] \"{windows[i].Title}\" (process: {windows[i].ProcessName})");
        return sb.ToString();
    }
}
