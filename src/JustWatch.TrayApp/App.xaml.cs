using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using JustWatch.Core.Models;
using JustWatch.Core.Services;

namespace JustWatch.TrayApp;

public partial class App : Application
{
    public static readonly Mutex AppMutex = new(true, "JustWatch.TrayApp.SingleInstance");
    // A second launch (Start Menu shortcut, `JustWatch.exe --about`) signals the
    // running instance through these instead of starting another copy.
    private static readonly EventWaitHandle ShowWindowSignal = new(false, EventResetMode.AutoReset, "JustWatch.TrayApp.ShowWindow");
    private static readonly EventWaitHandle ShowAboutSignal = new(false, EventResetMode.AutoReset, "JustWatch.TrayApp.ShowAbout");

    private TaskbarIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private ProcessManager? _processManager;
    private TelegramService? _telegram;
    private ChatSession? _sharedChat;
    private NotificationCenter? _notifications;
    private CommandCatalog? _commands;
    private WebAgentServer? _webServer;
    private JustWatchConfig? _config;
    private TrayFlyoutWindow? _trayFlyout;
    private UpdateService? _updates;
    private AboutWindow? _aboutWindow;
    private Version? _announcedUpdate;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _ownsMutex = AppMutex.WaitOne(0, false);
        if (!_ownsMutex)
        {
            if (e.Args.Contains("--about")) ShowAboutSignal.Set();
            else if (!e.Args.Contains("--minimized")) ShowWindowSignal.Set();
            Shutdown();
            return;
        }

        _config = ConfigLoader.Load();
        _processManager = new ProcessManager(_config);
        _commands = new CommandCatalog(_config);
        _processManager.StatusChanged += OnProcessStatusChanged;

        // Single shared ChatSession for both UI and Telegram
        _sharedChat = new ChatSession(_config.Chat);

        // Resume any tasks left incomplete if the app was restarted mid-work.
        _sharedChat.RecoverIncompleteTasks();

        // Created after RecoverIncompleteTasks so the journal replay of old task
        // states doesn't fire a burst of stale notifications.
        _notifications = new NotificationCenter(_config.Notifications);
        _notifications.Watch(_processManager);
        _notifications.Watch(_sharedChat);
        _notifications.NotificationAdded += OnNotificationAdded;

        _trayIcon = new TaskbarIcon
        {
            IconSource = TrayIconGenerator.Generate(_config.Processes, _processManager.GetStatus),
            ToolTipText = _config.ProjectName
        };

        _processManager.ConfigurationChanged += () => Dispatcher.InvokeAsync(() =>
        {
            UpdateTrayIcon();
            UpdateTrayTooltip();
            _trayFlyout?.RefreshServices();
        });
        _trayIcon.ContextMenu = BuildContextMenu();
        _trayIcon.TrayLeftMouseUp += (_, _) => ToggleTrayFlyout();
        _trayIcon.TrayMouseDoubleClick += (_, _) => ShowMainWindow();

        _processManager.StartAll();

        // Built before the web server so POST /api/telegram/messages can relay status
        // updates to the configured groups even when the conversational bot is off.
        _telegram = new TelegramService(_config.Telegram, _config.Chat.FfmpegPath);
        _telegram.Log += log => System.Diagnostics.Debug.WriteLine($"[Telegram] {log}");

        if (_config.Web.Enabled)
            StartWebServer();

        if (_config.Telegram.Enabled)
            StartTelegram();

        _updates = new UpdateService(_config.Updates);
        _updates.StateChanged += OnUpdateStateChanged;
        _updates.StartBackgroundChecks();

        if (e.Args.Contains("--about"))
            ShowAbout();
        else if (!e.Args.Contains("--minimized"))
            ShowMainWindow();

