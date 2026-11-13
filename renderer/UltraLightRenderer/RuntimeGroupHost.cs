using System.Windows.Forms;

namespace UltraLightRenderer;

public sealed class RuntimeGroupHost : IDisposable
{
    private readonly object _sync = new();
    private readonly HostOptions _options;
    private readonly Func<IReadOnlyList<RendererWindowSpec>> _specFactory;
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private RuntimeGroupContext? _context;
    private Exception? _startupException;
    private int _disposed;

    public RuntimeGroupHost(string name, HostOptions options, Func<IReadOnlyList<RendererWindowSpec>> specFactory)
    {
        Name = name;
        _options = options;
        _specFactory = specFactory;
    }

    public string Name { get; }

    public void Start()
    {
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = $"UltraLightRenderer:{Name}"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_startupException != null)
            throw new InvalidOperationException($"Runtime group '{Name}' failed to start.", _startupException);
    }

    public IReadOnlyList<WallpaperWindow> GetDesktopWindows()
    {
        lock (_sync)
            return _context?.GetDesktopWindows() ?? Array.Empty<WallpaperWindow>();
    }

    public string InvokeStatus() => Invoke(context => context.GetStatus());
    public void InvokePause(bool paused) => Invoke(context => context.SetPaused(paused));
    public void InvokeFullscreenMonitor(IntPtr monitor) => Invoke(context => context.SetFullscreenMonitor(monitor));
    public void InvokeShowMenu() => Invoke(context => context.ShowStartMenu());
    public void InvokeHideMenu() => Invoke(context => context.HideStartMenu());
    public void InvokeReload() => Invoke(context => context.Reload());

    public void Stop()
    {
        RuntimeGroupContext? context;
        lock (_sync) context = _context;
        if (context != null)
            context.RequestStop();
        _thread?.Join(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Stop();
        _ready.Dispose();
    }

    private T Invoke<T>(Func<RuntimeGroupContext, T> callback)
    {
        RuntimeGroupContext context;
        lock (_sync)
            context = _context ?? throw new InvalidOperationException($"Runtime group '{Name}' is not running.");
        return context.Invoke(callback);
    }

    private void Invoke(Action<RuntimeGroupContext> callback) => Invoke(context =>
    {
        callback(context);
        return true;
    });

    private void ThreadMain()
    {
        RuntimeGroupContext? context = null;
        try
        {
            context = new RuntimeGroupContext(Name, _options, _specFactory);
            lock (_sync) _context = context;
            _ready.Set();
            Application.Run(context);
        }
        catch (Exception ex)
        {
            _startupException = ex;
            _ready.Set();
        }
        finally
        {
            context?.Dispose();
            lock (_sync) _context = null;
        }
    }
}

internal sealed class RuntimeGroupContext : ApplicationContext, IDisposable
{
    private readonly string _name;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly HostOptions _options;
    private readonly Func<IReadOnlyList<RendererWindowSpec>> _specFactory;
    private readonly Control _dispatcher = new();
    private readonly object _windowSync = new();
    private readonly UltralightRuntime _runtime;
    private readonly DesktopEmbedder _embedder;
    private readonly StartMenuWindowController _startMenuController;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly List<AppWindow> _windows = new();
    private WindowRegistry _registry = new();
    private AutoHideMonitor? _autoHide;
    private List<RendererWindowSpec> _currentSpecs;
    private AppWindow? _startMenu;
    private bool _desiredStartMenuVisible = true;
    private bool _paused;
    private IntPtr _fullscreenMonitor;
    private bool _stopping;
    private bool _disposed;
    private DateTimeOffset _nextReconcile = DateTimeOffset.MinValue;
    private string _topologySignature = GetTopologySignature();

    public RuntimeGroupContext(
        string name,
        HostOptions options,
        Func<IReadOnlyList<RendererWindowSpec>> specFactory)
    {
        _name = name;
        _options = options;
        _specFactory = specFactory;
        _currentSpecs = specFactory().Where(spec => spec.ThreadGroup == name).ToList();
        _dispatcher.CreateControl();
        _runtime = new UltralightRuntime(options.UltralightRoot);
        _embedder = new DesktopEmbedder(options.Verbose);
        _startMenuController = new StartMenuWindowController(options.Verbose);
        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += (_, _) => Tick();

        ReplaceWindows(_currentSpecs);
        if (_startMenu != null)
        {
            _autoHide = new AutoHideMonitor(
                _dispatcher,
                () => _startMenu?.Spec.AutoHide == true,
                () => _startMenu?.IsVisible == true,
                () => _startMenu?.Hwnd ?? IntPtr.Zero,
                HideStartMenu);
            _autoHide.Start();
        }
        _timer.Start();
    }

