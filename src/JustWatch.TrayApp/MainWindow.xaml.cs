using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using JustWatch.Core.Models;
using JustWatch.Core.Services;
using Microsoft.Web.WebView2.Core;

namespace JustWatch.TrayApp;

public partial class MainWindow : Window
{
    private readonly ProcessManager _pm;
    private readonly ChatSession _chat;
    private readonly Action? _showAbout;
    private readonly CommandCatalog _commands;
    private readonly string? _webUrl;
    private readonly NotificationCenter? _notifications;
    private readonly Dictionary<string, ToggleButton> _tabButtons = new();
    private readonly Dictionary<string, TextBlock> _statusIndicators = new();
    private readonly Dictionary<string, (System.Windows.Shapes.Ellipse Dot, TextBlock Port)> _navigationStatus = new();
    private string? _activeTabId;
    private bool _terminalReady;
    private bool _isChatActive;
    private bool _isEmulatorsActive;
    private bool _emuRefreshing;
    private DispatcherTimer? _emuTimer;

    private static readonly JsonSerializerOptions EmuJsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public MainWindow(ProcessManager pm, ChatSession chat, CommandCatalog commands, string? webUrl = null,
        NotificationCenter? notifications = null, Action? showAbout = null)
    {
        _pm = pm;
        _showAbout = showAbout;
        _chat = chat;
        _commands = commands;
        _webUrl = webUrl;
        _notifications = notifications;
        InitializeComponent();
        if (_showAbout == null) BtnAbout.Visibility = Visibility.Collapsed;
        SizeChanged += (_, _) => SidebarColumn.MaxWidth = Math.Max(210, Math.Min(360, ActualWidth - 590));
        Title = pm.Config.ProjectName;
        if (!_commands.IsEnabled)
            BtnCommands.Visibility = Visibility.Collapsed;
        _emuTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _emuTimer.Tick += async (_, _) => await RefreshEmulatorsAsync();
        if (_pm.Config.Android.Enabled) _emuTimer.Start();
        BuildTabs();
        BuildStatusIndicators();
        UpdateStatusBar();

        _pm.OutputReceived += OnProcessOutput;
        _pm.StatusChanged += OnProcessStatusChanged;
        _pm.ConfigurationChanged += OnServiceConfigurationChanged;

        _chat.AgentChunk += OnAgentChunk;
        _chat.AgentStatus += OnAgentStatus;
        _chat.TaskChanged += OnTaskChanged;
        _chat.ConvoMessage += OnConvoMessage;
        _chat.SessionCompleted += OnSessionCompleted;

        if (notifications != null)
            notifications.NotificationAdded += OnNotification;

        Loaded += OnLoaded;
        Closed += (_, _) => _pm.ConfigurationChanged -= OnServiceConfigurationChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(Path.GetTempPath(), "JustWatch.WebView2"));
            await TerminalWebView.EnsureCoreWebView2Async(env);
            TerminalWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            TerminalWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            TerminalWebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            TerminalWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            TerminalWebView.CoreWebView2.NavigationStarting += (_, _) => _terminalReady = false;

