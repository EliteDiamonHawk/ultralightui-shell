using System.Runtime.InteropServices;
using System.Windows.Forms;
using KhaosShell.Contracts;

namespace Chronos;

internal enum KeybindActionKind
{
    Cue = 0,
    AetherCommand = 1,
    ManagedMenu = 2
}

internal enum ManagedMenuAction
{
    Toggle = 0,
    Show = 1,
    Hide = 2
}

[Flags]
internal enum KeybindModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8
}

internal sealed record KeybindGesture(Keys Key, KeybindModifiers Modifiers = KeybindModifiers.None)
{
    public string DisplayName
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(KeybindModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(KeybindModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(KeybindModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(KeybindModifiers.Windows)) parts.Add("Win");
            parts.Add(KeyCode is Keys.LWin or Keys.RWin && Modifiers == KeybindModifiers.None ? "Win" : KeyCode.ToString());
            return string.Join("+", parts);
        }
    }

    public Keys KeyCode => Key & Keys.KeyCode;

    public static KeybindGesture FromKeyEvent(KeyEventArgs args)
    {
        KeybindModifiers modifiers = KeybindModifiers.None;
        if ((args.Modifiers & Keys.Control) != 0) modifiers |= KeybindModifiers.Control;
        if ((args.Modifiers & Keys.Alt) != 0) modifiers |= KeybindModifiers.Alt;
        if ((args.Modifiers & Keys.Shift) != 0) modifiers |= KeybindModifiers.Shift;
        if ((args.Modifiers & Keys.LWin) != 0) modifiers |= KeybindModifiers.Windows;
        return new(args.KeyCode, modifiers);
    }
}

internal sealed record KeybindAction(
    KeybindActionKind Kind,
    IReadOnlyList<string>? CueNames = null,
    RendererCommandKind Command = RendererCommandKind.None,
    string? Group = null,
    bool All = false,
    string? TargetId = null,
    string? Topic = null,
    IReadOnlyList<string>? ConfigPaths = null,
    SystemTrayAction TrayAction = SystemTrayAction.Toggle,
    ManagedMenuAction MenuAction = ManagedMenuAction.Toggle)
{
    public IReadOnlyList<string> EffectiveCueNames => CueNames ?? Array.Empty<string>();

    public RendererCommand ToRendererCommand() => new(
        Command,
        Group: Group,
        All: All,
        TargetId: TargetId,
        Topic: Topic,
        ConfigPaths: ConfigPaths,
        TrayAction: TrayAction);
}

internal sealed record KeybindDefinition(
    Guid Id,
    KeybindGesture Gesture,
    bool Enabled = true,
    bool Override = true,
    KeybindAction? Action = null,
    bool SystemBinding = false)
{
    public KeybindAction EffectiveAction => Action ?? new(KeybindActionKind.Cue);

    public string DisplayName => EffectiveAction.Kind == KeybindActionKind.Cue
        ? $"{Gesture.DisplayName} -> cue:{string.Join(", ", EffectiveAction.EffectiveCueNames)}"
        : EffectiveAction.Kind == KeybindActionKind.ManagedMenu
            ? $"{Gesture.DisplayName} -> menu:{EffectiveAction.MenuAction.ToString().ToLowerInvariant()}"
            : $"{Gesture.DisplayName} -> {EffectiveAction.Command}";

    public override string ToString() => DisplayName;
}

internal static class KeybindCatalog
{
    internal static readonly Guid WindowsBindingId = Guid.Parse("9b4e6f4f-9f92-4b95-a36d-0d7d6cfe72e6");
    private const int LegacyToggleMenuCommand = 13;

    public static IReadOnlyList<KeybindDefinition> NormalizeAndValidate(IReadOnlyList<KeybindDefinition> bindings)
    {
        var result = new List<KeybindDefinition>(bindings.Count);
        var gestures = new HashSet<KeybindGesture>();
        foreach (KeybindDefinition binding in bindings)
        {
            if (binding.Id == Guid.Empty)
                throw new InvalidOperationException("Keybind ID must not be empty.");
            if (!binding.SystemBinding)
                ValidateGesture(binding.Gesture);
            else
                ValidateSystemBinding(binding);
            if (!gestures.Add(binding.Gesture))
                throw new InvalidOperationException($"Duplicate keybind: {binding.Gesture.DisplayName}.");

            KeybindAction action = binding.EffectiveAction;
            if (action.Kind == KeybindActionKind.Cue)
            {
                string[] cues = action.EffectiveCueNames.Select(cue => cue.Trim()).ToArray();
                if (cues.Length == 0 || cues.Any(cue => cue.Length == 0))
                    throw new InvalidOperationException("Cue names must not be empty.");
                if (cues.Distinct(StringComparer.Ordinal).Count() != cues.Length)
                    throw new InvalidOperationException("Cue names must be unique per keybind.");
                action = action with { CueNames = cues };
            }
            else if (action.Kind == KeybindActionKind.AetherCommand)
            {
                ValidateCommand(action);
            }
            else if (action.Kind == KeybindActionKind.ManagedMenu)
            {
                if (!Enum.IsDefined(action.MenuAction))
                    throw new InvalidOperationException("Unknown managed menu action.");
            }
            else
            {
                throw new InvalidOperationException("Unknown keybind action type.");
            }

            result.Add(binding with { Action = action });
        }
        return result;
    }

