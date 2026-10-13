namespace JustWatch.Core.Services;

public static class ServiceManagerHtml
{
    public static string Generate() => WebWorkspace.Render("services", "settings");
}
