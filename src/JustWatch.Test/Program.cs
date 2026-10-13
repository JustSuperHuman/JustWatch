using JustWatch.Core.Services;
using JustWatch.Core.Models;

if (args.Contains("--project-tests") || args.Contains("--project-test-host")) { await ProjectDiscoveryTests.RunAsync(args.Contains("--project-test-host")); return; }
if (args.Contains("--windows-ui-tests")) { await WindowsUiTests.RunAsync(); return; }
if (args.Contains("--other-services-tests")) { await OtherWebServicesTests.RunAsync(); return; }
if (args.Contains("--other-flyout-ui-test")) { await OtherFlyoutUiTests.RunAsync(); return; }
if (args.Contains("--service-tests")) { await ServiceManagerTests.RunAsync(); return; }
if (args.Contains("--service-test-child")) { Console.WriteLine("Service test ready"); await Task.Delay(60000); return; }
if (args.Contains("--service-test-crash")) { await Task.Delay(300); Environment.Exit(7); return; }
if (args.Contains("--service-stop-fail")) { if (File.Exists(args[^1])) Environment.Exit(2); return; }

if (args.Contains("--hold-test-output")) { await Task.Delay(6000); return; }
if (args.Contains("--spawn-test-output-holder"))
{
    var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
    { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("--hold-test-output");
    using var holder = System.Diagnostics.Process.Start(start);
    return;
}

if (args.Contains("--android-tests"))
{
    await AndroidIntegrationTests.RunAsync();
    return;
}

Console.WriteLine("=== Pipeline Test: Fix Paywall Error + Demo ===\n");

var config = ConfigLoader.Load();
var chat = new ChatSession(config.Chat);

chat.AgentChunk += (ch, text) =>
{
    var color = ch switch { "opus" => ConsoleColor.Magenta, "sonnet" => ConsoleColor.Green, "orch" => ConsoleColor.Cyan, _ => ConsoleColor.Gray };
    Console.ForegroundColor = color;
    Console.Write(text);
    Console.ResetColor();
};

chat.TaskChanged += t =>
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"\n  TASK: {t.Name} -> {t.Status} ({t.Channel})");
    Console.ResetColor();
};

chat.ConvoMessage += (text, type, path) =>
{
    Console.ForegroundColor = type == "video" ? ConsoleColor.Green : type == "error" ? ConsoleColor.Red : ConsoleColor.DarkCyan;
    Console.WriteLine($"  CONVO: {text}" + (path != null ? $" [{path}]" : ""));
    Console.ResetColor();
};

chat.SessionCompleted += () => Console.WriteLine("\n=== SESSION DONE ===");

var prompt = """
Fix this bug in JustGains-Mobile: When opening "Present Paywall" from the Debug Menu,
a "Paywall Error: Cannot read property 'reload' of undefined" is shown.
The issue is that RevenueCat is not initialized (RC Initialized shows "No"),
so the paywall object is undefined when .reload() is called.

Fix the code to check if RevenueCat/paywall is initialized before calling reload,
and show a user-friendly message if RC isn't ready instead of crashing.

After fixing, record a demo of the paywall working from the debug menu on the Android emulator.
""";

Console.WriteLine($"Sending: {prompt[..80]}...\n");
await chat.SendAsync(prompt, config.ProjectPath);

chat.Dispose();
Console.WriteLine("\nDone.");
