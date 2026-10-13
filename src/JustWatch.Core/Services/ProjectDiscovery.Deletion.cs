using System.Security.Cryptography;
using JustWatch.Core.Models;

namespace JustWatch.Core.Services;

public sealed record ProjectDeletionPreview(string Token, string Name, string Directory, string[] Projects, string[] Services);

public sealed partial class ProjectDiscovery
{
    private sealed record DeletionTicket(string Id, string Name, string Directory, string Stamp, string Revision, DateTime Expires);
    private readonly Dictionary<string, DeletionTicket> _deletionTickets = new();
    private bool _deleting;

    private string ValidateDeletionTarget(DiscoveredProject project)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.Directory));
        if (!Path.IsPathFullyQualified(directory) || directory.Equals(Path.GetPathRoot(directory), StringComparison.OrdinalIgnoreCase) ||
            !_manager.Config.WatchFolders.Any(root => IsWithin(directory, root)) ||
            _manager.Config.WatchFolders.Any(root => IsWithin(Path.GetFullPath(root), directory)))
            throw new InvalidOperationException("A watch folder or drive cannot be deleted here. Choose an individual project inside a watch folder.");
        var protectedPaths = new[] { AppContext.BaseDirectory, _manager.Config.ConfigPath,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        if (protectedPaths.Any(p => !string.IsNullOrWhiteSpace(p) && IsWithin(Path.GetFullPath(p), directory)))
            throw new InvalidOperationException("JustWatch, its configuration, and system folders cannot be deleted here.");
        if (!Directory.Exists(directory) || !SafePath(directory) || !SafePath(Path.Combine(directory, "package.json")))
            throw new InvalidOperationException("The project folder changed or contains a linked package. Rescan before deleting.");
        var current = ReadProject(directory);
        if (current.Id != project.Id || current.Name != project.Name) throw new InvalidOperationException("The project changed. Review the deletion again.");
        return Directory.GetCreationTimeUtc(directory).Ticks + ":" + Convert.ToHexString(SHA256.HashData(ReadSmallFile(Path.Combine(directory, "package.json"), 1024 * 1024)));
    }

    public ProjectDeletionPreview PreviewDeletion(string id)
    {
        var project = GetProject(id);
        var stamp = ValidateDeletionTarget(project);
        var services = _manager.GetProcessConfigs();
        var names = services.Where(p => IsWithin(Path.GetFullPath(string.IsNullOrWhiteSpace(p.WorkingDirectory) ? _manager.Config.ProjectPath : p.WorkingDirectory, _manager.Config.ProjectPath), project.Directory))
            .Select(p => p.Name).ToArray();
        var nested = _scan.Projects.Where(p => IsWithin(p.Directory, project.Directory)).Select(p => p.Name).ToArray();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        lock (_gate)
        {
            foreach (var key in _deletionTickets.Where(p => p.Value.Expires <= DateTime.UtcNow).Select(p => p.Key).ToArray()) _deletionTickets.Remove(key);
            if (_deletionTickets.Count >= 64) _deletionTickets.Remove(_deletionTickets.Keys.First());
            _deletionTickets[token] = new(id, project.Name, project.Directory, stamp, ServiceConfiguration.Revision(services), DateTime.UtcNow.AddMinutes(5));
        }
        return new(token, project.Name, project.Directory, nested, names);
    }

    public async Task DeleteProjectAsync(string id, string token, string confirmation)
    {
        await _actions.WaitAsync();
        try
        {
            DeletionTicket ticket;
            Task scan;
            lock (_gate)
            {
                if (!_deletionTickets.Remove(token, out ticket!) || ticket.Id != id || ticket.Expires <= DateTime.UtcNow)
                    throw new ArgumentException("This confirmation expired. Review the deletion again.");
                if (confirmation != ticket.Name) throw new ArgumentException("Type the exact project name to confirm deletion.");
                _deleting = true;
                scan = _scanTask;
            }
            // Let an existing scan finish; no new scan can republish a removed card during deletion.
            await scan;
            var project = GetProject(id);
            void Validate()
            {
                if (project.Directory != ticket.Directory || ValidateDeletionTarget(project) != ticket.Stamp)
                    throw new InvalidOperationException("The project changed since confirmation. Review the deletion again.");
            }
            await _manager.DeleteProjectFilesAsync(project.Directory, ticket.Revision, Validate, () => DeleteProjectTree(project.Directory, Validate));
            lock (_gate)
            {
                var remaining = _scan.Projects.Where(p => !IsWithin(p.Directory, project.Directory)).ToArray();
                _scan = _scan with { Projects = remaining, Folders = _scan.Folders.Select(f => f with { ProjectCount = remaining.Count(p => IsWithin(p.Directory, f.Path)) }).ToArray() };
            }
        }
        finally
        {
            lock (_gate) _deleting = false;
            _actions.Release();
            _ = RequestScan(changed: true);
        }
    }

    private static void DeleteProjectTree(string directory, Action validate)
    {
        var original = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var parent = Path.GetDirectoryName(original) ?? throw new IOException("A drive cannot be deleted.");
        var staged = Path.Combine(parent, ".justwatch-deleting-" + Guid.NewGuid().ToString("N"));
        if (Path.GetDirectoryName(staged) != parent || !IsWithin(original, parent) || !SafePath(original)) throw new IOException("Invalid project deletion path.");
        // Detach the exact confirmed folder before traversal. Links inside it are removed as links.
        // Windows may retain a terminated server's directory handle briefly after process exit.
        Retry(() => { validate(); Directory.Move(original, staged); });
        try
        {
            var pending = new Stack<(string Path, bool Finish)>(); pending.Push((staged, false));
            while (pending.TryPop(out var item))
            {
                var full = Path.GetFullPath(item.Path);
                if (!IsWithin(full, staged)) throw new IOException("A deletion path escaped the project folder.");
                var attributes = File.GetAttributes(full);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Retry(() => { if (isDirectory) Directory.Delete(full); else File.Delete(full); });
                    continue;
                }
                if (!isDirectory)
                {
                    if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(full, attributes & ~FileAttributes.ReadOnly);
                    Retry(() => File.Delete(full));
                }
                else if (item.Finish) Retry(() => Directory.Delete(full));
                else
                {
                    pending.Push((full, true));
                    foreach (var entry in Directory.EnumerateFileSystemEntries(full)) pending.Push((entry, false));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var remaining = staged;
            try { if (Directory.Exists(staged) && !Directory.Exists(original)) { Directory.Move(staged, original); remaining = original; } } catch (IOException) { }
            throw new IOException($"Deletion could not finish. Some files may already be deleted. Remaining files: {remaining}. Services were stopped and removed. {ex.Message}", ex);
        }
    }

    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (Exception ex) when (attempt < 30 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}
