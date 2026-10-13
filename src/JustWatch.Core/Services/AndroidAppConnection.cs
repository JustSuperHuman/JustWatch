using JustWatch.Core.Models;
namespace JustWatch.Core.Services;

public sealed partial class AndroidReloader
{
    private async Task EnsureDevTunnelsAsync(string? avdName, string serial, int metroPort, CancellationToken ct)
    {
        // JustGains' local API must survive device reconnects alongside Metro.
        foreach (var port in new[] { metroPort, 5000 }.Distinct())
            await RunTrackedAdbAsync(avdName, serial, ["reverse", $"tcp:{port}", $"tcp:{port}"], ct);
    }

    private async Task WaitForMetroAsync(int port, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var until = DateTime.UtcNow.AddSeconds(Math.Max(1, _config.MetroReadyTimeoutSeconds));

        while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
        {
            try
            {
                // Metro answers GET /status with "packager-status:running" when ready.
                var resp = await http.GetStringAsync($"http://localhost:{port}/status", ct);
                if (resp.Contains("running", StringComparison.OrdinalIgnoreCase))
                {
                    Log?.Invoke("Metro is ready; reloading app.");
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* not up yet */ }

            await Task.Delay(1000, ct);
        }

        Log?.Invoke("Metro readiness timed out; reloading app anyway.");
    }

    private async Task RelaunchAppAsync(string serial, string? avdName, int metroPort, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.PackageName))
        {
            Log?.Invoke("No Android package configured; skipping relaunch.");
            return;
        }

        // Prefer deep-linking the Expo dev client straight into Metro
        // through an ADB reverse tunnel so the app loads
        // the JS bundle instead of parking on the dev-launcher home screen.
        // Force-stop first: deep-linking a running app triggers a warm reload,
        // which races native JSI module installs (e.g. Skia) and red-screens.
        if (!string.IsNullOrWhiteSpace(_config.DevClientScheme))
        {
            await EnsureDevTunnelsAsync(avdName, serial, metroPort, ct);
            var url = $"{_config.DevClientScheme.Trim()}://expo-development-client/?url=" +
                Uri.EscapeDataString($"http://127.0.0.1:{metroPort}");
            try
            {
                Log?.Invoke($"Reloading {_config.PackageName} on {serial} (dev-client deep link to Metro {metroPort}).");
                await RunTrackedAdbAsync(avdName, serial,
                    ["shell", "am", "force-stop", _config.PackageName], ct);
                await Task.Delay(500, ct);
                await RunTrackedAdbAsync(avdName, serial,
                    ["shell", "am", "start", "-a", "android.intent.action.VIEW", "-d", url, _config.PackageName],
                    ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log?.Invoke($"Dev-client deep link failed on {serial} ({ex.Message}); falling back to launcher intent.");
            }
        }

        Log?.Invoke($"Reloading {_config.PackageName} on {serial}.");
        await RunTrackedAdbAsync(avdName, serial,
            ["shell", "monkey", "-p", _config.PackageName, "-c", "android.intent.category.LAUNCHER", "1"],
            ct);
    }

    private async Task RestartAppOnSerialAsync(string serial, string? avdName, CancellationToken ct)
    {
        Log?.Invoke($"Force-stopping {_config.PackageName} on {serial}.");
        await RunTrackedAdbAsync(avdName, serial, ["shell", "am", "force-stop", _config.PackageName], ct);
        await Task.Delay(500, ct);
        await RelaunchAppAsync(serial, avdName, _config.MetroPort > 0 ? _config.MetroPort : 8081, ct);
    }

}
