using System.Windows.Forms;

namespace UltraLightStartMenu;

public sealed class WindowTopologyMonitor
{
    private string _signature = GetSignature();

    public bool HasChanged()
    {
        string current = GetSignature();
        if (current == _signature)
            return false;

        _signature = current;
        return true;
    }

    private static string GetSignature() =>
        string.Join("|", Screen.AllScreens.Select(screen => $"{screen.DeviceName}:{screen.Bounds}"));
}
