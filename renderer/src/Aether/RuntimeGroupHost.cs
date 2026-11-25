using System.Drawing;
using System.Windows.Forms;

namespace Aether;

public sealed class RuntimeGroupHost : IDisposable
{
    private readonly object _sync = new();
    private readonly HostOptions _options;
    private readonly Func<IReadOnlyList<RendererWindowSpec>> _specFactory;
    private readonly RendererLogger _logger;
    private readonly BackendConnectionManager? _backends;
    private readonly ControlPipeChannel? _control;
    private readonly Func<RendererCommand, string>? _runtimeCommandExecutor;
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private RuntimeGroupContext? _context;
    private Exception? _startupException;
    private int _disposed;

    internal RuntimeGroupHost(string name, HostOptions options, Func<IReadOnlyList<RendererWindowSpec>> specFactory, RendererLogger logger, BackendConnectionManager? backends = null, ControlPipeChannel? control = null, Func<RendererCommand, string>? runtimeCommandExecutor = null)
    {
        Name = name;
        _options = options;
        _specFactory = specFactory;
        _logger = logger;
        _backends = backends;
        _control = control;
        _runtimeCommandExecutor = runtimeCommandExecutor;
    }

    public string Name { get; }

    public void Start()
    {
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = $"Aether:{Name}"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_startupException != null)
            throw new InvalidOperationException(
                $"Runtime group '{Name}' failed to start: {_startupException.Message}",
                _startupException);
    }

    public IReadOnlyList<WallpaperWindow> GetDesktopWindows()
    {
        lock (_sync)
            return _context?.GetDesktopWindows() ?? Array.Empty<WallpaperWindow>();
    }

    public string InvokeStatus() => Invoke(context => context.GetStatus());
    public void InvokePause(bool paused) => Invoke(context => context.SetPaused(paused));
    public void InvokeFullscreenMonitor(IntPtr monitor) => Invoke(context => context.SetFullscreenMonitor(monitor));
    public void InvokeHideWindow(string? id) => Invoke(context => context.SetWindowVisible(id, false));
    public void InvokeRevealWindow(string? id) => Invoke(context => context.SetWindowVisible(id, true));
    public WindowSnapshot InvokeCreateWindow(RendererWindowSpec spec) => Invoke(context => context.CreateDynamicWindow(spec));
    public void InvokeDestroyWindow(string id) => Invoke(context => context.DestroyDynamicWindow(id));
    public WindowSnapshot InvokeWindowAction(string id, string action) => Invoke(context => context.ApplyWindowAction(id, action));
    public IReadOnlyList<WindowSnapshot> InvokeForegroundChanged(IntPtr foreground) => Invoke(context => context.UpdateFocus(foreground));
    public void InvokeReload() => Invoke(context => context.Reload());
    public void InvokeReload(IReadOnlyList<RendererWindowSpec> specs) => Invoke(context => context.Reload(specs));
    public IReadOnlyList<WindowSnapshot> GetWindowSnapshots() => Invoke(context => context.GetWindowSnapshots());

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
        EventHandler? idleHandler = null;
        int startupSignaled = 0;
        try
        {
            context = new RuntimeGroupContext(Name, _options, _specFactory, _logger, _backends, _control, _runtimeCommandExecutor);
            lock (_sync) _context = context;
            idleHandler = (_, _) =>
            {
                if (Interlocked.Exchange(ref startupSignaled, 1) != 0)
                    return;
                _logger.Info("runtime.group.started", new Dictionary<string, string?> { ["group"] = Name });
                _ready.Set();
            };
            Application.Idle += idleHandler;
            Application.Run(context);
        }
        catch (Exception ex)
        {
            _startupException = ex;
            _logger.Error("runtime.group.start_failed", new Dictionary<string, string?>
            {
                ["group"] = Name,
                ["exceptionType"] = ex.GetType().Name,
                ["message"] = ex.Message
            });
            Console.Error.WriteLine($"[Group:{Name}] {ex}");
            if (Interlocked.Exchange(ref startupSignaled, 1) == 0)
                _ready.Set();
        }
        finally
        {
            if (idleHandler != null)
                Application.Idle -= idleHandler;
            if (Interlocked.Exchange(ref startupSignaled, 1) == 0)
                _ready.Set();
            context?.Dispose();
            lock (_sync) _context = null;
            _logger.Info("runtime.group.stopped", new Dictionary<string, string?> { ["group"] = Name });
        }
    }
}

