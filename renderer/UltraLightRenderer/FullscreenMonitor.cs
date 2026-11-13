namespace UltraLightRenderer;

public sealed class FullscreenMonitor : IDisposable
{
    private readonly NativeMethods.WinEventDelegate _eventProc;
    private readonly Action<IntPtr> _changed;
    private IntPtr _hook;
    private bool _disposed;

    public FullscreenMonitor(Action<IntPtr> changed)
    {
        _changed = changed;
        _eventProc = OnWinEvent;
    }

    public void Start()
    {
        if (_disposed || _hook != IntPtr.Zero)
            return;
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _eventProc,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
        if (_hook == IntPtr.Zero)
            Console.Error.WriteLine("[Fullscreen] Could not install foreground monitoring hook.");
        Reevaluate();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (!_disposed && eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND)
            Reevaluate();
    }

    private void Reevaluate()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        IntPtr monitor = IntPtr.Zero;
        if (foreground != IntPtr.Zero && NativeMethods.IsWindow(foreground) && NativeMethods.IsWindowVisible(foreground) &&
            !NativeMethods.IsIconic(foreground) && foreground != NativeMethods.GetShellWindow() &&
            NativeMethods.GetWindowThreadProcessId(foreground, out uint processId) != 0 &&
            processId != (uint)Environment.ProcessId &&
            NativeMethods.TryGetWindowRect(foreground, out System.Drawing.Rectangle bounds))
        {
            IntPtr candidate = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (candidate != IntPtr.Zero)
            {
                var info = new NativeMethods.MONITORINFO { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (NativeMethods.GetMonitorInfoW(candidate, ref info))
                {
                    System.Drawing.Rectangle monitorBounds = System.Drawing.Rectangle.FromLTRB(
                        info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
                    if (bounds.Left <= monitorBounds.Left && bounds.Top <= monitorBounds.Top &&
                        bounds.Right >= monitorBounds.Right && bounds.Bottom >= monitorBounds.Bottom)
                        monitor = candidate;
                }
            }
        }
        _changed(monitor);
    }
}