    public IReadOnlyList<WallpaperWindow> GetDesktopWindows() =>
        SnapshotWindows().Where(window => window.Kind is WindowKind.Wallpaper or WindowKind.Widget)
            .Select(window => new WallpaperWindow(window))
            .ToArray();

    public T Invoke<T>(Func<RuntimeGroupContext, T> callback)
    {
        if (Environment.CurrentManagedThreadId == _ownerThreadId)
            return callback(this);

        T result = default!;
        Exception? error = null;
        _dispatcher.Invoke((Action)(() =>
        {
            try { result = callback(this); }
            catch (Exception ex) { error = ex; }
        }));
        if (error != null) throw new InvalidOperationException(error.Message, error);
        return result;
    }

    public void RequestStop()
    {
        if (_stopping)
            return;
        if (Environment.CurrentManagedThreadId == _ownerThreadId)
        {
            CloseAndExit();
            return;
        }
        try { _dispatcher.BeginInvoke((Action)CloseAndExit); }
        catch (InvalidOperationException) { }
    }

    public string GetStatus()
    {
        string paused = _paused ? "paused" : "active";
        string windows = string.Join(", ", SnapshotWindows().Select(window =>
            $"{window.LogicalId}:{window.State.ToString().ToLowerInvariant()}"));
        return $"{_name}: {paused}; windows=[{windows}]";
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        foreach (AppWindow window in _windows.ToArray())
        {
            if (window.State is AppWindowState.Closed or AppWindowState.Failed)
                continue;
            ApplySuspension(window);
            if (window.Kind == WindowKind.StartMenu)
            {
                if (paused) _startMenuController.Hide(window);
                else ApplyStartMenuVisibility(window);
            }
        }
        _timer.Interval = paused ? 1000 : 16;
    }

    public void SetFullscreenMonitor(IntPtr monitor)
    {
        _fullscreenMonitor = monitor;
        foreach (AppWindow window in _windows.ToArray())
        {
            window.FullscreenOccluded = monitor != IntPtr.Zero && window.MonitorHandle == monitor;
            if (window.Kind is WindowKind.Wallpaper or WindowKind.Widget)
                ApplySuspension(window);
        }
        if (!_paused)
            _timer.Interval = _windows.Any(window => !window.RendererSuspended) ? 16 : 1000;
    }

    public void ShowStartMenu()
    {
        _desiredStartMenuVisible = true;
        if (_startMenu == null)
        {
            RendererWindowSpec? spec = _specFactory().FirstOrDefault(item => item.ThreadGroup == _name && item.Kind == WindowKind.StartMenu);
            if (spec == null)
                throw new InvalidOperationException("No start-menu window is configured.");
            CreateAndAttach(spec, true);
        }
        if (!_paused && _startMenu != null)
            ApplyStartMenuVisibility(_startMenu);
    }

    public void HideStartMenu()
    {
        _desiredStartMenuVisible = false;
        if (_startMenu == null)
            return;

        if (_startMenu.Spec.HiddenBehavior == StartMenuHiddenBehavior.Unload)
        {
            AppWindow window = _startMenu;
            _startMenu = null;
            RemoveAndDestroy(window);
        }
        else
        {
            ApplyStartMenuVisibility(_startMenu);
        }
    }

    public void Reload()
    {
        _currentSpecs = _specFactory().Where(spec => spec.ThreadGroup == _name).ToList();
        ReplaceWindows(_currentSpecs);
    }

    private void Tick()
    {
        if (_stopping)
            return;
        try
        {
            if (!_paused)
                _runtime.Pump();

            if (_topologySignature != GetTopologySignature())
            {
                _topologySignature = GetTopologySignature();
                Reload();
                return;
            }

            if (DateTimeOffset.UtcNow < _nextReconcile)
                return;
            _nextReconcile = DateTimeOffset.UtcNow.AddMilliseconds(_options.ReconcileIntervalMs);
            foreach (AppWindow window in _windows.ToArray())
            {
                if (!NativeMethods.IsWindow(window.Hwnd))
                {
                    _registry.MarkUnexpectedClose(window);
                    Reload();
                    return;
                }

                if (window.Kind is WindowKind.Wallpaper or WindowKind.Widget)
                    _embedder.Reconcile(window);
                else
                    _startMenuController.Reconcile(window);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Group:{_name}] {ex.Message}");
        }
    }