internal sealed class RuntimeGroupContext : ApplicationContext, IDisposable
{
    private readonly string _name;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly HostOptions _options;
    private readonly Func<IReadOnlyList<RendererWindowSpec>> _specFactory;
    private readonly RendererLogger _logger;
    private readonly BackendConnectionManager? _backends;
    private readonly ControlPipeChannel? _control;
    private readonly Func<RendererCommand, string>? _runtimeCommandExecutor;
    private readonly Control _dispatcher = new();
    private readonly object _windowSync = new();
    private readonly UltralightRuntime _runtime;
    private readonly DesktopEmbedder _embedder;
    private readonly GenericWindowPlacementController _windowController;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly List<AppWindow> _windows = new();
    private WindowRegistry _registry = new();
    private List<RendererWindowSpec> _currentSpecs;
    private bool _paused;
    private IntPtr _fullscreenMonitor;
    private bool _stopping;
    private bool _disposed;
    private DateTimeOffset _nextReconcile = DateTimeOffset.MinValue;
    private string _topologySignature = GetTopologySignature();

    public RuntimeGroupContext(
        string name,
        HostOptions options,
        Func<IReadOnlyList<RendererWindowSpec>> specFactory,
        RendererLogger logger,
        BackendConnectionManager? backends = null,
        ControlPipeChannel? control = null,
        Func<RendererCommand, string>? runtimeCommandExecutor = null)
    {
        _name = name;
        _options = options;
        _specFactory = specFactory;
        _logger = logger;
        _backends = backends;
        _control = control;
        _runtimeCommandExecutor = runtimeCommandExecutor;
        _currentSpecs = specFactory().Where(spec => spec.ThreadGroup == name).ToList();
        _dispatcher.CreateControl();
        _runtime = new UltralightRuntime(options.UltralightRoot, _backends, _control, _runtimeCommandExecutor);
        _embedder = new DesktopEmbedder(options.Verbose);
        _windowController = new GenericWindowPlacementController();
        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += (_, _) => Tick();

        ReplaceWindows(_currentSpecs);
        _timer.Start();
    }

    public IReadOnlyList<WallpaperWindow> GetDesktopWindows() =>
        SnapshotWindows().Where(window => window.Spec.Placement == WindowPlacement.Desktop)
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

    public IReadOnlyList<WindowSnapshot> GetWindowSnapshots() => SnapshotWindows().Select(Snapshot).ToArray();

