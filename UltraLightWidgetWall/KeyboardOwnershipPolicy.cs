namespace UltraLightWidgetWall;

internal enum KeyboardOwnershipState
{
    NoBackgroundKeyboardInput,
    PendingBackgroundKeyboardInteraction,
    BackgroundKeyboardInteraction
}

internal enum KeyboardFocusKind
{
    None,
    RendererFallback
}

internal static class KeyboardInputPolicy
{
    internal static bool IsSystemOwnedKey(int message, int key, bool altHeld, bool winHeld, bool ctrlHeld)
    {
        if (message is NativeMethods.WM_SYSKEYDOWN or NativeMethods.WM_SYSKEYUP)
            return true;

        if (key is NativeMethods.VK_LWIN or NativeMethods.VK_RWIN or
            NativeMethods.VK_MENU or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU or
            NativeMethods.VK_ESCAPE or NativeMethods.VK_APPS or NativeMethods.VK_SNAPSHOT or
            NativeMethods.VK_PAUSE)
            return true;

        if (key is >= 0x70 and <= 0x87)
            return true;

        if (key is NativeMethods.VK_CAPITAL or NativeMethods.VK_NUMLOCK or NativeMethods.VK_SCROLL)
            return true;

        return altHeld || winHeld ||
            (key == NativeMethods.VK_TAB && altHeld) ||
            (key == NativeMethods.VK_ESCAPE && ctrlHeld);
    }

    internal static bool EndsOwnership(int message, int key, bool altHeld, bool winHeld, bool ctrlHeld) =>
        key is not (NativeMethods.VK_CAPITAL or NativeMethods.VK_NUMLOCK or NativeMethods.VK_SCROLL) &&
        IsSystemOwnedKey(message, key, altHeld, winHeld, ctrlHeld);
}

internal sealed class KeyboardOwnershipPolicy
{
    public KeyboardOwnershipState State { get; private set; } = KeyboardOwnershipState.NoBackgroundKeyboardInput;
    public KeyboardFocusKind FocusKind { get; private set; } = KeyboardFocusKind.None;

    public void Arm()
    {
        State = KeyboardOwnershipState.PendingBackgroundKeyboardInteraction;
        FocusKind = KeyboardFocusKind.None;
    }

    public bool TryBegin(bool focusValid, bool foregroundAllowed, KeyboardFocusKind focusKind)
    {
        if (State != KeyboardOwnershipState.PendingBackgroundKeyboardInteraction ||
            !focusValid || !foregroundAllowed || focusKind == KeyboardFocusKind.None)
            return false;

        State = KeyboardOwnershipState.BackgroundKeyboardInteraction;
        FocusKind = focusKind;
        return true;
    }

    public bool ShouldForward => State == KeyboardOwnershipState.BackgroundKeyboardInteraction;

    public bool ShouldEndForForegroundChange(bool foregroundAllowed) =>
        State != KeyboardOwnershipState.NoBackgroundKeyboardInput && !foregroundAllowed;

    public void End()
    {
        State = KeyboardOwnershipState.NoBackgroundKeyboardInput;
        FocusKind = KeyboardFocusKind.None;
    }
}