            var ids = _pm.GetServiceStates(includeStopped: true).Select(p => p.Id).ToArray();
            var html = TerminalHtml.Generate(ids);
            var webDir = Path.Combine(Path.GetTempPath(), "JustWatch.Terminal");
            Directory.CreateDirectory(webDir);
            File.WriteAllText(Path.Combine(webDir, "terminal.html"), html);
            TerminalWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "justwatch.local", webDir, CoreWebView2HostResourceAccessKind.Allow);
            TerminalWebView.CoreWebView2.Navigate("https://justwatch.local/terminal.html");
        }
        catch (Exception ex) { StatusText.Text = $"Init failed: {ex.Message}"; }
    }

    private void OnServiceConfigurationChanged()
    {
        Dispatcher.InvokeAsync(async () =>
        {
            var ids = _pm.GetServiceStates(includeStopped: true).Select(p => p.Id).ToArray();
            if (_terminalReady) await ExecuteJs($"syncServiceTerminals({JsonSerializer.Serialize(ids)})");
            BuildTabs(); BuildStatusIndicators(); UpdateStatusBar();
            if (_activeTabId != null && !_isChatActive && !_isEmulatorsActive) UpdateActionButtons(_activeTabId);
        });
    }

    private void About_Click(object sender, RoutedEventArgs e) => _showAbout?.Invoke();

    private void Services_Click(object sender, RoutedEventArgs e)
    {
        var url = (_webUrl ?? $"http://localhost:{_pm.Config.Web.Port}/").TrimEnd('/') + "/services";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private IReadOnlyList<ServiceState> VisibleServices() =>
        _pm.GetServiceStates(includeStopped: true).Where(ServiceVisibility.IsVisibleInTray).ToArray();

    private void BuildTabs()
    {
        var selected = _activeTabId;
        TabBar.Children.Clear();
        _tabButtons.Clear();
        _navigationStatus.Clear();
        var visible = VisibleServices();
        foreach (var project in visible.GroupBy(p => p.Project, StringComparer.OrdinalIgnoreCase))
        {
            TabBar.Children.Add(new Border
            {
                Margin = new Thickness(12, 16, 8, 8),
                Child = new TextBlock { Text = $"{project.Key.ToUpperInvariant()}   ·   {project.Count()}", FontSize = 10,
                    FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x89, 0x9B, 0xB1)),
                    TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = project.Key }
            });
            foreach (var service in project)
                AddTab(service.Id, service.Name);
        }
        TabBar.Children.Add(new TextBlock { Text = "WORKSPACE", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x89, 0x9B, 0xB1)), Margin = new Thickness(12, 26, 0, 8) });
        AddTab("__agent__", "Agent");
        if (_pm.Config.Android.Enabled) AddTab("__emulators__", "Emulators");
        var next = selected != null && _tabButtons.ContainsKey(selected) ? selected : visible.FirstOrDefault()?.Id ?? "__agent__";
        if (next != selected) SelectTab(next);
        else { foreach (var (id, button) in _tabButtons) button.IsChecked = id == next; UpdatePageHeader(); }
    }

    private void AddTab(string id, string name)
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = name, FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        // Measure the name at its natural width, then clip/fade only its viewport.
        // The auto-sized port column gets its full width before the name is laid out.
        var labels = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(label);
        var nameViewport = new Grid { ClipToBounds = true };
        nameViewport.Children.Add(labels);
        Grid.SetColumn(nameViewport, 1);
        content.Children.Add(nameViewport);
        void UpdateNameFade()
        {
            var width = nameViewport.ActualWidth;
            nameViewport.OpacityMask = width > 0 && label.ActualWidth > width + 0.5
                ? new LinearGradientBrush(Colors.Black, Colors.Transparent,
                    new Point(Math.Max(0, width - 24), 0), new Point(width, 0))
                    { MappingMode = BrushMappingMode.Absolute }
                : null;
        }
        nameViewport.SizeChanged += (_, _) => UpdateNameFade();
        label.SizeChanged += (_, _) => UpdateNameFade();
        var service = _pm.GetServiceState(id);
        if (service != null)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            var port = new TextBlock { FontSize = 11, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                Margin = new Thickness(8, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA9, 0xBD, 0xCC)) };
            Grid.SetColumn(port, 2);
            content.Children.Add(port);
            content.Children.Add(dot);
            _navigationStatus[id] = (dot, port);
            UpdateNavigationStatus(id, service.Status);
        }
        else content.Children.Add(new TextBlock { Text = id == "__agent__" ? "\uE8BD" : "\uE8EA",
            FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var button = new ToggleButton { Content = content, Tag = id, ToolTip = service?.Description is { Length: > 0 } description ? description : name,
            Style = (Style)FindResource("SidebarItem") };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += TabButton_Click;
        _tabButtons[id] = button;
        if (service != null) UpdateNavigationStatus(id, service.Status);
        TabBar.Children.Add(button);
    }

    private void BuildStatusIndicators()
    {
        StatusIndicators.Children.Clear();
        _statusIndicators.Clear();
        foreach (var service in VisibleServices())
        {
            var indicator = new TextBlock
            {
                Foreground = Brushes.White, FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                ToolTip = service.Project
            };
            _statusIndicators[service.Id] = indicator;
            StatusIndicators.Children.Add(indicator);
            UpdateStatusIndicator(service.Id, service.Status);
        }
    }

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton btn && btn.Tag is string id) SelectTab(id);
    }

    private async void SelectTab(string id)
    {
        _activeTabId = id;
        _isChatActive = id == "__agent__";
        _isEmulatorsActive = id == "__emulators__";
        foreach (var (tabId, btn) in _tabButtons) btn.IsChecked = tabId == id;
        UpdatePageHeader();
        ProcessActionPanel.Visibility = _isChatActive || _isEmulatorsActive ? Visibility.Collapsed : Visibility.Visible;
        if (!_isChatActive && !_isEmulatorsActive) UpdateActionButtons(id);
        if (!_terminalReady) return;

        if (_isChatActive)
        {
            await ExecuteJs("showChat()");
            ProcessActionPanel.Visibility = Visibility.Collapsed;
        }
        else if (_isEmulatorsActive)
        {
            await ExecuteJs("showEmulators()");
            ProcessActionPanel.Visibility = Visibility.Collapsed;
            _ = RefreshEmulatorsAsync();
        }
        else
        {
            await ExecuteJs($"switchTerminal({JsonSerializer.Serialize(id)})");
            ProcessActionPanel.Visibility = Visibility.Visible;
            UpdateActionButtons(id);
        }
    }

    private void UpdateActionButtons(string id)
    {
        BtnStartStop.Content = _pm.GetStatus(id) == ProcessStatus.Running ? "Stop" : "Start";
        var url = _pm.GetServiceState(id)?.Url;
        BtnOpenService.Visibility = url == null ? Visibility.Collapsed : Visibility.Visible;
        BtnOpenService.ToolTip = url;
        UpdatePageHeader();
    }

    private static (string Label, string Color) StatusAppearance(ProcessStatus status) => status switch
    {
        ProcessStatus.Running => ("Running", "#89E5B6"),
        ProcessStatus.Starting => ("Starting", "#E8C77C"),
        ProcessStatus.Stopping => ("Stopping", "#E8C77C"),
        ProcessStatus.Crashed => ("Crashed", "#F3939F"),
        ProcessStatus.RecoveringWithAgent => ("Recovering", "#C6AEF1"),
        _ => ("Stopped", "#7C8A9E")
    };

    private void UpdateNavigationStatus(string id, ProcessStatus status)
    {
        if (!_navigationStatus.TryGetValue(id, out var navigation)) return;
        var (label, color) = StatusAppearance(status);
        navigation.Dot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        var service = _pm.GetServiceState(id);
        navigation.Port.Text = service?.Port is > 0 ? $":{service.Port}" : "";
        navigation.Port.Visibility = service?.Port is > 0 ? Visibility.Visible : Visibility.Collapsed;
        navigation.Dot.ToolTip = label;
        if (_tabButtons.TryGetValue(id, out var button) && service != null)
        {
            button.ToolTip = $"{service.Name} · {label}" + (service.Port is > 0 ? $" · :{service.Port}" : "")
                + (string.IsNullOrWhiteSpace(service.Description) ? "" : $"\n{service.Description}");
            System.Windows.Automation.AutomationProperties.SetHelpText(button, $"{label} {navigation.Port.Text}".Trim());
        }
    }

    private void UpdatePageHeader()
    {
        var service = _activeTabId == null ? null : _pm.GetServiceState(_activeTabId);
        PageEyebrow.Text = service == null ? "Workspace  /" : $"{service.Project}  /";
        PageEyebrow.ToolTip = service?.Project ?? "Workspace";
        PageTitle.Text = service?.Name ?? (_isEmulatorsActive ? "Emulators" : "Agent workspace");
        PageTitle.ToolTip = PageTitle.Text;
        var (label, color) = service == null ? ("Ready", "#89E5B6") : StatusAppearance(service.Status);
        SelectedStatusDot.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(SelectedStatusDot, label);
        SelectedStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private void OpenService_Click(object sender, RoutedEventArgs e)
    {
        var url = _activeTabId == null ? null : _pm.GetServiceState(_activeTabId)?.Url;
        if (url == null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = $"Could not open service: {ex.Message}"; }
    }

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTabId == null || _isChatActive) return;
        if (_pm.GetStatus(_activeTabId) == ProcessStatus.Running) await _pm.StopAsync(_activeTabId);
        else _pm.Start(_activeTabId);
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTabId != null && !_isChatActive) await _pm.RestartAsync(_activeTabId);
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!_isChatActive && _activeTabId != null && _terminalReady)
            await ExecuteJs($"clearTerminal({JsonSerializer.Serialize(_activeTabId)})");
    }

    private void StartAll_Click(object sender, RoutedEventArgs e) => _pm.StartAll();
    private async void StopAll_Click(object sender, RoutedEventArgs e) => await _pm.StopAllAsync();

    private async void Commands_Click(object sender, RoutedEventArgs e)
    {
        await ShowCommandsMenuAsync();
    }

    private async Task ShowCommandsMenuAsync(bool forceRescan = false)
    {
        BtnCommands.IsEnabled = false;
        IReadOnlyList<CommandPackage> packages;
        try
        {
            packages = await _commands.GetPackagesAsync(forceRescan);
        }
        catch (Exception ex)
        {
            ShowCommandError($"Could not scan commands: {ex.Message}");
            return;
        }
        finally
        {
            BtnCommands.IsEnabled = true;
        }

        var menu = BuildCommandsMenu(packages);
        menu.PlacementTarget = BtnCommands;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    private ContextMenu BuildCommandsMenu(IReadOnlyList<CommandPackage> packages)
    {
        var menu = new ContextMenu();

        if (packages.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "No package scripts found",
                IsEnabled = false
            });
        }
        else
        {
            foreach (var root in GetConfiguredCommandRoots())
            {
                var rootPackages = packages
                    .Where(p => p.Root.Equals(root, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.RelativeDir, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (rootPackages.Length == 0) continue;

                var rootNode = BuildCommandDirectoryTree(GetRootHeader(root), rootPackages);
                var item = new MenuItem
                {
                    Header = GetRootHeader(root),
                    ToolTip = root,
                    InputGestureText = string.Empty
                };

                PopulateCommandDirectoryItem(item, rootNode);
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator());
        var refresh = new MenuItem { Header = "Refresh" };
        refresh.Click += async (_, _) => await ShowCommandsMenuAsync(forceRescan: true);
        menu.Items.Add(refresh);
        return menu;
    }

    private IEnumerable<string> GetConfiguredCommandRoots()
    {
        IEnumerable<string> roots = _pm.Config.Commands.Roots.Count > 0
            ? _pm.Config.Commands.Roots
            : [_pm.Config.ProjectPath];

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string fullPath;
            try { fullPath = Path.GetFullPath(root); }
            catch { continue; }
            yield return fullPath;
        }
    }

    private static CommandDirectoryNode BuildCommandDirectoryTree(
        string rootLabel,
        IEnumerable<CommandPackage> packages)
    {
        var root = new CommandDirectoryNode(rootLabel);
        foreach (var package in packages)
        {
            var current = root;
            if (package.RelativeDir != ".")
            {
                foreach (var segment in package.RelativeDir.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
                    current = current.GetOrAdd(segment);
            }

            current.Package = package;
        }

        return root;
    }

    private void PopulateCommandDirectoryItem(MenuItem item, CommandDirectoryNode node)
    {
        if (node.Package is { } package)
        {
            foreach (var scriptNode in BuildCommandTree(package.Scripts).Children.Values)
                item.Items.Add(BuildCommandNodeItem(package, scriptNode));
        }

        if (node.Children.Count == 0) return;
        if (node.Package is not null)
            item.Items.Add(new Separator());

        foreach (var child in node.Children.Values)
            item.Items.Add(BuildCommandDirectoryItem(child));
    }

    private MenuItem BuildCommandDirectoryItem(CommandDirectoryNode node)
    {
        var (label, compactNode) = CompactCommandDirectoryNode(node);
        var item = new MenuItem
        {
            Header = label,
            ToolTip = compactNode.Package is { } package
                ? package.Description ?? $"{package.DisplayName}{Environment.NewLine}{package.Dir}"
                : null,
            InputGestureText = string.Empty
        };
        PopulateCommandDirectoryItem(item, compactNode);
        return item;
    }

    private static (string Label, CommandDirectoryNode Node) CompactCommandDirectoryNode(CommandDirectoryNode node)
    {
        var label = node.Label;
        var current = node;
        while (current.Package is null && current.Children.Count == 1)
        {
            current = current.Children.Values.First();
            label += "\\" + current.Label;
        }

        return (label, current);
    }

    private static string GetRootHeader(string root)
    {
        var trimmed = root.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leaf = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(leaf) ? root : leaf;
    }

    private MenuItem BuildCommandNodeItem(CommandPackage package, CommandMenuNode node)
    {
        var item = new MenuItem { Header = node.Label };

        if (node.Script is not null && node.Children.Count == 0)
        {
            WireCommandMenuItem(item, package, node.Script);
            return item;
        }

        if (node.Script is not null)
        {
            var runItem = new MenuItem { Header = node.Label };
            WireCommandMenuItem(runItem, package, node.Script);
            item.Items.Add(runItem);
            item.Items.Add(new Separator());
        }

        foreach (var child in node.Children.Values)
            item.Items.Add(BuildCommandNodeItem(package, child));

        return item;
    }

    private void WireCommandMenuItem(MenuItem item, CommandPackage package, CommandScript script)
    {
        item.ToolTip = string.IsNullOrWhiteSpace(script.Description)
            ? script.Command
            : $"{script.Description}{Environment.NewLine}{script.Command}";
        item.Click += (_, _) =>
        {
            if (!_commands.TryRun(package.Dir, script.Name, out var error))
                ShowCommandError(error ?? "Could not run command.");
        };
    }

    private static CommandMenuNode BuildCommandTree(IEnumerable<CommandScript> scripts)
    {
        var root = new CommandMenuNode("root");
        foreach (var script in scripts)
        {
            var current = root;
            foreach (var part in script.Name.Split(':'))
                current = current.GetOrAdd(part);
            current.Script = script;
        }
        return root;
    }

    private void ShowCommandError(string message)
    {
        if (_notifications != null)
            _notifications.Notify("error", "Command failed", message, "commands");
        else
            MessageBox.Show(message, "Commands", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // --- Emulator management screen ---

    private async Task RefreshEmulatorsAsync()
    {
        if (!_terminalReady || !_isEmulatorsActive || !IsVisible || _emuRefreshing) return;
        _emuRefreshing = true;
        try
        {
            var dashboard = await _pm.GetAndroidDashboardAsync();
            await ExecuteJs($"renderEmulators({JsonSerializer.Serialize(dashboard, EmuJsonOpts)})");
        }
        catch (Exception ex)
        {
            await ExecuteJs($"renderEmulatorError({JsonSerializer.Serialize(ex.Message)})");
        }
        finally { _emuRefreshing = false; }
    }

    private async void HandleEmuMessage(WvMessage msg)
    {
        try
        {
            switch (msg.Action)
            {
                case "launch": if (!string.IsNullOrEmpty(msg.Id)) await _pm.LaunchAndroidEmulatorAsync(msg.Id); break;
                case "stop": if (!string.IsNullOrEmpty(msg.Id)) await _pm.StopAndroidEmulatorAsync(msg.Id); break;
                case "restart-app": if (!string.IsNullOrEmpty(msg.Id)) await _pm.RestartJustGainsAppAsync(msg.Id); break;
                case "delete": if (!string.IsNullOrEmpty(msg.Id)) await _pm.DeleteAndroidEmulatorAsync(msg.Id); break;
                case "clone": await _pm.CloneAndroidEmulatorAsync(); break;
                case "start-all": await _pm.StartAllAndroidEmulatorsAsync(); break;
                case "restart-all": await _pm.RestartAllAndroidEmulatorsAsync(); break;
                case "scorched-update": await _pm.ScorchedAndroidUpdateAsync(); break;
                case "save-config":
                    if (!string.IsNullOrEmpty(msg.Id))
                        await Task.Run(() => _pm.UpdateAndroidEmulatorConfig(msg.Id, msg.Ram, msg.Cores, msg.Gpu));
                    break;
                case "refresh": break;
            }
        }
        catch (Exception ex)
        {
            await ExecuteJs($"renderEmulatorError({JsonSerializer.Serialize(ex.Message)})");
        }
        await RefreshEmulatorsAsync();
    }

    private void OnProcessOutput(string id, string data) =>
        Dispatcher.InvokeAsync(() => WriteToTerminal(id, data));

    private void OnProcessStatusChanged(string id, ProcessStatus status)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var service = _pm.GetServiceState(id);
            var visible = service != null && ServiceVisibility.IsVisibleInTray(service);
            if (visible != _tabButtons.ContainsKey(id)) { BuildTabs(); BuildStatusIndicators(); }
            UpdateStatusIndicator(id, _pm.GetStatus(id));
            UpdateStatusBar();
            if (id == _activeTabId && !_isChatActive) UpdateActionButtons(id);
        });
    }

    private async void WriteToTerminal(string id, string data)
    {
        if (!_terminalReady) return;
        await ExecuteJs($"writeToTerminal({JsonSerializer.Serialize(id)}, {JsonSerializer.Serialize(data)})");
    }

    // --- Agent 3-column events ---

    private void OnAgentChunk(string channel, string text)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (!_terminalReady) return;

            if (channel == "orch")
            {
                var level = "info";
                var msg = text;
                if (msg.StartsWith("[info]")) { level = "info"; msg = msg[6..]; }
                else if (msg.StartsWith("[warn]")) { level = "warn"; msg = msg[6..]; }
                else if (msg.StartsWith("[error]")) { level = "error"; msg = msg[7..]; }
                else
                {
                    // Raw streaming text (e.g. from planning) — append as stream
                    await ExecuteJs($"appendAgentChunk(\"orch\", {JsonSerializer.Serialize(text)})");
                    return;
                }

                var fn = level switch
                {
                    "warn" => "addOrchWarn",
                    "error" => "addOrchError",
                    _ => "addOrchInfo"
                };
                await ExecuteJs($"{fn}({JsonSerializer.Serialize(msg)})");
            }
            else
            {
                await ExecuteJs($"appendAgentChunk({JsonSerializer.Serialize(channel)}, {JsonSerializer.Serialize(text)})");
            }
        });
    }

    private void OnAgentStatus(string channel, string status, string data)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (!_terminalReady) return;
            var ch = JsonSerializer.Serialize(channel);
            if (status == "start")
                await ExecuteJs($"setStopEnabled({ch}, true)");
            else if (status == "end")
                await ExecuteJs($"endAgentStream({ch})");
        });
    }

    private void OnTaskChanged(AgentTask task)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (!_terminalReady) return;
            var id = JsonSerializer.Serialize(task.Id);
            var name = JsonSerializer.Serialize(task.Name);
            var type = JsonSerializer.Serialize(task.Type.ToString().ToLower());
            var status = JsonSerializer.Serialize(task.Status.ToString().ToLower());
            var channel = JsonSerializer.Serialize(task.Channel);
            await ExecuteJs($"upsertTask({id},{name},{type},{status},{channel})");
        });
    }

    private void OnConvoMessage(string text, string? type, string? filePath)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (!_terminalReady) return;
            if (type == "video" && !string.IsNullOrEmpty(filePath))
                await ExecuteJs($"addConvoVideo({JsonSerializer.Serialize(text)},{JsonSerializer.Serialize(filePath)})");
            else
                await ExecuteJs($"addConvoMessage({JsonSerializer.Serialize(text)},{JsonSerializer.Serialize(type ?? "null")})");
        });
    }

    private void OnNotification(Notification n)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (!_terminalReady) return;
            await ExecuteJs($"showToast({JsonSerializer.Serialize(n.Level)},{JsonSerializer.Serialize(n.Title)},{JsonSerializer.Serialize(n.Message)})");
        });
    }

    private void OnSessionCompleted()
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (_terminalReady)
                await ExecuteJs("endAllStreams()");
        });
    }

    private async void HandleChatMessage(string message)
    {
        if (_chat.IsProcessing)
        {
            _chat.SendNewTask(message, _pm.Config.ProjectPath);
            // Re-enable input immediately for new messages
            _ = Dispatcher.InvokeAsync(async () =>
            {
                if (_terminalReady)
                    await ExecuteJs("endAllStreams()");
            });
        }
        else
        {
            await _chat.SendAsync(message, _pm.Config.ProjectPath);
        }
    }

    // --- WebView messages ---

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<WvMessage>(e.WebMessageAsJson, JsonOpts);
            if (msg == null || string.IsNullOrEmpty(msg.Type)) return;

            switch (msg.Type)
            {
                case "ready":
                    _terminalReady = msg.Initialized && !msg.Error;
                    if (!_terminalReady)
                    {
                        StatusText.Text = "Terminal failed to initialize. Click Reload terminal to retry.";
                        break;
                    }
                    UpdateStatusBar();
                    LoadBufferedOutput();
                    if (_activeTabId != null) SelectTab(_activeTabId);
                    break;
                case "input":
                    if (msg.Id != null && msg.Data != null) _pm.WriteInput(msg.Id, msg.Data);
                    break;
                case "chat":
                    if (!string.IsNullOrEmpty(msg.Message)) HandleChatMessage(msg.Message);
                    break;
                case "stop":
                    if (!string.IsNullOrEmpty(msg.Channel)) _chat.StopChannel(msg.Channel);
                    break;
                case "stop-task":
                    if (!string.IsNullOrEmpty(msg.TaskId)) _chat.StopTask(msg.TaskId);
                    break;
                case "open-file":
                    if (!string.IsNullOrEmpty(msg.Path))
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(msg.Path) { UseShellExecute = true }); } catch { }
                    break;
                case "emu":
                    HandleEmuMessage(msg);
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebMessage error: {ex.Message}");
        }
    }

    private async void LoadBufferedOutput()
    {
        foreach (var pc in _pm.GetProcessConfigs())
        {
            var buffered = _pm.GetBufferedOutput(pc.Id);
            if (string.IsNullOrEmpty(buffered)) continue;
            await ExecuteJs($"writeToTerminal({JsonSerializer.Serialize(pc.Id)}, {JsonSerializer.Serialize(buffered)})");
        }
    }

    private void UpdateStatusIndicator(string id, ProcessStatus status)
    {
        if (!_statusIndicators.TryGetValue(id, out var indicator)) return;
        var name = _pm.GetServiceState(id)?.Name ?? id;
        var (symbol, color) = status switch
        {
            ProcessStatus.Running => ("●", "#0dbc79"),
            ProcessStatus.Starting or ProcessStatus.Stopping => ("○", "#e5e510"),
            ProcessStatus.Crashed => ("●", "#f14c4c"),
            ProcessStatus.RecoveringWithAgent => ("⚙", "#d670d6"),
            _ => ("○", "#808080")
        };
        indicator.Text = $"{symbol} {name}";
        indicator.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        UpdateNavigationStatus(id, status);
    }

    private void UpdateStatusBar()
    {
        var configs = _pm.GetProcessConfigs();
        var running = configs.Count(p => _pm.GetStatus(p.Id) == ProcessStatus.Running);
        StatusText.Text = $"{running}/{configs.Count} processes running";
        SidebarSummary.Text = $"●  {running} running  ·  {configs.Count} configured";
    }

    private async Task ExecuteJs(string script)
    {
        try { if (TerminalWebView.CoreWebView2 != null) await TerminalWebView.CoreWebView2.ExecuteScriptAsync(script); }
        catch { }
    }

    protected override void OnClosing(CancelEventArgs e) { e.Cancel = true; Hide(); }

    private sealed class CommandMenuNode(string label)
    {
        public string Label { get; } = label;
        public CommandScript? Script { get; set; }
        public SortedDictionary<string, CommandMenuNode> Children { get; } = new(StringComparer.Ordinal);

        public CommandMenuNode GetOrAdd(string label)
        {
            if (Children.TryGetValue(label, out var node))
                return node;

            node = new CommandMenuNode(label);
            Children[label] = node;
            return node;
        }
    }

    private sealed class CommandDirectoryNode(string label)
    {
        public string Label { get; } = label;
        public CommandPackage? Package { get; set; }
        public SortedDictionary<string, CommandDirectoryNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public CommandDirectoryNode GetOrAdd(string label)
        {
            if (Children.TryGetValue(label, out var node))
                return node;

            node = new CommandDirectoryNode(label);
            Children[label] = node;
            return node;
        }
    }

    private class WvMessage
    {
        public string Type { get; set; } = "";
        public bool Initialized { get; set; }
        public bool Error { get; set; }
        public string? Id { get; set; }
        public string? Data { get; set; }
        public string? Message { get; set; }
        public string? Channel { get; set; }
        public string? TaskId { get; set; }
        public string? Path { get; set; }
        public string? Action { get; set; }
        public int? Ram { get; set; }
        public int? Cores { get; set; }
        public string? Gpu { get; set; }
    }
}
