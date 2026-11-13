namespace UltraLightRenderer;

public sealed class UltralightRuntime : IDisposable
{
    private IntPtr _app;

    public UltralightRuntime(string? ultralightRoot = null)
    {
        string stage = "runtime file validation";
        try
        {
            Initialize(ultralightRoot, out stage);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Ultralight initialization failed at {stage}: {ex.Message}", ex);
        }
    }

    private void Initialize(string? ultralightRoot, out string stage)
    {
        stage = "runtime file validation";
        string basePath = AppContext.BaseDirectory;
        RuntimeLayout layout = RuntimeLayout.Resolve(ultralightRoot, basePath);
        layout.Validate();
        UltralightNative.ConfigureLibraryDirectory(layout.LibraryDirectory);

        stage = "AppCore creation";
        IntPtr settings = UltralightNative.ulCreateSettings();
        if (settings == IntPtr.Zero)
            throw new InvalidOperationException("Ultralight settings could not be created.");

        IntPtr developerName = CreateString("UltraLightRenderer");
        IntPtr appName = CreateString("ultralightrenderer");
        IntPtr filesystemPath = CreateString(layout.FileSystemPath);
        try
        {
            UltralightNative.ulSettingsSetDeveloperName(settings, developerName);
            UltralightNative.ulSettingsSetAppName(settings, appName);
            UltralightNative.ulSettingsSetFileSystemPath(settings, filesystemPath);
            _app = UltralightNative.ulCreateApp(settings, IntPtr.Zero);
        }
        finally
        {
            DestroyString(appName);
            DestroyString(developerName);
            DestroyString(filesystemPath);
            UltralightNative.ulDestroySettings(settings);
        }

        if (_app == IntPtr.Zero)
            throw new InvalidOperationException("Ultralight AppCore could not be initialized.");
    }

    public AppWindow CreateWindow(RendererWindowSpec spec, TimeSpan timeout)
    {
        IntPtr monitor = ResolveMonitor(spec);
        double scale = Math.Max(0.01, UltralightNative.ulMonitorGetScale(monitor));
        double width = Math.Max(1, Math.Ceiling(spec.Bounds.Width / scale));
        double height = Math.Max(1, Math.Ceiling(spec.Bounds.Height / scale));
        IntPtr window = UltralightNative.ulCreateWindow(
            monitor,
            width,
            height,
            false,
            UltralightNative.WindowFlagsBorderless |
            UltralightNative.WindowFlagsHidden |
            (spec.Transparent ? UltralightNative.WindowFlagsTransparent : 0));

        if (window == IntPtr.Zero)
            throw new InvalidOperationException($"Ultralight window could not be created: {spec.LogicalId}");

        IntPtr panel = IntPtr.Zero;
        IntPtr view = IntPtr.Zero;
        try
        {
            IntPtr hwnd = UltralightNative.ulWindowGetNativeHandle(window);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException($"Ultralight window has no native handle: {spec.LogicalId}");

            UltralightNative.ulWindowSetTitle(window, $"UltraLightRenderer - {spec.LogicalId}");
            IntPtr viewConfig = UltralightNative.ulCreateViewConfig();
            if (viewConfig == IntPtr.Zero)
                throw new InvalidOperationException($"Ultralight view configuration could not be created: {spec.LogicalId}");

            try
            {
                UltralightNative.ulViewConfigSetEnableHiddenTimerThrottling(viewConfig, true);
                if (spec.Transparent)
                    UltralightNative.ulViewConfigSetIsTransparent(viewConfig, true);
                panel = UltralightNative.ulWindowAddPanel(window, IntPtr.Zero, viewConfig);
            }
            finally
            {
                UltralightNative.ulDestroyViewConfig(viewConfig);
            }

            if (panel == IntPtr.Zero)
                throw new InvalidOperationException($"Ultralight panel could not be created: {spec.LogicalId}");

            view = UltralightNative.ulPanelGetView(panel);
            if (view == IntPtr.Zero)
                throw new InvalidOperationException($"Ultralight view could not be created: {spec.LogicalId}");

            IntPtr url = CreateString(spec.Url);
            try
            {
                UltralightNative.ulViewLoadURL(view, url);
            }
            finally
            {
                DestroyString(url);
            }

            return new AppWindow(spec, timeout, window, panel, view, hwnd);
        }
        catch
        {
            DestroyWindow(window, panel, view);
            throw;
        }
    }

    public void ShowWhenReady(AppWindow appWindow, TimeSpan timeout) =>
        UltralightNative.ulWindowShowWhenReady(appWindow.UlWindow, Math.Max(0, timeout.TotalSeconds));

    public bool Pump() => _app != IntPtr.Zero && UltralightNative.ulAppRunOnce(_app, 0);

