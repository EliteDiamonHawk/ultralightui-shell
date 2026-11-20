using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;

namespace Aether;

internal static class UltralightNative
{
    private const string AppCore = "AppCore.dll";
    private const string Core = "Ultralight.dll";
    private static string? _libraryDirectory;
    private static readonly object NativeLogSync = new();
    private static readonly UltralightLogMessageCallback LogMessageCallback = OnLogMessage;
    private static StreamWriter? _nativeLogWriter;

    static UltralightNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(UltralightNative).Assembly, ResolveLibrary);
    }

    internal static void ConfigureLibraryDirectory(string libraryDirectory) =>
        _libraryDirectory = Path.GetFullPath(libraryDirectory);

    internal static void ConfigureLogger(string logPath)
    {
        lock (NativeLogSync)
        {
            if (_nativeLogWriter == null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                    _nativeLogWriter = new StreamWriter(
                        new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
                        new UTF8Encoding(false))
                    {
                        AutoFlush = true
                    };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"[Ultralight] Native log file unavailable: {ex.Message}");
                }
            }

            ulPlatformSetLogger(new UltralightLogger(LogMessageCallback));
        }
    }

    private static void OnLogMessage(int level, IntPtr message)
    {
        try
        {
            IntPtr data = message == IntPtr.Zero ? IntPtr.Zero : ulStringGetData(message);
            UIntPtr length = message == IntPtr.Zero ? UIntPtr.Zero : ulStringGetLength(message);
            ulong byteCount = length.ToUInt64();
            if (data == IntPtr.Zero || byteCount > int.MaxValue)
                return;

            byte[] bytes = new byte[(int)byteCount];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            string line = $"[{DateTimeOffset.UtcNow:O}] [{level}] {Encoding.UTF8.GetString(bytes)}";
            lock (NativeLogSync)
                _nativeLogWriter?.WriteLine(line);
        }
        catch
        {
            // Native logging must never take down the renderer.
        }
    }

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
    internal const uint JsApiAttachFlagsNone = 0;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr JsFunctionCallback(
        IntPtr userData,
        IntPtr context,
        IntPtr thisValue,
        IntPtr args,
        UIntPtr argumentCount,
        out IntPtr exception);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void JsAsyncFunctionCallback(
        IntPtr userData,
        IntPtr context,
        IntPtr thisValue,
        IntPtr args,
        UIntPtr argumentCount,
        IntPtr resolver);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr JsCompleteCallback(
        IntPtr userData,
        IntPtr context,
        out IntPtr exception);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void UserDataDestroyCallback(IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void DomDataSyncWalkCallback(IntPtr record, IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr DomDataAcquireCallback(IntPtr holderState);

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
    internal static extern IntPtr ulStringGetData(IntPtr str);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern UIntPtr ulStringGetLength(IntPtr str);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulPlatformSetLogger(UltralightLogger logger);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulCreateConfig();

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDestroyConfig(IntPtr config);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulConfigSetCachePath(IntPtr config, IntPtr cachePath);

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

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulCreateJSAPI([MarshalAs(UnmanagedType.LPUTF8Str)] string rootPath);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDestroyJSAPI(IntPtr api);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ulViewAttachJSAPI(
        IntPtr view,
        IntPtr api,
        uint flags,
        IntPtr originRules,
        UIntPtr originRuleCount);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulViewDetachJSAPI(IntPtr view, IntPtr api);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ulJSAPISetConstantString(
        IntPtr api,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ulJSAPISetConstantJSON(
        IntPtr api,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string json);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ulJSAPIBindFunction(
        IntPtr api,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        JsFunctionCallback callback,
        IntPtr userData,
        IntPtr destroyUserData);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ulJSAPIBindAsyncFunction(
        IntPtr api,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        JsAsyncFunctionCallback callback,
        IntPtr userData,
        IntPtr destroyUserData);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulJSAPIEmitEventJSON(
        IntPtr api,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string eventPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string argsJson);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulCreateJSValueStringFromCString(
        IntPtr context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulCreateJSValueFromJSON(
        IntPtr context,
        IntPtr json,
        out IntPtr exception);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulJSValueToJSON(
        IntPtr value,
        uint indent,
        out IntPtr exception);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDestroyJSValue(IntPtr value);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ulJSValueIsAlive(IntPtr value);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ulJSValueIsFunction(IntPtr value);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulCreateJSValueRef(IntPtr value);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulJSValueGetContext(IntPtr value);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDestroyJSContext(IntPtr context);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulJSFunctionCall(
        IntPtr function,
        IntPtr thisValue,
        IntPtr args,
        UIntPtr argumentCount,
        out IntPtr exception);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulCreateJSError(
        IntPtr context,
        int type,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string message);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulCreateDOMDataContext();

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDestroyDOMDataContext(IntPtr context);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ulViewAttachDOMDataContext(
        IntPtr view,
        IntPtr context,
        uint flags,
        IntPtr originRules,
        UIntPtr originRuleCount);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulViewDetachDOMDataContext(IntPtr view, IntPtr context);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulCreateDOMDataTypeBuilder(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? typeName);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDestroyDOMDataTypeBuilder(IntPtr builder);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ulDOMDataTypeBuilderAddField(
        IntPtr builder,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        uint kind,
        uint flags);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ulDOMDataTypeBuilderAddObject(
        IntPtr builder,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        IntPtr child,
        [MarshalAs(UnmanagedType.I1)] bool nullable);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ulDOMDataTypeBuilderAddList(
        IntPtr builder,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        IntPtr rowType,
        uint keyKind);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulDOMDataTypeBuilderBuild(IntPtr builder);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulDOMDataContextBind(
        IntPtr context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        IntPtr typeDescriptor,
        DomDataSyncWalkCallback walk,
        IntPtr holderState,
        DomDataAcquireCallback acquire,
        IntPtr release,
        UserDataDestroyCallback? destroyHolder);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDOMDataContextSync(IntPtr context);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDestroyDOMDataBinding(IntPtr binding);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDOMDataRecordSetBool(
        IntPtr record,
        uint slot,
        [MarshalAs(UnmanagedType.I1)] bool value);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDOMDataRecordSetDouble(IntPtr record, uint slot, double value);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern double ulDOMDataRecordGetDouble(IntPtr record, uint slot);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDOMDataRecordSetString(
        IntPtr record,
        uint slot,
        IntPtr value,
        UIntPtr length);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDOMDataRecordSetPresent(
        IntPtr record,
        uint slot,
        [MarshalAs(UnmanagedType.I1)] bool present);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulDOMDataRecordGetChild(IntPtr record, uint slot);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDOMDataRecordListBegin(IntPtr record, uint slot, UIntPtr count);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulDOMDataRecordListRow(IntPtr record, uint slot, UIntPtr ordinal);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDOMDataRecordListEnd(IntPtr record, uint slot);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ulCreateJSPromise(
        IntPtr context,
        out IntPtr resolver);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulJSPromiseResolverComplete(
        IntPtr resolver,
        JsCompleteCallback callback,
        IntPtr userData,
        UserDataDestroyCallback destroyUserData);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ulDestroyJSPromiseResolver(IntPtr resolver);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ulJSPromiseResolverIsDetached(IntPtr resolver);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void UltralightLogMessageCallback(int level, IntPtr message);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct UltralightLogger
    {
        internal UltralightLogger(UltralightLogMessageCallback logMessage) => LogMessage = logMessage;

        internal readonly UltralightLogMessageCallback LogMessage;
    }
}

