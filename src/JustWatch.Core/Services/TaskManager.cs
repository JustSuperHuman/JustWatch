using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JustWatch.Core.Models;

namespace JustWatch.Core.Services;

public class TaskManager : IDisposable
{
    private readonly ChatConfig _config;
    private readonly ScreenRecorder _recorder;
    private readonly HttpClient _http;
    private readonly List<AgentTask> _tasks = [];
    private readonly SemaphoreSlim _cliSlots;
    private readonly object _lock = new();
    private CancellationTokenSource _masterCts = new();

    // Guards against the execution loop declaring "all done" while a producer is
    // still decomposing a request and about to add tasks (the orphaned-task race).
    private int _pendingProducers;

    // Durable journal so in-flight tasks survive a tray-app restart.
    private readonly string _journalPath;
    private readonly object _journalLock = new();
    private static readonly JsonSerializerOptions JournalJsonOpts = new() { WriteIndented = false };

    private const int MaxQuickRetries = 3;
    private const int MaxOpusFixAttempts = 2;
    private static readonly string[] TransientErrors =
        ["EPERM", "EBUSY", "EACCES", "Permission denied", "being used by another process", "locked", "ENOTEMPTY"];

    public event Action<string, string>? AgentChunk;
    public event Action<string, string, string>? AgentStatus;
    public event Action<AgentTask>? TaskChanged;

    public RecordingQuality? RecordingQualityOverride { get; set; }

    public TaskManager(ChatConfig config, HttpClient http, int maxConcurrentCli = 4)
    {
        _config = config;
        _http = http;
        _recorder = new ScreenRecorder(config.FfmpegPath, config.RecordingsDir);
        _cliSlots = new SemaphoreSlim(maxConcurrentCli, maxConcurrentCli);

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JustWatch");
        Directory.CreateDirectory(dir);
        _journalPath = Path.Combine(dir, "task-journal.json");
    }

    // Producers (request decomposition) bracket their work with these so the loop
    // never exits between "queue drained" and "new task added".
    public void BeginProducing() => Interlocked.Increment(ref _pendingProducers);
    public void EndProducing() => Interlocked.Decrement(ref _pendingProducers);

    private void SetStatus(AgentTask task, AgentTaskStatus next)
    {
        lock (_lock) { task.Status = next; }
    }

    private bool TrySetStatus(AgentTask task, AgentTaskStatus expected, AgentTaskStatus next)
    {
        lock (_lock)
        {
            if (task.Status != expected) return false;
            task.Status = next;
        }
        return true;
    }

    // Raise TaskChanged and persist the queue. TaskChanged only fires on create
    // and status transitions (not per output chunk), so writing the journal here
    // is infrequent and cheap.
    private void RaiseTaskChanged(AgentTask task)
    {
        TaskChanged?.Invoke(task);
        PersistJournal();
    }

