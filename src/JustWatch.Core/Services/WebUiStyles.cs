namespace JustWatch.Core.Services;

public static class WebUiStyles
{
    public const string Scrollbars = """
        * { scrollbar-width: thin; scrollbar-color: #526075 transparent; }
        ::-webkit-scrollbar { width: 10px; height: 10px; }
        ::-webkit-scrollbar-track { background: transparent; }
        ::-webkit-scrollbar-thumb { background: #526075; border: 2px solid transparent; background-clip: padding-box; border-radius: 8px; }
        ::-webkit-scrollbar-thumb:hover { background-color: #8095b3; }
        ::-webkit-scrollbar-thumb:active { background-color: #a8c8f0; }
        ::-webkit-scrollbar-corner { background: transparent; }
        @media (forced-colors: active) { * { scrollbar-color: auto; } }
        """;
}
