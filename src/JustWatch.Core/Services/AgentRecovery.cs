using System.Diagnostics;
using System.Text;
using JustWatch.Core.Models;

namespace JustWatch.Core.Services;

public class AgentRecovery
{
    private readonly AgentConfig _config;

    public event Action<string>? OutputReceived;

    public AgentRecovery(AgentConfig config) => _config = config;

    public async Task<bool> AttemptRecoveryAsync(
        string processId,
        string[] recentOutput,
        string workingDirectory,
        CancellationToken ct)
    {
        var prompt = BuildRecoveryPrompt(processId, recentOutput);

        var psi = new ProcessStartInfo
        {
            FileName = _config.Command,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in _config.Args)
            psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(prompt);

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            var outputTask = ReadStreamAsync(process.StandardOutput, ct);
            var errorTask = ReadStreamAsync(process.StandardError, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                OutputReceived?.Invoke("[JUSTWATCH] Agent recovery timed out.\r\n");
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke($"[JUSTWATCH] Agent recovery failed to start: {ex.Message}\r\n");
            return false;
        }
    }

    private async Task ReadStreamAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var count = await reader.ReadAsync(buffer, ct);
                if (count == 0) break;
                OutputReceived?.Invoke(new string(buffer, 0, count));
            }
        }
        catch (OperationCanceledException) { }
    }

    private static string BuildRecoveryPrompt(string processId, string[] recentOutput)
    {
        var outputText = string.Join("", recentOutput);
        if (outputText.Length > 12000)
            outputText = outputText[^12000..];

        return $"""
            The '{processId}' development process has crashed repeatedly ({recentOutput.Length} recent crashes).

            Here is the recent output from the process:

            ```
            {outputText}
            ```

            Please diagnose the root cause of the crash and fix it. Focus on:
            1. Compilation errors or syntax issues
            2. Missing dependencies
            3. Configuration problems
            4. Port conflicts

            Apply the fix directly to the source files.
            """;
    }
}
