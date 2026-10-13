using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JustWatch.Core.Services;

public class ScreenRecorder : IDisposable
{
    private readonly string _ffmpegPath;
    private readonly string _recordingsDir;
    private Process? _process;
    private string? _outputPath;
    private string? _recordingTarget;

    public bool IsRecording => _process is { HasExited: false };
    public string? CurrentRecordingPath => _outputPath;
    public string? RecordingTarget => _recordingTarget;

    public event Action<string>? Log;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    public ScreenRecorder(string? ffmpegPath = null, string? recordingsDir = null)
    {
        _ffmpegPath = !string.IsNullOrEmpty(ffmpegPath) && File.Exists(ffmpegPath)
            ? ffmpegPath
            : FindFfmpeg();

        _recordingsDir = !string.IsNullOrEmpty(recordingsDir)
            ? recordingsDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "JustGains Tests");
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsDelegate lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsDelegate(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static IntPtr FindWindowByTitle(string title)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var len = GetWindowTextLength(hWnd);
            if (len == 0) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            if (sb.ToString().Contains(title[..Math.Min(title.Length, 30)], StringComparison.OrdinalIgnoreCase))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public (bool Success, string PathOrError) TryStartRecording(string windowTitle)
    {
        if (IsRecording)
            return (true, _outputPath!);

        if (!File.Exists(_ffmpegPath))
            return (false, $"FFmpeg not found at: {_ffmpegPath}");

        var hwnd = FindWindowByTitle(windowTitle);
        if (hwnd == IntPtr.Zero)
        {
            Log?.Invoke($"Window not found via EnumWindows, trying desktop fallback");
            return (false, $"Window not found: \"{windowTitle}\"");
        }

        ShowWindow(hwnd, SW_RESTORE);
        Thread.Sleep(300);
        SetForegroundWindow(hwnd);
        Thread.Sleep(200);

        // Validate window has real dimensions
        if (GetWindowRect(hwnd, out var rect))
        {
            var w = rect.Right - rect.Left;
            var h = rect.Bottom - rect.Top;
            if (w < 50 || h < 50)
            {
                Log?.Invoke($"Window too small ({w}x{h}), trying to restore...");
                ShowWindow(hwnd, SW_SHOW);
                Thread.Sleep(500);
                if (GetWindowRect(hwnd, out rect))
                {
                    w = rect.Right - rect.Left;
                    h = rect.Bottom - rect.Top;
                }
                if (w < 50 || h < 50)
                    return (false, $"Window has invalid dimensions ({w}x{h}). Is it minimized?");
            }
            Log?.Invoke($"Window dimensions: {w}x{h}");
        }

        return StartFfmpeg($"title={windowTitle}", windowTitle);
    }

    public (bool Success, string PathOrError) StartDesktopFallback(RecordingQuality? quality = null)
    {
        if (IsRecording)
            return (true, _outputPath!);

        return StartFfmpeg("desktop", "desktop", quality);
    }

    private (bool Success, string PathOrError) StartFfmpeg(string input, string targetLabel, RecordingQuality? quality = null)
    {
        Directory.CreateDirectory(_recordingsDir);
        _outputPath = Path.Combine(_recordingsDir, $"test_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        _recordingTarget = targetLabel;

        var q = quality ?? new RecordingQuality();

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("gdigrab");
        psi.ArgumentList.Add("-framerate");
        psi.ArgumentList.Add(q.Framerate.ToString());
        if (q.MaxSeconds > 0)
        {
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(q.MaxSeconds.ToString());
        }
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(input);
        if (!string.IsNullOrEmpty(q.Resolution))
        {
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add($"scale={q.Resolution.Replace("x", ":")}");
        }
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-preset");
        psi.ArgumentList.Add("ultrafast");
        psi.ArgumentList.Add("-crf");
        psi.ArgumentList.Add(q.Crf.ToString());
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add(_outputPath);

        try
        {
            _process = Process.Start(psi);
            Thread.Sleep(1500);

            if (_process is { HasExited: true })
            {
                var code = _process.ExitCode;
                _process.Dispose();
                _process = null;
                return (false, $"FFmpeg exited immediately (code {code}). Capture target may be unavailable.");
            }

            return (true, _outputPath);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to start FFmpeg: {ex.Message}");
        }
    }

    public string StopRecording()
    {
        var path = _outputPath ?? "";

        if (_process is { HasExited: false })
        {
            try
            {
                _process.StandardInput.WriteLine("q");
                _process.StandardInput.Flush();
                if (!_process.WaitForExit(8000))
                    Log?.Invoke("FFmpeg didn't exit cleanly, forcing kill");
            }
            catch { }

            if (_process is { HasExited: false })
                try { _process.Kill(entireProcessTree: true); } catch { }
        }

        _process?.Dispose();
        _process = null;
        _recordingTarget = null;

        // Validate the output file
        if (File.Exists(path))
        {
            var size = new FileInfo(path).Length;
            if (size < 1000)
            {
                Log?.Invoke($"Warning: Recording file is very small ({size} bytes), may be corrupted");
            }
        }
        else
        {
            Log?.Invoke("Warning: Recording file was not created");
        }

        return path;
    }

    private static string FindFfmpeg()
    {
        var candidates = new[]
        {
            @"C:\Program Files\ShareX\ffmpeg.exe",
            @"C:\Program Files (x86)\ShareX\ffmpeg.exe",
            "ffmpeg"
        };
        return candidates.FirstOrDefault(File.Exists) ?? "ffmpeg";
    }

    public void Dispose()
    {
        if (IsRecording) StopRecording();
        _process?.Dispose();
    }
}

public class RecordingQuality
{
    public int Framerate { get; set; } = 15;
    public int Crf { get; set; } = 28;
    public int MaxSeconds { get; set; }
    public string? Resolution { get; set; }

    public static RecordingQuality Default => new();

    public static RecordingQuality ForTelegram(Models.TelegramConfig cfg) => new()
    {
        Framerate = cfg.RecordingFramerate,
        Crf = cfg.RecordingCrf,
        MaxSeconds = cfg.MaxRecordingSeconds,
        Resolution = cfg.RecordingResolution
    };
}
