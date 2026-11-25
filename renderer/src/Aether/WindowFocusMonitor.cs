namespace Aether;

internal sealed class WindowFocusMonitor : IDisposable
{
    private readonly Action<IntPtr> _changed;
    private readonly NativeMethods.WinEventDelegate _callback;
    private IntPtr _hook;
    private bool _disposed;

    public WindowFocusMonitor(Action<IntPtr> changed)
    {
        _changed = changed;
        _callback = OnWinEvent;
    }

    public void Start()
    {
        if (_disposed || _hook != IntPtr.Zero)
            return;
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _callback,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
        if (_hook == IntPtr.Zero)
            Console.Error.WriteLine("[Focus] Could not install foreground monitoring hook.");
        _changed(NativeMethods.GetForegroundWindow());
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
            _changed(hwnd);
    }
}
