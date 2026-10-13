using System.IO;

namespace JustWatch.TrayApp;

// Embedded in the assembly and inlined into the document: startup never needs
// a CDN, a warm browser cache, or separate files alongside the deployed app.
internal static class TerminalAssets
{
    public static readonly string Styles = Read("xterm.css");
    public static readonly string Xterm = Read("xterm.js");
    public static readonly string Fit = Read("xterm-addon-fit.js");
    public static readonly string WebLinks = Read("xterm-addon-web-links.js");
    public static readonly string LogHighlighter = Read("log-highlighter.js");

    private static string Read(string name)
    {
        using var stream = typeof(TerminalAssets).Assembly.GetManifestResourceStream(
            "JustWatch.TrayApp.TerminalAssets." + name)
            ?? throw new InvalidOperationException("Missing bundled terminal asset: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
