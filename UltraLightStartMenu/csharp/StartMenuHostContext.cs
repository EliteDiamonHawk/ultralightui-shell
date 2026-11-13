namespace UltraLightStartMenu;

public sealed class StartMenuHostContext : ApplicationContext
{
    private readonly HostOptions _options;
    private readonly StartMenuConfiguration _configuration;
    private readonly SingleInstanceLease _instance;
    private readonly CommandPipeServer _commandServer;
    private readonly UltralightRuntime _runtime;
    private readonly StartMenuWindowController _windowController;
    private readonly StartMenuVisibilityController _visibility;
    private readonly StartMenuTrayController _tray;
    private readonly AutoHideMonitor _autoHideMonitor;
    private readonly WindowTopologyMonitor _topologyMonitor;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Control _eventDispatcher = new();

    private ReplacementBatch? _pending;
    private bool _exiting;
    private DateTimeOffset _nextReconcile = DateTimeOffset.MinValue;

    public StartMenuHostContext(HostOptions options)
    {
        _options = options;
        _configuration = options.LoadConfiguration();
        _instance = SingleInstanceLease.Acquire();

        try
        {
            StartMenuWindowSpec initialSpec = _configuration.ResolveWindowSpec();
            _runtime = new UltralightRuntime(options.UltralightRoot);
            _windowController = new StartMenuWindowController(options.Verbose);
            _visibility = new StartMenuVisibilityController(_windowController);
            _topologyMonitor = new WindowTopologyMonitor();

            _eventDispatcher.CreateControl();
            _autoHideMonitor = new AutoHideMonitor(
                _eventDispatcher,
                () => _configuration.StartMenu.AutoHide,
                () => _visibility.IsVisible,
                () => _visibility.Window?.Hwnd ?? IntPtr.Zero,
                () => HandleCommand(StartMenuCommand.Hide));
            _autoHideMonitor.Start();

            _tray = new StartMenuTrayController(
                () => HandleCommand(StartMenuCommand.Show),
                () => HandleCommand(StartMenuCommand.Hide),
                ReloadStartMenu,
                PrintStatus,
                ExitHost);
            _tray.Update(_visibility.IsVisible);

            _timer = new System.Windows.Forms.Timer { Interval = 16 };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();

            _commandServer = new CommandPipeServer();
            _commandServer.Start(_eventDispatcher, HandleCommand);
            BeginReplacement(initialSpec, null);
        }
        catch
        {
            _instance.Dispose();
            throw;
        }
    }

