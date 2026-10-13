using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using JustWatch.Core.Services;

namespace JustWatch.TrayApp;

public partial class AboutWindow : Window
{
    private readonly UpdateService _updates;
    private readonly string? _configPath;
    private readonly Func<Task> _exitForUpdateAsync;
    private readonly string _webUrl;
    private double? _downloadProgress;

    public AboutWindow(UpdateService updates, string? configPath, string webUrl, Func<Task> exitForUpdateAsync)
    {
        InitializeComponent();
        _webUrl = webUrl.TrimEnd('/');
        _updates = updates;
        _configPath = configPath;
        _exitForUpdateAsync = exitForUpdateAsync;

        var version = $"Version {UpdateService.CurrentVersion.ToString(3)}";
        VersionText.Text = UpdateService.IsDevelopmentBuild ? $"{version} · development build" : version;

        _updates.StateChanged += OnUpdateStateChanged;
        Closed += (_, _) => _updates.StateChanged -= OnUpdateStateChanged;
        Render();

        // Opening About is a good moment to refresh a stale check.
        if (_updates.State is UpdateState.Idle or UpdateState.Failed
            || _updates.LastChecked < DateTimeOffset.Now.AddMinutes(-10))
            _ = _updates.CheckAsync();
    }

    private void OnUpdateStateChanged() => Dispatcher.InvokeAsync(Render);

    private void Render()
    {
        var latest = _updates.Latest;
        UpdateButton.IsEnabled = true;
        UpdateButton.Visibility = Visibility.Visible;
        UpdateDot.Fill = Brush("TertiaryTextBrush");

        switch (_updates.State)
        {
            case UpdateState.Checking:
                UpdateTitle.Text = "Checking for updates…";
                UpdateDetail.Text = $"github.com/{_updates.Repository}";
                UpdateButton.Content = "Checking…";
                UpdateButton.IsEnabled = false;
                break;
            case UpdateState.Downloading:
                UpdateTitle.Text = $"Downloading {latest?.Tag}…";
                UpdateDetail.Text = _downloadProgress is { } p ? $"{p:P0}" : "Starting download";
                UpdateButton.Content = "Updating…";
                UpdateButton.IsEnabled = false;
                UpdateDot.Fill = Brush("AccentBrush");
                break;
            case UpdateState.Available when latest != null:
                UpdateTitle.Text = $"{latest.Tag} is available";
                UpdateDetail.Text = UpdateService.IsDevelopmentBuild
                    ? "Development build: pull and run bun run update."
                    : latest.DownloadUrl == null
                        ? "This release has no Windows download yet."
                        : $"You have {UpdateService.CurrentVersion.ToString(3)}. JustWatch restarts to finish.";
                UpdateButton.Content = UpdateService.IsDevelopmentBuild || latest.DownloadUrl == null ? "View release" : "Update now";
                UpdateDot.Fill = Brush("AccentBrush");
                break;
            case UpdateState.UpToDate:
                UpdateTitle.Text = "You're up to date";
                UpdateDetail.Text = latest == null
                    ? "No releases published yet."
                    : $"{latest.Tag} is the latest release · checked {_updates.LastChecked:t}";
                UpdateButton.Content = "Check again";
                UpdateDot.Fill = Brush("SuccessTextBrush");
                break;
            case UpdateState.Failed:
                UpdateTitle.Text = "Couldn't check for updates";
                UpdateDetail.Text = _updates.Error ?? "Unknown error";
                UpdateButton.Content = "Try again";
                UpdateDot.Fill = Brush("WarningTextBrush");
                break;
            default:
                UpdateTitle.Text = "Updates";
                UpdateDetail.Text = $"Releases from github.com/{_updates.Repository}";
                UpdateButton.Content = "Check now";
                break;
        }
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        var latest = _updates.Latest;
        if (_updates.State != UpdateState.Available || latest == null)
        {
            await _updates.CheckAsync();
            return;
        }

        if (UpdateService.IsDevelopmentBuild || latest.DownloadUrl == null)
        {
            Open(latest.PageUrl);
            return;
        }

        try
        {
            _downloadProgress = null;
            var progress = new Progress<double>(p => { _downloadProgress = p; Render(); });
            await _updates.InstallAsync(progress);
            await _exitForUpdateAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The update could not be installed.\n\n{ex.Message}", "JustWatch",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnGitHub(object sender, RoutedEventArgs e) => Open(_updates.RepositoryUrl);

    private void OnReleaseNotes(object sender, RoutedEventArgs e) => Open(_updates.Latest?.PageUrl is { Length: > 0 } url ? url : _updates.ReleasesUrl);

    private void OnReportIssue(object sender, RoutedEventArgs e) => Open($"{_updates.RepositoryUrl}/issues/new");

    private void OnAgentDocs(object sender, RoutedEventArgs e) => Open(_webUrl + "/llms.txt");

    private void OnConfigFolder(object sender, RoutedEventArgs e)
    {
        var path = _configPath ?? ConfigLoader.GetDefaultConfigPath();
        if (File.Exists(path))
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
            Open(dir);
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    private static void Open(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[About] Failed to open {target}: {ex.Message}");
        }
    }
}
