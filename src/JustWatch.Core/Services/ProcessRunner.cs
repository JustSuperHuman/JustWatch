using System.Diagnostics;
using System.Text;
using JustWatch.Core.Models;

namespace JustWatch.Core.Services;

public class ProcessRunner : IDisposable
{
    private Process? _process;
    private WindowsJobObject.ServiceJob? _serviceJob;
    private readonly ProcessConfig _config;
    private CancellationTokenSource? _cts;
    private bool _intentionalStop;
    private bool _disposed;
    private bool _restartPending;

    public event Action<string, string>? OutputReceived;
    public event Action<string, int>? ProcessExited;
    public event Action<string, string>? RestartRequested;

    // Cap on consecutive port-conflict auto-restarts, so two processes configured on
    // the same port can't kill each other forever. Cleared by an intentional stop.
    private const int MaxPortConflictRestarts = 2;
    private int _portConflictRestarts;

    public string Id => _config.Id;
    public string Name => _config.Name;
    public ProcessConfig Config => _config;
    public ProcessStatus Status { get; private set; } = ProcessStatus.Stopped;
    public int? Pid { get; private set; }
    public DateTime? StartedAt { get; private set; }

    public ProcessRunner(ProcessConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        if (_disposed || Status is ProcessStatus.Running or ProcessStatus.Starting) return;

        _intentionalStop = false;
        _restartPending = false;
        Status = ProcessStatus.Starting;

        if (!EnsurePortAvailable()) return;

        var psi = new ProcessStartInfo
        {
            FileName = _config.Command,
            WorkingDirectory = string.IsNullOrEmpty(_config.WorkingDirectory)
                ? Directory.GetCurrentDirectory()
                : _config.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in _config.Args)
            psi.ArgumentList.Add(arg);

        psi.Environment["FORCE_COLOR"] = "3";
        psi.Environment["TERM"] = "xterm-256color";
        psi.Environment.Remove("NO_COLOR");

        foreach (var (key, value) in _config.Environment)
            psi.Environment[key] = value;

        _cts?.Cancel();
        _cts?.Dispose();
        if (_process != null) _process.Exited -= OnProcessExited;
        _serviceJob?.Dispose();
        _serviceJob = null;
        if (_process != null) { _process.Exited -= OnProcessExited; _process.Dispose(); }
        _cts = new CancellationTokenSource();

        try
        {
            _process = new Process { StartInfo = psi };
            _process.Exited += OnProcessExited;
            _process.Start();

            // Tie the dev server (and its whole child tree) to the kill-on-close
            // job so it dies with the tray app even on a crash or external kill,
            // rather than being orphaned and piling up.
            WindowsJobObject.AssignProcess(_process);
            _serviceJob = WindowsJobObject.CreateServiceJob(_process);

            Pid = _process.Id;
            StartedAt = DateTime.UtcNow;
            Status = ProcessStatus.Running;

            _ = ReadStreamAsync(_process.StandardOutput.BaseStream, _cts.Token);
            _ = ReadStreamAsync(_process.StandardError.BaseStream, _cts.Token);
            _process.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Status = ProcessStatus.Crashed;
            OutputReceived?.Invoke(Id, $"\r\n\x1b[31m[ERROR] Failed to start '{_config.Name}': {ex.Message}\x1b[0m\r\n");
            ProcessExited?.Invoke(Id, -1);
        }
    }

    /// <summary>
    /// Clears the port before spawning. Returns false — leaving the runner Crashed
    /// and firing no exit event, so no restart loop starts — when the port is held by
    /// something we can't or won't kill; retrying in that state can only fail again.
    /// </summary>
    private bool EnsurePortAvailable()
    {
        var port = _config.Port ?? 0;
        if (port <= 0) return true;

        // Listeners pinned to one routable interface (Tailscale on 8081, a LAN IP) are
        // not conflicts — only wildcard/loopback holders are.
        var holders = PortGuard.FindBlocking(port);
        if (holders.Count == 0) return true;

        if (!_config.ReclaimPort)
        {
            Fail($"Port {port} is in use by {PortGuard.Describe(holders)} and reclaimPort is off for {_config.Name}. " +
                 $"Free the port or stop that process, then start again.");
            return false;
        }

        if (PortGuard.TryReclaim(port, Log, TimeSpan.FromSeconds(3)))
        {
            OutputReceived?.Invoke(Id,
                $"\r\n\x1b[36m[JUSTWATCH] Port {port} reclaimed — starting {_config.Name}.\x1b[0m\r\n");
            return true;
        }

        Fail($"Port {port} is still held by {PortGuard.Describe(PortGuard.FindBlocking(port))} after trying to reclaim it. " +
             $"{_config.Name} was not started.");
        return false;
    }

