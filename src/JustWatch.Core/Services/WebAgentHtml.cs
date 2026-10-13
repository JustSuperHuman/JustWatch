namespace JustWatch.Core.Services;

public static class WebAgentHtml
{
    public static string Generate() => WebWorkspace.Render("agent", "agent");
}
