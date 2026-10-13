using System.Diagnostics;
using System.Text;
using JustWatch.Core.Services;

namespace JustWatch.Core.Models;

public enum AgentTaskType
{
    Chat,
    Build,
    Test,
    Plan,
    Record,
    Agent
}

public enum AgentTaskStatus
{
    Pending,
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

public class AgentTask
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public AgentTaskType Type { get; set; }
    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Pending;

    public List<string> DependsOn { get; set; } = [];

    public string Channel { get; set; } = "orch";
    public string? Model { get; set; }
    public string Prompt { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";

    public bool Resume { get; set; }

    // Claude CLI conversation id. Set on the first task of a conversation
    // (--session-id) and reused by follow-ups (--resume) so resuming targets
    // a specific conversation rather than "the most recent session on disk".
    public string? SessionId { get; set; }

    // Lazily builds the prompt right before the CLI launches. Used so a task can
    // pull a dependency's output (e.g. a test task reading its plan task's result)
    // without a never-removed global TaskChanged handler. Evaluated once; the
    // result replaces Prompt. The task's dependencies guarantee the source is ready.
    public Func<string>? PromptFactory { get; set; }

    public string? BuildCommand { get; set; }
    public BuildRequest? BuildRequest { get; set; }
    public string? TestType { get; set; }
    public string? RecordingPath { get; set; }
    public bool ShouldRecord { get; set; }

    public Process? Process { get; set; }
    public CancellationTokenSource Cts { get; set; } = new();

    public StringBuilder OutputBuffer { get; } = new();
    public string? ResultData { get; set; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }

    public int QuickRetryCount { get; set; }
    public int OpusFixCount { get; set; }

    // Capture the persistable fields for the durable journal. Runtime-only state
    // (Process, Cts, OutputBuffer, OnComplete) is intentionally excluded.
    public TaskSnapshot ToSnapshot() => new()
    {
        Id = Id, Name = Name, Type = Type, Status = Status,
        DependsOn = [..DependsOn], Channel = Channel, Model = Model,
        Prompt = Prompt, WorkingDirectory = WorkingDirectory, Resume = Resume,
        SessionId = SessionId, BuildCommand = BuildCommand, TestType = TestType,
        ShouldRecord = ShouldRecord, RecordingPath = RecordingPath,
        CreatedAt = CreatedAt, StartedAt = StartedAt, CompletedAt = CompletedAt,
        ExitCode = ExitCode, Error = Error
    };

    // Rebuild a task from a journal snapshot. A task that was mid-flight when the
    // app stopped (Running) is reset to Pending so it re-executes from scratch;
    // its dead Process/Cts are not restored.
    public static AgentTask FromSnapshot(TaskSnapshot s, CancellationToken master) => new()
    {
        Id = s.Id, Name = s.Name, Type = s.Type,
        Status = s.Status == AgentTaskStatus.Running ? AgentTaskStatus.Pending : s.Status,
        DependsOn = [..s.DependsOn], Channel = s.Channel, Model = s.Model,
        Prompt = s.Prompt, WorkingDirectory = s.WorkingDirectory, Resume = s.Resume,
        SessionId = s.SessionId, BuildCommand = s.BuildCommand, TestType = s.TestType,
        ShouldRecord = s.ShouldRecord, RecordingPath = s.RecordingPath,
        CreatedAt = s.CreatedAt, StartedAt = s.StartedAt, CompletedAt = s.CompletedAt,
        ExitCode = s.ExitCode, Error = s.Error,
        Cts = CancellationTokenSource.CreateLinkedTokenSource(master)
    };
}

// Serializable form of AgentTask used for the durable task journal so that
// in-flight work survives a tray-app restart.
public class TaskSnapshot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public AgentTaskType Type { get; set; }
    public AgentTaskStatus Status { get; set; }
    public List<string> DependsOn { get; set; } = [];
    public string Channel { get; set; } = "orch";
    public string? Model { get; set; }
    public string Prompt { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public bool Resume { get; set; }
    public string? SessionId { get; set; }
    public string? BuildCommand { get; set; }
    public string? TestType { get; set; }
    public bool ShouldRecord { get; set; }
    public string? RecordingPath { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
}
