using System.Runtime.InteropServices;

namespace Chronos;

internal enum WinGestureDecision
{
    Pass,
    Suppress,
    ReplayAndSuppress,
    Toggle
}

internal sealed class WinGestureTracker
{
    private bool _held;
    private bool _candidate;
    private bool _combined;
    private bool _replayed;
    private int _key;

    public WinGestureDecision Handle(int virtualKey, bool keyDown, bool keyUp, bool repeat)
    {
        bool isWin = virtualKey is NativeMethods.VK_LWIN or NativeMethods.VK_RWIN;
        if (!isWin && !_held)
            return WinGestureDecision.Pass;

        if (isWin && keyDown)
        {
            if (repeat || _held)
            {
                _combined = true;
                return _replayed ? WinGestureDecision.ReplayAndSuppress : WinGestureDecision.Suppress;
            }

            _held = true;
            _candidate = true;
            _combined = false;
            _replayed = false;
            _key = virtualKey;
            return WinGestureDecision.Suppress;
        }

        if (isWin && keyUp)
        {
            WinGestureDecision result;
            if (_candidate && !_combined)
                result = WinGestureDecision.Toggle;
            else if (_replayed)
                result = WinGestureDecision.ReplayAndSuppress;
            else
                result = WinGestureDecision.Pass;
            Reset();
            return result;
        }

        if (_held && (keyDown || keyUp))
        {
            _combined = true;
            if (!_replayed)
            {
                _replayed = true;
                return WinGestureDecision.ReplayAndSuppress;
            }
        }

        return WinGestureDecision.Pass;
    }

    public void MarkCombined()
    {
        if (_held)
        {
            _combined = true;
            _replayed = true;
        }
    }

    public void Reset()
    {
        _held = false;
        _candidate = false;
        _combined = false;
        _replayed = false;
        _key = 0;
    }

    public int HeldKey => _key;
}

internal sealed class WinMenuToggleController : IDisposable
{
    private readonly RendererCommandService _commands;
    private readonly Action<string> _diagnostic;
    private readonly NativeMethods.LowLevelKeyboardProc _callback;
    private readonly WinGestureTracker _tracker = new();
    private IntPtr _hook;
    private bool _disposed;
    private bool _enabled;

    public WinMenuToggleController(RendererCommandService commands, bool enabled, Action<string> diagnostic)
    {
        _commands = commands;
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
            _diagnostic("Windows Key Override hook could not be installed; normal input remains enabled.");
            StateChanged?.Invoke(false);
            return;
        }

        _enabled = true;
        StateChanged?.Invoke(true);
    }

    public void ToggleEnabled()
    {
        SetEnabled(!_enabled);
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
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || !_enabled || _disposed)
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);

        NativeMethods.KbdLlHookStruct data = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
        if ((data.Flags & NativeMethods.LLKHF_INJECTED) != 0)
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);

        bool keyDown = wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN;
        bool keyUp = wParam == (IntPtr)NativeMethods.WM_KEYUP || wParam == (IntPtr)NativeMethods.WM_SYSKEYUP;
        bool repeat = keyDown && (data.Flags & 0x80) != 0;
        WinGestureDecision decision = _tracker.Handle((int)data.VirtualKeyCode, keyDown, keyUp, repeat);
        if (keyDown && data.VirtualKeyCode is NativeMethods.VK_LWIN or NativeMethods.VK_RWIN && HasModifierHeld())
        {
            _tracker.MarkCombined();
            SendWinKey((int)data.VirtualKeyCode, false);
            return IntPtr.Zero;
        }
        if (decision == WinGestureDecision.Pass)
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        if (decision == WinGestureDecision.ReplayAndSuppress)
        {
            SendWinKey(_tracker.HeldKey, keyUp);
            return IntPtr.Zero;
        }
        if (decision == WinGestureDecision.Toggle)
        {
            TriggerToggle();
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private void TriggerToggle()
    {
        if (!_commands.IsConnected)
        {
            SendWinTap(NativeMethods.VK_LWIN);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                AetherCommandResult result = await _commands.ToggleMenuAsync().ConfigureAwait(false);
                if (!result.Success)
                    SendWinTap(NativeMethods.VK_LWIN);
            }
            catch (Exception ex)
            {
                _diagnostic($"Windows Key Override failed: {ex.Message}");
                SendWinTap(NativeMethods.VK_LWIN);
            }
        });
    }

    private static void SendWinTap(int virtualKey)
    {
        SendWinKey(virtualKey, false);
        SendWinKey(virtualKey, true);
    }

    private static bool HasModifierHeld() =>
        (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0 ||
        (NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0 ||
        (NativeMethods.GetAsyncKeyState(0x12) & 0x8000) != 0;

    private static void SendWinKey(int virtualKey, bool keyUp)
    {
        var input = new NativeMethods.Input
        {
            Type = NativeMethods.INPUT_KEYBOARD,
            KeyboardInput = new NativeMethods.KeyboardInput
            {
                VirtualKey = (ushort)virtualKey,
                Flags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0
            }
        };
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.Input>());
    }
}
