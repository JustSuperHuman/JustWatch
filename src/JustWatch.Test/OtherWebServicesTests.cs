using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using JustWatch.TrayApp;

internal static class OtherWebServicesTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "justwatch-other-services-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pinsPath = Path.Combine(root, "pins.json");
        var readyPath = Path.Combine(root, "ready.txt");
        var spawnedPids = new HashSet<int>();
        try
        {
            var sample = OtherWebServices.ParseSnapshot("""
                [
                  {"port": 5173, "pid": 42, "startTime": 1700000000, "url": "http://localhost:5173/", "projectName": "Vite app", "projectRoot": "F:/demo", "framework": "Vite"},
                  {"port": 6767, "pid": 43, "startTime": 1700000001, "url": "http://localhost:6767/", "projectName": "JustWatch"},
                  {"port": 1234, "pid": 44, "url": "file:///tmp/test", "projectName": "Invalid"}
                ]
                """);
            Check(sample.Count == 2, "JustPorts JSON accepts only HTTP listeners");
            var catalog = new OtherWebServices(pinsPath);
            var scanned = await catalog.ScanAsync();
            Check(scanned.All(item => item.Port is > 0 and <= 65535),
                "Tray reads the installed JustPorts inventory");
            var visible = catalog.Merge(sample, new HashSet<int> { 6767 }, Environment.ProcessId);
            Check(visible.Count == 1 && !visible[0].IsPinned, "Managed ports are left out of Other");
            var pinnedWithSavedRecipe = visible[0].Service with
            {
                Launch = new OtherLaunchRecipe { Label = "npm run dev", Program = "npm", Cwd = root, Args = ["run", "dev"] }
            };
            catalog.Pin(pinnedWithSavedRecipe);
            Check(catalog.Merge([visible[0].Service], new HashSet<int>(), Environment.ProcessId)
                .Single().Service.Launch != null, "Pinned start command survives a sparse live scan");
            Check(catalog.Merge([], new HashSet<int>(), Environment.ProcessId).Single().IsPinned,
                "Pinned service stays visible while stopped");
            var reloaded = new OtherWebServices(pinsPath);
            Check(reloaded.Merge([], new HashSet<int>(), Environment.ProcessId).Single().Service.Pid == 0,
                "Pin survives reload without retaining stale process identity");
            reloaded.Unpin(visible[0].Service);
            Check(new OtherWebServices(pinsPath).Merge([], new HashSet<int>(), Environment.ProcessId).Count == 0,
                "Unpin removes the saved service");
            catalog.Unpin(visible[0].Service);

            var oldPort = sample[0] with { Port = 4317, Url = "http://localhost:4317/", ProcessName = "node.exe" };
            var newPort = oldPort with { Port = 5173, Url = "http://localhost:5173/", Pid = 99 };
            catalog.Pin(oldPort);
            var movedRows = catalog.Merge([newPort], new HashSet<int>(), Environment.ProcessId);
            Check(movedRows.Count == 1 && movedRows[0].IsPinned && movedRows[0].IsRunning
                && movedRows[0].Service.Port == 5173,
                "Pin follows a uniquely identified project listener to its new port");
            Check(new OtherWebServices(pinsPath).Merge([], new HashSet<int>(), Environment.ProcessId)
                .Single().Service.Port == 5173, "Changed port is persisted for the next launch");
            catalog.Unpin(movedRows[0].PinKey!);
            catalog.Pin(oldPort);
            var ambiguous = catalog.Merge([newPort, newPort with { Port = 5174, Url = "http://localhost:5174/" }],
                new HashSet<int>(), Environment.ProcessId);
            Check(ambiguous.Count(row => row.IsPinned) == 1 && !ambiguous.Single(row => row.IsPinned).IsRunning,
                "Sibling listeners are not mistaken for a moved pin");
            catalog.Unpin(oldPort);

            using var available = new TcpListener(IPAddress.Loopback, 0);
            available.Start();
            var port = ((IPEndPoint)available.LocalEndpoint).Port;
            available.Stop();
            using var oldPortReservation = new TcpListener(IPAddress.Loopback, 0);
            oldPortReservation.Start();
            var previousPort = ((IPEndPoint)oldPortReservation.LocalEndpoint).Port;
            oldPortReservation.Stop();
            File.WriteAllText(Path.Combine(root, "server.js"),
                $"const net = require('net'); const fs = require('fs'); " +
                $"net.createServer().listen({port}, '127.0.0.1', () => fs.writeFileSync('ready.txt', String(process.pid))); " +
                "setTimeout(() => process.exit(0), 30000);");
            File.WriteAllText(Path.Combine(root, "package.json"),
                JsonSerializer.Serialize(new { scripts = new Dictionary<string, string> { ["dev"] = "node server.js" } }));
            var service = new OtherWebService
            {
                Port = previousPort,
                Url = $"http://localhost:{previousPort}/",
                ProjectName = "Disposable test listener",
                ProjectRoot = root,
                ProcessName = "node.exe",
                Launch = new OtherLaunchRecipe
                {
                    Label = "npm run dev",
                    Program = "npm",
                    Args = ["run", "dev"],
                    Cwd = root
                }
            };
            await catalog.StartAsync(service);
            var firstPid = await WaitForPidAsync(readyPath, previous: 0);
            spawnedPids.Add(firstPid);
            var running = await FindListenerAsync(port, firstPid);
            Check(running.StartTime > 0, "JustPorts detects the disposable listener identity");
            catalog.Pin(service);
            var migrated = catalog.Merge([running], new HashSet<int>(), Environment.ProcessId);
            Check(migrated.Single().IsPinned && migrated.Single().IsRunning
                && migrated.Single().Service.Port == port,
                "Started service on a changed port remains pinned and running");
            var duplicateRejected = false;
            try { await catalog.StartAsync(service); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Already running"))
            {
                duplicateRejected = true;
            }
            Check(duplicateRejected && !Process.GetProcessById(firstPid).HasExited,
                "Start does not launch a duplicate when the pinned service moved ports");
            await catalog.StopAsync(running with { StartTime = running.StartTime + 1 });
            await WaitForExitAsync(firstPid);
            Check(true, "Stale tray identity is refreshed before the guarded stop");
            File.Delete(readyPath);
            await catalog.StartAsync(service);
            var middlePid = await WaitForPidAsync(readyPath, firstPid);
            spawnedPids.Add(middlePid);
            var middle = await FindListenerAsync(port, middlePid);
            File.Delete(readyPath);
            await catalog.RestartAsync(middle with { Launch = service.Launch });
            var secondPid = await WaitForPidAsync(readyPath, middlePid);
            spawnedPids.Add(secondPid);
            Check(secondPid != middlePid, "Restart replaces the exact pinned listener");
            var restarted = await FindListenerAsync(port, secondPid);
            await catalog.StopAsync(restarted);
            await WaitForExitAsync(secondPid);
            Check(true, "Stop terminates the pinned listener");

            var noListenerRoot = Path.Combine(root, "no-listener");
            Directory.CreateDirectory(noListenerRoot);
            var noListenerService = service with
            {
                ProjectRoot = noListenerRoot,
                Launch = new OtherLaunchRecipe
                {
                    Label = "node exits cleanly", Program = "node",
                    Args = ["-e", "process.exit(0)"], Cwd = noListenerRoot
                }
            };
            var fastCatalog = new OtherWebServices(Path.Combine(noListenerRoot, "pins.json"),
                startupTimeout: TimeSpan.FromSeconds(2));
            var missingListenerRejected = false;
            try { await fastCatalog.StartAsync(noListenerService); }
            catch (TimeoutException) { missingListenerRejected = true; }
            Check(missingListenerRejected, "Clean launcher exit is not reported as a successful start");
        }
        finally
        {
            foreach (var pid in spawnedPids)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { }
            }
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(root).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            }
        }
    }

    private static async Task<int> WaitForPidAsync(string path, int previous)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path), out var pid) && pid != previous)
                return pid;
            await Task.Delay(100);
        }
        throw new TimeoutException("Disposable listener did not start.");
    }

    private static async Task<OtherWebService> FindListenerAsync(int port, int pid)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var info = new ProcessStartInfo("justports.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            info.ArgumentList.Add("--json");
            info.ArgumentList.Add("--all");
            using var process = Process.Start(info)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Check(process.ExitCode == 0, "JustPorts inventory completes");
            var match = OtherWebServices.ParseSnapshot(output)
                .FirstOrDefault(service => service.Port == port && service.Pid == pid);
            if (match != null) return match;
            await Task.Delay(100);
        }
        throw new TimeoutException("JustPorts did not find the disposable listener.");
    }

    private static async Task WaitForExitAsync(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException)
        {
            // The guarded stop can finish before the exit wait acquires a handle.
        }
    }
}
