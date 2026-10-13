using System.Net.Http;
using System.Text;
using System.Text.Json;
using JustWatch.Core.Models;

namespace JustWatch.Core.Services;

public class ChatSession : IDisposable
{
    private readonly ChatConfig _config;
    private readonly List<ChatMessage> _history = [];
    private readonly HttpClient _http = new();
    private TaskManager _taskManager;
    private Task? _executionLoop;
    private readonly object _loopLock = new();
    private bool _hasCompletedOpusTask;

    // Claude CLI session id for the ongoing Opus coding conversation, so follow-ups
    // resume the right conversation (--resume <id>) instead of the newest on disk.
    private string? _opusSessionId;

    public event Action<string, string>? AgentChunk;
    public event Action<string, string, string>? AgentStatus;
    public event Action<AgentTask>? TaskChanged;
    public event Action<string, string?, string?>? ConvoMessage;
    public event Action? SessionCompleted;

    public bool IsProcessing { get; private set; }

    public ChatSession(ChatConfig config)
    {
        _config = config;
        _taskManager = CreateTaskManager();
    }

    private TaskManager CreateTaskManager()
    {
        var tm = new TaskManager(_config, _http);
        tm.RecordingQualityOverride = new RecordingQuality
        {
            Framerate = 10,
            Crf = 32,
            MaxSeconds = 30,
            Resolution = "1280x720"
        };
        tm.AgentChunk += (ch, text) => AgentChunk?.Invoke(ch, text);
        tm.AgentStatus += (ch, st, data) => AgentStatus?.Invoke(ch, st, data);
        tm.TaskChanged += task =>
        {
            TaskChanged?.Invoke(task);
            EmitConvoForTask(task);
            if (task.Channel == "opus" && task.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed)
                _hasCompletedOpusTask = true;
        };
        return tm;
    }

    // Re-queue any tasks left incomplete by a previous run (e.g. the tray app was
    // restarted by the watcher mid-task) and resume executing them.
    public void RecoverIncompleteTasks()
    {
        try
        {
            var restored = _taskManager.RestoreFromJournal();
            if (restored.Count == 0) return;
            AgentChunk?.Invoke("orch", $"[info]Recovered {restored.Count} incomplete task(s) from the previous session");
            EnsureLoopRunning(clearCompleted: false);
        }
        catch { }
    }

    private void EmitConvoForTask(AgentTask task)
    {
        // Only emit for meaningful transitions, skip Plan and internal noise
        if (task.Type == AgentTaskType.Plan) return;
        if (task.Status == AgentTaskStatus.Queued || task.Status == AgentTaskStatus.Pending) return;

        _ = Task.Run(async () =>
        {
            try
            {
                switch (task.Status)
                {
                    case AgentTaskStatus.Running:
                        // Brief "working on it" signal — the substantive reply comes on completion.
                        await EmitGenericAsync(
                            $"Task '{task.Name}' (type: {task.Type}) just started. Acknowledge this briefly and naturally. One short sentence.",
                            null);
                        break;

                    case AgentTaskStatus.Completed:
                        await RelayCompletedAsync(task);
                        break;

                    case AgentTaskStatus.Failed:
                        await RelayFailedAsync(task);
                        break;

                    case AgentTaskStatus.Cancelled:
                        await EmitGenericAsync(
                            $"Task '{task.Name}' was cancelled. Acknowledge briefly. One sentence.", null);
                        break;
                }
            }
            catch { }
        });
    }

    private async Task EmitGenericAsync(string context, string? messageType)
    {
        var reply = await _taskManager.CallOpenRouterQuietAsync(
            $"You're a friendly dev assistant chatting with a developer. {context}\nReply with ONLY the message, no quotes, no prefixes.",
            40, CancellationToken.None);
        reply = reply.Trim().Trim('"');
        if (!string.IsNullOrEmpty(reply))
            ConvoMessage?.Invoke(reply, messageType, null);
    }

