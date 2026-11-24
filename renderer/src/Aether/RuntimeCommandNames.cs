using KhaosShell.Contracts;

namespace Aether;

internal static class RuntimeCommandNames
{
    public static IReadOnlyList<string> Allowed { get; } = new[]
    {
        "showMenu",
        "hideMenu",
        "toggleMenu",
        "showWindow",
        "hideWindow",
        "pause",
        "resume",
        "status",
        "reload"
    };

    public static bool TryMap(string name, out RendererCommandKind kind)
    {
        kind = name switch
        {
            "showMenu" => RendererCommandKind.ShowMenu,
            "hideMenu" => RendererCommandKind.HideMenu,
            "toggleMenu" => RendererCommandKind.ToggleMenu,
            "showWindow" => RendererCommandKind.RevealWindow,
            "hideWindow" => RendererCommandKind.HideWindow,
            "pause" => RendererCommandKind.Pause,
            "resume" => RendererCommandKind.Resume,
            "status" => RendererCommandKind.Status,
            "reload" => RendererCommandKind.Reload,
            _ => RendererCommandKind.None
        };
        return kind != RendererCommandKind.None;
    }
}
