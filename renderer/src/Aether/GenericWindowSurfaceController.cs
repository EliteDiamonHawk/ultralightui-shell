using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Aether;

internal sealed class GenericWindowSurfaceController
{
    private const uint BaseFlags = NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_NOSENDCHANGING;

    public void Reconcile(AppWindow window)
    {
        EnsureWindow(window);
        ApplyStyles(window);
        IntPtr insertAfter = ResolveInsertAfter(window);
        uint flags = BaseFlags | NativeMethods.SWP_NOACTIVATE;
        if (window.Spec.EffectiveSurface.ZOrder == WindowZOrder.Normal)
            flags |= NativeMethods.SWP_NOZORDER;
        if (window.IsVisible)
            flags |= NativeMethods.SWP_SHOWWINDOW;
        if (!NativeMethods.SetWindowPos(window.Hwnd, insertAfter, window.Spec.Bounds.X, window.Spec.Bounds.Y,
                window.Spec.Bounds.Width, window.Spec.Bounds.Height, flags))
        {
            throw new InvalidOperationException($"Could not position window {window.LogicalId}: {FormatError(Marshal.GetLastPInvokeError())}");
        }
        ApplyWorkArea(window);
    }

    public void Show(AppWindow window, bool focus)
    {
        EnsureWindow(window);
        window.IsVisible = true;
        Reconcile(window);
        NativeWindowApi.ShowWindow(window.Hwnd,
            focus || window.Spec.EffectiveInput.Focus == WindowFocusMode.OnShow ? NativeWindowApi.SW_SHOW : NativeWindowApi.SW_SHOWNOACTIVATE);
        if (focus || window.Spec.EffectiveInput.Focus == WindowFocusMode.OnShow)
            Focus(window);
    }

    public void Hide(AppWindow window)
    {
        EnsureWindow(window);
        NativeWindowApi.ShowWindow(window.Hwnd, NativeWindowApi.SW_HIDE);
        window.IsVisible = false;
        ApplyWorkArea(window);
    }

    public bool Focus(AppWindow window)
    {
        EnsureWindow(window);
        if (!NativeWindowApi.SetForegroundWindow(window.Hwnd))
            return false;
        window.Focused = true;
        return true;
    }

    public void Blur(AppWindow window)
    {
        if (window.Hwnd != IntPtr.Zero && NativeMethods.GetForegroundWindow() == window.Hwnd)
            NativeWindowApi.SetForegroundWindow(NativeMethods.GetShellWindow());
        window.Focused = false;
    }

    private static void ApplyStyles(AppWindow window)
    {
        long currentStyle = NativeMethods.GetWindowLongPtrW(window.Hwnd, NativeMethods.GWL_STYLE).ToInt64();
        long desiredStyle = currentStyle;
        if (window.Spec.EffectiveSurface.Framed)
        {
            desiredStyle &= ~NativeMethods.WS_POPUP;
            desiredStyle |= NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME | NativeMethods.WS_SYSMENU;
            if (window.Spec.EffectiveSurface.Resizable)
                desiredStyle |= NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX;
        }
        else
        {
            desiredStyle &= ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME |
                              NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX | NativeMethods.WS_SYSMENU);
            desiredStyle |= NativeMethods.WS_POPUP;
        }

        long currentExStyle = NativeMethods.GetWindowLongPtrW(window.Hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        long desiredExStyle = currentExStyle;
        if (window.Spec.EffectiveSurface.ShowInTaskbar)
        {
            desiredExStyle &= ~NativeMethods.WS_EX_TOOLWINDOW;
            desiredExStyle |= NativeMethods.WS_EX_APPWINDOW;
        }
        else
        {
            desiredExStyle |= NativeMethods.WS_EX_TOOLWINDOW;
            desiredExStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        }
        if (window.Spec.EffectiveInput.ClickThrough ||
            (window.Spec.EffectiveInput.Mouse == WindowInputMode.None &&
             window.Spec.EffectiveInput.Keyboard == WindowInputMode.None))
            desiredExStyle |= NativeMethods.WS_EX_NOACTIVATE;
        else
            desiredExStyle &= ~NativeMethods.WS_EX_NOACTIVATE;
        if (window.Spec.EffectiveInput.ClickThrough)
            desiredExStyle |= NativeMethods.WS_EX_TRANSPARENT;
        else
            desiredExStyle &= ~NativeMethods.WS_EX_TRANSPARENT;

        if (currentStyle != desiredStyle)
            NativeMethods.SetWindowLongPtrW(window.Hwnd, NativeMethods.GWL_STYLE, new IntPtr(desiredStyle));
        if (currentExStyle != desiredExStyle)
            NativeMethods.SetWindowLongPtrW(window.Hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(desiredExStyle));
        if (currentStyle != desiredStyle || currentExStyle != desiredExStyle)
            NativeMethods.SetWindowPos(window.Hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
    }

    private static IntPtr ResolveInsertAfter(AppWindow window) => window.Spec.EffectiveSurface.ZOrder switch
    {
        WindowZOrder.Bottom => NativeMethods.HWND_BOTTOM,
        WindowZOrder.Top or WindowZOrder.TopmostWhenFocused when !window.Focused => NativeMethods.HWND_TOP,
        WindowZOrder.Topmost or WindowZOrder.TopmostWhenFocused => NativeWindowApi.HWND_TOPMOST,
        _ => NativeMethods.HWND_TOP
    };

    private static void ApplyWorkArea(AppWindow window)
    {
        // Work-area reservation is represented in the normalized model. The
        // native AppBar registration is intentionally isolated here so it can
        // be added without coupling the runtime group to window policy parsing.
        if (window.IsVisible && window.Spec.EffectiveWorkArea.Enabled)
            NativeWindowApi.SetAppBarReservation(window.Hwnd, window.Spec.EffectiveWorkArea);
        else
            NativeWindowApi.RemoveAppBarReservation(window.Hwnd);
    }

    private static void EnsureWindow(AppWindow window)
    {
        if (!NativeWindowApi.IsWindow(window.Hwnd))
            throw new InvalidOperationException($"Window is no longer valid: {window.LogicalId}");
    }

    private static string FormatError(int error) =>
        error == 0 ? "no Win32 error" : $"Win32 error {error}: {new Win32Exception(error).Message}";
}
