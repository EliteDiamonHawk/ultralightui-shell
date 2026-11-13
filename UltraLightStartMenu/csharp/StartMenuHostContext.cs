namespace UltraLightStartMenu;

public sealed class StartMenuHostContext : ApplicationContext
{
    private readonly HostOptions _options;
    private readonly StartMenuConfiguration _configuration;
    private readonly StartMenuInstance _instance;
    private readonly UltralightRuntime _runtime;
    private readonly StartMenuWindowController _windowController = new();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Control _eventDispatcher = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly List<IntPtr> _winEventHooks = new();
    private readonly NativeMethods.WinEventDelegate _winEventProc;

    private ToolStripMenuItem? _showItem;
    private ToolStripMenuItem? _hideItem;
    private UltralightWindow? _window;
    private ReplacementBatch? _pending;
    private bool _desiredVisible = true;
    private bool _exiting;
    private string _topologySignature;
    private DateTimeOffset _nextReconcile = DateTimeOffset.MinValue;

    private const int ActivePumpIntervalMs = 16;

    public StartMenuHostContext(HostOptions options)
    {
        _options = options;
        _configuration = options.LoadConfiguration();
        _instance = StartMenuInstance.Acquire();

        try
        {
            _runtime = new UltralightRuntime(options.UltralightRoot);
            _topologySignature = GetTopologySignature();

            _menu = new ContextMenuStrip();
            _tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "UltraLightStartMenu",
                Visible = true,
                ContextMenuStrip = _menu
            };
            BuildMenu();

            _eventDispatcher.CreateControl();
            _winEventProc = WinEventCallback;
            InstallForegroundHook();

            _timer = new System.Windows.Forms.Timer { Interval = ActivePumpIntervalMs };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();

            _instance.Start(_eventDispatcher, HandleCommand);
            BeginReplacement(_configuration.ResolveWindowSpec(), null);
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

    private void BuildMenu()
    {
        _menu.Items.Clear();

        _showItem = new ToolStripMenuItem("Show start menu", null, (_, _) => HandleCommand(StartMenuCommand.Show));
        _hideItem = new ToolStripMenuItem("Hide start menu", null, (_, _) => HandleCommand(StartMenuCommand.Hide));
        _menu.Items.Add(_showItem);
        _menu.Items.Add(_hideItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Reload start menu", null, (_, _) => ReloadStartMenu()));
        _menu.Items.Add(new ToolStripMenuItem("Print status", null, (_, _) => PrintStatus()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitHost()));
        UpdateMenuState();
    }

    private void PollWindow()
    {
        if (_pending != null)
        {
            PollPending();
            return;
        }

        if (GetTopologySignature() != _topologySignature)
        {
            _topologySignature = GetTopologySignature();
            BeginReplacement(_configuration.ResolveWindowSpec(), _window);
            return;
        }

        if (_window == null || _window.State != UltralightWindowState.Ready ||
            DateTimeOffset.UtcNow < _nextReconcile)
            return;
        _nextReconcile = DateTimeOffset.UtcNow.AddMilliseconds(_options.ReconcileIntervalMs);

        if (!NativeMethods.IsWindow(_window.Hwnd))
        {
            Console.Error.WriteLine($"[Lifecycle] Start-menu window lost: {_window.LogicalId}");
            BeginReplacement(_configuration.ResolveWindowSpec(), _window);
            return;
        }

        try
        {
            _windowController.Reconcile(_window);
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

        if (!NativeMethods.IsWindow(pending.Window.Hwnd))
        {
            pending.Window.State = UltralightWindowState.Failed;
            pending.Window.FailureReason = "native window handle became invalid";
            AbortPending();
            return;
        }

        try
        {
            _windowController.Reconcile(pending.Window);
            _runtime.ShowWhenReady(pending.Window, _options.StartupTimeout);
            pending.Window.State = UltralightWindowState.Ready;
            if (_desiredVisible)
                _windowController.Show(pending.Window);
            else
                _windowController.Hide(pending.Window);
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
            CloseWindow(pending.Target);

        _window = pending.Window;
        _pending = null;
        UpdateMenuState();
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
            BeginReplacement(_configuration.ResolveWindowSpec(), _window);
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

        _desiredVisible = command == StartMenuCommand.Show;
        if (_window is { State: UltralightWindowState.Ready } window)
        {
            try
            {
                if (_desiredVisible)
                    _windowController.Show(window);
                else
                    _windowController.Hide(window);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not {command.ToString().ToLowerInvariant()} the start menu: {ex.Message}", ex);
            }
        }

        UpdateMenuState();
        Console.WriteLine($"[Visibility] start menu {_desiredVisible switch { true => "shown", false => "hidden" }}.");
    }

    private void UpdateMenuState()
    {
        bool visible = _window?.IsVisible ?? _desiredVisible;
        if (_showItem != null) _showItem.Enabled = !visible;
        if (_hideItem != null) _hideItem.Enabled = visible;
    }

    private void PrintStatus()
    {
        if (_window == null)
        {
            Console.WriteLine("[Status] start-menu: not created");
            return;
        }

        Console.WriteLine($"[Status] {_window.LogicalId}: {_window.State}, " +
            $"HWND={FormatHwnd(_window.Hwnd)}, visible={_window.IsVisible}, " +
            $"topmost={_windowController.IsTopmost(_window)}, url={_window.Spec.Url}");
    }

    private void InstallForegroundHook()
    {
        IntPtr hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _winEventProc,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
        if (hook != IntPtr.Zero)
            _winEventHooks.Add(hook);
        else
            Console.Error.WriteLine("[Window] Could not install foreground monitoring hook.");
    }

    private void WinEventCallback(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (_exiting || eventType != NativeMethods.EVENT_SYSTEM_FOREGROUND ||
            !_configuration.StartMenu.AutoHide || _window?.IsVisible != true || IsOwnedWindow(hwnd))
            return;

        try
        {
            _eventDispatcher.BeginInvoke((Action)(() =>
            {
                if (!_exiting && _configuration.StartMenu.AutoHide && _window?.IsVisible == true && !IsOwnedWindow(hwnd))
                    HandleCommand(StartMenuCommand.Hide);
            }));
        }
        catch (InvalidOperationException) { }
    }

    private bool IsOwnedWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;
        if (_window != null && NativeMethods.IsDescendantOrSelf(hwnd, _window.Hwnd))
            return true;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        return processId == (uint)Environment.ProcessId;
    }

    private void ExitHost()
    {
        if (_exiting)
            return;

        _exiting = true;
        foreach (IntPtr hook in _winEventHooks)
            NativeMethods.UnhookWinEvent(hook);
        _winEventHooks.Clear();

        if (_pending != null)
        {
            _runtime.DestroyWindow(_pending.Window);
            _pending = null;
        }
        if (_window != null)
            CloseWindow(_window);

        _timer.Stop();
        _timer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        _eventDispatcher.Dispose();
        _runtime.Dispose();
        _instance.Dispose();
        ExitThread();
    }

    private void CloseWindow(UltralightWindow window)
    {
        _runtime.DestroyWindow(window);
    }

    private static string GetTopologySignature() =>
        string.Join("|", Screen.AllScreens.Select(screen => $"{screen.DeviceName}:{screen.Bounds}"));

    private static string FormatHwnd(IntPtr hwnd) => hwnd == IntPtr.Zero ? "<none>" : $"0x{hwnd.ToInt64():X}";

    private sealed record ReplacementBatch(UltralightWindow Window, UltralightWindow? Target);
}
