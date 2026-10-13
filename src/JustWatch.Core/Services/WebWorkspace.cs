namespace JustWatch.Core.Services;

/// <summary>Shared navigation and design assets for every browser workspace page.</summary>
public static class WebWorkspace
{
    public static string Render(string asset, string active)
    {
        var marker = asset switch { "projects" => "PROJECT", "services" => "SERVICE", _ => "AGENT" };
        return Read(asset + ".html")
            .Replace("</head>", "<link rel=\"icon\" type=\"image/svg+xml\" href=\"/favicon.svg\"></head>", StringComparison.Ordinal)
            .Replace("/* " + marker + "_STYLES */", Read(asset + ".css") + Read("workspace.css") + WebUiStyles.Scrollbars, StringComparison.Ordinal)
            .Replace("<!-- WORKSPACE_NAV -->", Navigation(active), StringComparison.Ordinal)
            .Replace("/* " + marker + "_SCRIPT */", Read(asset + ".js"), StringComparison.Ordinal);
    }

    internal static string Read(string name)
    {
        using var stream = typeof(WebWorkspace).Assembly.GetManifestResourceStream("JustWatch.Core.Assets." + name)
            ?? throw new InvalidOperationException("Missing workspace asset: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Navigation(string active)
    {
        var pages = new[] {
            ("projects", "/projects", "Applications", "M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z"),
            ("agent", "/#agent", "Agent workspace", "m5 6 6 6-6 6m8 0h6"),
            ("emulators", "/#emulators", "Emulators", "M7 2h10v20H7zM11 18h2"),
            ("commands", "/#commands", "Commands", "m13 2-9 12h7l-1 8 10-13h-7l0-7Z"),
            ("settings", "/services", "Settings & tray", "M4 7h5m5 0h6M4 17h10m5 0h1M9 4v6m5 4v6")
        };
        var links = string.Join("", pages.Select(p =>
            $"<a class=\"ws-nav-link\" data-page=\"{p.Item1}\" href=\"{p.Item2}\" aria-label=\"{p.Item3}\"" +
            (active == p.Item1 ? " aria-current=\"page\"" : "") +
            $"><svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"{p.Item4}\"/></svg><span class=\"ws-link-label\">{p.Item3}</span><span class=\"ws-mobile-name\" aria-hidden=\"true\">{(p.Item1 == "agent" ? "Agents" : p.Item1 == "settings" ? "Settings" : p.Item3)}</span></a>"));
        return """
            <a class="ws-skip" href="#workspace-main">Skip to content</a>
            <aside class="ws-sidebar">
              <a class="ws-brand" href="/projects" aria-label="JustWatch home"><img class="ws-brand-mark" src="/favicon.svg" alt="" width="34" height="34"><span>JustWatch<small>Local workspace</small></span></a>
              <div class="ws-nav-label">WORKSPACE</div>
              <nav class="ws-nav" aria-label="Workspace">
            """ + links + """
              </nav>
              <div class="ws-sidebar-note"><span class="ws-local-dot"></span>On this computer</div>
            </aside>
            """;
    }
}