    public static void ValidateGesture(KeybindGesture gesture)
    {
        Keys key = gesture.KeyCode;
        if (key == Keys.None || IsModifier(key))
            throw new InvalidOperationException("Keybind must contain one non-modifier key.");
        if (key == Keys.Escape)
            throw new InvalidOperationException("Escape is reserved for Panic Escape.");
        if ((key is Keys.LWin or Keys.RWin) && gesture.Modifiers == KeybindModifiers.None)
            throw new InvalidOperationException("Bare Windows key is reserved for Windows Key Override.");
    }

    public static void ValidateCommand(KeybindAction action)
    {
        if (action.Command == RendererCommandKind.None)
            throw new InvalidOperationException("Select an Aether command.");
        if (action.Command == RendererCommandKind.Show && string.IsNullOrWhiteSpace(action.Topic))
            throw new InvalidOperationException("Show command requires topic.");
        if (action.Command is (RendererCommandKind.HideWindow or RendererCommandKind.RevealWindow or
            RendererCommandKind.UnloadWindow or RendererCommandKind.ReloadWindow) && string.IsNullOrWhiteSpace(action.TargetId))
            throw new InvalidOperationException("Window command requires target ID.");
        if (action.Command == RendererCommandKind.ApplyConfig && (action.ConfigPaths == null || action.ConfigPaths.Count == 0))
            throw new InvalidOperationException("ApplyConfig requires at least one config path.");
    }

    public static bool IsModifier(Keys key) => key is
        Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or
        Keys.Menu or Keys.LMenu or Keys.RMenu or
        Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or
        Keys.LWin or Keys.RWin;

    public static IReadOnlyList<KeybindDefinition> DefaultBindings => new[] { CreateWindowsBinding(false) };

    public static KeybindDefinition CreateWindowsBinding(bool enabled) => new(
        WindowsBindingId,
        new KeybindGesture(Keys.LWin),
        enabled,
        Override: true,
        new KeybindAction(KeybindActionKind.ManagedMenu, MenuAction: ManagedMenuAction.Toggle),
        SystemBinding: true);

    public static IReadOnlyList<KeybindDefinition> EnsureWindowsBinding(IReadOnlyList<KeybindDefinition>? bindings, bool fallbackEnabled)
    {
        var result = (bindings ?? Array.Empty<KeybindDefinition>()).Where(binding => !binding.SystemBinding).ToList();
        KeybindDefinition? existing = bindings?.FirstOrDefault(binding => binding.SystemBinding);
        if (existing is { EffectiveAction.Kind: KeybindActionKind.AetherCommand } &&
            (int)existing.EffectiveAction.Command == LegacyToggleMenuCommand)
        {
            existing = existing with
            {
                Action = existing.EffectiveAction with
                {
                    Kind = KeybindActionKind.ManagedMenu,
                    Command = RendererCommandKind.None,
                    MenuAction = ManagedMenuAction.Toggle
                }
            };
        }
        result.Insert(0, existing ?? CreateWindowsBinding(fallbackEnabled));
        return NormalizeAndValidate(result);
    }

    public static IReadOnlyList<KeybindDefinition> SetWindowsBindingEnabled(IReadOnlyList<KeybindDefinition> bindings, bool enabled)
    {
        IReadOnlyList<KeybindDefinition> normalized = EnsureWindowsBinding(bindings, enabled);
        return normalized.Select(binding => binding.SystemBinding ? binding with { Enabled = enabled } : binding).ToArray();
    }

    private static void ValidateSystemBinding(KeybindDefinition binding)
    {
        if (binding.Id != WindowsBindingId || binding.Gesture != new KeybindGesture(Keys.LWin) ||
            binding.EffectiveAction.Kind != KeybindActionKind.ManagedMenu ||
            binding.EffectiveAction.MenuAction != ManagedMenuAction.Toggle)
            throw new InvalidOperationException("Invalid system Windows key binding.");
    }
}