    private void PersistJournal()
    {
        List<TaskSnapshot> snapshot;
        lock (_lock) { snapshot = _tasks.Select(t => t.ToSnapshot()).ToList(); }
        lock (_journalLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(snapshot, JournalJsonOpts);
                var tmp = _journalPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _journalPath, overwrite: true);
            }
            catch { /* journal is best-effort */ }
        }
    }

    // Reload tasks left incomplete by a previous run. Mutating/in-flight tasks
    // are re-queued; their already-completed dependencies are restored too so
    // dependency resolution still works. Returns the tasks that still need to run.
    public IReadOnlyList<AgentTask> RestoreFromJournal()
    {
        List<TaskSnapshot>? snapshot = null;
        lock (_journalLock)
        {
            try
            {
                if (File.Exists(_journalPath))
                    snapshot = JsonSerializer.Deserialize<List<TaskSnapshot>>(File.ReadAllText(_journalPath));
            }
            catch { snapshot = null; }
        }
        if (snapshot is null || snapshot.Count == 0) return [];

        static bool IsTerminal(AgentTaskStatus s) =>
            s is AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled;

        // A task that was Running is treated as incomplete (reset to Pending on restore).
        var incomplete = snapshot.Where(s => !IsTerminal(s.Status)).ToList();
        if (incomplete.Count == 0) return [];

        var neededDeps = incomplete.SelectMany(s => s.DependsOn).ToHashSet();
        var toRestore = snapshot
            .Where(s => !IsTerminal(s.Status) || neededDeps.Contains(s.Id))
            .ToList();

        var restored = new List<AgentTask>();
        lock (_lock)
        {
            foreach (var s in toRestore)
            {
                var task = AgentTask.FromSnapshot(s, _masterCts.Token);
                _tasks.Add(task);
                if (task.Status is AgentTaskStatus.Pending or AgentTaskStatus.Queued)
                    restored.Add(task);
            }
        }
        foreach (var t in restored) TaskChanged?.Invoke(t);
        return restored;
    }

    public AgentTask CreateTask(string name, AgentTaskType type, string channel,
        string prompt, string workingDirectory, params string[] dependsOn)
    {
        var task = new AgentTask
        {
            Name = name,
            Type = type,
            Channel = channel,
            Prompt = prompt,
            WorkingDirectory = workingDirectory,
            DependsOn = [..dependsOn],
            Cts = CancellationTokenSource.CreateLinkedTokenSource(_masterCts.Token)
        };
        lock (_lock) { _tasks.Add(task); }
        RaiseTaskChanged(task);
        return task;
    }

    public IReadOnlyList<AgentTask> GetTasks()
    {
        lock (_lock) { return _tasks.ToList().AsReadOnly(); }
    }

    public void CancelTask(string taskId)
    {
        AgentTask? task;
        lock (_lock) { task = _tasks.FirstOrDefault(t => t.Id == taskId); }
        if (task == null) return;

        task.Cts.Cancel();
        if (task.Process is { HasExited: false })
            try { task.Process.Kill(entireProcessTree: true); } catch { }

        bool cancelled;
        lock (_lock)
        {
            cancelled = task.Status is AgentTaskStatus.Pending or AgentTaskStatus.Queued or AgentTaskStatus.Running;
            if (cancelled)
            {
                task.Status = AgentTaskStatus.Cancelled;
                task.CompletedAt = DateTime.UtcNow;
            }
        }
        if (cancelled) RaiseTaskChanged(task);
    }

    public void CancelChannel(string channel)
    {
        List<AgentTask> tasks;
        lock (_lock) { tasks = _tasks.Where(t => t.Channel == channel && t.Status <= AgentTaskStatus.Running).ToList(); }
        foreach (var t in tasks) CancelTask(t.Id);
    }

    public void CancelAll()
    {
        try { _masterCts.Cancel(); } catch (ObjectDisposedException) { }
        List<AgentTask> tasks;
        lock (_lock) { tasks = _tasks.Where(t => t.Status <= AgentTaskStatus.Running).ToList(); }
        foreach (var t in tasks) CancelTask(t.Id);
    }

    public void ClearCompletedTasks()
    {
        lock (_lock)
        {
            _tasks.RemoveAll(t => t.Status is AgentTaskStatus.Completed
                or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled);
        }
        PersistJournal();
    }

    public void Reset()
    {
        CancelAll();
        _masterCts.Dispose();
        _masterCts = new CancellationTokenSource();
        lock (_lock) { _tasks.Clear(); }
        PersistJournal();
    }

    public async Task ExecuteAllAsync()
    {
        var running = new List<Task>();

        while (!_masterCts.Token.IsCancellationRequested)
        {
            List<AgentTask> snapshot;
            lock (_lock) { snapshot = _tasks.ToList(); }

            var allTerminal = snapshot.All(t =>
                t.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled);
            if (allTerminal && snapshot.Count > 0 && running.Count == 0
                && Volatile.Read(ref _pendingProducers) == 0)
            {
                // Wait briefly in case new tasks are being queued
                try { await Task.Delay(300, _masterCts.Token); } catch { break; }
                lock (_lock) { snapshot = _tasks.ToList(); }
                allTerminal = snapshot.All(t =>
                    t.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled);
                // Re-check producers: a request may have started decomposing during the grace window.
                if (allTerminal && Volatile.Read(ref _pendingProducers) == 0) break;
            }

            foreach (var task in snapshot.Where(t => t.Status == AgentTaskStatus.Pending))
            {
                if (HasFailedDependency(task, snapshot))
                {
                    lock (_lock)
                    {
                        if (task.Status != AgentTaskStatus.Pending) continue;
                        task.Status = AgentTaskStatus.Cancelled;
                        task.Error = "Dependency failed";
                        task.CompletedAt = DateTime.UtcNow;
                    }
                    RaiseTaskChanged(task);
                    continue;
                }

                if (AreDependenciesMet(task, snapshot) && TrySetStatus(task, AgentTaskStatus.Pending, AgentTaskStatus.Queued))
                    RaiseTaskChanged(task);
            }

            foreach (var task in snapshot.Where(t => t.Status == AgentTaskStatus.Queued))
            {
                var t = task;
                // Claim the task atomically so it's launched exactly once.
                if (!TrySetStatus(t, AgentTaskStatus.Queued, AgentTaskStatus.Running)) continue;
                var launched = Task.Run(async () =>
                {
                    t.StartedAt = DateTime.UtcNow;
                    RaiseTaskChanged(t);
                    AgentStatus?.Invoke(t.Channel, "start", t.Id);

                    // Emit a visible startup message to the channel column
                    var modelLabel = t.Model ?? "default";
                    AgentChunk?.Invoke(t.Channel, $"[{t.Name}] Starting ({modelLabel})...\n");

                    try
                    {
                        await RunTaskAsync(t);
                        // RunTaskAsync may itself mark the task Failed (e.g. build).
                        TrySetStatus(t, AgentTaskStatus.Running, AgentTaskStatus.Completed);
                    }
                    catch (OperationCanceledException)
                    {
                        TrySetStatus(t, AgentTaskStatus.Running, AgentTaskStatus.Cancelled);
                    }
                    catch (Exception ex)
                    {
                        if (TrySetStatus(t, AgentTaskStatus.Running, AgentTaskStatus.Failed))
                            t.Error = ex.Message;
                    }
                    finally
                    {
                        t.CompletedAt ??= DateTime.UtcNow;
                        var endMsg = t.Status switch
                        {
                            AgentTaskStatus.Completed => $"\n[{t.Name}] Completed.\n",
                            AgentTaskStatus.Failed => $"\n[{t.Name}] Failed: {t.Error}\n",
                            AgentTaskStatus.Cancelled => $"\n[{t.Name}] Cancelled.\n",
                            _ => ""
                        };
                        if (!string.IsNullOrEmpty(endMsg))
                            AgentChunk?.Invoke(t.Channel, endMsg);
                        RaiseTaskChanged(t);
                        AgentStatus?.Invoke(t.Channel, "end", t.Id);
                    }
                });
                running.Add(launched);
            }

            if (running.Count > 0)
            {
                var completed = await Task.WhenAny(running.Append(Task.Delay(500, _masterCts.Token)).ToArray());
                running.RemoveAll(t => t.IsCompleted);
            }
            else
            {
                try { await Task.Delay(200, _masterCts.Token); } catch { break; }
            }
        }

        if (running.Count > 0)
            await Task.WhenAll(running);
    }

    private async Task RunTaskAsync(AgentTask task)
    {
        switch (task.Type)
        {
            case AgentTaskType.Chat:
            case AgentTaskType.Agent:
                await RunCliTaskAsync(task);
                break;
            case AgentTaskType.Build:
                await RunBuildTaskAsync(task);
                break;
            case AgentTaskType.Test:
                await RunTestTaskAsync(task);
                break;
            case AgentTaskType.Plan:
                await RunPlanTaskAsync(task);
                break;
            case AgentTaskType.Record:
                await RunRecordTaskAsync(task);
                break;
        }
    }

    private async Task RunCliTaskAsync(AgentTask task)
    {
        await _cliSlots.WaitAsync(task.Cts.Token);

        // Watchdog: cancel (and kill) the task if the CLI runs past the timeout so
        // a hung agent can't hold a slot forever. Distinguished from a user cancel.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(task.Cts.Token);
        if (_config.AgentTimeoutSeconds > 0)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.AgentTimeoutSeconds));
        var ct = timeoutCts.Token;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _config.ClaudeCommand,
                WorkingDirectory = task.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            if (!string.IsNullOrEmpty(task.Model))
            {
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(task.Model);
            }
            // Scope conversation resume to a specific session id rather than
            // "the most recent session on disk".
            if (task.Resume && !string.IsNullOrEmpty(task.SessionId))
            {
                psi.ArgumentList.Add("--resume");
                psi.ArgumentList.Add(task.SessionId);
            }
            else if (task.Resume)
            {
                psi.ArgumentList.Add("--resume");
            }
            else if (!string.IsNullOrEmpty(task.SessionId))
            {
                psi.ArgumentList.Add("--session-id");
                psi.ArgumentList.Add(task.SessionId);
            }

            foreach (var arg in _config.ClaudeArgs)
                psi.ArgumentList.Add(arg);

            var prompt = task.PromptFactory?.Invoke() ?? task.Prompt;
            if (!task.Resume && ShouldUseGoal(task))
            {
                var goal = DeriveGoal(task);
                prompt = $"/goal {goal}\n\n{prompt}";
            }
            psi.ArgumentList.Add(prompt);

            task.Process = new Process { StartInfo = psi };
            task.Process.Start();
            WindowsJobObject.AssignProcess(task.Process);

            async Task ReadStream(System.IO.StreamReader reader)
            {
                var buf = new char[2048];
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var n = await reader.ReadAsync(buf.AsMemory(), ct);
                        if (n == 0) break;
                        var chunk = new string(buf, 0, n);
                        task.OutputBuffer.Append(chunk);
                        AgentChunk?.Invoke(task.Channel, chunk);
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            }

            try
            {
                await Task.WhenAll(
                    ReadStream(task.Process.StandardOutput),
                    ReadStream(task.Process.StandardError));

                await task.Process.WaitForExitAsync(ct);
                task.ExitCode = task.Process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                if (task.Process is { HasExited: false })
                    try { task.Process.Kill(entireProcessTree: true); } catch { }

                // A timeout (not a user/master cancel) is a failure, not a cancellation.
                if (!task.Cts.Token.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                    throw new TimeoutException($"Agent task exceeded {_config.AgentTimeoutSeconds}s timeout");
                throw;
            }
        }
        finally
        {
            _cliSlots.Release();
            task.Process?.Dispose();
            task.Process = null;
        }
    }

    private async Task RunBuildTaskAsync(AgentTask task)
    {
        if (string.IsNullOrEmpty(task.BuildCommand)) return;

        for (int quick = 1; quick <= MaxQuickRetries; quick++)
        {
            if (task.Cts.Token.IsCancellationRequested) return;

            Emit(task, quick > 1 ? $"Quick retry {quick}/{MaxQuickRetries}..." : "Starting build...");

            var (exitCode, output) = await RunBuildProcessAsync(task);

            if (exitCode == 0) { task.ExitCode = 0; Emit(task, "Build succeeded!"); return; }

            if (IsTransientError(output))
            {
                var delay = quick * 3;
                Emit(task, $"Transient error. Waiting {delay}s...");
                await CleanWorkspaceAsync(task);
                await Task.Delay(delay * 1000, task.Cts.Token);
                continue;
            }

            Emit(task, $"Build failed (exit code {exitCode})");
            var wasFixed = await FixWithOpusAsync(task, output);
            if (wasFixed) { task.ExitCode = 0; return; }

            task.ExitCode = exitCode;
            task.Error = "Build failed after all retries";
            TrySetStatus(task, AgentTaskStatus.Running, AgentTaskStatus.Failed);
            return;
        }

        task.Error = "Build failed after quick retries";
        TrySetStatus(task, AgentTaskStatus.Running, AgentTaskStatus.Failed);
    }

    private async Task<bool> FixWithOpusAsync(AgentTask buildTask, string lastOutput)
    {
        for (int fix = 1; fix <= MaxOpusFixAttempts; fix++)
        {
            if (buildTask.Cts.Token.IsCancellationRequested) return false;

            Emit(buildTask, $"Sending to Opus for fix (attempt {fix}/{MaxOpusFixAttempts})...");

            var errorTail = lastOutput.Length > 8000 ? lastOutput[^8000..] : lastOutput;
            var fixTask = new AgentTask
            {
                Name = $"Fix {buildTask.Name}",
                Type = AgentTaskType.Agent,
                Channel = "opus",
                Model = _config.ClaudeModel,
                WorkingDirectory = buildTask.WorkingDirectory,
                Prompt = $"Build failed:\n```\n{errorTail}\n```\n\nDiagnose and fix source files. Focus on compilation errors, missing deps, type errors. Do NOT fix EPERM/EBUSY.",
                Cts = CancellationTokenSource.CreateLinkedTokenSource(buildTask.Cts.Token)
            };

            AgentStatus?.Invoke("opus", "start", fixTask.Id);
            try { await RunCliTaskAsync(fixTask); } catch { }
            AgentStatus?.Invoke("opus", "end", fixTask.Id);

            Emit(buildTask, "Opus applied fixes. Retrying...");
            await Task.Delay(3000);

            var (exitCode, output) = await RunBuildProcessAsync(buildTask);
            if (exitCode == 0) { Emit(buildTask, "Build succeeded after Opus fix!"); return true; }
            lastOutput = output;
        }
        return false;
    }

    private async Task<(int, string)> RunBuildProcessAsync(AgentTask task)
    {
        var (args, useBun) = MapBuildCommand(task.BuildCommand!);
        var psi = new ProcessStartInfo
        {
            FileName = useBun ? "bun" : "node",
            WorkingDirectory = task.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (useBun) psi.ArgumentList.Add("run");
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        psi.Environment["FORCE_COLOR"] = "3";
        psi.Environment["TERM"] = "xterm-256color";
        if (!string.IsNullOrEmpty(_config.AppleId))
        {
            psi.Environment["EXPO_APPLE_ID"] = _config.AppleId;
            psi.Environment["APPLE_ID"] = _config.AppleId;
        }
        if (!string.IsNullOrEmpty(_config.ApplePassword))
        {
            psi.Environment["EXPO_APPLE_PASSWORD"] = _config.ApplePassword;
            psi.Environment["APPLE_APP_SPECIFIC_PASSWORD"] = _config.ApplePassword;
        }

        var output = new StringBuilder();
        var recent = new StringBuilder();
        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc == null) return (-1, "Failed to start build process");
            WindowsJobObject.AssignProcess(proc);
            task.Process = proc;

            async Task ReadStream(StreamReader reader)
            {
                var buf = new char[2048];
                try
                {
                    while (!task.Cts.Token.IsCancellationRequested)
                    {
                        var n = await reader.ReadAsync(buf.AsMemory(), task.Cts.Token);
                        if (n == 0) break;
                        var text = new string(buf, 0, n);
                        output.Append(text);
                        task.OutputBuffer.Append(text);
                        AgentChunk?.Invoke(task.Channel, text);
                        recent.Append(text);
                        if (recent.Length > 2000) recent.Remove(0, recent.Length - 1000);
                        await HandleBuildPromptsAsync(recent.ToString(), proc);
                    }
                }
                catch { }
            }

            await Task.WhenAll(ReadStream(proc.StandardOutput), ReadStream(proc.StandardError));
            await proc.WaitForExitAsync(task.Cts.Token);
            return (proc.ExitCode, output.ToString());
        }
        catch (OperationCanceledException)
        {
            if (proc is { HasExited: false }) try { proc.Kill(entireProcessTree: true); } catch { }
            return (-1, output.ToString());
        }
        finally
        {
            task.Process = null;
            proc?.Dispose();
        }
    }

    private async Task RunPlanTaskAsync(AgentTask task)
    {
        var result = await StreamOpenRouterAsync(task.Prompt, task.Channel, task.Cts.Token);
        task.ResultData = result;
        task.OutputBuffer.Append(result);
    }

    private async Task RunTestTaskAsync(AgentTask task)
    {
        if (task.ShouldRecord)
        {
            // Bring the target app window to foreground so it's visible in the recording
            var testType = task.TestType ?? "web";
            await Task.Run(() => BringTestWindowToFront(task, testType));
            await Task.Delay(500);

            Emit(task, "Starting screen recording...");
            var (ok, path) = await Task.Run(() => _recorder.StartDesktopFallback(RecordingQualityOverride));
            if (ok)
            {
                task.RecordingPath = path;
                Emit(task, $"Recording -> {path}");
            }
            else
            {
                Emit(task, $"Recording failed to start: {path}");
            }
        }

        await RunCliTaskAsync(task);

        if (task.ShouldRecord && _recorder.IsRecording)
        {
            var path = await Task.Run(() => _recorder.StopRecording());
            task.RecordingPath = path;
            if (File.Exists(path))
            {
                var size = new FileInfo(path).Length;
                Emit(task, $"Recording saved: {path} ({size / 1024}KB)");
            }
            else
            {
                Emit(task, "Recording file was not created");
            }
        }
    }

    private async Task RunRecordTaskAsync(AgentTask task)
    {
        if (task.Prompt == "start")
        {
            var (ok, path) = await Task.Run(() => _recorder.StartDesktopFallback());
            if (ok) { task.RecordingPath = path; task.ResultData = path; Emit(task, $"Recording -> {path}"); }
            else Emit(task, $"Recording failed: {path}");
        }
        else if (task.Prompt == "stop" && _recorder.IsRecording)
        {
            var path = await Task.Run(() => _recorder.StopRecording());
            task.RecordingPath = path;
            task.ResultData = path;
            Emit(task, $"Recording saved: {path}");
        }
    }

    // --- OpenRouter helpers ---

    public async Task<string> StreamOpenRouterAsync(string prompt, string channel, CancellationToken ct)
    {
        var messages = new List<object>
        {
            new { role = "system", content = "You are a QA test planner. Output clear step-by-step test plans in plain English. Never output XML or tool calls." },
            new { role = "user", content = prompt }
        };
        var requestBody = new { model = _config.OpenRouterModel, messages, max_tokens = _config.MaxTokens, stream = true };
        var json = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions")
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.OpenRouterApiKey);
        request.Headers.Add("HTTP-Referer", "https://justgains.com");
        request.Headers.Add("X-Title", "JustWatch");

        var response = new StringBuilder();
        try
        {
            using var httpResp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!httpResp.IsSuccessStatusCode) return string.Empty;
            using var stream = await httpResp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (!line.StartsWith("data: ")) continue;
                var data = line[6..];
                if (data == "[DONE]") break;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");
                    if (delta.TryGetProperty("content", out var c))
                    {
                        var text = c.GetString();
                        if (text != null) { response.Append(text); AgentChunk?.Invoke(channel, text); }
                    }
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        return response.ToString();
    }

    public Task<string> CallOpenRouterQuietAsync(string prompt, int maxTokens, CancellationToken ct) =>
        CallOpenRouterAsync("Reply concisely. No explanation.", prompt, maxTokens, ct);

    // General one-shot OpenRouter completion with a caller-supplied system prompt.
    // Used for orchestration decisions and for synthesizing user-facing replies
    // from an agent's raw output (where "reply concisely" would be wrong).
    public async Task<string> CallOpenRouterAsync(string system, string prompt, int maxTokens, CancellationToken ct)
    {
        var messages = new List<object>
        {
            new { role = "system", content = system },
            new { role = "user", content = prompt }
        };
        var requestBody = new { model = _config.OpenRouterModel, messages, max_tokens = maxTokens, stream = false };
        var json = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions")
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.OpenRouterApiKey);
        request.Headers.Add("HTTP-Referer", "https://justgains.com");
        try
        {
            using var httpResp = await _http.SendAsync(request, ct);
            var body = await httpResp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }
        catch { return ""; }
    }

    // --- Build helpers ---

    private async Task HandleBuildPromptsAsync(string recent, Process proc)
    {
        if (proc.HasExited) return;
        async Task<bool> Write(string text)
        {
            try { await proc.StandardInput.WriteAsync(text); await proc.StandardInput.FlushAsync(); return true; }
            catch { return false; }
        }

        if (Match(recent, "log in to your Apple account", "(Y/n)")) { await Write("Y\n"); return; }
        if (Match(recent, "Apple ID:") && !string.IsNullOrEmpty(_config.AppleId)) { await Write(_config.AppleId + "\n"); return; }
        if (Match(recent, "Password", "password") && !string.IsNullOrEmpty(_config.ApplePassword)) { await Write(_config.ApplePassword + "\n"); return; }
        if (Match(recent, "(Y/n)")) { await Write("Y\n"); return; }
        if (Match(recent, "(y/N)")) { await Write("N\n"); return; }
        if (Match(recent, "❯") && Match(recent, "›")) { await Write("\n"); }
    }

    private static bool Match(string text, params string[] patterns) =>
        patterns.All(p => (text.Length > 500 ? text[^500..] : text).Contains(p, StringComparison.OrdinalIgnoreCase));

    private static bool IsTransientError(string output) =>
        TransientErrors.Any(e => output.Contains(e, StringComparison.OrdinalIgnoreCase));

    private async Task CleanWorkspaceAsync(AgentTask task)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "justgains-eas-workspace");
        if (!Directory.Exists(workspace)) return;
        Emit(task, "Cleaning EAS workspace...");
        for (int i = 0; i < 3; i++)
        {
            try { Directory.Delete(workspace, true); Emit(task, "Workspace cleared."); return; }
            catch { await Task.Delay(1000); }
        }
    }

    public static (string[] Args, bool UseBun) MapBuildCommand(string script) => script switch
    {
        "eas:ios:testflight" => (["scripts/eas-build.js", "production", "ios", "--auto-submit-with-profile", "production", "--fresh-workspace"], false),
        "eas:ios:prod" => (["scripts/eas-build.js", "production", "ios", "--fresh-workspace"], false),
        "eas:ios:dev" => (["scripts/eas-build.js", "development", "ios", "--fresh-workspace"], false),
        "eas:android:prod" => (["scripts/eas-build.js", "production", "android", "--fresh-workspace"], false),
        "eas:android:dev" => (["scripts/eas-build.js", "development", "android", "--fresh-workspace"], false),
        _ when script.StartsWith("bun:") => ([script[4..]], true),
        _ => ([script], true)
    };

    public static string GetBuildCommand(string platform, string profile, bool withTestFlight) =>
        (withTestFlight && platform == "ios") ? "eas:ios:testflight" :
        (platform, profile) switch
        {
            ("ios", "development") => "eas:ios:dev",
            ("ios", _) => "eas:ios:prod",
            ("android", "development") => "eas:android:dev",
            ("android", _) => "eas:android:prod",
            _ => $"bun:build:{platform}"
        };

    // --- Dependency helpers ---

    private static bool AreDependenciesMet(AgentTask task, List<AgentTask> all) =>
        task.DependsOn.All(depId => all.Any(t => t.Id == depId && t.Status == AgentTaskStatus.Completed));

    private static bool HasFailedDependency(AgentTask task, List<AgentTask> all) =>
        task.DependsOn.Any(depId => all.Any(t => t.Id == depId && t.Status is AgentTaskStatus.Failed or AgentTaskStatus.Cancelled));

    private static bool ShouldUseGoal(AgentTask task) =>
        task.Type is AgentTaskType.Agent or AgentTaskType.Test
        && !string.IsNullOrEmpty(task.Name)
        && task.Model != null;

    private static string DeriveGoal(AgentTask task) => task.Type switch
    {
        AgentTaskType.Agent when task.Name.StartsWith("Fix") =>
            $"fix the build error so that the build command succeeds on the next run",
        AgentTaskType.Test =>
            $"execute the test plan completely, reporting pass/fail for each step",
        _ => task.Name
    };

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void BringTestWindowToFront(AgentTask task, string testType)
    {
        var candidates = WindowFinder.FindForTestType(testType);
        if (candidates.Count == 0)
        {
            Emit(task, $"No {testType} window found to foreground");
            return;
        }

        var target = candidates[0];
        Emit(task, $"Bringing to front: \"{target.Title}\"");

        try
        {
            var proc = System.Diagnostics.Process.GetProcessById(target.Pid);
            var hwnd = proc.MainWindowHandle;
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, 9); // SW_RESTORE
                Thread.Sleep(200);
                SetForegroundWindow(hwnd);
            }
            proc.Dispose();
        }
        catch (Exception ex)
        {
            Emit(task, $"Could not foreground window: {ex.Message}");
        }
    }

    private void Emit(AgentTask task, string text) =>
        AgentChunk?.Invoke("orch", $"[info][{task.Name}] {text}");

    public void Dispose()
    {
        CancelAll();
        _recorder.Dispose();
        _cliSlots.Dispose();
        _masterCts.Dispose();
    }
}