    private void ReplaceWindows(IReadOnlyList<RendererWindowSpec> specs)
    {
        var created = new List<AppWindow>();
        try
        {
            foreach (RendererWindowSpec spec in specs)
                created.Add(CreateWindow(spec));
        }
        catch
        {
            foreach (AppWindow window in created)
                DestroyWindow(window);
            throw;
        }

        foreach (AppWindow oldWindow in _windows.ToArray())
            RemoveAndDestroy(oldWindow);
        lock (_windowSync)
        {
            _windows.Clear();
            _registry = new WindowRegistry();
            _windows.AddRange(created);
            foreach (AppWindow window in created)
                _registry.Add(window);
            _startMenu = _windows.FirstOrDefault(window => window.Kind == WindowKind.StartMenu);
        }
        if (_paused)
            SetPaused(true);
        else if (_startMenu != null)
            ApplyStartMenuVisibility(_startMenu);
    }

    private AppWindow CreateWindow(RendererWindowSpec spec)
    {
        AppWindow window = _runtime.CreateWindow(spec, _options.StartupTimeout);
        window.OwnerThreadId = Environment.CurrentManagedThreadId;
        window.OwnerInvoker = action => InvokeOwner(action);
        NativeMethods.POINT center = new()
        {
            X = spec.Bounds.Left + Math.Max(1, spec.Bounds.Width / 2),
            Y = spec.Bounds.Top + Math.Max(1, spec.Bounds.Height / 2)
        };
        window.MonitorHandle = NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONEAREST);
        try
        {
            if (spec.Kind is WindowKind.Wallpaper or WindowKind.Widget)
            {
                _embedder.Embed(window);
            }
            else
            {
                window.IsVisible = _desiredStartMenuVisible;
                _startMenuController.Reconcile(window);
                if (window.IsVisible)
                    _startMenuController.Show(window);
            }
            _runtime.ShowWhenReady(window, _options.StartupTimeout);
            window.State = AppWindowState.Embedded;
            if (window.Kind is WindowKind.Wallpaper or WindowKind.Widget)
                ApplySuspension(window);
            return window;
        }
        catch
        {
            DestroyWindow(window);
            throw;
        }
    }

    private void CreateAndAttach(RendererWindowSpec spec, bool visible)
    {
        _desiredStartMenuVisible = visible;
        AppWindow window = CreateWindow(spec);
        lock (_windowSync)
        {
            _registry.Add(window);
            _windows.Add(window);
            _startMenu = window;
        }
        if (_paused)
            SetPaused(true);
    }

    private void ApplyStartMenuVisibility(AppWindow window)
    {
        if (_desiredStartMenuVisible && !_paused)
        {
            if (window.RendererSuspended)
            {
                _runtime.TrySetSuspended(window, false);
                window.RendererSuspended = false;
            }
            _startMenuController.Show(window);
        }
        else
        {
            _startMenuController.Hide(window);
            if (window.Spec.HiddenBehavior == StartMenuHiddenBehavior.Pause && !_paused)
            {
                _runtime.TrySetSuspended(window, true);
                window.RendererSuspended = true;
            }
        }
    }

    private void ApplySuspension(AppWindow window)
    {
        bool suspended = _paused ||
            (window.Kind is WindowKind.Wallpaper or WindowKind.Widget && window.FullscreenOccluded);
        window.DesiredSuspended = suspended;
        if (_runtime.TrySetSuspended(window, suspended))
            window.RendererSuspended = suspended;
    }

    private void RemoveAndDestroy(AppWindow window)
    {
        _registry.MarkIntentionalClose(window);
        _registry.Remove(window);
        _embedder.Close(window);
        lock (_windowSync) _windows.Remove(window);
        DestroyWindow(window);
    }

    private void DestroyWindow(AppWindow window)
    {
        if (window.State != AppWindowState.Closed)
            _runtime.DestroyWindow(window);
    }

    private bool InvokeOwner(Action action)
    {
        if (_dispatcher.IsDisposed)
            return false;
        try
        {
            if (Environment.CurrentManagedThreadId == _ownerThreadId)
                action();
            else
                _dispatcher.Invoke(action);
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    private void CloseAndExit()
    {
        if (_stopping)
            return;
        _stopping = true;
        _timer.Stop();
        _autoHide?.Dispose();
        foreach (AppWindow window in _windows.ToArray())
            RemoveAndDestroy(window);
        lock (_windowSync) _windows.Clear();
        Application.ExitThread();
    }

    public new void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_stopping)
            CloseAndExit();
        _timer.Dispose();
        _runtime.Dispose();
        _dispatcher.Dispose();
        base.Dispose();
    }

    private static string GetTopologySignature() =>
        string.Join("|", Screen.AllScreens.Select(screen => $"{screen.DeviceName}:{screen.Bounds}"));

    private AppWindow[] SnapshotWindows()
    {
        lock (_windowSync)
            return _windows.ToArray();
    }
}
