namespace JustWatch.Core.Models;

public class ProcessConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string[] Args { get; set; } = [];
    public string WorkingDirectory { get; set; } = string.Empty;
    public bool AutoStart { get; set; } = true;
    public string Description { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public bool ShowInTray { get; set; } = true;
    // Overrides pinning and lifecycle visibility without stopping the service.
    public bool HideFromTray { get; set; }
    public bool ShowTrayDot { get; set; }
    public string TrayColor { get; set; } = "#34A853";
    public bool AutoRestart { get; set; } = true;
    public bool ReloadAndroidOnRestart { get; set; }
    public string[] RestartOutputPatterns { get; set; } = ["EMFILE: too many open files", "EMFILE:", "EADDRINUSE"];
    public string StopCommand { get; set; } = string.Empty;
    public string[] StopArgs { get; set; } = [];
    public int? Port { get; set; }
    public int CrashThreshold { get; set; } = 5;
    public int CrashWindowSeconds { get; set; } = 30;
    public bool AgentRecovery { get; set; } = true;

    // Before starting, kill whatever already listens on Port. Without this, a server
    // left behind outside the JustWatch keeps the port and every start dies on
    // EADDRINUSE. Set false for a port the JustWatch must never take over.
    public bool ReclaimPort { get; set; } = true;

    public string Url { get; set; } = string.Empty;
    public Dictionary<string, string> Environment { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public string ProjectLabel => string.IsNullOrWhiteSpace(Project) ? "General" : Project.Trim();

    [System.Text.Json.Serialization.JsonIgnore]
    public string? EffectiveUrl =>
        !string.IsNullOrWhiteSpace(Url)
            ? Url
            : Port is > 0
                ? $"http://localhost:{Port}"
                : null;
}
