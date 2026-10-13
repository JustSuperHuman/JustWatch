using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using JustWatch.Core.Models;
using JustWatch.Core.Services;
using JustWatch.TrayApp;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

internal static class WindowsUiTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

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
            app.Startup += async (_, _) =>
            {
                try { await TestWindowAsync(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { app.Shutdown(); }
            };
            app.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static async Task TestWindowAsync()
    {
        // Never use the real config, services, credentials, or browser cache.
        var root = Path.Combine(Path.GetTempPath(), "justwatch-windows-ui-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var originalProfile = Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER");
        var originalTemp = Environment.GetEnvironmentVariable("TMP");
        var tempParent = Path.GetFullPath(Path.GetTempPath());
        // MainWindow also writes terminal.html under the temp directory.
        Environment.SetEnvironmentVariable("TMP", root);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(root, "webview"));
        var pinned = new ProcessConfig
        {
            Id = "mobile", Name = "Mobile", Project = "JustGains", ShowInTray = true,
            Command = Environment.ProcessPath!, Args = ["--service-test-child"],
            AutoStart = false, AutoRestart = false, AgentRecovery = false, ReclaimPort = false
        };
        var unpinned = ServiceConfiguration.Clone([pinned])[0];
        unpinned.Id = "optional"; unpinned.Name = "Optional"; unpinned.ShowInTray = false;
        var longName = ServiceConfiguration.Clone([pinned])[0];
        longName.Id = "long-name";
        longName.Name = "An unusually long service name that must leave its port readable";
        longName.Port = 65535;
        var config = new JustWatchConfig
        {
            ProjectName = "JustWatch UI regression tests", ProjectPath = root,
            ConfigPath = Path.Combine(root, "justwatch-config.json"), Processes = [pinned, unpinned, longName]
        };
        File.WriteAllText(config.ConfigPath, JsonSerializer.Serialize(config, ServiceConfiguration.JsonOptions));
        using var manager = new ProcessManager(config);
        using var chat = new ChatSession(config.Chat);
        var window = new MainWindow(manager, chat, new CommandCatalog(config))
        {
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
        };
        var webview = (WebView2)window.FindName("TerminalWebView");
        var externalRequests = new List<string>();
        var readyMessages = 0;
        webview.CoreWebView2InitializationCompleted += (_, args) =>
        {
            if (!args.IsSuccess) return;
            var core = webview.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, request) =>
            {
                if (new Uri(request.Request.Uri).Host == "justwatch.local") return;
                externalRequests.Add(request.Request.Uri);
                request.Response = core.Environment.CreateWebResourceResponse(null, 503, "Offline test", "");
            };
            core.WebMessageReceived += (_, message) =>
            {
                using var json = JsonDocument.Parse(message.WebMessageAsJson);
                if (json.RootElement.GetProperty("type").GetString() == "ready") readyMessages++;
            };
        };

        bool HasTab(string id) => ((StackPanel)window.FindName("TabBar")).Children
            .OfType<ToggleButton>().Any(button => (string)button.Tag == id);
        ToggleButton Tab(string id) => ((StackPanel)window.FindName("TabBar")).Children
            .OfType<ToggleButton>().Single(button => (string)button.Tag == id);
        bool HostReady() => (bool)typeof(MainWindow).GetField("_terminalReady", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        Task<string> Js(string script) => webview.CoreWebView2.ExecuteScriptAsync(script);
        async Task WaitJs(string expression, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (await Js(expression) != "true")
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException(message);
                await Task.Delay(50);
            }
        }

        try
        {
            window.Show();
            await WaitAsync(() => readyMessages > 0, "Initial WebView ready message");
            var initialized = await Js("initialized && !loadError");
            Console.WriteLine($"Startup evidence: initialized={initialized}, stopped pinned tab={HasTab("mobile")}, external requests={externalRequests.Count}");
            Check(initialized == "true" && HasTab("mobile"), "Cold offline startup renders terminal and stopped pinned Mobile tab");
            Check(externalRequests.Count == 0, "Terminal startup makes no external requests");
            Check(HostReady() && Tab("mobile").IsChecked == true, "Stopped pinned service is selected and host is ready");
            Check(((Button)window.FindName("BtnStartStop")).Content.ToString() == "Start", "Stopped pinned service offers Start");
            Check(((StackPanel)window.FindName("StatusIndicators")).Children.OfType<TextBlock>().Any(t => t.Text.Contains("Mobile")),
                "Stopped pinned service has a status indicator");
            Check(!HasTab("optional"), "Stopped unpinned service is hidden");
            var sidebar = (StackPanel)window.FindName("TabBar");
            var navigation = (ScrollViewer)window.FindName("ServiceTabs");
            Check(sidebar.Orientation == Orientation.Vertical && navigation.VerticalScrollBarVisibility == ScrollBarVisibility.Auto
                && navigation.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled, "Services use a vertically scrolling sidebar");
            Check(((TextBlock)window.FindName("PageTitle")).Text == "Mobile", "Selected service has a clear page heading");
            ((ColumnDefinition)window.FindName("SidebarColumn")).Width = new GridLength(360);
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.UpdateLayout();
            var sidebarBounds = navigation.TransformToAncestor(window).TransformBounds(new Rect(navigation.RenderSize));
            var terminalBounds = webview.TransformToAncestor(window).TransformBounds(new Rect(webview.RenderSize));
            var actions = (FrameworkElement)window.FindName("ProcessActionPanel");
            var actionBounds = actions.TransformToAncestor(window).TransformBounds(new Rect(actions.RenderSize));
            Check(sidebarBounds.Right < terminalBounds.Left && terminalBounds.Width >= 450,
                "Minimum window size keeps navigation beside a usable terminal");
            Check(actionBounds.Right <= window.ActualWidth && actionBounds.Left > sidebarBounds.Right,
                "Service controls fit at the minimum window size");
            var longRow = (Grid)Tab("long-name").Content;
            var nameViewport = longRow.Children.OfType<Grid>().Single();
            var port = longRow.Children.OfType<TextBlock>().Single();
            var portBounds = port.TransformToAncestor(longRow).TransformBounds(new Rect(port.RenderSize));
            var nameBounds = nameViewport.TransformToAncestor(longRow).TransformBounds(new Rect(nameViewport.RenderSize));
            Check(Tab("long-name").ActualHeight <= 42 && port.Text == ":65535"
                && port.ActualWidth + port.Margin.Left + port.Margin.Right >= port.DesiredSize.Width - 1,
                "Service rows stay one line and preserve the full five-digit port");
            Check(nameBounds.Right <= portBounds.Left && portBounds.Right <= longRow.ActualWidth + 1
                && nameViewport.OpacityMask != null, "Overflowing names fade before the right-aligned port");
            var shortRow = (Grid)Tab("mobile").Content;
            Check(shortRow.Children.OfType<Grid>().Single().OpacityMask == null
                && shortRow.Children.OfType<TextBlock>().Single().Visibility == Visibility.Collapsed,
                "Short names remain sharp and services without ports reclaim the space");
            var heading = (FrameworkElement)window.FindName("PageHeading");
            var headingBounds = heading.TransformToAncestor(window).TransformBounds(new Rect(heading.RenderSize));
            Check(heading.ActualHeight <= 24 && heading.Parent == actions.Parent
                && headingBounds.Right <= actionBounds.Left && headingBounds.Top >= actionBounds.Top
                && headingBounds.Bottom <= actionBounds.Bottom && window.FindName("PageDescription") == null,
                "Breadcrumbs fit beside service controls in the same toolbar at minimum window size");
            Tab("__agent__").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(((TextBlock)window.FindName("PageTitle")).Text == "Agent workspace" && actions.Visibility == Visibility.Collapsed,
                "Agent navigation updates the heading and hides service actions");
            Tab("mobile").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(actions.Visibility == Visibility.Visible && ((TextBlock)window.FindName("PageTitle")).Text == "Mobile",
                "Returning to a service restores its heading and controls");
            window.Width = 1260; window.Height = 800;
            ((ColumnDefinition)window.FindName("SidebarColumn")).Width = new GridLength(244);
            Check(await Js("getComputedStyle(document.querySelector('.xterm')).position === 'relative'") == "true", "Bundled xterm stylesheet is applied");
            Check(await Js("!!terminals.mobile.fitAddon && typeof WebLinksAddon !== 'undefined'") == "true", "Fit and web link addons load offline");

            await Js("""
                window.highlightFixture = '2026-10-03T12:34:56.789Z INFO GET https://localhost:6767/health\r\n' +
                    'ERROR WARN READY DEBUG src/main.ts:42:7 128ms "result": true\r\n';
                window.fixtureWritten = false;
                terminals.mobile.term.reset();
                terminals.mobile.term.write(highlightFixture.slice(0, 51));
                terminals.mobile.term.write(highlightFixture.slice(51), () => { window.fixtureWritten = true; });
                """);
            await WaitJs("""fixtureWritten && ['timestamp','info','method','url','error','warning','success','debug','path','duration','key'].every(k => document.querySelector('[data-log-token="' + k + '"]'))""",
                "Rendered live output highlights all token types across separate writes");
            await WaitJs("""['rgb(143, 201, 255)','rgb(243, 147, 159)','rgb(143, 162, 184)'].every(color => Array.from(terminals.mobile.container.querySelectorAll('.xterm-rows span')).some(s => getComputedStyle(s).color === color))""",
                "URL, error and timestamp colors reach the rendered terminal text");
            Check(true, "Syntax colors render on terminal text for URLs, timestamps and log levels");
            Check(await Js("""terminals.mobile.term.selectAll(); terminals.mobile.term.getSelection().replace(/\r?\n/g, '\n').includes(highlightFixture.trimEnd().replace(/\r?\n/g, '\n'))""") == "true",
                "Highlighting preserves selectable output text across split writes");
            await Js("terminals.mobile.term.clearSelection(); terminals.mobile.term.reset(); window.fixtureWritten=false; terminals.mobile.term.write('\u001b[35mERROR https://example.test\u001b[0m',()=>{fixtureWritten=true;terminals.mobile.highlighter.refresh()})");
            await WaitJs("fixtureWritten && !document.querySelector('[data-log-token]')", "ANSI colors take precedence over automatic highlighting");
            Check(await Js("!terminals.mobile.term.buffer.active.getLine(0).getCell(0).isFgDefault()") == "true",
                "Existing ANSI colors remain intact without additional highlights");

            await Js("""
                terminals.mobile.term.reset();
                window.fixtureWritten=false;
                const terminal=terminals.mobile.term;
                terminal.write('界 '.repeat(Math.floor(terminal.cols / 3) - 2) + 'https://localhost:6767/wrapped/path',()=>{fixtureWritten=true});
                """);
            await WaitJs("fixtureWritten && document.querySelectorAll('[data-log-token=\"url\"]').length >= 2", "URL highlight follows wide characters and wrapped lines");
            Check(await Js("terminals.mobile.term.buffer.active.getLine(1).isWrapped") == "true",
                "URLs stay highlighted across terminal wraps after wide characters");
            await Js("terminals.mobile.term.reset(); window.fixtureWritten=false; terminals.mobile.term.write('ERROR',()=>{fixtureWritten=true})");
            await WaitJs("fixtureWritten && !!document.querySelector('[data-log-token=\"error\"]')", "Error highlight is rendered before overwrite");
            await Js("""window.fixtureWritten=false; terminals.mobile.term.write('\r\u001b[2Kplain output',()=>{fixtureWritten=true;terminals.mobile.highlighter.refresh()})""");
            await WaitJs("fixtureWritten && !document.querySelector('[data-log-token]')", "Overwritten progress line loses stale highlights");
            Check(true, "Carriage returns and line erasure remove stale highlights");
            await Js("window.fixtureWritten=false; terminals.mobile.term.write('\u001b[?1049hERROR https://example.test',()=>{fixtureWritten=true;terminals.mobile.highlighter.refresh()})");
            await WaitJs("fixtureWritten && terminals.mobile.term.buffer.active.type === 'alternate' && !document.querySelector('[data-log-token]')", "Interactive alternate screen stays unmodified");
            Check(true, "Interactive alternate-screen applications keep their own presentation");
            await Js("terminals.mobile.term.write('\u001b[?1049l'); terminals.mobile.term.reset()");

            for (var run = 0; run < 2; run++)
            {
                var previous = readyMessages;
                webview.CoreWebView2.Reload();
                await WaitAsync(() => readyMessages > previous, "Reload ready message");
                Check(HostReady() && await Js("initialized && !loadError") == "true", "Offline terminal reload " + (run + 1));
            }

            manager.Start("mobile");
            await WaitAsync(() => ((Button)window.FindName("BtnStartStop")).Content.ToString() == "Stop", "Started pinned service action updates");
            await WaitAsync(() => manager.GetBufferedOutput("mobile").Contains("Service test ready"), "Child service output");
            await manager.StopAsync("mobile");
            await WaitAsync(() => ((Button)window.FindName("BtnStartStop")).Content.ToString() == "Start", "Stopped pinned service action updates");
            Check(HasTab("mobile") && Tab("mobile").IsChecked == true, "Stopping a pinned service preserves its selected tab");
            var text = await Js("Array.from({length:terminals.mobile.term.buffer.active.length},(_,i)=>terminals.mobile.term.buffer.active.getLine(i).translateToString()).join('\\n')");
            Check(text.Contains("Service test ready"), "Stopped pinned terminal retains its logs");

            manager.Start("optional");
            await WaitAsync(() => HasTab("optional"), "Started unpinned service appears");
            Tab("optional").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await manager.StopAsync("optional");
            await WaitAsync(() => !HasTab("optional"), "Stopped unpinned service disappears");
            Check(Tab("mobile").IsChecked == true, "Selection falls back to pinned service after unpinned service stops");

            var changed = manager.GetProcessConfigs().ToList();
            changed[1].ShowInTray = true;
            await manager.ApplyConfigurationAsync(changed, manager.ConfigurationRevision);
            await WaitAsync(() => HasTab("optional"), "Pinning a stopped service adds its tab immediately");
            changed[1].ShowInTray = false;
            await manager.ApplyConfigurationAsync(changed, manager.ConfigurationRevision);
            await WaitAsync(() => !HasTab("optional"), "Unpinning a stopped service removes its tab immediately");

            manager.Start("optional");
            await WaitAsync(() => HasTab("optional"), "Running optional tab returns before hiding");
            var hiddenPid = manager.GetPid("optional");
            Tab("optional").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            changed[1].HideFromTray = true;
            await manager.ApplyConfigurationAsync(changed, manager.ConfigurationRevision);
            await WaitAsync(() => !HasTab("optional"), "Hidden running service leaves Windows tabs immediately");
            Check(manager.GetPid("optional") == hiddenPid && Tab("mobile").IsChecked == true,
                "Hiding selected service preserves its process and selects a visible tab");
            changed[1].HideFromTray = false;
            await manager.ApplyConfigurationAsync(changed, manager.ConfigurationRevision);
            await WaitAsync(() => HasTab("optional"), "Unhiding running service restores Windows tab");
            await manager.StopAsync("optional");

            await Js("showError('Simulated initialization failure'); window.chrome.webview.postMessage({type:'ready',initialized:false,error:true})");
            await WaitAsync(() => !HostReady(), "Failed initialization is not accepted as ready");
            var previousReady = readyMessages;
            await Js("document.querySelector('#error-msg button').click()");
            await WaitAsync(() => readyMessages > previousReady && HostReady(), "Terminal recovers on reload");
            Check(await Js("initialized && document.getElementById('error-msg').classList.contains('hidden')") == "true",
                "Reload terminal button recovers from an initialization failure");
            Check(externalRequests.Count == 0, "Startup, reloads and lifecycle changes stay offline");
            Console.WriteLine("Windows UI regression tests passed.");
        }
        finally
        {
            await manager.StopAllAsync();
            webview.Dispose();
            window.Hide();
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", originalProfile);
            Environment.SetEnvironmentVariable("TMP", originalTemp);
            // WebView2 releases its profile asynchronously; all test data is confined to root.
            Check(Path.GetFullPath(root).StartsWith(Path.TrimEndingDirectorySeparator(tempParent) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "Test cleanup stays within the temporary directory");
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { } // WebView2 can retain temporary profile locks.
        }
    }

    private static async Task WaitAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException(message);
            await Task.Delay(50);
        }
    }
}
