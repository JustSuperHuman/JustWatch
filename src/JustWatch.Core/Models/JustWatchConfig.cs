namespace JustWatch.Core.Models;

public class JustWatchConfig
{
    public string ProjectName { get; set; } = "JustWatch";
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ConfigPath { get; set; }
    public string ProjectPath { get; set; } = Directory.GetCurrentDirectory();
    public List<ProcessConfig> Processes { get; set; } = [];
    public List<string> WatchFolders { get; set; } = [];
    public AgentConfig Agent { get; set; } = new();
    public ChatConfig Chat { get; set; } = new();
    public TelegramConfig Telegram { get; set; } = new();
    public WatcherConfig Watcher { get; set; } = new();
    public WebConfig Web { get; set; } = new();
    public AndroidConfig Android { get; set; } = new();
    public CommandsConfig Commands { get; set; } = new();
    public NotificationsConfig Notifications { get; set; } = new();
    public UpdatesConfig Updates { get; set; } = new();
}

public class UpdatesConfig
{
    // Check GitHub releases on launch and every few hours afterwards.
    public bool CheckAutomatically { get; set; } = true;

    // owner/name of the GitHub repository whose releases carry JustWatch builds.
    public string Repository { get; set; } = "JustSuperHuman/JustWatch";
}

public class NotificationsConfig
{
    public bool Enabled { get; set; } = true;

    // Windows tray balloon tips. Only success/warn/error levels pop a balloon;
    // info-level notifications stay in the in-app/web toast stream.
    public bool TrayBalloons { get; set; } = true;

    // Notify on dev process transitions (running/stopped/crashed/recovering).
    public bool ProcessStatus { get; set; } = true;

    // Notify on agent task transitions (started/completed/failed/cancelled).
    public bool TaskStatus { get; set; } = true;
}

public class AndroidConfig
{
    public bool Enabled { get; set; }
    // Path to adb. "adb" assumes Android SDK platform-tools is on PATH.
    public string AdbPath { get; set; } = "adb";

    // Empty discovers MuMu from its running process, installation registry, or standard paths.
    public string MuMuManagerPath { get; set; } = string.Empty;
    public int CoreInstanceIndex { get; set; } = 0;
    public string EmulatorNamePrefix { get; set; } = "JG";

    // Legacy static launcher config is still deserialized so old config files
    // keep loading, but Android commands are now built into the app.
    public List<AndroidEmulatorLaunchConfig> EmulatorLaunchers { get; set; } = [];

    // App package relaunched on a running emulator after the mobile dev server
    // restarts, so it reconnects to the fresh Metro bundler instead of sitting on
    // a stale/dead connection. Empty disables the relaunch step.
    public string PackageName { get; set; } = "com.justgains.justgainsmobile";

    // Expo dev-client URL scheme. When set, relaunches deep-link the app straight
    // into the Metro bundler (scheme://expo-development-client/?url=...) instead
    // of parking on the dev-launcher home screen. Empty = plain launcher intent.
    public string DevClientScheme { get; set; } = "justgains";

    // When the "mobile" process restarts (manual, crash, or EMFILE auto-restart),
    // wait for Metro to come back and reload the app on any attached emulator.
    public bool ReloadOnMobileRestart { get; set; } = true;

    // Metro bundler port polled for readiness before reloading. 0 = use the
    // "mobile" process Port.
    public int MetroPort { get; set; }

    // Max seconds to wait for Metro to report ready before reloading anyway.
    public int MetroReadyTimeoutSeconds { get; set; } = 60;
}

public class CommandsConfig
{
    public bool Enabled { get; set; } = true;
    public List<string> Roots { get; set; } = [];
    public int MaxDepth { get; set; } = 3;
}

public class AndroidEmulatorLaunchConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string[] Args { get; set; } = [];
    public string WorkingDirectory { get; set; } = string.Empty;
}

public class AndroidEmulatorInfo
{
    public string Id { get; set; } = string.Empty;
    public string AvdName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Index { get; set; }
    public bool IsCore { get; set; }
    public bool IsClone { get; set; }
    public string? Serial { get; set; }
    public bool IsRunning { get; set; }
    public bool IsReady => !string.IsNullOrWhiteSpace(Serial);
    public string? LastCommand { get; set; }
    public DateTimeOffset? LastCommandAt { get; set; }
    public string? IdleTime { get; set; }
    public double? IdleSeconds { get; set; }
    public string? CommandIdleTime { get; set; }
    public double? CommandIdleSeconds { get; set; }
}

public class WebConfig
{
    public bool Enabled { get; set; } = true;

    // Port the browser-facing agent UI + control API listens on. Fixed at 6767
    // across every build configuration (Debug, dev, Release) so the API is always
    // reachable at the same address.
    public int Port { get; set; } = 6767;