    private void Log(string message) =>
        OutputReceived?.Invoke(Id, $"\r\n\x1b[33m[JUSTWATCH] {message}\x1b[0m\r\n");

    private void Fail(string message)
    {
        Status = ProcessStatus.Crashed;
        StartedAt = null;
        Pid = null;
        OutputReceived?.Invoke(Id, $"\r\n\x1b[31m[JUSTWATCH] {message}\x1b[0m\r\n");
    }

    public async Task StopAsync()
    {
        _portConflictRestarts = 0;
        _intentionalStop = true;
        if (_process != null && !string.IsNullOrWhiteSpace(_config.StopCommand))
        {
            var psi = new ProcessStartInfo(_config.StopCommand)
            {
                WorkingDirectory = _config.WorkingDirectory, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in _config.StopArgs) psi.ArgumentList.Add(arg);
            foreach (var (key, value) in _config.Environment) psi.Environment[key] = value;
            using var stop = Process.Start(psi) ?? throw new InvalidOperationException("Stop command failed to start.");
            var stdout = stop.StandardOutput.ReadToEndAsync();
            var stderr = stop.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await stop.WaitForExitAsync(timeout.Token); }
            catch { try { stop.Kill(true); } catch { } throw; }
            OutputReceived?.Invoke(Id, await stdout + await stderr);
            if (stop.ExitCode != 0) throw new InvalidOperationException($"Stop command for {_config.Name} exited with code {stop.ExitCode}.");
        }

        // npm and Yarn may outlive their command wrapper. Terminate the entire
        // assigned service job, including descendants whose original parent exited.
        if (_serviceJob != null)
        {
            await _serviceJob.TerminateAsync();
            _serviceJob.Dispose();
            _serviceJob = null;
        }

        if (_process == null || _process.HasExited)
        {
            _cts?.Cancel();
            Status = ProcessStatus.Stopped;
            StartedAt = null;
            Pid = null;
            return;
        }

        _intentionalStop = true;
        Status = ProcessStatus.Stopping;

        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch { }

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { _process.Kill(); } catch { }
        }

        _cts?.Cancel();
        Status = ProcessStatus.Stopped;
        StartedAt = null;
        Pid = null;
    }

    public void WriteInput(string data)
    {
        if (_process is { HasExited: false })
        {
            try
            {
                _process.StandardInput.Write(data);
                _process.StandardInput.Flush();
            }
            catch { }
        }
    }

    private async Task ReadStreamAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (count == 0) break;
                var text = Encoding.UTF8.GetString(buffer, 0, count);
                OutputReceived?.Invoke(Id, text);
                CheckForFatalPatterns(text);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private void CheckForFatalPatterns(string text)
    {
        if (_restartPending || _intentionalStop || !_config.AutoRestart) return;

        foreach (var pattern in _config.RestartOutputPatterns)
        {
            if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                if (pattern == "EADDRINUSE")
                {
                    // Astro/Node report a failed listen as an unhandled rejection and
                    // then exit 0, so name the real cause instead of leaving the log
                    // showing a "clean" exit.
                    OutputReceived?.Invoke(Id,
                        $"\r\n\x1b[31m[JUSTWATCH] {_config.Name} could not bind port {_config.Port} — another process took it. " +
                        $"(This exits with code 0, so it looks like a clean shutdown.)\x1b[0m\r\n");

                    if (++_portConflictRestarts > MaxPortConflictRestarts)
                    {
                        Log($"Giving up after {MaxPortConflictRestarts} port-conflict restarts — check for another service configured on port {_config.Port}.");
                        _restartPending = true;
                        return;
                    }
                }

                _restartPending = true;
                OutputReceived?.Invoke(Id,
                    $"\r\n\x1b[33m[JUSTWATCH] Detected \"{pattern}\" — scheduling automatic restart\x1b[0m\r\n");
                RestartRequested?.Invoke(Id, pattern);
                return;
            }
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _process)) return;
        var exitCode = -1;
        try { exitCode = _process?.ExitCode ?? -1; } catch { }

        StartedAt = null;
        Pid = null;

        if (!_intentionalStop)
        {
            Status = ProcessStatus.Crashed;
            OutputReceived?.Invoke(Id,
                $"\r\n\x1b[33m[Process exited with code {exitCode}]\x1b[0m\r\n");
        }
        else
        {
            Status = ProcessStatus.Stopped;
            OutputReceived?.Invoke(Id,
                $"\r\n\x1b[90m[Process stopped]\x1b[0m\r\n");
        }

        ProcessExited?.Invoke(Id, exitCode);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _intentionalStop = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _serviceJob?.Dispose();
        _serviceJob = null;

        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }

        _process?.Dispose();
    }
}
