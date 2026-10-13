using System.Text.Json;
using System.Text.Json.Serialization;
using JustWatch.Core.Models;

namespace JustWatch.Core.Services;

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Environment variable that overrides <see cref="JustWatchConfig.ProjectPath"/> when set.
    /// Lets the JustGains dev directory move without editing config.
    /// </summary>
    public const string ProjectPathEnvVar = "JUSTGAINS_DEV_DIR";

    public static JustWatchConfig Load(string? configPath = null)
    {
        configPath ??= FindConfigPath();

        // Installed copies (no source checkout) keep their config in %APPDATA% so
        // in-place updates never touch it. Seed a starter file on first launch.
        if (configPath == null && RepoPaths.FindRepoRoot() == null)
        {
            configPath = UserConfigPath;
            if (!File.Exists(configPath)) WriteStarterConfig(configPath);
        }

        JustWatchConfig config;
        if (configPath != null && File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            config = JsonSerializer.Deserialize<JustWatchConfig>(json, JsonOptions)
                     ?? CreateDefault();
        }
        else
        {
            config = CreateDefault();
        }

        config.ConfigPath = configPath;
        ApplyEnvironmentOverrides(config);
        ServiceConfiguration.Validate(config.Processes);
        return config;
    }

    private static void ApplyEnvironmentOverrides(JustWatchConfig config)
    {
        var devDir = Environment.GetEnvironmentVariable("JUSTWATCH_PROJECT_PATH")
            ?? Environment.GetEnvironmentVariable("BUILDER_PROJECT_PATH")
            ?? Environment.GetEnvironmentVariable(ProjectPathEnvVar);
        if (!string.IsNullOrWhiteSpace(devDir))
            config.ProjectPath = devDir.Trim();
    }

    public static void Save(JustWatchConfig config, string configPath)
    {
        var dir = Path.GetDirectoryName(configPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(configPath, json);
    }

    public static string GetDefaultConfigPath()
    {
        var root = RepoPaths.FindRepoRoot();
        return root != null
            ? Path.Combine(root, "config", "justwatch-config.json")
            : UserConfigPath;
    }

    /// <summary>Per-user config used by installed (non-source) copies.</summary>
    public static string UserConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JustWatch", "justwatch-config.json");

    private static void WriteStarterConfig(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var home = JsonSerializer.Serialize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            File.WriteAllText(path, $$"""
                {
                  // JustWatch configuration. Add services from Settings (http://localhost:6767/services)
                  // or edit this file and restart JustWatch. See https://github.com/JustSuperHuman/JustWatch
                  "projectName": "JustWatch",
                  "projectPath": {{home}},
                  "watchFolders": [],
                  "processes": [],
                  "android": { "enabled": false },
                  "updates": { "checkAutomatically": true }
                }

                """);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Config] Could not create {path}: {ex.Message}");
        }
    }

    private static string? FindConfigPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("JUSTWATCH_CONFIG_PATH")
            ?? Environment.GetEnvironmentVariable("BUILDER_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
        var sourcePointer = Path.Combine(AppContext.BaseDirectory, "config-source.txt");
        if (File.Exists(sourcePointer) && File.Exists(File.ReadAllText(sourcePointer).Trim()))
            return File.ReadAllText(sourcePointer).Trim();

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "justwatch-config.json"),
            Path.Combine(AppContext.BaseDirectory, "config", "justwatch-config.json"),
        };

        var root = RepoPaths.FindRepoRoot();
        if (root != null)
            candidates.Add(Path.Combine(root, "config", "justwatch-config.json"));

        return candidates.FirstOrDefault(File.Exists);
    }

    private static JustWatchConfig CreateDefault() => new()
    {
        ProjectPath = Directory.GetCurrentDirectory()
    };
}
