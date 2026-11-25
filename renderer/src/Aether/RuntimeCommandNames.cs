using KhaosShell.Contracts;

namespace Aether;

internal static class RuntimeCommandNames
{
    public static IReadOnlyList<string> Allowed { get; } = new[]
    {
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
