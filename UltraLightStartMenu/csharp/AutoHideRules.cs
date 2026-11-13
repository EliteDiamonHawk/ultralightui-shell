namespace UltraLightStartMenu;

internal static class AutoHideRules
{
    internal static bool ShouldHide(bool enabled, bool visible, bool ownedWindow) =>
        enabled && visible && !ownedWindow;
}
