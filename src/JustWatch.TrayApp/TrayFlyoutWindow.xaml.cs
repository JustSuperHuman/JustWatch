using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using JustWatch.Core.Models;
using JustWatch.Core.Services;
using IOPath = System.IO.Path;

namespace JustWatch.TrayApp;

public partial class TrayFlyoutWindow : Window
{
    private const string PlayGlyph = "\uE768";
    private const string StopGlyph = "\uE71A";
    private const string RestartGlyph = "\uE72C";
    private const string MoreGlyph = "\uE712";
    private const string OpenGlyph = "\uE8A7";
    // Transparent margin around the shell that holds its drop shadow.
    private const double ShadowInset = 16;
    private const double ScreenGap = 12;
    private static readonly JsonSerializerOptions StateJsonOptions = new();

    private readonly ProcessManager _processManager;
    private readonly NotificationCenter? _notifications;
    private readonly Action _showMainWindow;
    private readonly Action _openWebUi;
    private readonly Func<Task> _exitAsync;
    private readonly string _statePath;
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<string, DevServerRow> _devServerRows = new();
    private readonly Dictionary<string, EmulatorRow> _emulatorRows = new();
    private List<EmulatorSnapshot> _lastEmulatorSnapshots = [];
    private DateTimeOffset _lastDeactivatedAt = DateTimeOffset.MinValue;
    private bool _dashboardRefreshInFlight;
    private bool _dashboardRefreshPending;

    public TrayFlyoutWindow(
        ProcessManager processManager,
        NotificationCenter? notifications,
        Action showMainWindow,
        Action openWebUi,
        Func<Task> exitAsync,
        UpdateService? updates = null,
        Action? showAbout = null,
        OtherWebServices? otherWebServices = null)
    {
        InitializeComponent();

        _processManager = processManager;
        _notifications = notifications;
        _showMainWindow = showMainWindow;
        _openWebUi = openWebUi;
        _exitAsync = exitAsync;
        _otherServices = otherWebServices ?? new OtherWebServices();
        _statePath = GetStatePath();
        _lastEmulatorSnapshots = LoadEmulatorState();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (_, _) =>
            await Task.WhenAll(RefreshDashboardAsync(), RefreshOtherAsync());

        EmulatorSection.Visibility = _processManager.Config.Android.Enabled ? Visibility.Visible : Visibility.Collapsed;
        BuildDevServerRows();
        WireOtherActions();
        WireHeaderActions();
        WireFooterActions();
        ManageServicesButton.Click += (_, _) => OpenUrl($"http://localhost:{_processManager.Config.Web.Port}/services");
        ProjectLibraryButton.Click += (_, _) => OpenUrl($"http://localhost:{_processManager.Config.Web.Port}/projects");
        WireAboutActions(updates, showAbout);
    }

    private void WireAboutActions(UpdateService? updates, Action? showAbout)
    {
        void OpenAbout() { Dismiss(); showAbout?.Invoke(); }
        AboutButton.Visibility = showAbout == null ? Visibility.Collapsed : Visibility.Visible;
        AboutButton.Click += (_, _) => OpenAbout();
        UpdatePillButton.Click += (_, _) => OpenAbout();
        if (updates == null) return;

        void RenderUpdatePill()
        {
            var available = updates.UpdateAvailable;
            UpdatePillButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            UpdatePillButton.Content = available ? $"Update {updates.Latest!.Tag}" : null;
        }
        updates.StateChanged += () => Dispatcher.InvokeAsync(RenderUpdatePill);
        RenderUpdatePill();
    }

    public bool IsWithinTrayReopenGuard =>
        DateTimeOffset.Now - _lastDeactivatedAt < TimeSpan.FromMilliseconds(250);

    public void OpenFromTray()
    {
        BuildDevServerRows();
        RenderDevServerStatuses();
        RenderOtherServices();
        if (_lastEmulatorSnapshots.Count > 0)
            ApplyEmulatorSnapshots(_lastEmulatorSnapshots);
        else
            RenderCachedEmulators();
        MaxHeight = Math.Min(712, SystemParameters.WorkArea.Height - 2 * (ScreenGap - ShadowInset));
        Measure(new Size(Width, MaxHeight));
        PositionNearTaskbar(DesiredSize.Height);
        Show();
        UpdateLayout();
        PositionNearTaskbar();
        Activate();
        PlayEntrance();
        _refreshTimer.Start();
        _ = RefreshDashboardAsync();
        _ = RefreshOtherAsync();
    }

