using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace JustWatch.TrayApp;

public partial class TrayFlyoutWindow
{
    private readonly OtherWebServices _otherServices;
    private IReadOnlyList<OtherWebService> _otherSnapshot = [];
    private DateTimeOffset _otherRefreshedAt = DateTimeOffset.MinValue;
    private bool _otherRefreshInFlight;
    private bool _otherRefreshPending;
    private string _otherStatus = "Looking for web services…";

    private void WireOtherActions()
    {
        RefreshOtherButton.Click += async (_, _) => await RefreshOtherAsync(force: true);
    }

    private async Task RefreshOtherAsync(bool force = false)
    {
        if (!IsVisible && !force) return;
        if (!force && DateTimeOffset.UtcNow - _otherRefreshedAt < TimeSpan.FromSeconds(15)) return;
        if (_otherRefreshInFlight)
        {
            if (force) _otherRefreshPending = true;
            return;
        }

        _otherRefreshInFlight = true;
        OtherBusyText.Visibility = Visibility.Visible;
        RefreshOtherButton.IsEnabled = false;
        try
        {
            _otherSnapshot = await _otherServices.ScanAsync();
            _otherStatus = "";
            _otherRefreshedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _otherSnapshot = [];
            _otherStatus = "JustPorts could not scan local services. Check that it is installed.";
            _otherRefreshedAt = DateTimeOffset.UtcNow;
            System.Diagnostics.Debug.WriteLine($"[TrayFlyout] JustPorts scan failed: {ex.Message}");
        }
        finally
        {
            OtherBusyText.Visibility = Visibility.Collapsed;
            RefreshOtherButton.IsEnabled = true;
            _otherRefreshInFlight = false;
            RenderOtherServices();
            if (_otherRefreshPending)
            {
                _otherRefreshPending = false;
                _ = Dispatcher.InvokeAsync(() => RefreshOtherAsync(force: true));
            }
        }
    }

    private void RenderOtherServices()
    {
        var managedPorts = _processManager.GetProcessConfigs()
            .SelectMany(config =>
            {
                var ports = new List<int>();
                if (config.Port is > 0) ports.Add(config.Port.Value);
                if (Uri.TryCreate(config.EffectiveUrl, UriKind.Absolute, out var url))
                    ports.Add(url.Port);
                return ports;
            }).ToHashSet();
        var rows = _otherServices.Merge(_otherSnapshot, managedPorts, Environment.ProcessId);

        OtherRowsPanel.Children.Clear();
        foreach (var row in rows)
            OtherRowsPanel.Children.Add(CreateOtherRow(row));
        OtherStatusText.Text = _otherStatus.Length > 0
            ? _otherStatus
            : rows.Count == 0 ? "No other web services found." : "";
        OtherStatusText.Visibility = OtherStatusText.Text.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;
        ((Border)OtherRowsPanel.Parent).Visibility = rows.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        if (IsVisible) { UpdateLayout(); PositionNearTaskbar(); }
    }