    // "localhost" = this machine only. "+" (or "0.0.0.0") = all interfaces so other
    // devices on the LAN can reach it (may require an admin URL ACL / firewall rule).
    public string Host { get; set; } = "localhost";
}

public class AgentConfig
{
    public string Command { get; set; } = "claude";
    public string[] Args { get; set; } = ["-p", "--dangerously-skip-permissions"];
    public int OutputBufferLines { get; set; } = 200;
}

public class ChatConfig
{
    public string Mode { get; set; } = "openrouter";
    public string OpenRouterApiKey { get; set; } = string.Empty;
    public string OpenRouterModel { get; set; } = "anthropic/claude-sonnet-4";
    public int MaxTokens { get; set; } = 8192;
    public string ClaudeCommand { get; set; } = "claude";
    public string ClaudeModel { get; set; } = "opus";
    public string ClaudeTestModel { get; set; } = "sonnet";
    public string[] ClaudeArgs { get; set; } = ["-p", "--dangerously-skip-permissions"];

    // Watchdog: a Claude CLI task that runs longer than this is killed and marked
    // failed, so a hung agent can't occupy a CLI slot forever. 0 disables.
    public int AgentTimeoutSeconds { get; set; } = 600;
    public string AppleId { get; set; } = string.Empty;
    public string ApplePassword { get; set; } = string.Empty;
    public string FfmpegPath { get; set; } = @"C:\Program Files\ShareX\ffmpeg.exe";
    public string RecordingsDir { get; set; } = string.Empty;
}

public class TelegramConfig
{
    public bool Enabled { get; set; }
    public string BotToken { get; set; } = string.Empty;
    public long ChatId { get; set; }

    /// <summary>
    /// Named destinations for POST /api/telegram/messages. Each entry is one chat,
    /// group, channel or forum topic; add a group here and it is immediately
    /// addressable by its Id. Empty falls back to a single "default" group built
    /// from <see cref="BotToken"/> + <see cref="ChatId"/>.
    /// </summary>
    public List<TelegramGroup> Groups { get; set; } = [];

    /// <summary>Group Id used when a request names none. Empty = first configured group.</summary>
    public string DefaultGroup { get; set; } = string.Empty;
    public int MaxRecordingSeconds { get; set; } = 30;
    public int RecordingCrf { get; set; } = 35;
    public int RecordingFramerate { get; set; } = 10;
    public string RecordingResolution { get; set; } = "1280x720";

    // Inbound voice messages: transcribed via an OpenAI-compatible Whisper endpoint,
    // then routed to the agent exactly like a typed message. Photos/documents are
    // always saved and their path handed to the agent regardless of these settings.
    public bool TranscribeVoice { get; set; } = true;
    public string TranscriptionApiKey { get; set; } = string.Empty;
    public string TranscriptionModel { get; set; } = "whisper-1";
    public string TranscriptionBaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>
    /// Where POST /api/telegram/files puts files too big for the Bot API (50 MB);
    /// they're uploaded here and posted as a link instead.
    /// </summary>
    public TelegramStorageConfig Storage { get; set; } = new();
}

/// <summary>
/// S3-compatible bucket for oversized Telegram files. Defaults match the JustGains
/// API's Tigris bucket, served publicly from m.justgains.com; only the keys need setting.
/// </summary>
public class TelegramStorageConfig
{
    public string ServiceUrl { get; set; } = "https://t3.storage.dev";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Bucket { get; set; } = "justgains";

    /// <summary>Public base URL the bucket is served from; the object key is appended.</summary>
    public string PublicBaseUrl { get; set; } = "https://m.justgains.com";
    public string KeyPrefix { get; set; } = "telegram";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ServiceUrl) && !string.IsNullOrWhiteSpace(AccessKey) &&
        !string.IsNullOrWhiteSpace(SecretKey) && !string.IsNullOrWhiteSpace(Bucket);
}

/// <summary>
/// One Telegram destination. <see cref="ChatId"/> is a string so it can hold a
/// numeric id (123456789), a supergroup/channel id (-1001234567890) or an
/// @publicchannelname. <see cref="BotToken"/> is optional: leave it empty to use
/// the shared <see cref="TelegramConfig.BotToken"/>, set it when a group is only
/// reachable by a different bot.
/// </summary>
public class TelegramGroup
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
    public string BotToken { get; set; } = string.Empty;

    /// <summary>Forum topic id (message_thread_id) for supergroups with topics. 0 = none.</summary>
    public int ThreadId { get; set; }

    /// <summary>Skip this group when a request targets "all". Direct sends by Id still work.</summary>
    public bool ExcludeFromAll { get; set; }
}

public class WatcherConfig
{
    public int CheckIntervalSeconds { get; set; } = 10;
    public string TrayAppExecutable { get; set; } = "JustWatch.exe";
}

public enum ProcessStatus
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Crashed,
    RecoveringWithAgent
}
