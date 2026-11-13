namespace UltraLightStartMenu;

public sealed class AutoHideMonitor : IDisposable
{
    private readonly Control _dispatcher;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _visible;
    private readonly Func<IntPtr> _windowHandle;
    private readonly Action _hide;
    private readonly WinEventApi.WinEventDelegate _eventProc;
    private IntPtr _hook;
    private bool _disposed;

    public AutoHideMonitor(
        Control dispatcher,
        Func<bool> enabled,
        Func<bool> visible,
        Func<IntPtr> windowHandle,
        Action hide)
    {
        _dispatcher = dispatcher;
        _enabled = enabled;
        _visible = visible;
        _windowHandle = windowHandle;
        _hide = hide;
        _eventProc = OnWinEvent;
    }

    public void Start()
    {
        if (_disposed || _hook != IntPtr.Zero)
            return;

        _hook = WinEventApi.SetWinEventHook(
            WinEventApi.EVENT_SYSTEM_FOREGROUND,
            WinEventApi.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _eventProc,
            0,
            0,
            WinEventApi.WINEVENT_OUTOFCONTEXT);
        if (_hook == IntPtr.Zero)
            Console.Error.WriteLine("[Window] Could not install foreground monitoring hook.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_hook != IntPtr.Zero)
        {
            WinEventApi.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed || eventType != WinEventApi.EVENT_SYSTEM_FOREGROUND ||
            !AutoHideRules.ShouldHide(_enabled(), _visible(), IsOwnedWindow(hwnd)))
            return;

        try
        {
            _dispatcher.BeginInvoke((Action)(() =>
            {
                if (!_disposed && AutoHideRules.ShouldHide(_enabled(), _visible(), IsOwnedWindow(hwnd)))
                    _hide();
            }));
        }
        catch (InvalidOperationException) { }
    }

    private bool IsOwnedWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        IntPtr window = _windowHandle();
        if (NativeWindowApi.IsDescendantOrSelf(hwnd, window))
            return true;

        NativeWindowApi.GetWindowThreadProcessId(hwnd, out uint processId);
        return processId == (uint)Environment.ProcessId;
    }
}
