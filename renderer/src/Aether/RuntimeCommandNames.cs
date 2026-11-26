using KhaosShell.Contracts;

namespace Aether;

internal static class RuntimeCommandNames
{
    public static IReadOnlyList<string> Allowed { get; } = new[]
    {
        "showWindow",
        "hideWindow",
        "unloadWindow",
        "reloadWindow",
        "pause",
        "resume",
        "status",
        "reload"
    };

    public static bool TryMap(string name, out RendererCommandKind kind)
    {
        kind = name switch
        {
            "showWindow" => RendererCommandKind.RevealWindow,
            "hideWindow" => RendererCommandKind.HideWindow,
            "unloadWindow" => RendererCommandKind.UnloadWindow,
            "reloadWindow" => RendererCommandKind.ReloadWindow,
            "pause" => RendererCommandKind.Pause,
            "resume" => RendererCommandKind.Resume,
            "status" => RendererCommandKind.Status,
            "reload" => RendererCommandKind.Reload,
            _ => RendererCommandKind.None
        };
        return kind != RendererCommandKind.None;
    }
}
