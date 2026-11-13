using System.Runtime.InteropServices;

namespace UltraLightStartMenu;

public sealed class StartMenuWindowController
{
    private const uint BasePlacementFlags = NativeMethods.SWP_NOACTIVATE |
                                             NativeMethods.SWP_NOSENDCHANGING |
                                             NativeMethods.SWP_NOOWNERZORDER;

    public void Reconcile(UltralightWindow window)
    {
        EnsureWindow(window);
        Place(window, window.IsVisible);
        if (!window.IsVisible)
            NativeMethods.ShowWindow(window.Hwnd, NativeMethods.SW_HIDE);
    }

    public void Show(UltralightWindow window)
    {
        EnsureWindow(window);
        Place(window, true);
        if (NativeMethods.ShowWindow(window.Hwnd, NativeMethods.SW_SHOWNOACTIVATE) == 0)
            NativeMethods.ShowWindow(window.Hwnd, NativeMethods.SW_SHOW);
        NativeMethods.SetForegroundWindow(window.Hwnd);
        window.IsVisible = true;
    }

    public void Hide(UltralightWindow window)
    {
        EnsureWindow(window);
        NativeMethods.ShowWindow(window.Hwnd, NativeMethods.SW_HIDE);
        window.IsVisible = false;
    }

    internal static uint GetPlacementFlags(bool show) =>
        BasePlacementFlags | (show ? NativeMethods.SWP_SHOWWINDOW : 0);

    private static void Place(UltralightWindow window, bool show)
    {
        Rectangle bounds = window.Spec.Bounds;
        if (!NativeMethods.SetWindowPos(
                window.Hwnd,
                NativeMethods.HWND_TOPMOST,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                GetPlacementFlags(show)))
        {
            throw new InvalidOperationException(
                $"Could not position start-menu window: {FormatWin32Error(Marshal.GetLastPInvokeError())}");
        }
        window.IsTopmost = true;
    }

    public bool IsTopmost(UltralightWindow window) => window.IsTopmost;

    private static void EnsureWindow(UltralightWindow window)
    {
        if (!NativeMethods.IsWindow(window.Hwnd))
            throw new InvalidOperationException($"Start-menu window is no longer valid: {window.LogicalId}");
    }

    private static string FormatWin32Error(int error) =>
        error == 0 ? "no Win32 error" : $"Win32 error {error}: {new System.ComponentModel.Win32Exception(error).Message}";
}
