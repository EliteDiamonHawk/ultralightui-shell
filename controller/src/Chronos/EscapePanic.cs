using System.Runtime.InteropServices;

namespace Chronos;

internal sealed class EscapePanicGestureTracker
{
    private readonly long _windowMilliseconds;
    private readonly int _triggerCount;
    private readonly Queue<long> _presses = new();

    public EscapePanicGestureTracker(TimeSpan window, int triggerCount = 10)
    {
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));
        if (triggerCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(triggerCount));

        _windowMilliseconds = checked((long)window.TotalMilliseconds);
        _triggerCount = triggerCount;
    }

    public bool RegisterPress(long timestampMilliseconds)
    {
        while (_presses.Count > 0 && timestampMilliseconds - _presses.Peek() > _windowMilliseconds)
            _presses.Dequeue();

        _presses.Enqueue(timestampMilliseconds);
        if (_presses.Count < _triggerCount)
            return false;

        _presses.Clear();
        return true;
    }

    public void Reset() => _presses.Clear();
}

internal sealed class EscapePanicController : IDisposable
{
    internal const int TriggerCount = 10;
    internal static readonly TimeSpan TriggerWindow = TimeSpan.FromSeconds(2);

    private readonly Action _trigger;
    private readonly Action<string> _diagnostic;
    private readonly NativeMethods.LowLevelKeyboardProc _callback;
    private readonly EscapePanicGestureTracker _tracker = new(TriggerWindow, TriggerCount);
    private IntPtr _hook;
    private bool _disposed;
    private bool _enabled;
    private int _triggered;

    public EscapePanicController(bool enabled, Action trigger, Action<string> diagnostic)
    {
        _trigger = trigger;
        _diagnostic = diagnostic;
        _callback = HookCallback;
        SetEnabled(enabled);
    }

    public event Action<bool>? StateChanged;
    public bool Enabled => _enabled;

    public void SetEnabled(bool enabled)
    {
        if (_disposed)
            return;
        if (_enabled == enabled && (enabled == false || _hook != IntPtr.Zero))
            return;

        if (!enabled)
        {
            _enabled = false;
            Interlocked.Exchange(ref _triggered, 0);
            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            _tracker.Reset();
            StateChanged?.Invoke(false);
            return;
        }

        IntPtr module = NativeMethods.GetModuleHandle(null);
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _callback, module, 0);
        if (_hook == IntPtr.Zero)
        {
            _enabled = false;
            _diagnostic("Escape Panic hook could not be installed; Escape remains normal input.");
            StateChanged?.Invoke(false);
            return;
        }

        _enabled = true;
        Interlocked.Exchange(ref _triggered, 0);
        StateChanged?.Invoke(true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_hook != IntPtr.Zero)
            NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _tracker.Reset();
        Interlocked.Exchange(ref _triggered, 1);
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _enabled && !_disposed)
        {
            NativeMethods.KbdLlHookStruct data = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
            bool keyDown = wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN;
            bool repeat = keyDown && (data.Flags & 0x80) != 0;
            if ((data.Flags & NativeMethods.LLKHF_INJECTED) == 0 &&
                data.VirtualKeyCode == NativeMethods.VK_ESCAPE && keyDown && !repeat &&
                _tracker.RegisterPress(Environment.TickCount64) &&
                Interlocked.Exchange(ref _triggered, 1) == 0)
            {
                try { _trigger(); }
                catch (Exception ex) { _diagnostic($"Escape Panic trigger dispatch failed: {ex.Message}"); }
            }
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }
}
