using System.Drawing;
using System.Windows.Forms;

namespace UltraLightStartMenu;

internal static class MonitorResolution
{
    internal static Rectangle GetWindowsBounds(int monitorNumber)
    {
        Screen[] screens = Screen.AllScreens;
        if (monitorNumber <= 0 || monitorNumber > screens.Length)
            throw new StartMenuConfigurationException(
                $"Start menu selects monitor {monitorNumber}, but only {screens.Length} monitor(s) are available.");

        return screens[monitorNumber - 1].Bounds;
    }

    internal static IntPtr GetUltralightMonitor(IntPtr app, int monitorNumber)
    {
        uint monitorCount = UltralightNative.ulAppGetMonitorCount(app);
        if (monitorNumber <= 0 || (uint)monitorNumber > monitorCount)
            throw new InvalidOperationException(
                $"Start menu selects Ultralight monitor {monitorNumber}, but runtime exposes {monitorCount} monitor(s).");

        IntPtr monitor = UltralightNative.ulAppGetMonitor(app, (uint)(monitorNumber - 1));
        if (monitor == IntPtr.Zero)
            throw new InvalidOperationException($"Ultralight monitor {monitorNumber} could not be resolved.");

        return monitor;
    }
}