    private void Tick()
    {
        if (_exiting)
            return;

        try
        {
            _runtime.Pump();
            PollWindow();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Runtime] tick failed: {ex.Message}");
        }
    }

    private void PollWindow()
    {
        if (_pending != null)
        {
            PollPending();
            return;
        }

        if (_topologyMonitor.HasChanged())
        {
            BeginReplacement(_configuration.ResolveWindowSpec(), _visibility.Window);
            return;
        }

        UltralightWindow? window = _visibility.Window;
        if (window == null || window.State != UltralightWindowState.Ready ||
            DateTimeOffset.UtcNow < _nextReconcile)
            return;
        _nextReconcile = DateTimeOffset.UtcNow.AddMilliseconds(_options.ReconcileIntervalMs);

        if (!NativeWindowApi.IsWindow(window.Hwnd))
        {
            Console.Error.WriteLine($"[Lifecycle] Start-menu window lost: {window.LogicalId}");
            BeginReplacement(_configuration.ResolveWindowSpec(), window);
            return;
        }

        try
        {
            _visibility.Reconcile();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Window] reconciliation failed: {ex.Message}");
        }
    }

    private void PollPending()
    {
        ReplacementBatch pending = _pending!;
        if (pending.Window.IsExpired(DateTimeOffset.UtcNow))
        {
            pending.Window.State = UltralightWindowState.Failed;
            pending.Window.FailureReason = "window did not become ready before startup timeout";
            AbortPending();
            return;
        }

        if (!NativeWindowApi.IsWindow(pending.Window.Hwnd))
        {
            pending.Window.State = UltralightWindowState.Failed;
            pending.Window.FailureReason = "native window handle became invalid";
            AbortPending();
            return;
        }

        try
        {
            _runtime.ShowWhenReady(pending.Window, _options.StartupTimeout);
            pending.Window.State = UltralightWindowState.Ready;
            _visibility.Prepare(pending.Window);
            CommitPending();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Window] start-menu setup failed: {ex.Message}");
            pending.Window.FailureReason = ex.Message;
            AbortPending();
        }
    }

    private void BeginReplacement(StartMenuWindowSpec spec, UltralightWindow? target)
    {
        if (_pending != null)
        {
            Console.WriteLine("[Replace] request ignored while another replacement is pending.");
            return;
        }

        try
        {
            UltralightWindow window = _runtime.CreateWindow(spec, _options.StartupTimeout);
            _pending = new ReplacementBatch(window, target);
            Console.WriteLine($"[Ultralight] created {spec.LogicalId}: {spec.Url}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Replace] creation failed: {ex.Message}");
        }
    }

    private void CommitPending()
    {
        ReplacementBatch pending = _pending!;
        if (pending.Target != null)
        {
            _visibility.Detach(pending.Target);
            CloseWindow(pending.Target);
        }

        _visibility.Attach(pending.Window);
        _pending = null;
        _tray.Update(_visibility.IsVisible);
        Console.WriteLine("[Replace] committed new Ultralight start-menu window.");
    }

    private void AbortPending()
    {
        ReplacementBatch pending = _pending!;
        _runtime.DestroyWindow(pending.Window);
        _pending = null;
        Console.Error.WriteLine($"[Replace] aborted: {pending.Window.FailureReason ?? "unknown failure"}; existing window retained.");
    }

    private void ReloadStartMenu()
    {
        try
        {
            BeginReplacement(_configuration.ResolveWindowSpec(), _visibility.Window);
        }
        catch (StartMenuConfigurationException ex)
        {
            Console.Error.WriteLine($"[Config] reload rejected: {ex.Message}");
            MessageBox.Show(ex.Message, "UltraLightStartMenu - Configuration Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void HandleCommand(StartMenuCommand command)
    {
        if (_exiting || command == StartMenuCommand.Run)
            return;

        try
        {
            _visibility.SetDesiredVisible(command == StartMenuCommand.Show);
            _tray.Update(_visibility.IsVisible);
            string state = _visibility.DesiredVisible ? "shown" : "hidden";
            Console.WriteLine($"[Visibility] start menu {state}.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not {command.ToString().ToLowerInvariant()} the start menu: {ex.Message}", ex);
        }
    }

    private void PrintStatus()
    {
        UltralightWindow? window = _visibility.Window;
        if (window == null)
        {
            Console.WriteLine("[Status] start-menu: not created");
            return;
        }

        Console.WriteLine($"[Status] {window.LogicalId}: {window.State}, " +
            $"HWND={FormatHwnd(window.Hwnd)}, visible={_visibility.IsVisible}, " +
            $"topmost-placement={_visibility.TopmostPlacementApplied}, url={window.Spec.Url}");
    }

    private void ExitHost()
    {
        if (_exiting)
            return;

        _exiting = true;
        _autoHideMonitor.Dispose();

        if (_pending != null)
        {
            _runtime.DestroyWindow(_pending.Window);
            _pending = null;
        }
        if (_visibility.Window != null)
        {
            UltralightWindow window = _visibility.Window;
            _visibility.Detach(window);
            CloseWindow(window);
        }

        _timer.Stop();
        _timer.Dispose();
        _commandServer.Dispose();
        _tray.Dispose();
        _eventDispatcher.Dispose();
        _runtime.Dispose();
        _instance.Dispose();
        ExitThread();
    }

    private void CloseWindow(UltralightWindow window) => _runtime.DestroyWindow(window);

    private static string FormatHwnd(IntPtr hwnd) => hwnd == IntPtr.Zero ? "<none>" : $"0x{hwnd.ToInt64():X}";

    private sealed record ReplacementBatch(UltralightWindow Window, UltralightWindow? Target);
}