        ListenForActivation();
    }

    private void ListenForActivation()
    {
        var thread = new Thread(() =>
        {
            var signals = new WaitHandle[] { ShowWindowSignal, ShowAboutSignal };
            while (true)
            {
                var which = WaitHandle.WaitAny(signals);
                Dispatcher.InvokeAsync(which == 0 ? ShowMainWindow : ShowAbout);
            }
        }) { IsBackground = true, Name = "JustWatch activation" };
        thread.Start();
    }

    private void OnUpdateStateChanged()
    {
        var latest = _updates?.Latest;
        if (_updates?.UpdateAvailable != true || latest == null || latest.Version == _announcedUpdate) return;
        _announcedUpdate = latest.Version;
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                _trayIcon?.ShowBalloonTip("JustWatch update available",
                    $"{latest.Tag} is ready. Open About to install it.", BalloonIcon.Info);
            }
            catch { }
        });
    }

    private void ShowAbout()
    {
        if (_updates == null) return;
        if (_aboutWindow is { IsLoaded: true })
        {
            _aboutWindow.Activate();
            return;
        }
        _aboutWindow = new AboutWindow(_updates, _config?.ConfigPath,
            _webServer?.BoundUrl ?? $"http://localhost:{_config!.Web.Port}/", ExitAsync);
        _aboutWindow.Show();
        _aboutWindow.Activate();
    }

    private void StartWebServer()
    {
        try
        {
            _webServer = new WebAgentServer(_sharedChat!, _config!, _processManager, _notifications, _commands, _telegram);
            _webServer.Log += log => System.Diagnostics.Debug.WriteLine($"[Web] {log}");
            _webServer.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Web] Failed to start: {ex.Message}");
        }
    }

    private void StartTelegram()
    {
        if (_telegram is null) return;

        // Telegram sends convo messages + videos
        _sharedChat!.ConvoMessage += (text, type, filePath) =>
        {
            if (type == "video" && !string.IsNullOrEmpty(filePath))
                _ = _telegram.SendVideoAsync(filePath, text);
            else
                _ = _telegram.SendTextAsync(text);
        };

        // Incoming Telegram messages go through the shared ChatSession
        _telegram.MessageReceived += msg =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_sharedChat.IsProcessing)
                        _sharedChat.SendNewTask(msg, _config!.ProjectPath);
                    else
                        await _sharedChat.SendAsync(msg, _config!.ProjectPath);
                }
                catch (Exception ex)
                {
                    await _telegram.SendTextAsync($"Error: {ex.Message}");
                }
            });
        };

        _ = _telegram.StartPollingAsync(CancellationToken.None);
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var showItem = new MenuItem { Header = "Show Window", FontWeight = FontWeights.Bold };
        showItem.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(showItem);

        var servicesItem = new MenuItem { Header = "Settings & tray" };
        var projectsItem = new MenuItem { Header = "Project Library" };
        projectsItem.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            (_webServer?.BoundUrl ?? $"http://localhost:{_config!.Web.Port}/").TrimEnd('/') + "/projects") { UseShellExecute = true });
        menu.Items.Add(projectsItem);
        servicesItem.Click += (_, _) => OpenServices();
        menu.Items.Add(servicesItem);

        var webItem = new MenuItem { Header = "Open Web UI" };
        webItem.Click += (_, _) => OpenWebUi();
        menu.Items.Add(webItem);

        menu.Items.Add(new Separator());

        var dashboardItem = new MenuItem { Header = "Dashboard" };
        dashboardItem.Click += (_, _) => ShowTrayFlyout();
        menu.Items.Add(dashboardItem);

        var aboutItem = new MenuItem { Header = "About & updates" };
        aboutItem.Click += (_, _) => ShowAbout();
        menu.Items.Add(aboutItem);

        menu.Items.Add(new Separator());
        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += async (_, _) => await ExitAsync();
        menu.Items.Add(exitItem);

        return menu;
    }

    private void ToggleTrayFlyout()
    {
        if (_trayFlyout?.IsVisible == true)
        {
            _trayFlyout.Dismiss();
            return;
        }

        if (_trayFlyout?.IsWithinTrayReopenGuard == true) return;
        ShowTrayFlyout();
    }

    private void ShowTrayFlyout()
    {
        if (_processManager == null) return;
        _trayFlyout ??= new TrayFlyoutWindow(
            _processManager,
            _notifications,
            ShowMainWindow,
            OpenWebUi,
            ExitAsync,
            _updates,
            ShowAbout);
        _trayFlyout.OpenFromTray();
    }

    private void OnNotificationAdded(Notification n)
    {
        // Balloons only for the levels worth interrupting the desktop over;
        // info-level notifications still show as toasts in the window and web UI.
        if (_config?.Notifications.TrayBalloons != true || n.Level == "info") return;
        Dispatcher.InvokeAsync(() =>
        {
            var icon = n.Level switch
            {
                "error" => BalloonIcon.Error,
                "warn" => BalloonIcon.Warning,
                _ => BalloonIcon.Info
            };
            try
            {
                _trayIcon?.ShowBalloonTip(n.Title,
                    string.IsNullOrEmpty(n.Message) ? n.Title : n.Message, icon);
            }
            catch { }
        });
    }

    private void OnProcessStatusChanged(string id, ProcessStatus status)
    {
        Dispatcher.InvokeAsync(() =>
        {
            UpdateTrayIcon();
            UpdateTrayTooltip();
            _trayFlyout?.UpdateProcessStatus(id, status);
        });
    }

    private void UpdateTrayIcon()
    {
        if (_trayIcon == null || _processManager == null) return;
        _trayIcon.IconSource = TrayIconGenerator.Generate(
            _processManager.GetProcessConfigs(), _processManager.GetStatus);
    }

    private void UpdateTrayTooltip()
    {
        if (_trayIcon == null || _processManager == null) return;
        var configs = _processManager.GetProcessConfigs();
        var running = configs.Count(p => _processManager.GetStatus(p.Id) == ProcessStatus.Running);
        _trayIcon.ToolTipText = $"{_config!.ProjectName} - {running}/{configs.Count} running";
    }

    private void OpenServices()
    {
        var url = (_webServer?.BoundUrl ?? $"http://localhost:{_config!.Web.Port}/").TrimEnd('/') + "/services";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void OpenWebUi()
    {
        var url = _webServer?.BoundUrl ?? $"http://localhost:{_config!.Web.Port}/";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Web] Failed to open {url}: {ex.Message}");
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow == null || !_mainWindow.IsLoaded)
        {
            _mainWindow = new MainWindow(_processManager!, _sharedChat!, _commands!, _webServer?.BoundUrl, _notifications, ShowAbout);
            _mainWindow.Show();
        }
        else
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }
    }

    private async Task ExitAsync()
    {
        await _processManager!.StopAllAsync();
        _trayIcon?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayFlyout?.Close();
        _trayIcon?.Dispose();
        _webServer?.Dispose();
        _telegram?.Dispose();
        _updates?.Dispose();
        _sharedChat?.Dispose();
        _processManager?.Dispose();
        if (_ownsMutex) AppMutex.ReleaseMutex();
        base.OnExit(e);
    }
}
