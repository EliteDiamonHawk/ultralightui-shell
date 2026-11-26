namespace Aether;

internal static class WallpaperPausePolicy
{
    public static bool ShouldPause(
        bool manualPauseEnabled,
        bool pauseWhenFullscreenEnabled,
        bool fullscreenOccluded) =>
        manualPauseEnabled || (pauseWhenFullscreenEnabled && fullscreenOccluded);
}