    // The heart of the fix: actually use what the agent reported. For agent/test
    // tasks we relay the agent's real output (the answer / what changed), not a
    // vague "done". Short outputs are relayed verbatim; long ones are distilled
    // by the orchestrator while preserving specifics. Builds and empty outputs
    // fall back to a confirmation.
    private async Task RelayCompletedAsync(AgentTask task)
    {
        var isAgentResult = task.Type is AgentTaskType.Chat or AgentTaskType.Agent or AgentTaskType.Test;
        var output = isAgentResult ? CleanAgentOutput(task.OutputBuffer.ToString()) : "";

        string reply;
        if (isAgentResult && output.Length > 0)
        {
            reply = await SynthesizeAgentReplyAsync(task, output);
            if (string.IsNullOrWhiteSpace(reply))
                reply = output.Length > 3500 ? output[..3500] + "\n\n…(truncated)" : output;
        }
        else if (!string.IsNullOrEmpty(task.RecordingPath))
        {
            reply = await OneLinerAsync($"Task '{task.Name}' completed and a screen recording was saved. Tell the user their video is ready. One sentence.");
        }
        else
        {
            reply = await OneLinerAsync($"Task '{task.Name}' (type: {task.Type}) completed successfully. Confirm briefly, mentioning what was done. One sentence.");
        }

        if (string.IsNullOrWhiteSpace(reply)) return;

        if (!string.IsNullOrEmpty(task.RecordingPath))
            ConvoMessage?.Invoke(reply, "video", task.RecordingPath);
        else
            ConvoMessage?.Invoke(reply, null, null);
    }

