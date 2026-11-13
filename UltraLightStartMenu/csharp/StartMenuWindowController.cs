using System.Runtime.InteropServices;

namespace UltraLightStartMenu;

public interface IStartMenuWindowPlacement
{
    void Reconcile(UltralightWindow window);
    void Show(UltralightWindow window);
    void Hide(UltralightWindow window);
}

public sealed class StartMenuWindowController : IStartMenuWindowPlacement
{
    private readonly bool _verbose;
    private const uint BasePlacementFlags = NativeWindowApi.SWP_NOACTIVATE |
                                             NativeWindowApi.SWP_NOSENDCHANGING |
                                             NativeWindowApi.SWP_NOOWNERZORDER;

    public StartMenuWindowController(bool verbose = false) => _verbose = verbose;

    public void Reconcile(UltralightWindow window)
    {
        EnsureWindow(window);
        Place(window, window.IsVisible);
        if (!window.IsVisible)
            NativeWindowApi.ShowWindow(window.Hwnd, NativeWindowApi.SW_HIDE);
    }

    public void Show(UltralightWindow window)
    {
        EnsureWindow(window);
        Place(window, true);
        if (NativeWindowApi.ShowWindow(window.Hwnd, NativeWindowApi.SW_SHOWNOACTIVATE) == 0)
            NativeWindowApi.ShowWindow(window.Hwnd, NativeWindowApi.SW_SHOW);
        NativeWindowApi.SetForegroundWindow(window.Hwnd);
        window.IsVisible = true;
    }

    public void Hide(UltralightWindow window)
    {
        EnsureWindow(window);
        NativeWindowApi.ShowWindow(window.Hwnd, NativeWindowApi.SW_HIDE);
        window.IsVisible = false;
    }

    internal static uint GetPlacementFlags(bool show) =>
        BasePlacementFlags | (show ? NativeWindowApi.SWP_SHOWWINDOW : 0);

    private void Place(UltralightWindow window, bool show)
    {
        Rectangle bounds = window.Spec.Bounds;
        if (!NativeWindowApi.SetWindowPos(
                window.Hwnd,
                NativeWindowApi.HWND_TOPMOST,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                GetPlacementFlags(show)))
        {
            throw new InvalidOperationException(
                $"Could not position start-menu window: {FormatWin32Error(Marshal.GetLastPInvokeError())}");
        }
        window.TopmostPlacementApplied = true;
        if (_verbose)
            Console.WriteLine($"[Window] placed {window.LogicalId} at {bounds}; visible={show}; topmost=true");
    }

    private static void EnsureWindow(UltralightWindow window)
    {
        if (!NativeWindowApi.IsWindow(window.Hwnd))
            throw new InvalidOperationException($"Start-menu window is no longer valid: {window.LogicalId}");
    }

    private static string FormatWin32Error(int error) =>
        error == 0 ? "no Win32 error" : $"Win32 error {error}: {new System.ComponentModel.Win32Exception(error).Message}";
}
