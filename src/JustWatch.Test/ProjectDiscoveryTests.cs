using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using JustWatch.Core.Models;
using JustWatch.Core.Services;

internal static class ProjectDiscoveryTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }

    private static async Task Wait(Func<Task<bool>> ready, string message)
    {
        for (var n = 0; n < 120; n++) { if (await ready()) return; await Task.Delay(100); }
        throw new Exception("Timed out: " + message);
    }

    public static async Task RunAsync(bool host = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "justwatch-project-tests-" + Guid.NewGuid().ToString("N"));
        var watch = Path.Combine(root, "Workspace & projects"); Directory.CreateDirectory(watch);
        var configPath = Path.Combine(root, "justwatch-config.json");
        var config = new JustWatchConfig { ProjectPath = root, ConfigPath = configPath, ProjectName = "Project discovery tests" };
        // Exercise insertion into a legacy config without watchFolders, preserving BOM, comments and trailing commas.
        var original = "/* { preserved comment } */\n" + JsonSerializer.Serialize(config, ServiceConfiguration.JsonOptions);
        original = original.Replace("  \"watchFolders\": [],\n", "").Replace("\"projectName\":", "// keep this too\n  \"projectName\":");
        File.WriteAllText(configPath, original, new UTF8Encoding(true));
        using var manager = new ProcessManager(config);
        using var chat = new ChatSession(config.Chat);
        using var discovery = new ProjectDiscovery(manager);
        WebAgentServer? server = null;
        string Make(string folder, string name, string pm = "bun", bool dev = true)
        {
            var path = Path.Combine(watch, folder); Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "package.json"), JsonSerializer.Serialize(new
            {
                name, @private = true,
                scripts = dev ? new Dictionary<string,string> { ["dev"] = "node server.mjs", ["dev:alternate"] = "node server.mjs", ["build"] = "echo build", ["prestart"] = "echo prestart" } : [],
                devDependencies = new Dictionary<string,string> { ["vite"] = "*" }
            }));
            File.WriteAllText(Path.Combine(path, pm switch { "bun" => "bun.lock", "pnpm" => "pnpm-lock.yaml", "yarn" => "yarn.lock", _ => "package-lock.json" }), "");
            File.WriteAllText(Path.Combine(path, "server.mjs"), "import http from 'node:http'; const s=http.createServer((q,r)=>r.end('JustWatch project fixture')); s.listen(0,'127.0.0.1',()=>console.log('Local: http://localhost:'+s.address().port+'/')); ");
            return path;
        }
        try
        {
            WebServiceDetectorTests.Run(root);
            var bun = Make("Aurora Studio", "aurora-studio");
            var npm = Make("nested/Nimbus", "nimbus", "npm");
            var pnpm = Make("Folio", "folio", "pnpm");
            var yarn = Make("Canvas", "canvas", "yarn");
            var lib = Make("Shared", "shared-library", dev:false);
            Make("node_modules/ignored", "ignored"); Make("nested/.next/ignored", "ignored-next"); Make("tmp/deploy/ignored", "ignored-temp"); Make("bun-cache/package/examples/site", "ignored-cache");
            var bad = Path.Combine(watch, "invalid"); Directory.CreateDirectory(bad); File.WriteAllText(Path.Combine(bad, "package.json"), "{bad}");
            Directory.CreateDirectory(Path.Combine(bun, "public"));
            File.WriteAllText(Path.Combine(npm, "favicon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 40 40\"><circle cx=\"20\" cy=\"20\" r=\"18\" fill=\"#3b82f6\"/></svg>");
            File.WriteAllText(Path.Combine(bun, "public/favicon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 40 40\"><rect width=\"40\" height=\"40\" rx=\"10\" fill=\"#8272d9\"/><path d=\"M10 28 20 10 30 28M14 23h12\" fill=\"none\" stroke=\"white\" stroke-width=\"3\"/></svg>");
            var parsed = ProjectDiscovery.ScanFolders([watch, Path.Combine(watch, "nested"), Path.Combine(root, "missing")]);
            Check(parsed.Projects.Length == 4 && parsed.Projects.Select(p => p.Id).Distinct().Count() == 4, "Recursive scan deduplicates overlapping roots and skips dependency/build/temp folders");
            Check(parsed.Folders[0].Skipped == 1 && parsed.Folders[2].Error != null, "Malformed packages and unavailable drives are reported without aborting discovery");
            Check(parsed.Projects.Single(p => p.Name == "aurora-studio").Icon?.ContentType == "image/svg+xml", "Local project favicon discovered");
            Check(parsed.Projects.All(p => p.Name != "shared-library" && p.Scripts.Length > 0), "Library contains only projects with detected web services");
            Check(parsed.Projects.Take(2).Select(p => p.Name).SequenceEqual(["aurora-studio", "nimbus"]), "Apps with favicons sort ahead of alphabetically earlier apps without icons");
            Check(parsed.Projects.All(p => p.Scripts.All(s => s.Name is "dev" or "dev:alternate")), "Build and lifecycle scripts are not offered as dev servers");
            Check(parsed.Projects.Single(p => p.Name == "nimbus").PackageManager == "npm", "Project lockfile selects its package manager");
            var inherited = Path.Combine(watch, "monorepo/apps/web"); Directory.CreateDirectory(inherited);
            File.WriteAllText(Path.Combine(watch, "monorepo/package.json"), "{\"packageManager\":\"pnpm@10\"}");
            File.WriteAllText(Path.Combine(inherited, "package.json"), "{\"name\":\"inherited\",\"scripts\":{\"dev\":\"vite\"}}");
            Check(ProjectDiscovery.ReadProject(inherited).PackageManager == "pnpm", "Nested workspace inherits its package manager");
            File.WriteAllText(Path.Combine(inherited, "bun.lock"), "");
            Check(ProjectDiscovery.ReadProject(inherited).PackageManager == "bun", "Nearest workspace lockfile wins");
            Check(!ProjectDiscovery.IsWithin(watch + "-other", watch), "Folder matching respects directory boundaries");
            Check(ProjectDiscovery.IsWithin(@"F:\Projects\app", @"F:\") && !ProjectDiscovery.IsWithin(@"J:\Projects", @"F:\"), "Whole-drive watch folders include their descendants");
            await manager.UpdateWatchFolderAsync(watch, false);
            var saved = File.ReadAllText(configPath);
            Check(saved.Contains("/* { preserved comment } */") && saved.Contains("// keep this too") && File.ReadAllBytes(configPath).Take(3).SequenceEqual(new byte[] {239,187,191}), "Watch-folder save preserves configuration comments and UTF-8 BOM");
            var reloaded = ConfigLoader.Load(configPath);
            Check(reloaded.WatchFolders.SequenceEqual([watch]) && reloaded.Processes.Count == 0, "Watch folders persist across reload without changing services");
            await manager.UpdateWatchFolderAsync(watch.ToUpperInvariant() + Path.DirectorySeparatorChar, false);
            Check(config.WatchFolders.Count == 1, "Duplicate paths and trailing separators are normalized");
            await discovery.RequestScan();
            Check(discovery.Scan.Projects.Length == 5, "Background discovery publishes only web applications");
            var first = discovery.Scan.Projects.Single(p => p.Name == "aurora-studio");
            await Task.WhenAll(discovery.StartProjectAsync(first.Id, "dev"), discovery.StartProjectAsync(first.Id, "dev"));
            var service = discovery.FindService(first, "dev")!;
            await Wait(() => Task.FromResult(manager.GetStatus(service.Id) == ProcessStatus.Running), "Bun starts");
            Check(manager.GetProcessConfigs().Count == 1 && manager.GetPid(service.Id) > 0, "Concurrent starts create one persisted service and one process");
            Check(!service.AutoStart && !service.ReclaimPort && !service.AgentRecovery && !service.AutoRestart, "Discovered projects launch only on request without crash loops or port takeover");
            await manager.StopAsync(service.Id);
            Check(manager.GetPid(service.Id) == null, "Discovered process stops cleanly");
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); config.Web.Port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            server = new WebAgentServer(chat, config, manager); server.Start();
            using var http = new HttpClient { BaseAddress = new Uri(server.BoundUrl!) };
            async Task<JsonDocument> Snapshot() => JsonDocument.Parse(await http.GetStringAsync("api/projects"));
            async Task<HttpResponseMessage> Send(string route, object value, HttpMethod? method = null) => await http.SendAsync(new HttpRequestMessage(method ?? HttpMethod.Post, route) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") });
            await Wait(async () => { using var snap=await Snapshot(); return !snap.RootElement.GetProperty("scanning").GetBoolean() && snap.RootElement.GetProperty("projects").GetArrayLength() == 5; }, "HTTP discovery");
            var html = await http.GetStringAsync("projects");
            Check(html.Contains("aria-label=\"Workspace\"") && html.Contains("function projectCard") && html.Contains(".project-card {") && !html.Contains("/* PROJECT_") && !html.Contains("<!-- WORKSPACE_NAV -->"), "Complete application UI served with shared navigation, embedded styles and scripts");
            var iconResponse = await http.GetAsync("api/projects/"+first.Id+"/icon");
            Check(iconResponse.IsSuccessStatusCode && iconResponse.Content.Headers.ContentType?.MediaType == "image/svg+xml" && iconResponse.Headers.Contains("Content-Security-Policy"), "Favicon endpoint serves only a discovered image with isolated SVG policy");
            http.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
            var rejected = await Send("api/projects/"+first.Id+"/start",new { script="dev" });
            Check(rejected.StatusCode == HttpStatusCode.Forbidden && !rejected.Headers.Contains("Access-Control-Allow-Origin"), "Project launch and filesystem discovery reject cross-origin callers");
            http.DefaultRequestHeaders.Remove("Origin");
            foreach (var name in new[] { "aurora-studio", "nimbus", "folio", "canvas" })
            {
                var project = discovery.Scan.Projects.Single(p=>p.Name==name);
                var response = await Send("api/projects/"+project.Id+"/start", new { script="dev" });
                Check(response.IsSuccessStatusCode, "HTTP start accepted for "+project.PackageManager+": "+(response.IsSuccessStatusCode?"ok":await response.Content.ReadAsStringAsync()));
                string? url = null;
                await Wait(async () => { using var snap=await Snapshot(); var script=snap.RootElement.GetProperty("projects").EnumerateArray().Single(p=>p.GetProperty("id").GetString()==project.Id).GetProperty("scripts")[0]; if (script.GetProperty("status").GetString()=="crashed") throw new Exception(manager.GetBufferedOutput(discovery.FindService(project,"dev")!.Id)); url=script.GetProperty("url").GetString(); return url != null; }, project.PackageManager+" server URL");
                using var appHttp = new HttpClient();
                Check(await appHttp.GetStringAsync(url!.Replace("localhost", "127.0.0.1")) == "JustWatch project fixture", project.PackageManager+" starts a real HTTP dev server from a path with spaces and ampersand");
                var output=await http.GetStringAsync("api/projects/"+project.Id+"/logs?script=dev");
                Check(output.Contains("Local:"), "Logs captured for "+project.PackageManager);
                await Send("api/projects/"+project.Id+"/stop",new { script="dev" });
                Check(manager.GetPid(discovery.FindService(project,"dev")!.Id)==null, project.PackageManager+" process stops through the API");
                await Wait(async () => { using var probe = new TcpClient(); try { await probe.ConnectAsync("127.0.0.1", new Uri(url!).Port); return false; } catch (SocketException) { return true; } }, project.PackageManager+" listener closes on Stop");
                Check(true, project.PackageManager+" server listener is closed after Stop");
            }
            // Removed scripts must not execute even when the last scan still advertises them.
            File.WriteAllText(Path.Combine(bun,"package.json"), "{\"name\":\"aurora-studio\"}");
            Check((await Send("api/projects/"+first.Id+"/start",new { script="dev" })).StatusCode == HttpStatusCode.BadRequest, "Start revalidates package.json instead of executing a stale script");
            File.WriteAllText(Path.Combine(bun,"package.json"), "{\"name\":\"aurora-studio\",\"scripts\":{\"dev\":\"echo no longer a server\"}}");
            Check((await Send("api/projects/"+first.Id+"/start",new { script="dev" })).StatusCode == HttpStatusCode.BadRequest, "A script changed to a non-web command cannot launch from stale discovery");
            Make("Aurora Studio","aurora-studio");
            var deletePath = Make("Delete Me", "delete-me");
            Make("Delete Me/nested-child", "nested-child");
            var external = Path.Combine(root, "external-preserved"); Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "keep.txt"), "preserve this outside the project");
            var link = Path.Combine(deletePath, "linked-dependency");
            var junction = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", link, external }) junction.ArgumentList.Add(arg);
            using (var child = System.Diagnostics.Process.Start(junction)!) { await child.WaitForExitAsync(); Check(child.ExitCode == 0, "Deletion fixture creates a junction to an unrelated folder"); }
            var readOnly = Path.Combine(deletePath, "readonly.txt"); File.WriteAllText(readOnly, "fixture"); File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            await Send("api/projects/scan", new { });
            string? deleteId = null, nestedId = null;
            await Wait(async () => { using var snap = await Snapshot(); var projects = snap.RootElement.GetProperty("projects").EnumerateArray().ToArray(); deleteId = projects.Where(p => p.GetProperty("name").GetString() == "delete-me").Select(p=>p.GetProperty("id").GetString()).FirstOrDefault(); nestedId = projects.Where(p => p.GetProperty("name").GetString() == "nested-child").Select(p=>p.GetProperty("id").GetString()).FirstOrDefault(); return deleteId != null && nestedId != null && !snap.RootElement.GetProperty("scanning").GetBoolean(); }, "Deletion fixtures discovered");
            async Task<string> DeletionToken() { using var preview = JsonDocument.Parse(await http.GetStringAsync("api/projects/"+deleteId+"/delete-preview")); return preview.RootElement.GetProperty("token").GetString()!; }
            Check((await Send("api/projects/"+deleteId, new { confirmation="delete-me" }, HttpMethod.Delete)).StatusCode == HttpStatusCode.BadRequest && Directory.Exists(deletePath), "Deletion requires a fresh server-issued confirmation");
            var wrongToken = await DeletionToken();
            Check((await Send("api/projects/"+deleteId, new { token=wrongToken, confirmation="wrong name" }, HttpMethod.Delete)).StatusCode == HttpStatusCode.BadRequest && Directory.Exists(deletePath), "Incorrect confirmation cannot delete files");
            var scopedToken = await DeletionToken();
            Check((await Send("api/projects/"+first.Id, new { token=scopedToken, confirmation="delete-me" }, HttpMethod.Delete)).StatusCode == HttpStatusCode.BadRequest && Directory.Exists(bun) && Directory.Exists(deletePath), "Deletion confirmation is bound to exactly one project");
            var configurationToken = await DeletionToken();
            var updatedServices = manager.GetProcessConfigs().ToList(); updatedServices[0].Name += " updated";
            await manager.ApplyConfigurationAsync(updatedServices, manager.ConfigurationRevision);
            Check((await Send("api/projects/"+deleteId, new { token=configurationToken, confirmation="delete-me" }, HttpMethod.Delete)).StatusCode == HttpStatusCode.Conflict && Directory.Exists(deletePath), "Service configuration changes require a new deletion review");
            var staleToken = await DeletionToken();
            File.AppendAllText(Path.Combine(deletePath,"package.json"), " ");
            Check((await Send("api/projects/"+deleteId, new { token=staleToken, confirmation="delete-me" }, HttpMethod.Delete)).StatusCode == HttpStatusCode.Conflict && Directory.Exists(deletePath), "A changed manifest invalidates deletion confirmation");
            await manager.UpdateWatchFolderAsync(deletePath, false);
            Check((await http.GetAsync("api/projects/"+deleteId+"/delete-preview")).StatusCode == HttpStatusCode.Conflict, "A watch-folder root cannot be deleted");
            await manager.UpdateWatchFolderAsync(deletePath, true);
            var urls = new List<string>();
            foreach (var target in new[] { deleteId, nestedId })
            {
                Check((await Send("api/projects/"+target+"/start", new { script="dev" })).IsSuccessStatusCode, "Deletion fixture HTTP service starts");
                await Wait(async () => { using var snap=await Snapshot(); var url=snap.RootElement.GetProperty("projects").EnumerateArray().Single(p=>p.GetProperty("id").GetString()==target).GetProperty("scripts")[0].GetProperty("url").GetString(); if(url==null)return false;urls.Add(url);return true; }, "Deletion fixture URL");
            }
            using (var preview = JsonDocument.Parse(await http.GetStringAsync("api/projects/"+deleteId+"/delete-preview")))
                Check(preview.RootElement.GetProperty("projects").GetArrayLength()==2 && preview.RootElement.GetProperty("services").GetArrayLength()==2 && preview.RootElement.GetProperty("directory").GetString()!.Equals(deletePath,StringComparison.OrdinalIgnoreCase), "Deletion preview identifies the exact folder, nested projects, and affected services");
            var token = await DeletionToken();
            http.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
            Check((await Send("api/projects/"+deleteId,new { token,confirmation="delete-me" },HttpMethod.Delete)).StatusCode == HttpStatusCode.Forbidden && Directory.Exists(deletePath), "Cross-origin callers cannot delete project files");
            http.DefaultRequestHeaders.Remove("Origin");
            var deleted = await Send("api/projects/"+deleteId, new { token, confirmation="delete-me" }, HttpMethod.Delete);
            Check(deleted.IsSuccessStatusCode, "Confirmed filesystem deletion succeeds: "+(deleted.IsSuccessStatusCode?"ok":await deleted.Content.ReadAsStringAsync()));
            Check(!Directory.Exists(deletePath) && File.ReadAllText(Path.Combine(external,"keep.txt"))=="preserve this outside the project", "Deletion removes project files and readonly files without following junction targets");
            Check(manager.GetProcessConfigs().Count==4 && ConfigLoader.Load(configPath).Processes.Count==4 && Directory.Exists(bun) && Directory.Exists(npm), "Deletion removes only affected service configurations and preserves other projects");
            foreach (var url in urls)
                await Wait(async () => { using var probe=new TcpClient();try{await probe.ConnectAsync("127.0.0.1",new Uri(url).Port);return false;}catch(SocketException){return true;} }, "Deleted project listener closes");
            Check(true, "Deleting a running project closes its own and nested HTTP listeners");
            Check(!(await Send("api/projects/"+deleteId,new { token,confirmation="delete-me" },HttpMethod.Delete)).IsSuccessStatusCode, "Deletion confirmation cannot be replayed");
            using (var snap=await Snapshot()) Check(!snap.RootElement.GetProperty("projects").EnumerateArray().Any(p=>p.GetProperty("id").GetString()==deleteId||p.GetProperty("id").GetString()==nestedId), "Deleted projects disappear from the live library");
            if (host)
            {
                // Interactive UI verification uses this isolated server; Enter cleans up everything.
                Console.WriteLine("UI_URL="+server.BoundUrl+"projects");
                Console.WriteLine("UI_WATCH_FOLDER="+watch);
                Console.WriteLine("Press Enter after browser verification to stop and clean the fixtures.");
                await Task.Run(Console.ReadLine);
            }
            await manager.UpdateWatchFolderAsync(watch, true);
            await discovery.RequestScan(changed:true);
            Check(discovery.Scan.Projects.Length==0 && manager.GetProcessConfigs().Count==4, "Removing a watch folder clears discovery while preserving managed service configurations");
            Check(ConfigLoader.Load(configPath).WatchFolders.Count==0, "Folder removal persists");
            Console.WriteLine("Project discovery integration tests passed.");
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); throw; }
        finally
        {
            server?.Dispose(); discovery.Dispose(); await manager.StopAllAsync(); manager.Dispose();
            // Every fixture is under the unique test directory, never under a real watched project.
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup path");
            var fixtureLink = Path.Combine(watch, "Delete Me/linked-dependency");
            if (Directory.Exists(fixtureLink) && (File.GetAttributes(fixtureLink) & FileAttributes.ReparsePoint) != 0) Directory.Delete(fixtureLink);
            var fixtureReadOnly = Path.Combine(watch,"Delete Me/readonly.txt");
            if (File.Exists(fixtureReadOnly)) File.SetAttributes(fixtureReadOnly, FileAttributes.Normal);
            for (var attempt=0; ; attempt++)
            {
                try { Directory.Delete(root,true); break; }
                catch (IOException) when (attempt<20) { await Task.Delay(250); }
            }
        }
    }
}
