using System.Drawing;
using System.Text.Json;

namespace Aether;

// WindowKind identifies the compatibility adapter which produced a window. The
// runtime never uses it to select native behavior; surface and input policy do
// that instead.
public enum WindowKind
{
    Generic,
    Wallpaper,
    Widget
}

public enum WindowSurfaceKind
{
    Desktop,
    TopLevel
}

public enum WindowInitialVisibility
{
    Visible,
    Hidden
}

public enum WindowZOrder
{
    Bottom,
    Normal,
    Top,
    Topmost,
    TopmostWhenFocused
}

public enum WindowHiddenBehavior
{
    Retain,
    Pause,
    Unload
}

public enum WindowInputMode
{
    None,
    Native,
    Forwarded
}

public enum WindowFocusMode
{
    Never,
    OnShow,
    OnReveal,
    OnClick
}

public sealed record WindowInputPolicy(
    WindowInputMode Mouse = WindowInputMode.Native,
    WindowInputMode Keyboard = WindowInputMode.Native,
    WindowFocusMode Focus = WindowFocusMode.Never,
    bool ClickThrough = false);

public sealed record WindowNotificationPolicy(
    bool Focus = false,
    bool Visibility = false,
    bool Geometry = false,
    bool Lifecycle = false);

public sealed record WorkAreaReservation(
    bool Enabled = false,
    string Edge = "bottom",
    int Thickness = 0);

public sealed record WindowGeometry(
    double X,
    double Y,
    double Width,
    double Height,
    int Monitor = 1,
    string CoordinateSpace = "monitor",
    WidgetAnchor Anchor = WidgetAnchor.TopLeft,
    string PositionUnit = "pixels",
    string SizeUnit = "pixels");

public sealed record WindowSurface(
    WindowSurfaceKind Kind = WindowSurfaceKind.TopLevel,
    string? Title = null,
    WindowZOrder ZOrder = WindowZOrder.Normal,
    bool Transparent = false,
    bool Framed = false,
    bool Resizable = false,
    bool ShowInTaskbar = false,
    WorkAreaReservation? WorkArea = null);

public sealed record WindowLifecycle(
    WindowInitialVisibility InitialVisibility = WindowInitialVisibility.Visible,
    WindowHiddenBehavior HiddenBehavior = WindowHiddenBehavior.Retain,
    bool SuspendOnFullscreen = true);

public sealed record RendererWindowSpec(
    string LogicalId,
    string Url,
    Rectangle Bounds,
    Rectangle VirtualBounds,
    string RuntimeGroup,
    WindowKind Kind,
    WindowGeometry? Geometry = null,
    WindowSurface? Surface = null,
    WindowLifecycle? Lifecycle = null,
    int Monitor = 0,
    BridgePolicy? Bridge = null,
    IReadOnlyDictionary<string, JsonElement>? Variables = null,
    IReadOnlyList<string>? Cues = null,
    WindowInputPolicy? Input = null,
    WindowNotificationPolicy? Notifications = null)
{
    public WindowSurface EffectiveSurface => Surface ?? new();
    public WindowLifecycle EffectiveLifecycle => Lifecycle ?? new();
    public WindowInputPolicy EffectiveInput => Input ?? new();
    public WindowNotificationPolicy EffectiveNotifications => Notifications ?? new();
    public WorkAreaReservation EffectiveWorkArea => EffectiveSurface.WorkArea ?? new();
    public string EffectiveTitle => string.IsNullOrWhiteSpace(EffectiveSurface.Title) ? $"Aether - {LogicalId}" : EffectiveSurface.Title!;
}

public sealed record WindowSnapshot(
    string Id,
    string Url,
    string RuntimeGroup,
    WindowKind Kind,
    Rectangle ConfiguredBounds,
    Rectangle CurrentBounds,
    int Monitor,
    bool Visible,
    bool Focused,
    bool Suspended,
    AppWindowState State,
    WindowSurfaceKind SurfaceKind = WindowSurfaceKind.TopLevel,
    WindowZOrder ZOrder = WindowZOrder.Normal);

public sealed record BridgePolicy(
    bool? Enabled,
    IReadOnlyList<string> BackendIds,
    IReadOnlyList<string> ControlRoutes,
    IReadOnlyList<string> RuntimeCommands)
{
    public static BridgePolicy Default { get; } =
        new(null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    public bool IsEnabled(string url) => Enabled ?? url.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
}
