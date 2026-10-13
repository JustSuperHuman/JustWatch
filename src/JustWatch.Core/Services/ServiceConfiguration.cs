using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JustWatch.Core.Models;

namespace JustWatch.Core.Services;

public static class ServiceConfiguration
{
    // Called under ProcessManager's configuration lock, just like service saves.
    // Patch only this value so comments and unrelated settings remain untouched.
    public static void SaveWatchFolders(string path, List<string> folders, List<string> expected)
    {
        var bytes = File.ReadAllBytes(path);
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        var reader = new Utf8JsonReader(bytes.AsSpan(offset), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var start = -1; var end = -1; var rootStart = -1; var rootEnd = -1; var hasProperties = false;
        List<string> current = [];
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject && reader.CurrentDepth == 0) rootStart = (int)reader.BytesConsumed + offset;
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0) rootEnd = (int)reader.TokenStartIndex + offset;
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
            hasProperties = true;
            if (!reader.GetString()!.Equals("watchFolders", StringComparison.OrdinalIgnoreCase)) continue;
            reader.Read(); start = (int)reader.TokenStartIndex + offset;
            reader.Skip(); end = (int)reader.BytesConsumed + offset;
            current = JsonSerializer.Deserialize<List<string>>(bytes.AsSpan(start, end - start), JsonOptions) ?? [];
        }
        if (!current.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Watch folders changed on disk. Restart JustWatch before saving.");
        var value = JsonSerializer.Serialize(folders, JsonOptions);
        if (start < 0)
        {
            if (rootEnd < 0) throw new InvalidOperationException("Invalid JustWatch configuration.");
            // Insert first, avoiding a duplicate comma when the object already has a trailing comma.
            start = end = rootStart;
            value = "\n  \"watchFolders\": " + value + (hasProperties ? "," : "") + "\n";
        }
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = File.Create(temp))
            {
                output.Write(bytes.AsSpan(0, start));
                output.Write(Encoding.UTF8.GetBytes(value));
                output.Write(bytes.AsSpan(end)); output.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static List<ProcessConfig> Clone(IEnumerable<ProcessConfig> services) =>
        JsonSerializer.Deserialize<List<ProcessConfig>>(JsonSerializer.Serialize(services, JsonOptions), JsonOptions)!;

    public static string Revision(IEnumerable<ProcessConfig> services) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(services, JsonOptions))));

    public static void Validate(IReadOnlyList<ProcessConfig> services)
    {
        if (services.Count > 100) throw new ArgumentException("At most 100 services are supported.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ports = new HashSet<int>();
        foreach (var p in services)
        {
            if (p == null || p.Id == null || !Regex.IsMatch(p.Id, "^[a-z][a-z0-9-]{0,63}$") || p.Id == "configuration")
                throw new ArgumentException("Service IDs must start with a lowercase letter and contain only lowercase letters, digits and hyphens (maximum 64 characters). 'configuration' is reserved.");
            if (!ids.Add(p.Id)) throw new ArgumentException($"Duplicate service ID: {p.Id}.");
            if (string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Command))
                throw new ArgumentException($"{p.Id}: name and command are required.");
            if (p.Project?.Length > 80)
                throw new ArgumentException($"{p.Id}: project name must be at most 80 characters.");
            if (p.Port is not null && (p.Port < 1 || p.Port > 65535 || !ports.Add(p.Port.Value)))
                throw new ArgumentException($"{p.Id}: port must be unique and between 1 and 65535.");
            if (!Regex.IsMatch(p.TrayColor ?? "", "^#[0-9a-fA-F]{6}$"))
                throw new ArgumentException($"{p.Id}: trayColor must be a six-digit hex color, such as #34A853.");
            if (!string.IsNullOrWhiteSpace(p.Url) && (!Uri.TryCreate(p.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")))
                throw new ArgumentException($"{p.Id}: URL must use http or https.");
            if (p.CrashThreshold < 1 || p.CrashWindowSeconds < 1 || p.Args == null || p.StopArgs == null || p.Environment == null || p.RestartOutputPatterns == null ||
                p.Args.Any(a => a == null) || p.StopArgs.Any(a => a == null) || p.RestartOutputPatterns.Any(string.IsNullOrWhiteSpace) ||
                p.Environment.Any(e => string.IsNullOrWhiteSpace(e.Key) || e.Key.Contains('=') || e.Value == null))
                throw new ArgumentException($"{p.Id}: invalid recovery, arguments, environment or restart patterns.");
        }
        if (services.Count(p => p.ShowTrayDot) > 9)
            throw new ArgumentException("Select at most nine tray dots so each stays visible at taskbar size.");
    }

    public static void CheckDiskRevision(string path, string expectedRevision)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var property = document.RootElement.EnumerateObject().First(p => p.Name.Equals("processes", StringComparison.OrdinalIgnoreCase));
        var current = property.Value.Deserialize<List<ProcessConfig>>(JsonOptions)!;
        if (Revision(current) != expectedRevision) throw new InvalidOperationException("Configuration changed on disk. Restart JustWatch to load external changes before saving.");
    }

    // Replace only the processes array. Preserve comments, formatting and unrelated
    // configuration (including credentials) byte for byte. Atomic file replacement.
    public static void Save(string path, IReadOnlyList<ProcessConfig> services, string expectedRevision)
    {
        var bytes = File.ReadAllBytes(path);
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        var reader = new Utf8JsonReader(bytes.AsSpan(offset), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.GetString()!.Equals("processes", StringComparison.OrdinalIgnoreCase)) continue;
            reader.Read();
            var start = (int)reader.TokenStartIndex + offset;
            reader.Skip();
            var end = (int)reader.BytesConsumed + offset;
            var current = JsonSerializer.Deserialize<List<ProcessConfig>>(bytes.AsSpan(start, end - start), JsonOptions)!;
            if (Revision(current) != expectedRevision) throw new InvalidOperationException("Configuration changed on disk. Reload before saving.");
            var replacement = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(services, JsonOptions));
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = File.Create(temp))
                {
                    output.Write(bytes.AsSpan(0, start));
                    output.Write(replacement);
                    output.Write(bytes.AsSpan(end));
                    output.Flush(true);
                }
                File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return;
        }
        throw new InvalidOperationException("Configuration file must contain a processes array.");
    }
}