    public void Dismiss()
    {
        _lastDeactivatedAt = DateTimeOffset.Now;
        _refreshTimer.Stop();
        Hide();
    }

    public void UpdateProcessStatus(string id, ProcessStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => UpdateProcessStatus(id, status));
            return;
        }

        var service = _processManager.GetServiceState(id);
        var visible = service != null && ServiceVisibility.IsVisibleInTray(service);
        if (visible != _devServerRows.ContainsKey(id))
        {
            BuildDevServerRows();
            if (IsVisible) { UpdateLayout(); PositionNearTaskbar(); }
        }
        if (service != null && _devServerRows.TryGetValue(id, out var row))
            ApplyDevServerStatus(row, service.Status);
        RenderSummary();
    }

    public void RefreshServices()
    {
        BuildDevServerRows();
        RenderOtherServices();
    }

    private void PlayEntrance()
    {
        var duration = TimeSpan.FromMilliseconds(170);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Shell.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        ShellOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration) { EasingFunction = ease });
    }

    private void BuildDevServerRows()
    {
        DevServerRowsPanel.Children.Clear();
        _devServerRows.Clear();

        var projects = _processManager.GetServiceStates(includeStopped: true).Where(ServiceVisibility.IsVisibleInTray)
            .GroupBy(p => p.Project, StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            DevServerRowsPanel.Children.Add(new TextBlock
            {
                Text = project.Key.ToUpperInvariant(),
                Foreground = TertiaryBrush(),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4, 10, 0, 6)
            });
            var rows = new StackPanel();
            foreach (var process in project)
            {
                var row = new DevServerRow(process.Id, process.Name, process.Url);
                rows.Children.Add(CreateDevServerRow(row));
                _devServerRows[process.Id] = row;
                ApplyDevServerStatus(row, process.Status);
            }
            DevServerRowsPanel.Children.Add(new Border { Style = (Style)FindResource("CardStyle"), Child = rows });
        }
        if (_devServerRows.Count == 0)
        {
            DevServerRowsPanel.Children.Add(new Border
            {
                Style = (Style)FindResource("CardStyle"),
                Margin = new Thickness(0, 8, 0, 0),
                Child = CreateEmptyState(
                    "No services pinned to the tray",
                    "Start a service and it appears here, or set one to Always in Settings & tray.")
            });
        }
        RenderSummary();
    }

    private FrameworkElement CreateDevServerRow(DevServerRow row)
    {
        var grid = CreateRowGrid();

        row.Dot = CreateStatusDot();
        // Name keeps its full width; a long host is what gets trimmed.
        var namePanel = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 8, 0),
            Background = Brushes.Transparent
        };
        namePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        namePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.NameText = new TextBlock
        {
            Text = row.Name,
            Foreground = PrimaryBrush(),
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center
        };
        namePanel.Children.Add(row.NameText);

        if (row.Url is { } url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var hostText = new TextBlock
                {
                    Text = uri.Authority,
                    Foreground = TertiaryBrush(),
                    FontSize = 11,
                    Margin = new Thickness(8, 1, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(hostText, 1);
                namePanel.Children.Add(hostText);
            }

            namePanel.Cursor = Cursors.Hand;
            namePanel.ToolTip = $"Open {url}";
            namePanel.MouseEnter += (_, _) => row.NameText.Foreground = AccentBrush();
            namePanel.MouseLeave += (_, _) => row.NameText.Foreground = row.NameBrush;
            namePanel.MouseLeftButtonUp += (_, _) => OpenUrl(url);
        }

        row.StatusText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(8, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        if (row.Url is { } serviceUrl)
        {
            var openButton = CreateIconButton(OpenGlyph, $"Open {row.Name} in the browser");
            System.Windows.Automation.AutomationProperties.SetName(openButton, $"Open {row.Name}");
            openButton.Click += (_, _) => OpenUrl(serviceUrl);
            actions.Children.Add(openButton);
        }

        row.ToggleButton = CreateIconButton(PlayGlyph, "Start");
        row.ToggleButton.Click += async (_, _) =>
        {
            if (IsStopCapable(_processManager.GetStatus(row.Id)))
            {
                await RunActionAsync(
                    () => _processManager.StopAsync(row.Id),
                    $"stop {row.Name}",
                    row.Buttons);
            }
            else
            {
                await RunActionAsync(
                    () =>
                    {
                        _processManager.Start(row.Id);
                        return Task.CompletedTask;
                    },
                    $"start {row.Name}",
                    row.Buttons);
            }
        };

        row.RestartButton = CreateIconButton(RestartGlyph, "Restart");
        row.RestartButton.Click += async (_, _) => await RunActionAsync(
            () => _processManager.RestartAsync(row.Id),
            $"restart {row.Name}",
            row.Buttons);

        actions.Children.Add(row.ToggleButton);
        actions.Children.Add(row.RestartButton);

        Grid.SetColumn(row.Dot, 0);
        Grid.SetColumn(namePanel, 1);
        Grid.SetColumn(row.StatusText, 2);
        Grid.SetColumn(actions, 3);
        grid.Children.Add(row.Dot);
        grid.Children.Add(namePanel);
        grid.Children.Add(row.StatusText);
        grid.Children.Add(actions);

        return CreateRowShell(grid);
    }

    private void WireHeaderActions()
    {
        StartAllProcessesButton.Click += async (_, _) => await RunActionAsync(
            () =>
            {
                _processManager.StartAll();
                return Task.CompletedTask;
            },
            "start all services",
            AllDevServerButtons());

        StopAllProcessesButton.Click += async (_, _) => await RunActionAsync(
            () => _processManager.StopAllAsync(),
            "stop all services",
            AllDevServerButtons());

        RestartAllProcessesButton.Click += async (_, _) => await RunActionAsync(
            () => _processManager.RestartAllAsync(),
            "restart all services",
            AllDevServerButtons());

        StartAllEmulatorsButton.Click += async (_, _) => await RunActionAsync(
            () => _processManager.StartAllAndroidEmulatorsAsync(),
            "start all Android emulators",
            AllEmulatorButtons());

        EmulatorMoreButton.ContextMenu = BuildEmulatorActionsMenu();
        EmulatorMoreButton.Click += (_, _) =>
        {
            if (EmulatorMoreButton.ContextMenu == null) return;
            EmulatorMoreButton.ContextMenu.PlacementTarget = EmulatorMoreButton;
            EmulatorMoreButton.ContextMenu.IsOpen = true;
        };
    }

    private void WireFooterActions()
    {
        ShowWindowButton.Click += async (_, _) => await RunActionAsync(
            () =>
            {
                _showMainWindow();
                Dismiss();
                return Task.CompletedTask;
            },
            "show main window",
            [ShowWindowButton],
            refreshDashboard: false);

        OpenWebUiButton.Click += async (_, _) => await RunActionAsync(
            () =>
            {
                _openWebUi();
                return Task.CompletedTask;
            },
            "open web UI",
            [OpenWebUiButton],
            refreshDashboard: false);

        ExitButton.Click += async (_, _) => await RunActionAsync(
            _exitAsync,
            "exit JustWatch",
            [ExitButton],
            refreshDashboard: false);
    }

    private ContextMenu BuildEmulatorActionsMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem("Restart all emulators", async () => await RunActionAsync(
            () => _processManager.RestartAllAndroidEmulatorsAsync(),
            "restart all Android emulators",
            AllEmulatorButtons())));
        menu.Items.Add(CreateMenuItem("Restart app on running emulators", async () => await RunActionAsync(
            () => _processManager.RestartJustGainsAppAsync(),
            "restart JustGains app on all running Android emulators",
            AllEmulatorButtons())));
        menu.Items.Add(CreateMenuItem("Build and install app on all", async () => await RunActionAsync(
            () => _processManager.InstallJustGainsAppOnAllAndroidEmulatorsAsync(),
            "build and install JustGains app on all Android emulators",
            AllEmulatorButtons())));
        menu.Items.Add(CreateMenuItem("Scorched update", async () => await RunActionAsync(
            () => _processManager.ScorchedAndroidUpdateAsync(),
            "run scorched Android update",
            AllEmulatorButtons())));
        menu.Items.Add(CreateMenuItem("Clone core emulator", async () => await RunActionAsync(
            () => _processManager.CloneAndroidEmulatorAsync(),
            "clone Android emulator",
            AllEmulatorButtons())));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Refresh list", async () => await RunActionAsync(
            () => RefreshDashboardAsync(force: true),
            "refresh Android dashboard",
            AllEmulatorButtons(),
            refreshDashboard: false)));
        return menu;
    }

    private void RenderDevServerStatuses()
    {
        foreach (var row in _devServerRows.Values)
            ApplyDevServerStatus(row, _processManager.GetStatus(row.Id));
        RenderSummary();
    }

    private void RenderCachedEmulators()
    {
        // MuMu discovery is a subprocess: let the async dashboard refresh do it
        // after the flyout opens, including surfacing a missing installation.
        ApplyEmulatorSnapshots([]);
        ShowEmulatorNotice("Looking for MuMu instances…", isWarning: false);
    }

    private async Task RefreshDashboardAsync(bool force = false)
    {
        if (!_processManager.Config.Android.Enabled) return;
        if (_dashboardRefreshInFlight)
        {
            // A forced refresh (post-action) must not be lost to an in-flight
            // timer tick — its result would predate the action's effect.
            if (force) _dashboardRefreshPending = true;
            return;
        }

        _dashboardRefreshInFlight = true;
        EmulatorBusyText.Visibility = Visibility.Visible;

        try
        {
            var dashboard = await _processManager.GetAndroidDashboardAsync(CancellationToken.None);
            var emulators = dashboard.Emulators
                .Select(e => new EmulatorSnapshot(
                    e.Id,
                    e.Name,
                    e.IsCore,
                    e.IsClone,
                    e.IsRunning,
                    e.CpuPercent,
                    e.MemoryMb,
                    e.IdleTime,
                    e.IdleSeconds))
                .ToList();

            _lastEmulatorSnapshots = emulators;
            SaveEmulatorStateAsync(emulators);
            ApplyEmulatorSnapshots(emulators);
            if (string.IsNullOrWhiteSpace(dashboard.Error))
            {
                EmulatorWarningText.Visibility = Visibility.Collapsed;
            }
            else
            {
                ShowEmulatorNotice(dashboard.Error, isWarning: true);
            }
        }
        catch (Exception ex)
        {
            ShowEmulatorNotice(ex.Message, isWarning: true);
            System.Diagnostics.Debug.WriteLine($"[TrayFlyout] Failed to refresh Android dashboard: {ex.Message}");
        }
        finally
        {
            EmulatorBusyText.Visibility = Visibility.Collapsed;
            _dashboardRefreshInFlight = false;
            if (_dashboardRefreshPending)
            {
                _dashboardRefreshPending = false;
                _ = Dispatcher.InvokeAsync(() => RefreshDashboardAsync());
            }
            if (IsVisible)
                PositionNearTaskbar();
        }
    }

    private void ShowEmulatorNotice(string text, bool isWarning)
    {
        EmulatorWarningText.Text = text;
        EmulatorWarningText.Foreground = isWarning ? (Brush)FindResource("WarningTextBrush") : SecondaryBrush();
        EmulatorWarningText.Visibility = Visibility.Visible;
    }

    private void ApplyEmulatorSnapshots(IReadOnlyList<EmulatorSnapshot> snapshots)
    {
        var currentIds = _emulatorRows.Keys.OrderBy(id => id).ToArray();
        var newIds = snapshots.Select(e => e.Id).OrderBy(id => id).ToArray();
        if (!currentIds.SequenceEqual(newIds))
            RebuildEmulatorRows(snapshots);
        else
            UpdateEmulatorRows(snapshots);
    }

    private void RebuildEmulatorRows(IReadOnlyList<EmulatorSnapshot> snapshots)
    {
        EmulatorRowsPanel.Children.Clear();
        _emulatorRows.Clear();

        if (snapshots.Count == 0)
        {
            EmulatorRowsPanel.Children.Add(CreateEmptyState(
                "No JustGains emulators yet",
                "Instances you create in MuMu Player show up here, ready to launch."));
            return;
        }

        foreach (var snapshot in snapshots)
        {
            var row = new EmulatorRow(snapshot.Id);
            EmulatorRowsPanel.Children.Add(CreateEmulatorRow(row));
            _emulatorRows[snapshot.Id] = row;
            ApplyEmulatorSnapshot(row, snapshot);
        }
    }

    private FrameworkElement CreateEmulatorRow(EmulatorRow row)
    {
        var grid = CreateRowGrid();

        var infoPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 6)
        };

        var namePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Dot = CreateStatusDot();
        row.NameText = new TextBlock
        {
            Foreground = PrimaryBrush(),
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.CoreBadge = new Border
        {
            BorderBrush = (Brush)FindResource("HairlineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "The protected base instance that clones are made from",
            Child = new TextBlock
            {
                Text = "Core",
                Foreground = SecondaryBrush(),
                FontSize = 10.5,
                FontWeight = FontWeights.Medium
            }
        };

        namePanel.Children.Add(row.NameText);
        namePanel.Children.Add(row.CoreBadge);
        infoPanel.Children.Add(namePanel);

        row.StatsText = new TextBlock
        {
            Foreground = TertiaryBrush(),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            Visibility = Visibility.Collapsed
        };
        infoPanel.Children.Add(row.StatsText);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        row.ToggleButton = CreateIconButton(PlayGlyph, "Launch");
        row.ToggleButton.Click += async (_, _) =>
        {
            var labelName = string.IsNullOrWhiteSpace(row.NameText.Text) ? row.Id : row.NameText.Text;
            if (row.IsRunning)
            {
                await RunActionAsync(
                    () => _processManager.StopAndroidEmulatorAsync(row.Id),
                    $"stop Android emulator '{labelName}'",
                    row.Buttons);
            }
            else
            {
                await RunActionAsync(
                    () => _processManager.LaunchAndroidEmulatorAsync(row.Id),
                    $"launch Android emulator '{labelName}'",
                    row.Buttons);
            }
        };

        row.RestartAppButton = CreateIconButton(RestartGlyph, "Restart JustGains app");
        row.RestartAppButton.Click += async (_, _) =>
        {
            var labelName = string.IsNullOrWhiteSpace(row.NameText.Text) ? row.Id : row.NameText.Text;
            await RunActionAsync(
                () => _processManager.RestartJustGainsAppAsync(row.Id),
                $"restart JustGains app on Android emulator '{labelName}'",
                row.Buttons);
        };

        row.MoreButton = CreateIconButton(MoreGlyph, "More actions");
        row.MoreButton.Click += (_, _) =>
        {
            row.MoreButton.ContextMenu = BuildEmulatorRowMenu(row);
            row.MoreButton.ContextMenu.PlacementTarget = row.MoreButton;
            row.MoreButton.ContextMenu.IsOpen = true;
        };

        actions.Children.Add(row.ToggleButton);
        actions.Children.Add(row.RestartAppButton);
        actions.Children.Add(row.MoreButton);

        Grid.SetColumn(row.Dot, 0);
        Grid.SetColumn(infoPanel, 1);
        Grid.SetColumn(actions, 3);
        grid.Children.Add(row.Dot);
        grid.Children.Add(infoPanel);
        grid.Children.Add(actions);

        return CreateRowShell(grid);
    }

    private ContextMenu BuildEmulatorRowMenu(EmulatorRow row)
    {
        var menu = new ContextMenu();
        if (row.IsCore)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "The core emulator is protected",
                IsEnabled = false
            });
        }
        else
        {
            var labelName = string.IsNullOrWhiteSpace(row.NameText.Text) ? row.Id : row.NameText.Text;
            menu.Items.Add(CreateMenuItem("Delete this clone", async () => await RunActionAsync(
                () => _processManager.DeleteAndroidEmulatorAsync(row.Id),
                $"delete Android emulator clone '{labelName}'",
                row.Buttons)));
        }

        return menu;
    }

    private void UpdateEmulatorRows(IReadOnlyList<EmulatorSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            if (_emulatorRows.TryGetValue(snapshot.Id, out var row))
                ApplyEmulatorSnapshot(row, snapshot);
        }
    }

    private void ApplyEmulatorSnapshot(EmulatorRow row, EmulatorSnapshot snapshot)
    {
        row.IsRunning = snapshot.IsRunning;
        row.IsCore = snapshot.IsCore;
        row.IsClone = snapshot.IsClone;
        row.NameText.Text = snapshot.Name;
        row.NameText.Foreground = snapshot.IsRunning ? PrimaryBrush() : SecondaryBrush();
        row.Dot.Fill = StatusBrush(snapshot.IsRunning ? ProcessStatus.Running : ProcessStatus.Stopped);
        row.CoreBadge.Visibility = snapshot.IsCore ? Visibility.Visible : Visibility.Collapsed;
        row.ToggleButton.Content = snapshot.IsRunning ? StopGlyph : PlayGlyph;
        row.ToggleButton.ToolTip = snapshot.IsRunning ? "Stop emulator" : "Launch emulator";
        row.RestartAppButton.IsEnabled = snapshot.IsRunning;

        ApplyEmulatorStats(row, snapshot);
    }

    private void ApplyEmulatorStats(EmulatorRow row, EmulatorSnapshot snapshot)
    {
        row.StatsText.Inlines.Clear();
        if (!snapshot.IsRunning)
        {
            row.StatsText.Visibility = Visibility.Collapsed;
            return;
        }

        var parts = BuildEmulatorStats(snapshot);
        if (parts.Count == 0)
        {
            row.StatsText.Visibility = Visibility.Collapsed;
            return;
        }

        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
                row.StatsText.Inlines.Add(new Run(" · "));
            row.StatsText.Inlines.Add(new Run(parts[i].Text) { Foreground = parts[i].Brush });
        }

        row.StatsText.Visibility = Visibility.Visible;
    }

    private List<StatPart> BuildEmulatorStats(EmulatorSnapshot snapshot)
    {
        var parts = new List<StatPart>();
        var secondary = TertiaryBrush();
        if (snapshot.CpuPercent is { } cpu)
            parts.Add(new StatPart($"CPU {Math.Round(cpu):0}%", secondary));
        if (snapshot.MemoryMb is { } memory)
            parts.Add(new StatPart(FormatMemory(memory), secondary));
        if (snapshot.IdleSeconds is < 60)
            parts.Add(new StatPart("In use", StatusBrush(ProcessStatus.RecoveringWithAgent)));
        else if (!string.IsNullOrWhiteSpace(snapshot.IdleTime))
            parts.Add(new StatPart($"Idle {snapshot.IdleTime}", secondary));
        return parts;
    }

    private static string FormatMemory(long memoryMb) =>
        memoryMb >= 1024
            ? $"{memoryMb / 1024d:0.0} GB"
            : $"{memoryMb} MB";

    // Columns: status dot | name (fills) | status label | actions.
    private Grid CreateRowGrid()
    {
        var grid = new Grid
        {
            MinHeight = 36
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return grid;
    }

    private Border CreateRowShell(Grid content)
    {
        var shell = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 0, 4, 0),
            Child = content
        };
        shell.MouseEnter += (_, _) => shell.Background = (Brush)FindResource("RowHoverBrush");
        shell.MouseLeave += (_, _) => shell.Background = Brushes.Transparent;
        return shell;
    }

    private FrameworkElement CreateEmptyState(string title, string detail)
    {
        var panel = new StackPanel { Margin = new Thickness(10, 10, 10, 11) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = PrimaryBrush(),
            FontWeight = FontWeights.Medium
        });
        panel.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = SecondaryBrush(),
            FontSize = 11.5,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });
        return panel;
    }

    private Ellipse CreateStatusDot() =>
        new()
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

    private Button CreateIconButton(string glyph, string tooltip) =>
        new()
        {
            Content = glyph,
            ToolTip = tooltip,
            Style = (Style)FindResource("IconButtonStyle")
        };

    private MenuItem CreateMenuItem(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private void ApplyDevServerStatus(DevServerRow row, ProcessStatus status)
    {
        row.Dot.Fill = StatusBrush(status);
        var stopCapable = IsStopCapable(status);
        row.ToggleButton.Content = stopCapable ? StopGlyph : PlayGlyph;
        row.ToggleButton.ToolTip = stopCapable ? $"Stop {row.Name}" : $"Start {row.Name}";
        row.RestartButton.ToolTip = $"Restart {row.Name}";

        row.NameBrush = status == ProcessStatus.Stopped ? SecondaryBrush() : PrimaryBrush();
        row.NameText.Foreground = row.NameBrush;

        // Running and stopped are the dot's job; only the in-between and broken states get a word.
        var label = status switch
        {
            ProcessStatus.Starting => "Starting…",
            ProcessStatus.Stopping => "Stopping…",
            ProcessStatus.Crashed => "Crashed",
            ProcessStatus.RecoveringWithAgent => "Agent repairing",
            _ => null
        };
        row.StatusText.Text = label ?? string.Empty;
        row.StatusText.Foreground = status is ProcessStatus.Crashed or ProcessStatus.RecoveringWithAgent
            ? StatusBrush(status)
            : SecondaryBrush();
        row.StatusText.Visibility = label == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderSummary()
    {
        var statuses = _devServerRows.Values.Select(row => _processManager.GetStatus(row.Id)).ToArray();
        var total = statuses.Length;
        var running = statuses.Count(s => s == ProcessStatus.Running);
        var crashed = statuses.Count(s => s == ProcessStatus.Crashed);
        var recovering = statuses.Count(s => s == ProcessStatus.RecoveringWithAgent);
        var moving = statuses.Count(s => s is ProcessStatus.Starting or ProcessStatus.Stopping);

        string text;
        var tone = ProcessStatus.Running;
        if (total == 0)
        {
            text = "Nothing pinned to the tray yet";
            tone = ProcessStatus.Stopped;
        }
        else if (crashed > 0)
        {
            text = $"{Count(crashed)} crashed · {running} running";
            tone = ProcessStatus.Crashed;
        }
        else if (recovering > 0)
        {
            text = $"Agent is repairing {Count(recovering)} · {running} running";
            tone = ProcessStatus.RecoveringWithAgent;
        }
        else if (moving > 0)
        {
            text = $"{Count(moving)} changing state · {running} running";
            tone = ProcessStatus.Starting;
        }
        else if (running == 0)
        {
            text = total == 1 ? "Your service is stopped" : $"All {total} services are stopped";
            tone = ProcessStatus.Stopped;
        }
        else if (running == total)
        {
            text = total == 1 ? "Your service is up and running" : $"All {total} services are up and running";
        }
        else
        {
            text = $"{running} of {total} services running";
        }

        SummaryText.Text = text;
        SummaryDot.Fill = StatusBrush(tone);

        static string Count(int n) => n == 1 ? "1 service" : $"{n} services";
    }

    private static bool IsStopCapable(ProcessStatus status) =>
        status is ProcessStatus.Running or ProcessStatus.RecoveringWithAgent;

    private Brush StatusBrush(ProcessStatus status)
    {
        var color = status switch
        {
            ProcessStatus.Running => Color.FromRgb(0x4A, 0xDE, 0x80),
            ProcessStatus.Crashed => Color.FromRgb(0xEF, 0x44, 0x44),
            ProcessStatus.RecoveringWithAgent => Color.FromRgb(0xFB, 0xBF, 0x24),
            ProcessStatus.Starting or ProcessStatus.Stopping => Color.FromRgb(0xA1, 0xA1, 0xAA),
            _ => Color.FromRgb(0x52, 0x52, 0x5B)
        };
        return new SolidColorBrush(color);
    }

    private Brush PrimaryBrush() => (Brush)FindResource("PrimaryTextBrush");

    private Brush SecondaryBrush() => (Brush)FindResource("SecondaryTextBrush");

    private Brush TertiaryBrush() => (Brush)FindResource("TertiaryTextBrush");

    private Brush AccentBrush() => (Brush)FindResource("AccentBrush");

    private IEnumerable<Button> AllDevServerButtons() =>
        _devServerRows.Values.SelectMany(row => row.Buttons)
            .Concat([StartAllProcessesButton, StopAllProcessesButton, RestartAllProcessesButton]);

    private IEnumerable<Button> AllEmulatorButtons() =>
        _emulatorRows.Values.SelectMany(row => row.Buttons)
            .Concat([StartAllEmulatorsButton, EmulatorMoreButton]);

    private async Task RunActionAsync(
        Func<Task> action,
        string label,
        IEnumerable<Button>? buttons = null,
        bool refreshDashboard = true)
    {
        var buttonList = buttons?.Distinct().ToArray() ?? [];
        foreach (var button in buttonList)
            button.IsEnabled = false;

        try
        {
            await action();
            _notifications?.Notify("success", "Action complete", $"Finished: {label}.", "action");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayFlyout] Failed to {label}: {ex.Message}");
            _notifications?.Notify("error", "Action failed", $"Could not {label}: {ex.Message}", "action");
        }
        finally
        {
            foreach (var button in buttonList)
                button.IsEnabled = true;

            RenderDevServerStatuses();
            if (refreshDashboard)
                await RefreshDashboardAsync(force: true);
        }
    }

    private void PositionNearTaskbar(double? measuredHeight = null)
    {
        var area = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = measuredHeight.GetValueOrDefault(ActualHeight);
        if (height <= 0 || double.IsNaN(height))
            height = DesiredSize.Height > 0 ? DesiredSize.Height : MaxHeight;

        // The window is larger than the visible shell by the shadow inset on every side.
        var gap = ScreenGap - ShadowInset;
        Left = area.Right - width - gap;
        Top = Math.Max(area.Top + gap, area.Bottom - height - gap);
    }

    private static string GetStatePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return IOPath.Combine(root, "JustWatch", "tray-flyout-state.json");
    }

    private List<EmulatorSnapshot> LoadEmulatorState()
    {
        try
        {
            if (!File.Exists(_statePath)) return [];
            var json = File.ReadAllText(_statePath);
            return (JsonSerializer.Deserialize<List<EmulatorSnapshot>>(json, StateJsonOptions) ?? [])
                .Where(e => e.Id.StartsWith("mumu-", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayFlyout] Failed to load cached state: {ex.Message}");
            return [];
        }
    }

    private void SaveEmulatorStateAsync(IReadOnlyList<EmulatorSnapshot> snapshots)
    {
        var statePath = _statePath;
        var snapshotCopy = snapshots.ToList();
        _ = Task.Run(() =>
        {
            try
            {
                var directory = IOPath.GetDirectoryName(statePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                var json = JsonSerializer.Serialize(snapshotCopy, StateJsonOptions);
                File.WriteAllText(statePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TrayFlyout] Failed to save cached state: {ex.Message}");
            }
        });
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayFlyout] Failed to open {url}: {ex.Message}");
        }
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        Dismiss();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        Dismiss();
    }

    private sealed class DevServerRow(string id, string name, string? url)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string? Url { get; } = url;
        public Ellipse Dot { get; set; } = null!;
        public TextBlock NameText { get; set; } = null!;
        public Brush NameBrush { get; set; } = Brushes.White;
        public TextBlock StatusText { get; set; } = null!;
        public Button ToggleButton { get; set; } = null!;
        public Button RestartButton { get; set; } = null!;
        public IEnumerable<Button> Buttons => [ToggleButton, RestartButton];
    }

    private sealed class EmulatorRow(string id)
    {
        public string Id { get; } = id;
        public bool IsRunning { get; set; }
        public bool IsCore { get; set; }
        public bool IsClone { get; set; }
        public Ellipse Dot { get; set; } = null!;
        public TextBlock NameText { get; set; } = null!;
        public Border CoreBadge { get; set; } = null!;
        public TextBlock StatsText { get; set; } = null!;
        public Button ToggleButton { get; set; } = null!;
        public Button RestartAppButton { get; set; } = null!;
        public Button MoreButton { get; set; } = null!;
        public IEnumerable<Button> Buttons => [ToggleButton, RestartAppButton, MoreButton];
    }

    private sealed record StatPart(string Text, Brush Brush);

    private sealed record EmulatorSnapshot(
        string Id,
        string Name,
        bool IsCore,
        bool IsClone,
        bool IsRunning,
        double? CpuPercent,
        long? MemoryMb,
        string? IdleTime,
        double? IdleSeconds);
}
