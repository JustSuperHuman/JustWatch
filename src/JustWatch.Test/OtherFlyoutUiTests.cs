using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JustWatch.Core.Models;
using JustWatch.Core.Services;
using JustWatch.TrayApp;

internal static class OtherFlyoutUiTests
{
    public static Task RunAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/JustWatch;component/Theme.xaml", UriKind.Relative)
            });
            app.Startup += (_, _) =>
            {
                try { RenderFixture(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { app.Shutdown(); }
            };
            app.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void RenderFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "justwatch-other-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var catalog = new OtherWebServices(Path.Combine(root, "pins.json"));
        var pinnedLive = Service(5173, "Project with a very long title", "F:/projects/long-project");
        var pinnedOffline = Service(3100, "Saved site", "F:/projects/saved-site");
        var unpinned = Service(8088, "Docs", "F:/projects/docs");
        catalog.Pin(pinnedLive);
        catalog.Pin(pinnedOffline);

        var config = new JustWatchConfig
        {
            ProjectPath = root,
            Processes = [new ProcessConfig
            {
                Id = "managed", Name = "Managed API", Project = "JustGains",
                Command = Environment.ProcessPath!, AutoStart = false,
                Port = 5000, ShowInTray = true
            }]
        };
        using var manager = new ProcessManager(config);
        var flyout = new TrayFlyoutWindow(manager, null, () => { }, () => { },
            () => Task.CompletedTask, otherWebServices: catalog);
        try
        {
            typeof(TrayFlyoutWindow).GetField("_otherSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(flyout, new[] { pinnedLive with { Pid = 9876, StartTime = 1700000000 },
                    unpinned with { Pid = 9877, StartTime = 1700000001 } });
            typeof(TrayFlyoutWindow).GetField("_otherStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(flyout, "");
            flyout.RefreshServices();
            var shell = (Grid)flyout.FindName("Shell");
            shell.Measure(new Size(432, 712));
            var size = new Size(432, Math.Clamp(shell.DesiredSize.Height, 200, 712));
            shell.Arrange(new Rect(size));
            shell.UpdateLayout();

            var panel = (StackPanel)flyout.FindName("OtherRowsPanel");
            Check(panel.Children.Count == 3, "Other renders pinned live, pinned stopped, and unpinned live rows");
            var names = Buttons(panel).Select(AutomationProperties.GetName).ToArray();
            Check(names.Any(name => name.StartsWith("Stop Project with a very long title")),
                "Pinned live service has a Stop action");
            Check(names.Any(name => name.StartsWith("Start Saved site")),
                "Pinned stopped service keeps a Start action");
            Check(names.Any(name => name.StartsWith("Pin Docs"))
                && !names.Any(name => name.StartsWith("Stop Docs")),
                "Unpinned live service has Open and Pin without lifecycle actions");

            var pinDocs = Buttons(panel).Single(button => AutomationProperties.GetName(button) == "Pin Docs on port 8088");
            pinDocs.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Buttons(panel).Any(button => AutomationProperties.GetName(button) == "Unpin Docs on port 8088")
                && new OtherWebServices(Path.Combine(root, "pins.json"))
                    .Merge([], new HashSet<int>(), Environment.ProcessId).Count(row => row.IsPinned) == 3,
                "Tray Pin click updates the row and saves the pin");
            var unpinDocs = Buttons(panel).Single(button => AutomationProperties.GetName(button) == "Unpin Docs on port 8088");
            unpinDocs.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Buttons(panel).Any(button => AutomationProperties.GetName(button) == "Pin Docs on port 8088")
                && new OtherWebServices(Path.Combine(root, "pins.json"))
                    .Merge([], new HashSet<int>(), Environment.ProcessId).Count(row => row.IsPinned) == 2,
                "Tray Unpin click removes the saved pin");

            shell.Measure(new Size(432, 712));
            shell.Arrange(new Rect(size));
            shell.UpdateLayout();

            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width),
                (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(shell);
            var output = Path.Combine(Path.GetTempPath(), "justwatch-other-flyout-preview.png");
            using var file = File.Create(output);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(file);
            Console.WriteLine("Preview: " + output);
        }
        finally
        {
            flyout.Close();
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(root).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, recursive: true);
        }
    }

    private static OtherWebService Service(int port, string name, string root) => new()
    {
        Port = port,
        Url = $"http://localhost:{port}/",
        ProjectName = name,
        ProjectRoot = root,
        Framework = "Vite",
        ProcessName = "node.exe",
        Launch = new OtherLaunchRecipe { Label = "npm run dev", Program = "npm", Cwd = root, Args = ["run", "dev"] }
    };

    private static IEnumerable<Button> Buttons(DependencyObject root)
    {
        if (root is Button button) yield return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Buttons(VisualTreeHelper.GetChild(root, index)))
                yield return child;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
}