    private async Task RelayFailedAsync(AgentTask task)
    {
        // Include the tail of the agent output / error so the user gets real detail.
        var output = CleanAgentOutput(task.OutputBuffer.ToString());
        var tail = output.Length > 1500 ? output[^1500..] : output;
        var detail = string.IsNullOrWhiteSpace(tail) ? (task.Error ?? "unknown error") : tail;

        var reply = await _taskManager.CallOpenRouterAsync(
            "You're a dev assistant. Relay a task failure to the user clearly and helpfully. State what failed and the concrete reason from the output. Keep it to 1-3 sentences. No fluff.",
            $"Task '{task.Name}' failed. Output/error:\n\n{detail}",
            300, CancellationToken.None);
        reply = reply.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(reply))
            reply = $"'{task.Name}' failed: {task.Error ?? "see the agent log"}.";
        ConvoMessage?.Invoke(reply, "error", null);
    }

    private async Task<string> OneLinerAsync(string context)
    {
        var reply = await _taskManager.CallOpenRouterQuietAsync(
            $"You're a friendly dev assistant. {context}\nReply with ONLY the message.", 50, CancellationToken.None);
        return reply.Trim().Trim('"');
    }

    // Turn an agent's raw output into the user-facing reply, faithfully conveying
    // the actual answer/result. Long outputs are distilled; everything material is
    // preserved (names, paths, code, values). Never collapses to a bare "done".
    private async Task<string> SynthesizeAgentReplyAsync(AgentTask task, string output)
    {
        // Short, already-conversational answers are relayed as-is (most faithful,
        // no extra latency, no risk of the summarizer dropping detail).
        if (output.Length <= 1200) return output;

        var clipped = output.Length > 16000
            ? output[..8000] + "\n\n…(middle omitted)…\n\n" + output[^8000..]
            : output;
        var request = task.Prompt.Length > 600 ? task.Prompt[..600] + "…" : task.Prompt;

        var system =
            "You are the voice of a dev assistant relaying an AI coding agent's results to the user in chat. "
            + "Convey the agent's ACTUAL findings or answer completely and faithfully. "
            + "Preserve specifics: names, file paths, function/type names, code snippets, and values. "
            + "If the agent answered a question, give the full answer. If it changed code, say what changed and where. "
            + "Use markdown when helpful. NEVER reply with a vague 'done' or 'task completed' — convey the substance. "
            + "Do not invent anything that isn't in the agent's output.";
        var user = $"The user's request was:\n\"{request}\"\n\nThe agent's output:\n\n{clipped}\n\nWrite the reply to the user.";

        var reply = await _taskManager.CallOpenRouterAsync(system, user, 2000, CancellationToken.None);
        return reply.Trim().Trim('"');
    }

    // Strip ANSI escapes and normalize whitespace so relayed CLI output reads cleanly.
    private static string CleanAgentOutput(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var noAnsi = System.Text.RegularExpressions.Regex.Replace(raw, @"\x1B\[[0-9;?]*[ -/]*[@-~]", "");
        noAnsi = noAnsi.Replace("\r\n", "\n").Replace("\r", "\n");
        // Strip our own "[Name] Starting/Completed" framing lines if present.
        var lines = noAnsi.Split('\n')
            .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l.Trim(),
                @"^\[.+?\] (Starting|Completed|Failed|Cancelled)\b"));
        return string.Join('\n', lines).Trim();
    }

    public async Task SendAsync(string userMessage, string workingDirectory)
    {
        _history.Add(new ChatMessage("user", userMessage));

        // Hold a producer token across decomposition + task creation so the
        // execution loop can't declare "all done" and exit mid-flight, orphaning
        // the tasks we're about to add.
        _taskManager.BeginProducing();
        Task loop;
        try
        {
            AgentChunk?.Invoke("orch", "[info]Analyzing request...");
            var steps = await DecomposeRequestAsync(userMessage);

            foreach (var step in steps)
                AgentChunk?.Invoke("orch", $"[info]Step: [{step.Action}] {step.Details}");

            // Generate a conversational acknowledgment
            _ = Task.Run(async () =>
            {
                try
                {
                    var stepSummary = string.Join(", ", steps.Select(s => s.Details));
                    var ackPrompt = steps.Count == 1
                        ? $"User asked: \"{userMessage}\". You're about to do: {stepSummary}. Acknowledge naturally in one short sentence."
                        : $"User asked: \"{userMessage}\". You're about to do {steps.Count} things: {stepSummary}. Acknowledge naturally in one short sentence, mentioning what you'll do.";
                    var reply = await _taskManager.CallOpenRouterQuietAsync(
                        $"You're a friendly dev assistant. {ackPrompt}\nReply with ONLY the message.",
                        40, CancellationToken.None);
                    reply = reply.Trim().Trim('"');
                    if (!string.IsNullOrEmpty(reply))
                        ConvoMessage?.Invoke(reply, null, null);
                }
                catch { }
            });

            await CreateTasksFromStepsAsync(steps, userMessage, workingDirectory);
            loop = EnsureLoopRunning();
        }
        finally
        {
            _taskManager.EndProducing();
        }

        await loop;
    }

    // Starts the execution loop if it isn't already running. Synchronized so two
    // concurrent producers (UI + Telegram) can't both start it. Combined with the
    // producer token in SendAsync/SendNewTask, this guarantees every queued task
    // is picked up by a live loop.
    private Task EnsureLoopRunning(bool clearCompleted = true)
    {
        lock (_loopLock)
        {
            if (_executionLoop is { IsCompleted: false }) return _executionLoop;

            // Fresh loop: drop terminal tasks from the previous run so the queue
            // doesn't grow without bound. Skipped during crash recovery, where
            // restored completed tasks are still needed as dependency targets.
            if (clearCompleted) _taskManager.ClearCompletedTasks();
            IsProcessing = true;
            _executionLoop = Task.Run(async () =>
            {
                try
                {
                    await _taskManager.ExecuteAllAsync();
                }
                finally
                {
                    var responses = _taskManager.GetTasks()
                        .Where(t => t.Type is AgentTaskType.Chat or AgentTaskType.Test or AgentTaskType.Agent
                            && t.Status == AgentTaskStatus.Completed)
                        .Select(t => t.OutputBuffer.ToString())
                        .Where(s => !string.IsNullOrEmpty(s));
                    foreach (var r in responses)
                        _history.Add(new ChatMessage("assistant", r));

                    lock (_loopLock) { IsProcessing = false; }
                    SessionCompleted?.Invoke();
                }
            });
            return _executionLoop;
        }
    }

    public void SendNewTask(string userMessage, string workingDirectory)
    {
        _history.Add(new ChatMessage("user", userMessage));

        Task.Run(async () =>
        {
            _taskManager.BeginProducing();
            try
            {
                AgentChunk?.Invoke("orch", $"[info]New input: {userMessage[..Math.Min(80, userMessage.Length)]}...");
                var steps = await DecomposeRequestAsync(userMessage);

                // Check if this is a correction/interjection for a running task
                var isInterjection = steps.Count == 1
                    && steps[0].Action is "followup" or "chat"
                    && _hasCompletedOpusTask;

                if (isInterjection || steps.Any(s => s.Action == "followup"))
                {
                    // Cancel current running opus tasks and resume with this message
                    _taskManager.CancelChannel("opus");
                    AgentChunk?.Invoke("orch", "[info]Interjecting — resuming conversation with new input");

                    var prompt = steps[0].Prompt;
                    var task = _taskManager.CreateTask(
                        steps[0].Details.Length > 3 ? steps[0].Details : "Follow-up",
                        AgentTaskType.Chat, "opus", prompt, workingDirectory);
                    task.Model = _config.ClaudeModel;
                    task.SessionId = _opusSessionId ?? NewSessionId();
                    _opusSessionId = task.SessionId;
                    task.Resume = _hasCompletedOpusTask;

                    ConvoMessage?.Invoke("Got it, adjusting course...", null, null);
                }
                else
                {
                    foreach (var step in steps)
                        AgentChunk?.Invoke("orch", $"[info]Queued: [{step.Action}] {step.Details}");
                    await CreateTasksFromStepsAsync(steps, userMessage, workingDirectory);

                    var reply = await _taskManager.CallOpenRouterQuietAsync(
                        $"You're a friendly dev assistant. User sent a follow-up: \"{userMessage}\". Acknowledge briefly. One sentence.",
                        30, CancellationToken.None);
                    reply = reply.Trim().Trim('"');
                    if (!string.IsNullOrEmpty(reply))
                        ConvoMessage?.Invoke(reply, null, null);
                }

                // Make sure a live loop will pick up whatever we just queued, even
                // if the previous loop finished while we were decomposing.
                EnsureLoopRunning();
            }
            catch (Exception ex)
            {
                AgentChunk?.Invoke("orch", $"[error]Failed to queue: {ex.Message}");
                ConvoMessage?.Invoke($"Sorry, couldn't process that: {ex.Message}", "error", null);
            }
            finally
            {
                _taskManager.EndProducing();
            }
        });
    }

    private string BuildConversationContext()
    {
        if (_history.Count <= 1) return "";
        var recent = _history.TakeLast(6).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("Recent conversation context:");
        foreach (var msg in recent)
        {
            var preview = msg.Content.Length > 300 ? msg.Content[..300] + "..." : msg.Content;
            sb.AppendLine($"[{msg.Role}]: {preview}");
        }
        return sb.ToString();
    }

    private async Task<List<IntentClassification>> DecomposeRequestAsync(string message)
    {
        var context = BuildConversationContext();
        var contextSection = string.IsNullOrEmpty(context) ? "" : $"\n{context}\n";

        var prompt = "You are the orchestrator for a dev tool. Decompose this user request into ordered steps.\n"
            + "Project: fitness app monorepo — Next.js web (port 3000), .NET API (port 5000), React Native/Expo mobile (port 8081), EAS Build, TestFlight.\n"
            + contextSection + "\n"
            + $"User request: \"{message}\"\n\n"
            + "Reply with ONLY a JSON array of steps (no markdown):\n"
            + "[{\"action\":\"chat|build|test|record_demo|followup\",\"prompt\":\"rewritten prompt for this step only\","
            + "\"test_type\":\"web|mobile|null\",\"platforms\":[],\"build_profile\":\"production\","
            + "\"deploy_testflight\":false,\"should_record\":false,\"details\":\"summary\"}]\n\n"
            + "Rules:\n"
            + "- If the message mentions a file path (image, screenshot, etc.), include the FULL file path in the step's prompt so the agent can read/use it\n"
            + "- Use conversation context to understand references like 'it', 'that', 'the image', 'do it differently'\n"
            + "- 'followup' = the user is continuing/correcting a previous conversation (e.g. 'no do it this way', 'also add X', 'what about Y'). Include context from the previous messages in the prompt.\n"
            + "- If the user references a previously sent image/file, include that file path in the followup prompt\n"
            + "- Split compound requests into separate steps. 'add X and record a demo' = [{action:chat,prompt:'add X'},{action:record_demo,...}]\n"
            + "- Each step's 'prompt' should be rewritten to ONLY contain that step's work\n"
            + "- 'chat' = coding/questions/fixes for Opus agent. 'followup' = continue previous agent conversation.\n"
            + "- 'build' = EAS app build. 'test' = run live test. 'record_demo' = record a video\n"
            + "- Default to [{action:'chat'}] for new requests, [{action:'followup'}] for corrections/continuations";

        try
        {
            var response = await _taskManager.CallOpenRouterQuietAsync(prompt, 500, CancellationToken.None);
            response = response.Trim();
            if (response.StartsWith("```"))
            {
                var lines = response.Split('\n');
                response = string.Join('\n', lines.Skip(1).Take(lines.Length - 2));
            }

            var steps = new List<IntentClassification>();
            using var doc = JsonDocument.Parse(response);

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var platforms = new List<string>();
                if (el.TryGetProperty("platforms", out var p) && p.ValueKind == JsonValueKind.Array)
                    foreach (var item in p.EnumerateArray())
                        if (item.GetString() is string s) platforms.Add(s);

                steps.Add(new IntentClassification
                {
                    Action = el.TryGetProperty("action", out var a) ? a.GetString() ?? "chat" : "chat",
                    Prompt = el.TryGetProperty("prompt", out var pr) ? pr.GetString() ?? message : message,
                    Platforms = platforms,
                    BuildProfile = el.TryGetProperty("build_profile", out var bp) ? bp.GetString() ?? "production" : "production",
                    DeployTestFlight = el.TryGetProperty("deploy_testflight", out var dt) && dt.GetBoolean(),
                    TestType = el.TryGetProperty("test_type", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString() : null,
                    ShouldRecord = el.TryGetProperty("should_record", out var sr) && sr.GetBoolean(),
                    Details = el.TryGetProperty("details", out var d) ? d.GetString() ?? "" : ""
                });
            }

            return steps.Count > 0 ? steps : [new IntentClassification { Action = "chat", Prompt = message }];
        }
        catch (Exception ex)
        {
            AgentChunk?.Invoke("orch", $"[warn]Decomposition failed ({ex.Message}), defaulting to chat");
            return [new IntentClassification { Action = "chat", Prompt = message, Details = "fallback" }];
        }
    }

    private async Task CreateTasksFromStepsAsync(List<IntentClassification> steps, string originalMessage, string workingDir)
    {
        // Dependency model: tasks that mutate the working tree (chat/followup/build)
        // are serialized on the previous mutator so two agents never edit the same
        // repo at once. Independent work (sibling platform builds, test planning)
        // is left un-chained so it can run in parallel across the CLI slots.
        var existingTasks = _taskManager.GetTasks();
        var lastMutator = existingTasks.LastOrDefault(t =>
            t.Status <= AgentTaskStatus.Running
            && t.Type is AgentTaskType.Chat or AgentTaskType.Agent or AgentTaskType.Build);
        string? previousMutatorId = lastMutator?.Id;

        foreach (var step in steps)
        {
            switch (step.Action)
            {
                case "chat":
                case "followup":
                {
                    var prompt = step.Prompt;
                    // If prompt references an image file, instruct the agent to read it
                    var imageMatch = System.Text.RegularExpressions.Regex.Match(prompt,
                        @"saved to:\s*(.+\.(?:png|jpg|jpeg|gif|webp|bmp))", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (imageMatch.Success)
                    {
                        var imgPath = imageMatch.Groups[1].Value.Trim();
                        prompt += $"\n\nIMPORTANT: An image/screenshot was provided at: {imgPath}\nRead this image file to understand the visual context before proceeding.";
                    }

                    var deps = previousMutatorId != null ? new[] { previousMutatorId } : [];
                    var isFollowup = step.Action == "followup" && _hasCompletedOpusTask && _opusSessionId != null;
                    var task = _taskManager.CreateTask(
                        step.Details.Length > 3 ? step.Details : (isFollowup ? "Follow-up" : "Code"),
                        AgentTaskType.Chat, "opus", prompt, workingDir, deps);
                    task.Model = _config.ClaudeModel;
                    if (isFollowup)
                    {
                        task.SessionId = _opusSessionId;
                        task.Resume = true;
                    }
                    else
                    {
                        task.SessionId = NewSessionId();
                        _opusSessionId = task.SessionId;
                    }
                    previousMutatorId = task.Id;
                    break;
                }
                case "build":
                {
                    // All platform builds depend on the latest code change but not on
                    // each other, so e.g. iOS and Android build concurrently.
                    var codeDeps = previousMutatorId != null ? new[] { previousMutatorId } : [];
                    var platforms = step.Platforms.Count > 0 ? step.Platforms : ["ios", "android"];
                    string? lastBuildId = null;
                    foreach (var platform in platforms)
                    {
                        var cmd = TaskManager.GetBuildCommand(platform, step.BuildProfile,
                            step.DeployTestFlight && platform == "ios");
                        var task = _taskManager.CreateTask($"Build {platform}", AgentTaskType.Build,
                            "orch", step.Prompt, workingDir, codeDeps);
                        task.BuildCommand = cmd;
                        task.BuildRequest = new BuildRequest(platforms, step.BuildProfile, step.DeployTestFlight);
                        lastBuildId = task.Id;
                    }
                    // A subsequent step (e.g. a test) should wait for the build to finish.
                    previousMutatorId = lastBuildId ?? previousMutatorId;
                    break;
                }
                case "test":
                case "record_demo":
                {
                    var testType = step.TestType ?? "mobile";
                    var target = testType == "web"
                        ? "Next.js web app at http://localhost:3000"
                        : "React Native/Expo mobile app on Android";
                    var planPrompt = $"Create a concise test/demo plan.\nTarget: {target}\nProject: {workingDir}\nRequest: {step.Prompt}\n\nOutput 5-10 specific numbered steps. Plain English only.";

                    // Planning is pure LLM work — safe to run in parallel with other tasks.
                    var planTask = _taskManager.CreateTask("Plan", AgentTaskType.Plan, "orch",
                        planPrompt, workingDir);

                    // The test waits for both its plan and the latest code/build.
                    var testDeps = previousMutatorId != null
                        ? new[] { planTask.Id, previousMutatorId }
                        : new[] { planTask.Id };
                    var testTask = _taskManager.CreateTask(
                        step.Action == "record_demo" ? "Record demo" : $"Test {testType}",
                        AgentTaskType.Test, "sonnet", "", workingDir, testDeps);
                    testTask.Model = _config.ClaudeTestModel;
                    testTask.TestType = testType;
                    testTask.ShouldRecord = step.ShouldRecord || step.Action == "record_demo";

                    // Build the executor prompt lazily from the plan's result at launch
                    // time. The dependency on planTask guarantees ResultData is ready,
                    // so no global TaskChanged handler (which would leak) is needed.
                    var capturedPlan = planTask;
                    testTask.PromptFactory = () =>
                        BuildExecutorPrompt(capturedPlan.ResultData ?? "", testType, workingDir);
                    break;
                }
            }
        }
    }

    private static string NewSessionId() => Guid.NewGuid().ToString();

    public void StopChannel(string channel) => _taskManager.CancelChannel(channel);
    public void StopTask(string taskId) => _taskManager.CancelTask(taskId);
    public void Cancel() => _taskManager.CancelAll();
    public void ClearHistory() => _history.Clear();

    private static string BuildExecutorPrompt(string plan, string testType, string workingDir)
    {
        var tools = testType == "web"
            ? "Use the chrome browser automation MCP tools (mcp__claude-in-chrome__*) to interact with the web app at http://localhost:3000. Start by calling tabs_context_mcp, then create a new tab."
            : "Use the android MCP tools to interact with the mobile app. Take screenshots to verify each step.";
        return $"Execute this test plan.\nProject: {workingDir}\n\n{tools}\n\nTest plan:\n{plan}\n\nExecute each step, report results, summarize pass/fail at the end.";
    }

    public void Dispose()
    {
        _taskManager.Dispose();
        _http.Dispose();
    }
}

public record ChatMessage(string Role, string Content);

public record BuildRequest(List<string> Platforms, string Profile, bool DeployTestFlight)
{
    public override string ToString()
    {
        var parts = new List<string> { string.Join("+", Platforms), Profile };
        if (DeployTestFlight) parts.Add("→ TestFlight");
        return string.Join(" | ", parts);
    }
}

public class IntentClassification
{
    public string Action { get; set; } = "chat";
    public string Prompt { get; set; } = "";
    public List<string> Platforms { get; set; } = [];
    public string BuildProfile { get; set; } = "production";
    public bool DeployTestFlight { get; set; }
    public string? TestType { get; set; }
    public bool ShouldRecord { get; set; }
    public string Details { get; set; } = "";
}