    public bool TrySetSuspended(AppWindow appWindow, bool suspended)
    {
        if (_app == IntPtr.Zero || appWindow.UlView == IntPtr.Zero ||
            appWindow.State is AppWindowState.Closed or AppWindowState.Failed)
        {
            return false;
        }

        try
        {
            UltralightNative.ulViewSetVisible(appWindow.UlView, !suspended);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Suspension] transition failed for {appWindow.LogicalId}: {ex.Message}");
            return false;
        }
    }

    public void DestroyWindow(AppWindow appWindow)
    {
        DestroyWindow(appWindow.UlWindow, appWindow.UlPanel, appWindow.UlView);
        appWindow.UlWindow = IntPtr.Zero;
        appWindow.UlPanel = IntPtr.Zero;
        appWindow.UlView = IntPtr.Zero;
        appWindow.Hwnd = IntPtr.Zero;
        appWindow.RendererSuspended = false;
        appWindow.State = AppWindowState.Closed;
    }

    public void Dispose()
    {
        if (_app == IntPtr.Zero)
            return;

        UltralightNative.ulDestroyApp(_app);
        _app = IntPtr.Zero;
    }

    private IntPtr ResolveMonitor(RendererWindowSpec spec)
    {
        if (spec.MonitorNumber > 0)
        {
            IntPtr monitor = UltralightNative.ulAppGetMonitor(_app, (uint)(spec.MonitorNumber - 1));
            if (monitor != IntPtr.Zero)
                return monitor;
        }

        return UltralightNative.ulAppGetMainMonitor(_app);
    }

    private static IntPtr CreateString(string value) => UltralightNative.ulCreateString(value);

    private static void DestroyString(IntPtr value)
    {
        if (value != IntPtr.Zero)
            UltralightNative.ulDestroyString(value);
    }

    private static void DestroyWindow(IntPtr window, IntPtr panel, IntPtr view)
    {
        if (window == IntPtr.Zero)
            return;

        UltralightNative.ulWindowClose(window);
        if (view != IntPtr.Zero)
            UltralightNative.ulDestroyView(view);
        if (panel != IntPtr.Zero)
            UltralightNative.ulDestroyPanel(panel);
        UltralightNative.ulDestroyWindow(window);
    }
}

internal sealed record RuntimeLayout(string LibraryDirectory, string FileSystemPath)
{
    private static readonly string[] RequiredLibraries =
        { "AppCore.dll", "Ultralight.dll", "UltralightCore.dll", "WebCore.dll" };

    internal static RuntimeLayout Resolve(string? ultralightRoot, string basePath)
    {
        if (string.IsNullOrWhiteSpace(ultralightRoot))
        {
            string bundledFileSystemPath = Path.Combine(basePath, "assets");
            return new RuntimeLayout(basePath, bundledFileSystemPath);
        }

        string root = Path.GetFullPath(ultralightRoot);
        return new RuntimeLayout(Path.Combine(root, "bin"), root);
    }

    internal void Validate()
    {
        foreach (string library in RequiredLibraries)
        {
            string path = Path.Combine(LibraryDirectory, library);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Ultralight runtime file is missing: {path}", path);
        }

        if (!Directory.Exists(FileSystemPath))
            throw new DirectoryNotFoundException($"Ultralight filesystem root is missing: {FileSystemPath}");

        string resourcesPath = Path.Combine(FileSystemPath, "resources");
        if (!Directory.Exists(resourcesPath))
            throw new DirectoryNotFoundException($"Ultralight resources directory is missing: {resourcesPath}");
    }
}

public sealed class AppWindow
{
    public AppWindow(RendererWindowSpec spec, TimeSpan timeout)
        : this(spec, timeout, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)
    {
    }

    internal AppWindow(
        RendererWindowSpec spec,
        TimeSpan timeout,
        IntPtr ulWindow,
        IntPtr ulPanel,
        IntPtr ulView,
        IntPtr hwnd)
    {
        LogicalId = spec.LogicalId;
        Url = spec.Url;
        Spec = spec;
        StartedAt = DateTimeOffset.UtcNow;
        DiscoveryTimeout = timeout;
        Deadline = StartedAt + timeout;
        UlWindow = ulWindow;
        UlPanel = ulPanel;
        UlView = ulView;
        Hwnd = hwnd;
    }

    public string LogicalId { get; }
    public string Url { get; }
    public RendererWindowSpec Spec { get; }
    public DateTimeOffset StartedAt { get; }
    public TimeSpan DiscoveryTimeout { get; }
    public DateTimeOffset Deadline { get; internal set; }
    public IntPtr Hwnd { get; internal set; }
    public IntPtr MonitorHostHwnd { get; internal set; }
    public IntPtr MonitorHandle { get; internal set; }
    public bool FullscreenOccluded { get; internal set; }
    public bool DesiredSuspended { get; internal set; }
    public bool RendererSuspended { get; internal set; }
    public bool IsVisible { get; internal set; }
    public bool TopmostPlacementApplied { get; internal set; }
    public WindowKind Kind => Spec.Kind;
    public AppWindowState State { get; internal set; } = AppWindowState.Pending;
    public WindowCloseIntent CloseIntent { get; internal set; } = WindowCloseIntent.None;
    public string? FailureReason { get; internal set; }
    public int OwnerThreadId { get; internal set; }
    public Func<Action, bool>? OwnerInvoker { get; internal set; }
    internal IntPtr UlWindow { get; set; }
    internal IntPtr UlPanel { get; set; }
    internal IntPtr UlView { get; set; }

    public bool IsExpired(DateTimeOffset now) => now >= Deadline;

    public void InvokeOnOwner(Action action)
    {
        if (OwnerThreadId == 0 || OwnerThreadId == Environment.CurrentManagedThreadId)
        {
            action();
            return;
        }

        if (OwnerInvoker == null || !OwnerInvoker(action))
            throw new InvalidOperationException($"Window owner thread is unavailable: {LogicalId}");
    }

    public T InvokeOnOwner<T>(Func<T> action)
    {
        T result = default!;
        InvokeOnOwner(() => result = action());
        return result;
    }
}

public enum AppWindowState
{
    Pending,
    Embedded,
    Closed,
    Failed
}

public enum WindowCloseIntent
{
    None,
    Intentional,
    Unexpected
}

public static class CloseCheck
{
    public static bool AllClosed(IEnumerable<AppWindow> windows) =>
        windows.Any() && windows.All(window => window.State is AppWindowState.Closed or AppWindowState.Failed);

    public static bool IsIntentional(IEnumerable<AppWindow> windows) =>
        windows.Any() && windows.All(window => window.CloseIntent == WindowCloseIntent.Intentional);
}

