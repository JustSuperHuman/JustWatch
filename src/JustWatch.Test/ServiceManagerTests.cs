using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using JustWatch.Core.Models;
using JustWatch.Core.Services;

internal static class ServiceManagerTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "justwatch-service-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "justwatch-config.json");
        var service = new ProcessConfig { Id = "other-project", Name = "Other project", Command = Environment.ProcessPath!,
            Args = ["--service-test-child"], AutoStart = false, AgentRecovery = false, ReclaimPort = false };
        var config = new JustWatchConfig { ProjectName = "Another project", ProjectPath = root, ConfigPath = path, Processes = [service] };
        var original = JsonSerializer.Serialize(config, ServiceConfiguration.JsonOptions);
        original = original.Replace("\"processes\":", "// Keep this comment\n  \"processes\":");
        await File.WriteAllTextAsync(path, original);
        using var manager = new ProcessManager(config);
        try
        {
            foreach (var invalid in new[] {
                new ProcessConfig { Id = "bad/id", Name = "Bad", Command = "x" },
                new ProcessConfig { Id = "configuration", Name = "Bad", Command = "x" },
                new ProcessConfig { Id = "bad", Name = "Bad", Command = "x", TrayColor = "red" },
                new ProcessConfig { Id = "bad", Name = "Bad", Command = "x", Url = "javascript:alert(1)" } })
            {
                var rejected = false;
                try { ServiceConfiguration.Validate([invalid]); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Invalid definition rejected: " + invalid.Id + " / " + invalid.TrayColor);
            }
            var duplicate = false;
            try { ServiceConfiguration.Validate([service, service]); } catch (ArgumentException) { duplicate = true; }
            Check(duplicate, "Duplicate IDs rejected");
            manager.Start(service.Id);
            await Task.Delay(450);
            var pid = manager.GetPid(service.Id);
            Check(pid > 0 && manager.GetStatus(service.Id) == ProcessStatus.Running, "Arbitrary project service starts");
            var revision = manager.ConfigurationRevision;
            var changed = manager.GetProcessConfigs().ToList();
            changed[0].Name = "Renamed service";
            changed[0].Project = "Renamed project";
            changed[0].ShowInTray = false;
            changed[0].TrayColor = "#123456";
            changed[0].ShowTrayDot = true;
            await manager.ApplyConfigurationAsync(changed, revision);
            Check(manager.GetPid(service.Id) == pid, "Project, menu visibility and tray changes preserve running PID");
            Check(ServiceVisibility.IsVisibleInTray(manager.GetServiceState(service.Id)!), "Started unpinned service always appears in tray");
            var trayState = manager.GetServiceState(service.Id)!;
            Check(!ServiceVisibility.IsVisibleInTray(trayState with { Status = ProcessStatus.Stopped }), "Unpinned stopped service leaves tray");
            Check(ServiceVisibility.IsVisibleInTray(trayState with { Status = ProcessStatus.Stopped, ShowInTray = true }), "Pinned stopped service remains in tray");
            Check(new[] { ProcessStatus.Starting, ProcessStatus.Stopping, ProcessStatus.RecoveringWithAgent }
                .All(status => ServiceVisibility.IsVisibleInTray(trayState with { Status = status })), "Lifecycle transitions keep started services in tray");
            Check(Enum.GetValues<ProcessStatus>().All(status => !ServiceVisibility.IsVisibleInTray(
                trayState with { Status = status, ShowInTray = true, HideFromTray = true })),
                "Hidden services stay out of tray in every lifecycle state, even when pinned");
            changed[0].HideFromTray = true;
            await manager.ApplyConfigurationAsync(changed, manager.ConfigurationRevision);
            Check(manager.GetPid(service.Id) == pid && !ServiceVisibility.IsVisibleInTray(manager.GetServiceState(service.Id)!),
                "Hiding a running service updates visibility without restarting it");
            Check(ConfigLoader.Load(path).Processes[0].HideFromTray, "Hidden preference survives configuration reload");
            changed[0].HideFromTray = false;
            await manager.ApplyConfigurationAsync(changed, manager.ConfigurationRevision);
            Check(manager.GetPid(service.Id) == pid && ServiceVisibility.IsVisibleInTray(manager.GetServiceState(service.Id)!),
                "Unhiding a running service restores visibility without restarting it");
            Check(File.ReadAllText(path).Contains("// Keep this comment"), "Save preserves unrelated comments");
            Check(ConfigLoader.Load(path).Processes[0].TrayColor == "#123456", "Configuration survives reload");
            var conflict = false;
            try { await manager.ApplyConfigurationAsync(changed, revision); } catch (InvalidOperationException) { conflict = true; }
            Check(conflict, "Stale configuration revision rejected");
            changed[0].Environment["SERVICE_TEST"] = "changed";
            await manager.ApplyConfigurationAsync(changed, manager.ConfigurationRevision);
            Check(manager.GetPid(service.Id) != pid && manager.GetPid(service.Id) > 0, "Command environment change restarts affected service");
            await manager.ApplyConfigurationAsync([], manager.ConfigurationRevision);
            Check(manager.GetProcessConfigs().Count == 0 && manager.GetPid(service.Id) == null, "Removing running service stops it");

            service.Args = ["--service-test-crash"];
            await manager.ApplyConfigurationAsync([service], manager.ConfigurationRevision);
            manager.Start(service.Id);
            for (var attempt = 0; attempt < 50 && manager.GetStatus(service.Id) != ProcessStatus.Crashed; attempt++)
                await Task.Delay(100);
            Check(manager.GetStatus(service.Id) == ProcessStatus.Crashed, "Crash schedules recovery");
            Check(manager.GetServiceStates().Count == 1 && !ServiceVisibility.IsStarted(manager.GetStatus(service.Id)), "Failed service stays visible to agents but is not a started tab");
            await manager.StopAsync(service.Id);
            await Task.Delay(2300);
            Check(manager.GetStatus(service.Id) == ProcessStatus.Stopped && manager.GetPid(service.Id) == null, "Stop cancels delayed crash restart");

            service.Args = ["--service-test-child"];
            service.StopCommand = Environment.ProcessPath!;
            var stopFailureFlag = Path.Combine(root, "fail-stop");
            File.WriteAllText(stopFailureFlag, "test");
            service.StopArgs = ["--service-stop-fail", stopFailureFlag];
            await manager.ApplyConfigurationAsync([service], manager.ConfigurationRevision);
            manager.Start(service.Id);
            var beforeFailure = File.ReadAllText(path);
            var beforeRevision = manager.ConfigurationRevision;
            var rejectedStop = false;
            try { await manager.ApplyConfigurationAsync([], beforeRevision); }
            catch (InvalidOperationException) { rejectedStop = true; }
            Check(rejectedStop && manager.ConfigurationRevision == beforeRevision && File.ReadAllText(path) == beforeFailure,
                "Failed stop preserves disk and runtime configuration");
            File.Delete(stopFailureFlag);
            await manager.ApplyConfigurationAsync([], manager.ConfigurationRevision);
            service.StopCommand = ""; service.StopArgs = []; service.Args = ["--service-test-child"];
            await manager.ApplyConfigurationAsync([service], manager.ConfigurationRevision);

            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            config.Web.Port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            using var chat = new ChatSession(new ChatConfig());
            using var server = new WebAgentServer(chat, config, manager);
            server.Start();
            using var client = new HttpClient { BaseAddress = new Uri(server.BoundUrl!) };
            var response = await client.GetAsync("api/services");
            var body = await response.Content.ReadAsStringAsync();
            Check(response.IsSuccessStatusCode && !body.Contains("other-project"), "Default API omits stopped services");
            service.Project = "Project A";
            service.Url = "https://example.test/service";
            var parked = ServiceConfiguration.Clone([service])[0];
            parked.Id = "occasional-worker"; parked.Project = "Project B";
            parked.Description = new string('x', 4000);
            await manager.ApplyConfigurationAsync([service, parked], manager.ConfigurationRevision);
            manager.Start(service.Id);
            body = await client.GetStringAsync("api/services");
            Check(body.Contains("other-project") && body.Contains("Project A") && !body.Contains("occasional-worker") &&
                !body.Contains("description") && !body.Contains("trayColor") && !body.Contains("environment"),
                "Default API returns compact active status without parked metadata or secrets");
            var allBody = await client.GetStringAsync("api/services?scope=all");
            Check(allBody.Contains("occasional-worker") && !allBody.Contains("description"), "Explicit discovery includes stopped services with compact payloads");
            var fullBody = await client.GetStringAsync("api/services?scope=all&detail=full");
            Check(fullBody.Contains("trayColor") && fullBody.Contains("showInTray") && !fullBody.Contains("environment") && fullBody.Length > allBody.Length,
                "Full display metadata is opt-in and still excludes execution secrets");
            body = await client.GetStringAsync("api/services?scope=all&project=project%20b");
            Check(body.Contains("occasional-worker") && !body.Contains("other-project"), "Project filters are case-insensitive and apply before serialization");
            Check((await client.GetStringAsync("api/services?scope=all&project=Project+B")).Contains("occasional-worker"), "Form-encoded project names are supported");
            Check((await client.GetStringAsync("api/services?project=Project%20B")) == "{\"services\":[]}", "Project filter preserves active-only default");
            Check((await client.GetAsync("api/services?scope=typo")).StatusCode == HttpStatusCode.BadRequest, "Invalid scope does not silently expand agent output");
            Check((await client.GetAsync("api/services?detail=typo")).StatusCode == HttpStatusCode.BadRequest, "Invalid detail rejected");
            body = await client.GetStringAsync("api/services/occasional-worker");
            Check(body.Contains("stopped") && body.Contains("Project B"), "Stopped service remains accessible directly by ID");
            Check(body.Contains("https://example.test/service"), "Stopped service retains its configured link");
            Check(new ProcessConfig { Port = 4567 }.EffectiveUrl == "http://localhost:4567", "Service port provides default link");
            Check(new ProcessConfig { Port = 4567, Url = "https://example.test/override" }.EffectiveUrl == "https://example.test/override", "Explicit link takes precedence over port");
            Check((await client.DeleteAsync("api/services/other-project/start")).StatusCode == HttpStatusCode.MethodNotAllowed, "Invalid action method rejected");
            Check((await client.GetAsync("api/services/other-project/start/extra")).StatusCode == HttpStatusCode.NotFound, "Extra action segments rejected");
            var configuration = await client.GetStringAsync("api/services/configuration");
            using var document = JsonDocument.Parse(configuration);
            var request = new { revision = document.RootElement.GetProperty("revision").GetString(), services = manager.GetProcessConfigs() };
            response = await client.PutAsync("api/services/configuration", new StringContent(JsonSerializer.Serialize(request, ServiceConfiguration.JsonOptions), Encoding.UTF8, "application/json"));
            Check(response.IsSuccessStatusCode, "Configuration API saves successfully");
            client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
            response = await client.GetAsync("api/services/configuration");
            Check(response.StatusCode == HttpStatusCode.Forbidden && !response.Headers.Contains("Access-Control-Allow-Origin"), "Configuration blocks cross-origin callers");
            client.DefaultRequestHeaders.Remove("Origin");
            Check((await client.GetStringAsync("services")).Contains("Save changes"), "Services UI served by live HTTP server");
            Console.WriteLine("Service manager integration tests passed.");
        }
        finally
        {
            await manager.StopAllAsync();
            manager.Dispose();
            // Release native process handles before deleting their working directory.
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(root, true); break; }
                catch (IOException) when (attempt < 20) { await Task.Delay(250); }
            }
        }
    }
}