internal sealed class KeybindController : IDisposable
{
    private readonly Func<KeybindDefinition, Task> _trigger;
    private readonly Action<string> _diagnostic;
    private readonly NativeMethods.LowLevelKeyboardProc _callback;
    private readonly object _sync = new();
    private readonly HashSet<Keys> _suppressedKeys = new();
    private KeybindDefinition[] _bindings = Array.Empty<KeybindDefinition>();
    private KeybindModifiers _modifiers;
    private IntPtr _hook;
    private bool _disposed;

    public KeybindController(IReadOnlyList<KeybindDefinition> bindings, Func<KeybindDefinition, Task> trigger, Action<string> diagnostic)
    {
        _trigger = trigger;
        _diagnostic = diagnostic;
        _callback = HookCallback;
        SetBindings(bindings);
    }

    public void SetBindings(IReadOnlyList<KeybindDefinition> bindings)
    {
        KeybindDefinition[] normalized = KeybindCatalog.NormalizeAndValidate(bindings)
            .Where(binding => !binding.SystemBinding)
            .ToArray();
        lock (_sync)
        {
            if (_disposed)
                return;
            _bindings = normalized;
            bool needsHook = normalized.Any(binding => binding.Enabled);
            if (needsHook && _hook == IntPtr.Zero)
                InstallHook();
            else if (!needsHook && _hook != IntPtr.Zero)
                RemoveHook();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            RemoveHook();
            _suppressedKeys.Clear();
        }
    }

    private void InstallHook()
    {
        IntPtr module = NativeMethods.GetModuleHandle(null);
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _callback, module, 0);
        if (_hook == IntPtr.Zero)
            _diagnostic("Keybind hook could not be installed; keybinds remain inactive.");
    }

    private void RemoveHook()
    {
        if (_hook != IntPtr.Zero)
            NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _modifiers = KeybindModifiers.None;
        _suppressedKeys.Clear();
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || _disposed || _hook == IntPtr.Zero)
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);

        NativeMethods.KbdLlHookStruct data = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
        if ((data.Flags & NativeMethods.LLKHF_INJECTED) != 0)
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);

        bool keyDown = wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN;
        bool keyUp = wParam == (IntPtr)NativeMethods.WM_KEYUP || wParam == (IntPtr)NativeMethods.WM_SYSKEYUP;
        Keys key = (Keys)(int)data.VirtualKeyCode;
        if (keyDown)
        {
            UpdateModifier(key, true);
            bool repeat = (data.Flags & 0x80) != 0;
            if (!repeat && !KeybindCatalog.IsModifier(key))
            {
                KeybindDefinition? binding = FindBinding(key & Keys.KeyCode, _modifiers);
                if (binding != null)
                {
                    if (binding.Override)
                        _suppressedKeys.Add(key & Keys.KeyCode);
                    _ = DispatchAsync(binding);
                    if (binding.Override)
                        return IntPtr.Zero;
                }
            }
        }
        else if (keyUp)
        {
            Keys keyCode = key & Keys.KeyCode;
            bool suppress = _suppressedKeys.Remove(keyCode);
            UpdateModifier(key, false);
            if (suppress)
                return IntPtr.Zero;
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private KeybindDefinition? FindBinding(Keys key, KeybindModifiers modifiers)
    {
        lock (_sync)
        {
            return _bindings.FirstOrDefault(binding => binding.Enabled &&
                binding.Gesture.KeyCode == key && binding.Gesture.Modifiers == modifiers);
        }
    }

    private async Task DispatchAsync(KeybindDefinition binding)
    {
        try { await Task.Run(() => _trigger(binding)).ConfigureAwait(false); }
        catch (Exception ex) { _diagnostic($"Keybind '{binding.DisplayName}' failed: {ex.Message}"); }
    }

    private void UpdateModifier(Keys key, bool pressed)
    {
        KeybindModifiers? modifier = key switch
        {
            Keys.ControlKey or Keys.LControlKey or Keys.RControlKey => KeybindModifiers.Control,
            Keys.Menu or Keys.LMenu or Keys.RMenu => KeybindModifiers.Alt,
            Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey => KeybindModifiers.Shift,
            Keys.LWin or Keys.RWin => KeybindModifiers.Windows,
            _ => null
        };
        if (modifier is { } value)
        {
            if (pressed) _modifiers |= value;
            else _modifiers &= ~value;
        }
    }
}
