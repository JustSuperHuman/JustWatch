using System.Text.Json;
using JustWatch.Core.Services;

internal static class AndroidIntegrationTests
{
    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine($"PASS {name}"); count++;
        }
        var instances = MuMuClient.ParseInstances("""
            {"0":{"index":"0","name":"JG-1","error_code":0,"is_process_started":true,
              "is_android_started":true,"adb_host_ip":"127.0.0.1","adb_port":16384,"pid":123},
             "3":{"index":"3","name":"JG-4","error_code":0,"is_process_started":false}}
            """);
        Check(instances[0].Id == "mumu-0" && instances[0].Serial == "127.0.0.1:16384", "MuMu dynamic port and stable ID");
        Check(!instances[1].Started && instances[1].Serial == null, "Stopped instances do not pretend to be attached");
        var renamed = MuMuClient.ParseInstances("""{"index":0,"name":"Renamed","is_process_started":true,"is_android_started":false,"adb_host_ip":"127.0.0.1","adb_port":17000} """)[0];
        Check(renamed.Id == instances[0].Id && !renamed.AndroidReady, "Identity survives renames and booting is distinct from ready");
        var remote = MuMuClient.ParseInstances("""{"index":0,"name":"Remote","adb_host_ip":"192.0.2.1","adb_port":5555} """)[0];
        Check(remote.Serial == null, "Discovery cannot attach arbitrary remote ADB hosts");
        var devices = AndroidReloader.ParseDevices("List of devices attached\n127.0.0.1:16384\tdevice\n127.0.0.1:16416\toffline\nphone\tunauthorized\n");
        Check(devices.SetEquals(["127.0.0.1:16384"]), "Offline and unauthorized ADB devices excluded");
        var settings = new Dictionary<string, string> { ["performance_mode"] = "middle", ["performance_mem.middle"] = "6.000000",
            ["performance_cpu.middle"] = "4", ["performance_mem.custom"] = "4.000000", ["renderer_mode"] = "vk" };
        var config = AndroidReloader.ParseConfig(instances[0], settings);
        Check(config.RamSizeMb == 6144 && config.Cores == 4 && config.GpuMode == "vk", "Active preset RAM in GiB converted to MB, not stale custom value");
        settings["performance_mode"] = "custom"; settings["performance_cpu.custom"] = "2"; settings["performance_mem.custom"] = "1.75";
        Check(AndroidReloader.ParseConfig(instances[0], settings).RamSizeMb == 1792, "Fractional GiB tuning preserved");
        try { MuMuClient.ParseInstances("""{"0":{"error_code":42,"error_msg":"failed"}}"""); throw new Exception("Missing error"); }
        catch (InvalidOperationException) { Check(true, "CLI JSON failures throw even when process exits zero"); }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await MuMuClient.RunProcessAsync("powershell.exe", ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"], cancelled.Token); throw new Exception("Missing cancellation"); }
        catch (OperationCanceledException) { Check(true, "Cancellation stops the child process"); }
        try { await MuMuClient.RunProcessAsync("powershell.exe", ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"], default, 1); throw new Exception("Missing timeout"); }
        catch (TimeoutException) { Check(true, "Hung tools have a bounded timeout"); }
        // A real descendant inherits stdout and stays alive after its parent exits.
        // Build completion must follow the parent exit, not the descendant's lifetime.
        using var manager = new ProcessManager(new JustWatch.Core.Models.JustWatchConfig());
        var run = typeof(ProcessManager).GetMethod("RunLoggedCommandAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await (Task)run.Invoke(manager, [Environment.ProcessPath!, new[] { "--spawn-test-output-holder" },
            Environment.CurrentDirectory, "mobile", "output drain regression", CancellationToken.None])!;
        Check(timer.Elapsed < TimeSpan.FromSeconds(5), "Build completion is not held open by descendant output pipes");
        Console.WriteLine($"{count} Android integration checks passed.");
    }
}