    private FrameworkElement CreateOtherRow(OtherWebServiceRow entry)
    {
        var service = entry.Service;
        var name = string.IsNullOrWhiteSpace(service.ProjectName)
            ? service.ProcessName : service.ProjectName;
        var grid = CreateRowGrid();
        var dot = CreateStatusDot();
        dot.Fill = StatusBrush(entry.IsRunning ? Core.Models.ProcessStatus.Running : Core.Models.ProcessStatus.Stopped);

        var label = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        var nameText = new TextBlock
        {
            Text = name,
            Foreground = entry.IsRunning ? PrimaryBrush() : SecondaryBrush(),
            FontWeight = FontWeights.Medium,
            MaxWidth = 170,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = service.ProjectRoot ?? service.Url
        };
        label.Children.Add(nameText);
        label.Children.Add(new TextBlock
        {
            Text = service.Port.ToString(),
            Foreground = TertiaryBrush(),
            FontSize = 11,
            Margin = new Thickness(7, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        if (entry.IsRunning)
        {
            label.Cursor = Cursors.Hand;
            label.ToolTip = $"Open {service.Url}";
            label.MouseEnter += (_, _) => nameText.Foreground = AccentBrush();
            label.MouseLeave += (_, _) => nameText.Foreground = PrimaryBrush();
            label.MouseLeftButtonUp += (_, _) => OpenUrl(service.Url);
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        if (entry.IsRunning)
        {
            var open = CreateIconButton(OpenGlyph, $"Open {service.Url}");
            AutomationProperties.SetName(open, $"Open {name} on port {service.Port}");
            open.Click += (_, _) => OpenUrl(service.Url);
            actions.Children.Add(open);
        }

        var pin = new Button
        {
            Content = entry.IsPinned ? "Unpin" : "Pin",
            ToolTip = entry.IsPinned ? $"Unpin {name}" : $"Pin {name}",
            Style = (Style)FindResource("TextButtonStyle"),
            Padding = new Thickness(6, 5, 6, 5)
        };
        pin.Foreground = entry.IsPinned ? AccentBrush() : SecondaryBrush();
        AutomationProperties.SetName(pin, entry.IsPinned ? $"Unpin {name} on port {service.Port}" : $"Pin {name} on port {service.Port}");
        pin.Click += (_, _) =>
        {
            try
            {
                if (entry.IsPinned) _otherServices.Unpin(entry.PinKey!);
                else _otherServices.Pin(service);
                RenderOtherServices();
                _notifications?.Notify("success", entry.IsPinned ? "Service unpinned" : "Service pinned",
                    $"{name} on port {service.Port}.", "action");
            }
            catch (Exception ex)
            {
                _notifications?.Notify("error", "Could not save pin", ex.Message, "action");
            }
        };
        actions.Children.Add(pin);

        if (entry.IsPinned)
        {
            var toggle = CreateIconButton(entry.IsRunning ? StopGlyph : PlayGlyph,
                entry.IsRunning ? $"Stop {name}" : $"Start {name}");
            var restart = CreateIconButton(RestartGlyph, $"Restart {name}");
            toggle.IsEnabled = entry.IsRunning || service.Launch != null;
            restart.IsEnabled = entry.IsRunning && service.Launch != null;
            if (!entry.IsRunning && service.Launch == null)
                toggle.ToolTip = "No start command was detected";
            if (service.Launch == null)
                restart.ToolTip = "No start command was detected";
            AutomationProperties.SetName(toggle, entry.IsRunning ? $"Stop {name} on port {service.Port}" : $"Start {name} on port {service.Port}");
            AutomationProperties.SetName(restart, $"Restart {name} on port {service.Port}");
            toggle.Click += async (_, _) => await RunOtherActionAsync(
                entry.IsRunning ? () => _otherServices.StopAsync(service) : () => _otherServices.StartAsync(service),
                entry.IsRunning ? $"stop {name}" : $"start {name}", [toggle, restart]);
            restart.Click += async (_, _) => await RunOtherActionAsync(
                () => _otherServices.RestartAsync(service), $"restart {name}", [toggle, restart]);
            actions.Children.Add(toggle);
            actions.Children.Add(restart);
        }

        Grid.SetColumn(dot, 0);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(actions, 3);
        grid.Children.Add(dot);
        grid.Children.Add(label);
        grid.Children.Add(actions);
        return CreateRowShell(grid);
    }

    private async Task RunOtherActionAsync(Func<Task> action, string label,
        IReadOnlyList<Button> buttons)
    {
        foreach (var button in buttons) button.IsEnabled = false;
        try
        {
            await action();
            await RefreshOtherAsync(force: true);
            _notifications?.Notify("success", "Action complete", $"Finished: {label}.", "action");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayFlyout] Failed to {label}: {ex.Message}");
            _notifications?.Notify("error", "Action failed", $"Could not {label}: {ex.Message}", "action");
            await RefreshOtherAsync(force: true);
        }
        finally
        {
            foreach (var button in buttons) button.IsEnabled = true;
        }
    }

}
