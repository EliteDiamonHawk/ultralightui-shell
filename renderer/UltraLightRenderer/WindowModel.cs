using System.Drawing;

namespace UltraLightRenderer;

public enum WindowKind
{
    Wallpaper,
    Widget,
    StartMenu
}

public enum StartMenuHiddenBehavior
{
    None,
    Pause,
    Unload
}

public sealed record RendererWindowSpec(
    string LogicalId,
    string Url,
    Rectangle Bounds,
    Rectangle VirtualBounds,
    string ThreadGroup,
    WindowKind Kind,
    bool Transparent = true,
    int MonitorNumber = 0,
    bool AutoHide = true,
    StartMenuHiddenBehavior HiddenBehavior = StartMenuHiddenBehavior.None,
    BridgePolicy? Bridge = null);

public sealed record WindowSnapshot(
    string Id,
    string Url,
    string Group,
    WindowKind Kind,
    Rectangle ConfiguredBounds,
    Rectangle CurrentBounds,
    int Monitor,
    bool Visible,
    bool Suspended,
    AppWindowState State);

public sealed record BridgePolicy(
    bool? Enabled,
    IReadOnlyList<string> BackendIds,
    IReadOnlyList<string> ControlRoutes)
{
    public static BridgePolicy Default { get; } =
        new(null, Array.Empty<string>(), Array.Empty<string>());

    public bool IsEnabled(string url) => Enabled ?? url.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
}
