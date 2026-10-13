using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using JustWatch.Core.Models;

namespace JustWatch.Core.Services;

public sealed record UpdateInfo(Version Version, string Tag, string Name, string Notes, string PageUrl, string? DownloadUrl, long DownloadSize);

public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Failed }

/// <summary>
/// Checks GitHub releases for a newer JustWatch build and installs it in place.
/// Releases carry a <c>JustWatch-win-x64.zip</c> asset; installation swaps the
/// app folder from a detached PowerShell script once this process exits.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string AssetName = "JustWatch-win-x64.zip";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly UpdatesConfig _config;
    private readonly HttpClient _http;
    private CancellationTokenSource? _loopCts;

    public UpdateService(UpdatesConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("JustWatch", CurrentVersion.ToString(3)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public event Action? StateChanged;

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public UpdateInfo? Latest { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? LastChecked { get; private set; }

    public string Repository => _config.Repository;
    public string RepositoryUrl => $"https://github.com/{_config.Repository}";
    public string ReleasesUrl => $"{RepositoryUrl}/releases";

    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    /// <summary>Builds running inside the source checkout are updated with git, not releases.</summary>
    public static bool IsDevelopmentBuild => RepoPaths.FindRepoRoot() != null;

    public bool UpdateAvailable => Latest != null && Latest.Version > CurrentVersion;

    public void StartBackgroundChecks()
    {
        if (!_config.CheckAutomatically || _loopCts != null) return;
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _ = Task.Run(async () =>
        {
            // Let services start before touching the network.
            try { await Task.Delay(TimeSpan.FromSeconds(20), token); } catch { return; }
            while (!token.IsCancellationRequested)
            {
                await CheckAsync(token);
                try { await Task.Delay(CheckInterval, token); } catch { return; }
            }
        }, token);
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (State is UpdateState.Checking or UpdateState.Downloading) return Latest;
        SetState(UpdateState.Checking);
        try
        {
            var url = $"https://api.github.com/repos/{_config.Repository}/releases/latest";
            using var response = await _http.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // No published release yet.
                Latest = null;
                LastChecked = DateTimeOffset.Now;
                SetState(UpdateState.UpToDate);
                return null;
            }
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Latest = ParseRelease(doc.RootElement);
            LastChecked = DateTimeOffset.Now;
            SetState(UpdateAvailable ? UpdateState.Available : UpdateState.UpToDate);
            return Latest;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Error = ex.Message;
            SetState(UpdateState.Failed);
            return null;
        }
    }

    /// <summary>
    /// Downloads the release, stages it in %TEMP% and starts the swap script.
    /// The caller must exit the app afterwards; the script waits for that,
    /// replaces the install folder and relaunches JustWatch.
    /// </summary>
    public async Task InstallAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var release = Latest ?? throw new InvalidOperationException("Check for updates first.");
        if (release.DownloadUrl == null)
            throw new InvalidOperationException($"Release {release.Tag} has no {AssetName} download.");

        SetState(UpdateState.Downloading);
        try
        {
            var staging = Path.Combine(Path.GetTempPath(), $"JustWatch-update-{release.Version}");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            var zipPath = Path.Combine(staging, AssetName);

            using (var response = await _http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? release.DownloadSize;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(zipPath);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total > 0) progress?.Report((double)read / total);
                }
            }

            var payload = Path.Combine(staging, "app");
            ZipFile.ExtractToDirectory(zipPath, payload);
            if (!File.Exists(Path.Combine(payload, "JustWatch.exe")))
                throw new InvalidOperationException("The downloaded update does not contain JustWatch.exe.");

            var script = Path.Combine(staging, "apply-update.ps1");
            File.WriteAllText(script, ApplyScript, new UTF8Encoding(true));
            var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList =
                {
                    "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                    "-ProcessId", Environment.ProcessId.ToString(),
                    "-Source", payload,
                    "-Target", installDir,
                },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            SetState(UpdateState.Failed);
            throw;
        }
    }

    private static UpdateInfo? ParseRelease(JsonElement root)
    {
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version)) return null;
        string? download = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                download = asset.GetProperty("browser_download_url").GetString();
                size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            }
        }
        return new UpdateInfo(
            version,
            tag,
            root.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } n ? n : tag,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            root.GetProperty("html_url").GetString() ?? "",
            download,
            size);
    }

    public static bool TryParseVersion(string text, out Version version)
    {
        var trimmed = text.Trim().TrimStart('v', 'V');
        var cut = trimmed.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) trimmed = trimmed[..cut];
        if (Version.TryParse(trimmed, out var parsed))
        {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    private static Version ReadCurrentVersion()
    {
        var info = typeof(UpdateService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (info != null && TryParseVersion(info, out var version)) return version;
        var v = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    private void SetState(UpdateState state)
    {
        State = state;
        if (state != UpdateState.Failed) Error = null;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        _loopCts?.Cancel();
        _http.Dispose();
    }

    // Waits for JustWatch to exit, mirrors the new build over the install folder
    // (keeping local config files) and starts it again.
    private const string ApplyScript = """
        param([int]$ProcessId, [string]$Source, [string]$Target)
        $ErrorActionPreference = 'SilentlyContinue'
        $log = Join-Path (Split-Path $Source) 'apply-update.log'
        Start-Transcript -Path $log -Force | Out-Null
        $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($proc) { $proc.WaitForExit(60000) | Out-Null }
        Get-Process JustWatch -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($Target, [StringComparison]::OrdinalIgnoreCase) } |
            Stop-Process -Force
        Start-Sleep -Milliseconds 800
        for ($i = 0; $i -lt 5; $i++) {
            robocopy $Source $Target /MIR /R:3 /W:1 /NFL /NDL /NJH /NJS /NP /XF config-source.txt justwatch-config.json | Out-Null
            if ($LASTEXITCODE -lt 8) { break }
            Start-Sleep -Seconds 2
        }
        Start-Process -FilePath (Join-Path $Target 'JustWatch.exe') -WorkingDirectory $Target
        Stop-Transcript | Out-Null
        """;
}
