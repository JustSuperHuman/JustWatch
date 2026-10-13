namespace JustWatch.Core.Services;

public static class ProjectLibraryHtml
{
    public static string Generate() => WebWorkspace.Render("projects", "projects");
}
