using System.Runtime.InteropServices;
using System.Text;

namespace UltraLightRenderer;

public sealed class WallpaperWindow
{
    public WallpaperWindow(AppWindow appWindow) => AppWindow = appWindow;
    public AppWindow AppWindow { get; }
    public Rectangle Bounds => AppWindow.Spec.Bounds;
}

public sealed class InputRouter : IDisposable
{
    private readonly Func<IReadOnlyList<WallpaperWindow>> _windows;
    private readonly NativeMethods.LowLevelMouseProc _mouseProc;
    private readonly NativeMethods.LowLevelKeyboardProc _keyboardProc;
    private readonly NativeMethods.WinEventDelegate _foregroundProc;
    private readonly KeyboardOwnershipPolicy _keyboardOwnership = new();
    private readonly Dictionary<int, ForwardedUltralightKey> _forwardedKeys = new();
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private IntPtr _foregroundHook;
    private WallpaperWindow? _keyboardCandidate;
    private WallpaperWindow? _keyboardOwner;
    private bool _interactingWithDesktop;

    private readonly record struct ForwardedUltralightKey(int NativeKeyCode);

    public InputRouter(Func<IReadOnlyList<WallpaperWindow>> windows)
    {
        _windows = windows;
        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;
        _foregroundProc = ForegroundWindowCallback;

        IntPtr module = NativeMethods.GetModuleHandleW(null);
        _mouseHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);
        _keyboardHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _foregroundProc,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);

        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero || _foregroundHook == IntPtr.Zero)
            Console.Error.WriteLine("[Input] Could not install one or more global input hooks.");
    }

    public void Dispose()
    {
        EndBackgroundKeyboardInteraction("router disposed");

        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        if (_foregroundHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
    }

    internal void EndForWindow(AppWindow appWindow, string reason)
    {
        if (_keyboardOwner?.AppWindow == appWindow || _keyboardCandidate?.AppWindow == appWindow)
            EndBackgroundKeyboardInteraction(reason);
    }

    internal void EndForWindows(IEnumerable<WallpaperWindow> windows, string reason)
    {
        var targets = windows.Select(window => window.AppWindow).ToHashSet();
        if ((_keyboardOwner != null && targets.Contains(_keyboardOwner.AppWindow)) ||
            (_keyboardCandidate != null && targets.Contains(_keyboardCandidate.AppWindow)))
            EndBackgroundKeyboardInteraction(reason);
    }

    internal void EndAll(string reason) => EndBackgroundKeyboardInteraction(reason);

    private void ForegroundWindowCallback(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND && hwnd != IntPtr.Zero)
            OnForegroundWindowChanged(hwnd);
    }

    private void OnForegroundWindowChanged(IntPtr hwnd)
    {
        WallpaperWindow? target = _keyboardOwner ?? _keyboardCandidate;
        if (target == null)
            return;

        if (_keyboardOwnership.ShouldEndForForegroundChange(IsForegroundAllowed(hwnd, target)))
            EndBackgroundKeyboardInteraction(_keyboardOwner != null ? "foreground changed" : "foreground changed before keyboard ownership");
    }

    private IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
            return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);

        int message = (int)wParam;
        NativeMethods.MSLLHOOKSTRUCT info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
        NativeMethods.POINT point = info.pt;

        if (_interactingWithDesktop)
        {
            if (message is NativeMethods.WM_LBUTTONUP or NativeMethods.WM_RBUTTONUP)
                _interactingWithDesktop = false;
            return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        IntPtr underPointer = NativeMethods.WindowFromPoint(point);
        bool desktopSurface = IsDesktopSurface(underPointer);
        WallpaperWindow? target = FindWallpaperWindow(point);

        if (desktopSurface)
        {
            if (message is NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP)
            {
                if (message == NativeMethods.WM_RBUTTONDOWN)
                    EndBackgroundKeyboardInteraction("desktop right-click");
                return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
            }

            if (message is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONDBLCLK)
            {
                if (IsDesktopIcon(underPointer, point))
                {
                    EndBackgroundKeyboardInteraction("desktop icon interaction");
                    _interactingWithDesktop = true;
                    return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
                }

                NativeMethods.TryClearDesktopSelection(underPointer);
                CloseDesktopMenus();
            }

            if (message == NativeMethods.WM_LBUTTONDOWN)
                SetKeyboardCandidate(target);
            else if (message == NativeMethods.WM_MBUTTONDOWN)
                EndBackgroundKeyboardInteraction("background middle-click");

            if (target != null && message != NativeMethods.WM_RBUTTONDOWN && message != NativeMethods.WM_RBUTTONUP)
            {
                ForwardMouse(target, message, point, info.mouseData);
                return message == NativeMethods.WM_MOUSEMOVE
                    ? NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam)
                    : (IntPtr)1;
            }
        }
        else if (target != null && NativeMethods.IsDescendantOrSelf(underPointer, target.AppWindow.Hwnd))
        {
            if (message == NativeMethods.WM_LBUTTONDOWN)
                SetKeyboardCandidate(target);
            else if (message is NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN)
                EndBackgroundKeyboardInteraction("background non-left click");

            if (message != NativeMethods.WM_RBUTTONDOWN && message != NativeMethods.WM_RBUTTONUP)
            {
                ForwardMouse(target, message, point, info.mouseData);
                return message == NativeMethods.WM_MOUSEMOVE
                    ? NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam)
                    : (IntPtr)1;
            }
        }
        else if (message is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN)
        {
            EndBackgroundKeyboardInteraction("outside click");
        }

        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void SetKeyboardCandidate(WallpaperWindow? target)
    {
        if (target == null)
        {
            EndBackgroundKeyboardInteraction("background click without target");
            return;
        }

        if (_keyboardOwner == target && _keyboardOwnership.ShouldForward)
            _keyboardCandidate = target;
        else if (_keyboardOwner != null)
            EndBackgroundKeyboardInteraction("active background renderer changed");

        _keyboardCandidate = target;
        if (!_keyboardOwnership.ShouldForward)
            _keyboardOwnership.Arm();

        if (target.AppWindow.UlView != IntPtr.Zero)
        {
            try { target.AppWindow.InvokeOnOwner(() => UltralightNative.ulViewFocus(target.AppWindow.UlView)); }
            catch (Exception ex) { Console.Error.WriteLine($"[Input] Could not focus background renderer: {ex.Message}"); }
        }
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);

        int message = (int)wParam;
        if (message is not (NativeMethods.WM_KEYDOWN or NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYDOWN or NativeMethods.WM_SYSKEYUP))
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);

        NativeMethods.KBDLLHOOKSTRUCT info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        int key = unchecked((int)info.vkCode);
        bool keyDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        bool altHeld = IsKeyDown(NativeMethods.VK_MENU) || key == NativeMethods.VK_MENU;
        bool winHeld = IsKeyDown(NativeMethods.VK_LWIN) || IsKeyDown(NativeMethods.VK_RWIN) || key is NativeMethods.VK_LWIN or NativeMethods.VK_RWIN;
        bool ctrlHeld = IsKeyDown(NativeMethods.VK_CONTROL) || key == NativeMethods.VK_CONTROL;

        if (KeyboardInputPolicy.IsSystemOwnedKey(message, key, altHeld, winHeld, ctrlHeld))
        {
            if (KeyboardInputPolicy.EndsOwnership(message, key, altHeld, winHeld, ctrlHeld))
                EndBackgroundKeyboardInteraction(GetSystemKeyReason(key, altHeld));
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        if (!TryEnsureKeyboardOwner())
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);

        WallpaperWindow owner = _keyboardOwner!;
        if (!IsForegroundAllowed(NativeMethods.GetForegroundWindow(), owner))
        {
            EndBackgroundKeyboardInteraction("foreground validation failed");
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        try
        {
            int nativeKeyCode = unchecked((int)info.scanCode);
            bool autoRepeat = keyDown && _forwardedKeys.ContainsKey(key);
            if (keyDown) _forwardedKeys[key] = new ForwardedUltralightKey(nativeKeyCode);
            else _forwardedKeys.Remove(key);

            uint modifiers = GetModifiers(key, keyDown);
            owner.AppWindow.InvokeOnOwner(() => FireKeyEvent(owner.AppWindow.UlView,
                keyDown ? UltralightNative.KeyEventTypeRawKeyDown : UltralightNative.KeyEventTypeKeyUp,
                modifiers, key, nativeKeyCode, IntPtr.Zero, IntPtr.Zero, autoRepeat));
            if (keyDown)
                owner.AppWindow.InvokeOnOwner(() => FireCharacterEvent(owner.AppWindow.UlView, key, nativeKeyCode, modifiers, info));
            return (IntPtr)1;
        }
        catch (Exception ex)
        {
            EndBackgroundKeyboardInteraction($"keyboard forwarding failed: {ex.Message}");
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }
    }

    private bool TryEnsureKeyboardOwner()
    {
        if (_keyboardOwner != null && IsUsableRendererTarget(_keyboardOwner) && IsForegroundAllowed(NativeMethods.GetForegroundWindow(), _keyboardOwner))
            return true;

        if (_keyboardOwner != null)
            EndBackgroundKeyboardInteraction("renderer input focus lost");

        WallpaperWindow? candidate = _keyboardCandidate;
        if (candidate == null || !IsUsableRendererTarget(candidate) || !IsForegroundAllowed(NativeMethods.GetForegroundWindow(), candidate))
            return false;

        if (!_keyboardOwnership.TryBegin(
                candidate.AppWindow.InvokeOnOwner(() => UltralightNative.ulViewHasInputFocus(candidate.AppWindow.UlView)),
                IsForegroundAllowed(NativeMethods.GetForegroundWindow(), candidate),
                KeyboardFocusKind.RendererFallback))
            return false;

        _keyboardOwner = candidate;
        candidate.AppWindow.InvokeOnOwner(() => UltralightNative.ulViewFocus(candidate.AppWindow.UlView));
        Console.WriteLine($"[Input] Background keyboard interaction started: {candidate.AppWindow.LogicalId}");
        return true;
    }

    private bool IsUsableRendererTarget(WallpaperWindow window)
    {
        if (!_windows().Any(current => ReferenceEquals(current.AppWindow, window.AppWindow)) ||
            window.AppWindow.State != AppWindowState.Embedded ||
            window.AppWindow.RendererSuspended || !NativeMethods.IsWindow(window.AppWindow.Hwnd) ||
            window.AppWindow.UlView == IntPtr.Zero)
        {
            return false;
        }

        return window.AppWindow.InvokeOnOwner(() => UltralightNative.ulViewHasInputFocus(window.AppWindow.UlView));
    }

    private static bool IsForegroundAllowed(IntPtr foreground, WallpaperWindow window)
    {
        if (foreground == IntPtr.Zero)
            return false;
        if (foreground == window.AppWindow.Hwnd || NativeMethods.IsDescendantOrSelf(foreground, window.AppWindow.Hwnd))
            return true;
        if (foreground == NativeMethods.GetShellWindow())
            return true;

        IntPtr root = NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOT);
        var className = new StringBuilder(256);
        NativeMethods.GetClassNameW(root == IntPtr.Zero ? foreground : root, className, className.Capacity);
        return className.ToString() is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32";
    }

    private void EndBackgroundKeyboardInteraction(string reason)
    {
        WallpaperWindow? focusTarget = _keyboardOwner ?? _keyboardCandidate;
        if (focusTarget != null && focusTarget.AppWindow.UlView != IntPtr.Zero)
        {
            foreach ((int key, ForwardedUltralightKey forwarded) in _forwardedKeys.ToArray())
            {
                try
                {
                    focusTarget.AppWindow.InvokeOnOwner(() => FireKeyEvent(focusTarget.AppWindow.UlView,
                        UltralightNative.KeyEventTypeKeyUp, GetModifiers(key, false), key,
                        forwarded.NativeKeyCode, IntPtr.Zero, IntPtr.Zero, false));
                }
                catch (Exception ex) { Console.Error.WriteLine($"[Input] Could not release key {key}: {ex.Message}"); }
            }

            try { focusTarget.AppWindow.InvokeOnOwner(() => UltralightNative.ulViewUnfocus(focusTarget.AppWindow.UlView)); }
            catch (Exception ex) { Console.Error.WriteLine($"[Input] Could not unfocus background renderer: {ex.Message}"); }
        }

        bool hadState = _keyboardOwnership.ShouldForward || _keyboardCandidate != null;
        _keyboardOwnership.End();
        _keyboardCandidate = null;
        _keyboardOwner = null;
        _forwardedKeys.Clear();
        if (hadState)
            Console.WriteLine($"[Input] Background keyboard interaction ended: {reason}");
    }

    private static string GetSystemKeyReason(int key, bool altHeld) =>
        key is NativeMethods.VK_LWIN or NativeMethods.VK_RWIN ? "Win key" :
        altHeld && key == NativeMethods.VK_TAB ? "Alt+Tab" : altHeld ? "system Alt shortcut" :
        key == NativeMethods.VK_ESCAPE ? "Escape" : "system key";

    private static bool IsKeyDown(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;

    private static uint GetModifiers(int key, bool keyDown)
    {
        bool shift = IsKeyDown(NativeMethods.VK_SHIFT) || key is NativeMethods.VK_SHIFT or 0xA0 or 0xA1;
        bool ctrl = IsKeyDown(NativeMethods.VK_CONTROL) || key is NativeMethods.VK_CONTROL or 0xA2 or 0xA3;
        bool alt = IsKeyDown(NativeMethods.VK_MENU) || key is NativeMethods.VK_MENU or 0xA4 or 0xA5;
        bool meta = IsKeyDown(NativeMethods.VK_LWIN) || IsKeyDown(NativeMethods.VK_RWIN) || key is NativeMethods.VK_LWIN or NativeMethods.VK_RWIN;
        if (!keyDown)
        {
            if (key is 0x10 or 0xA0 or 0xA1) shift = false;
            if (key is 0x11 or 0xA2 or 0xA3) ctrl = false;
            if (key is 0x12 or 0xA4 or 0xA5) alt = false;
            if (key is NativeMethods.VK_LWIN or NativeMethods.VK_RWIN) meta = false;
        }

        uint modifiers = 0;
        if (alt) modifiers |= UltralightNative.KeyEventModifierAlt;
        if (ctrl) modifiers |= UltralightNative.KeyEventModifierCtrl;
        if (meta) modifiers |= UltralightNative.KeyEventModifierMeta;
        if (shift) modifiers |= UltralightNative.KeyEventModifierShift;
        return modifiers;
    }

    private static void FireKeyEvent(IntPtr view, int type, uint modifiers, int key, int nativeKeyCode, IntPtr text, IntPtr unmodifiedText, bool autoRepeat)
    {
        IntPtr ownedText = text;
        IntPtr ownedUnmodifiedText = unmodifiedText;
        try
        {
            if (ownedText == IntPtr.Zero) ownedText = UltralightNative.ulCreateString(string.Empty);
            if (ownedUnmodifiedText == IntPtr.Zero) ownedUnmodifiedText = UltralightNative.ulCreateString(string.Empty);
            IntPtr keyEvent = UltralightNative.ulCreateKeyEvent(type, modifiers, key, nativeKeyCode,
                ownedText, ownedUnmodifiedText, IsKeypadKey(key), autoRepeat, false);
            if (keyEvent == IntPtr.Zero)
                throw new InvalidOperationException("Ultralight could not create a key event.");
            try { UltralightNative.ulViewFireKeyEvent(view, keyEvent); }
            finally { UltralightNative.ulDestroyKeyEvent(keyEvent); }
        }
        finally
        {
            if (text == IntPtr.Zero && ownedText != IntPtr.Zero) UltralightNative.ulDestroyString(ownedText);
            if (unmodifiedText == IntPtr.Zero && ownedUnmodifiedText != IntPtr.Zero) UltralightNative.ulDestroyString(ownedUnmodifiedText);
        }
    }

    private static void FireCharacterEvent(IntPtr view, int key, int nativeKeyCode, uint modifiers, NativeMethods.KBDLLHOOKSTRUCT info)
    {
        string text = TranslateKey(info, key);
        if (text.Length == 0)
            return;
        IntPtr textHandle = UltralightNative.ulCreateString(text);
        IntPtr unmodifiedHandle = UltralightNative.ulCreateString(text);
        try { FireKeyEvent(view, UltralightNative.KeyEventTypeChar, modifiers, key, nativeKeyCode, textHandle, unmodifiedHandle, false); }
        finally
        {
            UltralightNative.ulDestroyString(textHandle);
            UltralightNative.ulDestroyString(unmodifiedHandle);
        }
    }

    private static string TranslateKey(NativeMethods.KBDLLHOOKSTRUCT info, int key)
    {
        byte[] state = new byte[256];
        for (int virtualKey = 0; virtualKey < state.Length; virtualKey++)
        {
            if (IsKeyDown(virtualKey)) state[virtualKey] |= 0x80;
            if ((NativeMethods.GetKeyState(virtualKey) & 0x0001) != 0) state[virtualKey] |= 0x01;
        }
        state[key] |= 0x80;
        uint scanCode = info.scanCode;
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(foreground, out uint threadId);
        IntPtr layout = NativeMethods.GetKeyboardLayout(threadId);
        if (scanCode == 0) scanCode = NativeMethods.MapVirtualKeyExW((uint)key, 0, layout);
        var buffer = new StringBuilder(8);
        int result = NativeMethods.ToUnicodeEx((uint)key, scanCode, state, buffer, buffer.Capacity, 0, layout);
        return result > 0 ? buffer.ToString() : string.Empty;
    }

    private static bool IsKeypadKey(int key) => key is >= 0x60 and <= 0x69 or 0x6A or 0x6D or 0x6E or 0x6F;

    private void ForwardMouse(WallpaperWindow window, int message, NativeMethods.POINT screenPoint, uint mouseData)
    {
        IntPtr target = ResolveInputWindow(window, screenPoint);
        if (target == IntPtr.Zero)
            return;
        NativeMethods.POINT clientPoint = screenPoint;
        NativeMethods.ScreenToClient(target, ref clientPoint);
        IntPtr lParam = new((clientPoint.Y << 16) | (clientPoint.X & 0xFFFF));
        int keyState = GetMouseKeyState(message);
        IntPtr wParam = message is NativeMethods.WM_MOUSEWHEEL or NativeMethods.WM_MOUSEHWHEEL
            ? new IntPtr((unchecked((short)(mouseData >> 16)) << 16) | keyState) : new IntPtr(keyState);
        NativeMethods.PostMessageW(target, message, wParam, lParam);
    }

    private WallpaperWindow? FindWallpaperWindow(NativeMethods.POINT point) =>
        _windows().Where(window => NativeMethods.IsWindow(window.AppWindow.Hwnd) && window.Bounds.Contains(point.X, point.Y))
            .OrderBy(window => window.Bounds.Width * (long)window.Bounds.Height).FirstOrDefault();

    private static IntPtr ResolveInputWindow(WallpaperWindow window, NativeMethods.POINT screenPoint)
    {
        IntPtr candidate = NativeMethods.WindowFromPoint(screenPoint);
        return candidate != IntPtr.Zero && NativeMethods.IsDescendantOrSelf(candidate, window.AppWindow.Hwnd) ? candidate : window.AppWindow.Hwnd;
    }

    private static int GetMouseKeyState(int message)
    {
        int state = 0;
        if ((NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0) state |= 0x0001;
        if ((NativeMethods.GetAsyncKeyState(0x02) & 0x8000) != 0) state |= 0x0002;
        if ((NativeMethods.GetAsyncKeyState(0x04) & 0x8000) != 0) state |= 0x0010;
        if ((NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0) state |= 0x0004;
        if ((NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0) state |= 0x0008;
        if (message is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP or NativeMethods.WM_LBUTTONDBLCLK) state |= 0x0001;
        if (message is NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP) state |= 0x0002;
        if (message is NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP) state |= 0x0010;
        return state;
    }

    private static bool IsDesktopSurface(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;
        IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        var className = new StringBuilder(256);
        NativeMethods.GetClassNameW(root, className, className.Capacity);
        return className.ToString() is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32";
    }

    private static bool IsDesktopIcon(IntPtr hwnd, NativeMethods.POINT point)
    {
        var className = new StringBuilder(256);
        NativeMethods.GetClassNameW(hwnd, className, className.Capacity);
        return className.ToString() == "SysListView32" && NativeMethods.IsDesktopIconAt(hwnd, point);
    }

    private static void CloseDesktopMenus()
    {
        NativeMethods.PostMessageW(NativeMethods.GetShellWindow(), NativeMethods.WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
        IntPtr menu = NativeMethods.FindWindowW("#32768", null);
        if (menu != IntPtr.Zero && NativeMethods.IsWindowVisible(menu))
        {
            NativeMethods.PostMessageW(menu, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            NativeMethods.PostMessageW(menu, NativeMethods.WM_KEYDOWN, new IntPtr(NativeMethods.VK_ESCAPE), IntPtr.Zero);
        }
    }
}

