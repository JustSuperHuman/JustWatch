namespace JustWatch.Core.Services;

/// <summary>
/// Locates the JustWatch repository relative to the running executable,
/// so paths resolve wherever the repo lives instead of a hardcoded drive letter.
/// </summary>
public static class RepoPaths
{
    private const string SolutionMarker = "JustWatch.slnx";

    /// <summary>
    /// Walks up from the executing assembly's directory until it finds the repo
    /// root (the directory containing <c>JustWatch.slnx</c>). Returns null
    /// when run from a deployed location outside the repo.
    /// </summary>
    public static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionMarker)))
                return dir.FullName;
        }

        return null;
    }
}
