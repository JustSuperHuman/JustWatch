using JustWatch.Core.Models;

namespace JustWatch.Core.Services;

public sealed class Notification
{
    public long Id { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Level { get; init; } = "info"; // info | success | warn | error
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public string Source { get; init; } = "";    // process | task | session | action
}

/// <summary>
/// Central hub for "something finished / something changed" notifications.
/// Translates ProcessManager and ChatSession events into user-facing
/// notifications and keeps a bounded history so every surface (tray balloons,
/// WPF window, web UI, HTTP API) renders the same stream.
/// </summary>
public sealed class NotificationCenter
{
    private const int BufferCap = 200;

    private readonly NotificationsConfig _config;
    private readonly object _lock = new();
    private readonly List<Notification> _buffer = [];
    private readonly Dictionary<string, ProcessStatus> _lastProcessStatus = new();
    private readonly Dictionary<string, AgentTaskStatus> _lastTaskStatus = new();
    private long _nextId;

    public event Action<Notification>? NotificationAdded;

    public NotificationCenter(NotificationsConfig config) => _config = config;

    public void Notify(string level, string title, string message, string source = "")
    {
        if (!_config.Enabled) return;

        Notification n;
        lock (_lock)
        {
            n = new Notification
            {
                Id = ++_nextId,
                Level = level,
                Title = title,
                Message = message,
                Source = source
            };
            _buffer.Add(n);
            if (_buffer.Count > BufferCap)
                _buffer.RemoveRange(0, _buffer.Count - BufferCap);
        }
        NotificationAdded?.Invoke(n);
    }

    public IReadOnlyList<Notification> GetRecent(long sinceId = 0, int limit = 100)
    {
        lock (_lock)
        {
            return _buffer
                .Where(n => n.Id > sinceId)
                .TakeLast(Math.Clamp(limit, 1, BufferCap))
                .ToList();
        }
    }

    public void Watch(ProcessManager processes)
    {
        processes.StatusChanged += (id, status) =>
        {
            if (!_config.ProcessStatus) return;

            // StatusChanged can re-fire the same status (e.g. Stopped from both the
            // exit handler and StopAsync) — only announce actual transitions.
            lock (_lock)
            {
                if (_lastProcessStatus.TryGetValue(id, out var prev) && prev == status) return;
                _lastProcessStatus[id] = status;
            }

            var name = processes.GetProcessConfigs().FirstOrDefault(p => p.Id == id)?.Name ?? id;
            switch (status)
            {
                case ProcessStatus.Running:
                    Notify("success", $"{name} running", $"{name} is up.", "process");
                    break;
                case ProcessStatus.Stopped:
                    Notify("info", $"{name} stopped", $"{name} has stopped.", "process");
                    break;
                case ProcessStatus.Crashed:
                    Notify("error", $"{name} crashed", $"{name} crashed and is not running.", "process");
                    break;
                case ProcessStatus.RecoveringWithAgent:
                    Notify("warn", $"{name} recovering", $"{name} crashed — agent recovery started.", "process");
                    break;
            }
        };
    }

    public void Watch(ChatSession chat)
    {
        chat.TaskChanged += task =>
        {
            if (!_config.TaskStatus) return;

            lock (_lock)
            {
                if (_lastTaskStatus.TryGetValue(task.Id, out var prev) && prev == task.Status) return;
                _lastTaskStatus[task.Id] = task.Status;
            }

            var label = $"{task.Type}: {task.Name}";
            switch (task.Status)
            {
                case AgentTaskStatus.Running:
                    Notify("info", "Task started", label, "task");
                    break;
                case AgentTaskStatus.Completed:
                    Notify("success", "Task completed", label, "task");
                    break;
                case AgentTaskStatus.Cancelled:
                    Notify("warn", "Task cancelled", label, "task");
                    break;
                case AgentTaskStatus.Failed:
                    Notify("error", "Task failed",
                        string.IsNullOrEmpty(task.Error) ? label : $"{label} — {task.Error}", "task");
                    break;
            }
        };

        chat.SessionCompleted += () =>
        {
            if (_config.TaskStatus)
                Notify("success", "All tasks finished", "The agent session has completed.", "session");
        };
    }
}
