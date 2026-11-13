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
    StartMenuHiddenBehavior HiddenBehavior = StartMenuHiddenBehavior.None);
