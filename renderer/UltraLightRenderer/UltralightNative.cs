using System.Runtime.InteropServices;
using System.Reflection;

namespace UltraLightRenderer;

internal static class UltralightNative
{
    private const string AppCore = "AppCore.dll";
    private const string Core = "Ultralight.dll";
    private static string? _libraryDirectory;

    static UltralightNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(UltralightNative).Assembly, ResolveLibrary);
    }

    internal static void ConfigureLibraryDirectory(string libraryDirectory) =>
        _libraryDirectory = Path.GetFullPath(libraryDirectory);

    private static IntPtr ResolveLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (_libraryDirectory is null ||
            !string.Equals(libraryName, AppCore, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(libraryName, Core, StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        string path = Path.Combine(_libraryDirectory, libraryName);
        return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
    }

    internal const uint WindowFlagsBorderless = 1u << 0;
    internal const uint WindowFlagsHidden = 1u << 4;
    internal const uint WindowFlagsTransparent = 1u << 5;

    internal const int KeyEventTypeKeyUp = 1;
    internal const int KeyEventTypeRawKeyDown = 2;
    internal const int KeyEventTypeChar = 3;
    internal const uint KeyEventModifierAlt = 1u << 0;
    internal const uint KeyEventModifierCtrl = 1u << 1;
    internal const uint KeyEventModifierMeta = 1u << 2;
    internal const uint KeyEventModifierShift = 1u << 3;

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulCreateSettings();

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDestroySettings(IntPtr settings);

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulSettingsSetDeveloperName(IntPtr settings, IntPtr name);

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulSettingsSetAppName(IntPtr settings, IntPtr name);

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulSettingsSetFileSystemPath(IntPtr settings, IntPtr path);

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulCreateApp(IntPtr settings, IntPtr config);

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDestroyApp(IntPtr app);

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ulAppRunOnce(IntPtr app, double maxWaitSeconds);

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulAppGetMainMonitor(IntPtr app);

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulAppGetMonitor(IntPtr app, uint index);

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint ulAppGetMonitorCount(IntPtr app);

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern double ulMonitorGetScale(IntPtr monitor);

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulCreateWindow(
        IntPtr monitor,
        double width,
        double height,
        [MarshalAs(UnmanagedType.I1)] bool fullscreen,
        uint windowFlags);

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDestroyWindow(IntPtr window);

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulWindowClose(IntPtr window);

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulWindowShowWhenReady(IntPtr window, double timeoutSeconds);

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulWindowGetNativeHandle(IntPtr window);

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulWindowSetTitle(IntPtr window, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulWindowAddPanel(IntPtr window, IntPtr description, IntPtr viewConfig);

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulPanelGetView(IntPtr panel);

    [DllImport(AppCore, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDestroyPanel(IntPtr panel);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulCreateString([MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDestroyString(IntPtr value);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulViewLoadURL(IntPtr view, IntPtr url);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulCreateViewConfig();

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDestroyViewConfig(IntPtr config);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulViewConfigSetEnableHiddenTimerThrottling(
        IntPtr config,
        [MarshalAs(UnmanagedType.I1)] bool enabled);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulViewConfigSetIsTransparent(
        IntPtr config,
        [MarshalAs(UnmanagedType.I1)] bool isTransparent);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulViewSetVisible(
        IntPtr view,
        [MarshalAs(UnmanagedType.I1)] bool visible);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulViewFocus(IntPtr view);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulViewUnfocus(IntPtr view);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ulViewHasInputFocus(IntPtr view);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulCreateKeyEvent(
        int type,
        uint modifiers,
        int virtualKeyCode,
        int nativeKeyCode,
        IntPtr text,
        IntPtr unmodifiedText,
        [MarshalAs(UnmanagedType.I1)] bool isKeypad,
        [MarshalAs(UnmanagedType.I1)] bool isAutoRepeat,
        [MarshalAs(UnmanagedType.I1)] bool isSystemKey);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDestroyKeyEvent(IntPtr keyEvent);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulViewFireKeyEvent(IntPtr view, IntPtr keyEvent);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDestroyView(IntPtr view);
}

