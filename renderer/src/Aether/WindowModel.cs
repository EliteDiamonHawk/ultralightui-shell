using System.Drawing;
using System.Text.Json;

namespace Aether;

// WindowKind identifies the compatibility adapter which produced a window. The
// runtime never uses it to select native behavior; placement and input policy
// do that instead.
public enum WindowKind
{
    Generic,
    Wallpaper,
    Widget
}

public enum WindowPlacement
{
    Desktop,
    TopLevel
}

public enum WindowZOrder
{
    Bottom,
    Normal,
    Top,
    Topmost,
    TopmostWhenFocused
}

public enum WindowInitialVisibility
{
    Shown,
    Hidden
}

public enum WindowInitialRuntime
{
    Active,
    Paused,
    Unloaded
}

public sealed record WindowInitialConfiguration(
    WindowInitialVisibility Visibility = WindowInitialVisibility.Shown,
    WindowInitialRuntime Runtime = WindowInitialRuntime.Active)
{
    public static WindowInitialConfiguration Default { get; } = new();
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

public sealed record RendererWindowSpec(
    string LogicalId,
    string Url,
    Rectangle Bounds,
    Rectangle VirtualBounds,
    string ThreadGroup,
    WindowKind Kind,
    bool Transparent = true,
    int MonitorNumber = 0,
    WindowInitialConfiguration? Initial = null,
    BridgePolicy? Bridge = null,
    IReadOnlyDictionary<string, JsonElement>? Variables = null,
    IReadOnlyList<string>? Cues = null,
    WindowPlacement Placement = WindowPlacement.TopLevel,
    WindowZOrder ZOrder = WindowZOrder.Normal,
    bool Framed = false,
    bool Resizable = false,
    bool ShowInTaskbar = false,
    bool ActivateOnShow = false,
    bool PauseOnFullscreen = true,
    string? Title = null,
    WindowInputPolicy? Input = null,
    WindowNotificationPolicy? Notifications = null,
    WorkAreaReservation? WorkArea = null)
{
    public WindowInitialConfiguration InitialConfiguration => Initial ?? WindowInitialConfiguration.Default;
    public WindowInputPolicy EffectiveInput => Input ?? new();
    public WindowNotificationPolicy EffectiveNotifications => Notifications ?? new();
    public WorkAreaReservation EffectiveWorkArea => WorkArea ?? new();
    public string EffectiveTitle => string.IsNullOrWhiteSpace(Title) ? $"Aether - {LogicalId}" : Title!;
}

public sealed record WindowSnapshot(
    string Id,
    string Url,
    string Group,
    WindowKind Kind,
    Rectangle ConfiguredBounds,
    Rectangle CurrentBounds,
    int Monitor,
    bool Visible,
    bool Focused,
    bool Paused,
    bool Loaded,
    AppWindowState State,
    WindowPlacement Placement = WindowPlacement.TopLevel,
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
