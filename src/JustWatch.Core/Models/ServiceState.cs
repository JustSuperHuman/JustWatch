namespace JustWatch.Core.Models;

// Public status metadata only. Polling never clones execution settings or secrets.
public sealed record ServiceState(
    string Id, string Name, string Project, string Description, string Group,
    bool AutoStart, bool ShowInTray, bool ShowTrayDot, string TrayColor,
    ProcessStatus Status, int? Pid, int? Port, string? Url, DateTime? StartedAt,
    bool HideFromTray = false);

public static class ServiceVisibility
{
    public static bool IsVisibleInTray(ServiceState service) =>
        !service.HideFromTray && (service.ShowInTray || IsStarted(service.Status));

    public static bool IsStarted(ProcessStatus status) => status is
        ProcessStatus.Starting or ProcessStatus.Running or ProcessStatus.Stopping or ProcessStatus.RecoveringWithAgent;

    // Failed services remain visible to agents until explicitly stopped, so a
    // failure cannot disappear just because the supervised process exited.
    public static bool IsActive(ProcessStatus status) => IsStarted(status) || status == ProcessStatus.Crashed;
}