    public void SetWindowVisible(string? id, bool visible)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Window ID is required.");
        AppWindow? window = SnapshotWindows().FirstOrDefault(item =>
            string.Equals(item.LogicalId, id, StringComparison.Ordinal));
        if (window == null)
            throw new InvalidOperationException($"Unknown window ID: {id}");
        if (!visible && window.Spec.HiddenBehavior == WindowHiddenBehavior.Unload)
        {
            DestroyDynamicWindow(id);
            return;
        }
        window.DesiredVisible = visible;
        ApplyWindowVisibility(window);
    }

    public WindowSnapshot CreateDynamicWindow(RendererWindowSpec spec)
    {
        if (!string.Equals(spec.ThreadGroup, _name, StringComparison.Ordinal))
            throw new InvalidOperationException($"Window group does not match runtime group '{_name}'.");
        if (SnapshotWindows().Any(window => string.Equals(window.LogicalId, spec.LogicalId, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Logical window ID is already registered: {spec.LogicalId}");
        AppWindow window = CreateWindow(spec);
        lock (_windowSync)
        {
            _registry.Add(window);
            _windows.Add(window);
        }
        return Snapshot(window);
    }

    public void DestroyDynamicWindow(string id)
    {
        AppWindow? window = SnapshotWindows().FirstOrDefault(item => string.Equals(item.LogicalId, id, StringComparison.Ordinal));
        if (window == null)
            throw new InvalidOperationException($"Unknown window ID: {id}");
        RemoveAndDestroy(window);
    }

    public WindowSnapshot ApplyWindowAction(string id, string action)
    {
        AppWindow? window = SnapshotWindows().FirstOrDefault(item => string.Equals(item.LogicalId, id, StringComparison.Ordinal));
        if (window == null)
            throw new InvalidOperationException($"Unknown window ID: {id}");
        switch (action.Trim().ToLowerInvariant())
        {
            case "show":
                window.DesiredVisible = true;
                ApplyWindowVisibility(window);
                break;
            case "hide":
                SetWindowVisible(id, false);
                break;
            case "toggle":
                SetWindowVisible(id, !window.DesiredVisible);
                break;
            case "focus":
                if (window.Spec.Placement == WindowPlacement.Desktop)
                    throw new InvalidOperationException("Desktop windows require forwarded input focus.");
                window.DesiredVisible = true;
                ApplyWindowVisibility(window);
                if (!_windowController.Focus(window))
                    throw new InvalidOperationException($"Could not focus window: {id}");
                break;
            case "blur":
                _windowController.Blur(window);
                break;
            case "pause":
                _runtime.TrySetSuspended(window, true);
                window.RendererSuspended = true;
                break;
            case "resume":
                _runtime.TrySetSuspended(window, false);
                window.RendererSuspended = false;
                break;
            case "reload":
                throw new InvalidOperationException("Window reload requires group reload.");
            default:
                throw new InvalidOperationException($"Unknown window action: {action}");
        }
        return Snapshot(window);
    }

    public IReadOnlyList<WindowSnapshot> UpdateFocus(IntPtr foreground)
    {
        var changed = new List<WindowSnapshot>();
        foreach (AppWindow window in SnapshotWindows())
        {
            bool focused = foreground != IntPtr.Zero &&
                (foreground == window.Hwnd || NativeMethods.IsDescendantOrSelf(foreground, window.Hwnd));
            if (focused == window.Focused)
                continue;
            window.Focused = focused;
            if (window.Spec.Notifications?.Focus == true || window.Spec.EffectiveNotifications.Focus)
                changed.Add(Snapshot(window));
            if (window.Spec.ZOrder == WindowZOrder.TopmostWhenFocused && window.Spec.Placement == WindowPlacement.TopLevel)
                _windowController.Reconcile(window);
        }
        return changed;
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        foreach (AppWindow window in _windows.ToArray())
        {
            if (window.State is AppWindowState.Closed or AppWindowState.Failed)
                continue;
            ApplySuspension(window);
            ApplyWindowVisibility(window, keepVisibleWhenPaused: true);
        }
        _timer.Interval = paused ? 1000 : 16;
    }

    public void SetFullscreenMonitor(IntPtr monitor)
    {
        _fullscreenMonitor = monitor;
        foreach (AppWindow window in _windows.ToArray())
        {
            window.FullscreenOccluded = monitor != IntPtr.Zero && window.MonitorHandle == monitor;
            if (window.Spec.SuspendOnFullscreen)
                ApplySuspension(window);
        }
        if (!_paused)
            _timer.Interval = _windows.Any(window => !window.RendererSuspended) ? 16 : 1000;
    }

    public void Reload()
    {
        Reload(_specFactory());
    }

    public void Reload(IReadOnlyList<RendererWindowSpec> specs)
    {
        List<RendererWindowSpec> candidateSpecs = specs.Where(spec => spec.ThreadGroup == _name).ToList();
        ReplaceWindows(candidateSpecs);
        _currentSpecs = candidateSpecs;
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

                if (window.Spec.Placement == WindowPlacement.Desktop)
                    _embedder.Reconcile(window);
                else
                    _windowController.Reconcile(window);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("runtime.error", new Dictionary<string, string?>
            {
                ["group"] = _name,
                ["exceptionType"] = ex.GetType().Name
            });
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
        }
        if (_paused)
            SetPaused(true);
    }

    private AppWindow CreateWindow(RendererWindowSpec spec)
    {
        AppWindow window = _runtime.CreateWindow(spec, _options.StartupTimeout, PostOwner);
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
            window.DesiredVisible = spec.InitialVisible;
            if (spec.Placement == WindowPlacement.Desktop)
            {
                _embedder.Embed(window);
                window.IsVisible = spec.InitialVisible;
            }
            else
            {
                window.IsVisible = spec.InitialVisible;
                _windowController.Reconcile(window);
                if (window.IsVisible)
                    _windowController.Show(window, spec.ActivateOnShow || spec.EffectiveInput.Focus == WindowFocusMode.OnShow);
            }
            _runtime.ShowWhenReady(window, _options.StartupTimeout);
            window.State = AppWindowState.Embedded;
            ApplySuspension(window);
            return window;
        }
        catch
        {
            DestroyWindow(window);
            throw;
        }
    }

    private void ApplyWindowVisibility(AppWindow window, bool keepVisibleWhenPaused = false)
    {
        if (window.DesiredVisible && (!_paused || keepVisibleWhenPaused))
        {
            ApplySuspension(window);
            if (window.Spec.Placement == WindowPlacement.Desktop)
            {
                NativeWindowApi.ShowWindow(window.Hwnd, NativeWindowApi.SW_SHOW);
                window.IsVisible = true;
            }
            else
            {
                _windowController.Show(window, window.Spec.EffectiveInput.Focus is WindowFocusMode.OnShow or WindowFocusMode.OnReveal);
            }
        }
        else
        {
            if (window.Spec.Placement == WindowPlacement.Desktop)
            {
                NativeWindowApi.ShowWindow(window.Hwnd, NativeWindowApi.SW_HIDE);
                window.IsVisible = false;
            }
            else
            {
                _windowController.Hide(window);
            }
            if (window.Spec.HiddenBehavior == WindowHiddenBehavior.Pause && !_paused)
            {
                _runtime.TrySetSuspended(window, true);
                window.RendererSuspended = true;
            }
        }
    }

    private static Rectangle GetCurrentBounds(AppWindow window)
    {
        if (window.Hwnd == IntPtr.Zero)
            return window.Spec.Bounds;
        NativeMethods.RECT rect = new();
        return NativeMethods.GetWindowRect(window.Hwnd, out rect)
            ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)
            : window.Spec.Bounds;
    }

    private WindowSnapshot Snapshot(AppWindow window) => new(
        window.LogicalId,
        window.Url,
        _name,
        window.Kind,
        window.Spec.Bounds,
        GetCurrentBounds(window),
        window.Spec.MonitorNumber,
        window.IsVisible,
        window.Focused,
        window.RendererSuspended,
        window.State,
        window.Spec.Placement,
        window.Spec.ZOrder);

    private void ApplySuspension(AppWindow window)
    {
        bool viewSuspended = window.Spec.SuspendOnFullscreen && window.FullscreenOccluded;
        window.DesiredSuspended = _paused || viewSuspended;
        if (_runtime.TrySetSuspended(window, _paused || viewSuspended))
            window.RendererSuspended = _paused || viewSuspended;
    }

    private void RemoveAndDestroy(AppWindow window)
    {
        _registry.MarkIntentionalClose(window);
        _registry.Remove(window);
        NativeWindowApi.RemoveAppBarReservation(window.Hwnd);
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

    private bool PostOwner(Action action)
    {
        if (_dispatcher.IsDisposed)
            return false;
        try
        {
            if (Environment.CurrentManagedThreadId == _ownerThreadId)
                action();
            else
                _dispatcher.BeginInvoke(action);
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
