using System.Windows.Forms;

namespace Aether;

public sealed class RendererHostContext : ApplicationContext
{
    private readonly HostOptions _options;
    private readonly SingleInstanceLease _lease;
    private readonly Control _dispatcher = new();
    private readonly NotifyIcon? _tray;
    private readonly ContextMenuStrip? _menu;
    private readonly CommandPipeServer _commandServer;
    private readonly InputRouter _input;
    private readonly FullscreenMonitor _fullscreenMonitor;
    private readonly RendererLogger _logger;
    private BackendConnectionManager? _backendManager;
    private readonly object _sync = new();
    private readonly Dictionary<string, RuntimeGroupHost> _groups = new(StringComparer.Ordinal);
    private ControlPipeChannel? _controlChannel;
    private IReadOnlyList<string> _configPaths;
    private bool _exiting;
    private RendererConfiguration _configuration;
    private IReadOnlyList<RendererWindowSpec> _specs;
    private Guid? _themeId;
    private Guid _themeActivationId;
    private long _themeGeneration;

    public RendererHostContext(HostOptions options, SingleInstanceLease lease)
    {
        _options = options;
        _lease = lease;
        _logger = new RendererLogger();
        _configPaths = options.ConfigPaths.ToArray();
        RendererPaths.Resolve(options.UltralightRoot).EnsureWritableRoot();
        _dispatcher.CreateControl();
        _configuration = _configPaths.Count == 0
            ? RendererConfiguration.Empty
            : RendererConfiguration.Load(_configPaths);
        _specs = _configPaths.Count == 0
            ? Array.Empty<RendererWindowSpec>()
            : _configuration.ResolveWindowSpecs();

        try
        {
            if (options.ControlReadHandle is { } readHandle && options.ControlWriteHandle is { } writeHandle)
            {
                _controlChannel = new ControlPipeChannel(readHandle, writeHandle, HandleControlRequest);
                _controlChannel.Disconnected += OnControlDisconnected;
                _logger.AttachControlChannel(_controlChannel);
                _controlChannel.Start();
            }
            _logger.Info("renderer.starting");

            _backendManager = new BackendConnectionManager(_controlChannel);

            foreach (KeyValuePair<string, RuntimeGroupHost> pair in CreateGroups(_specs))
                _groups.Add(pair.Key, pair.Value);

            _input = new InputRouter(GetDesktopWindows);
            _fullscreenMonitor = new FullscreenMonitor(OnFullscreenChanged);
            _fullscreenMonitor.Start();
            _commandServer = new CommandPipeServer(HandleCommand);
            _commandServer.Start();
            if (!options.NoTray)
            {
                _menu = BuildMenu();
                _tray = new NotifyIcon
                {
                    Text = "Aether",
                    Icon = SystemIcons.Application,
                    ContextMenuStrip = _menu,
                    Visible = options.InitialSystemTrayAction is not (SystemTrayAction.Hide or SystemTrayAction.Toggle)
                };
            }
            _logger.Info("renderer.ready", new Dictionary<string, string?>
            {
                ["configuredWindows"] = _specs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["controllerConnected"] = (_controlChannel != null).ToString().ToLowerInvariant()
            });
        }
        catch
        {
            StopGroups();
            _backendManager?.Dispose();
            _logger.Dispose();
            _controlChannel?.Dispose();
            _dispatcher.Dispose();
            throw;
        }
    }

    private Dictionary<string, RuntimeGroupHost> CreateGroups(IReadOnlyList<RendererWindowSpec> specs)
    {
        var result = new Dictionary<string, RuntimeGroupHost>(StringComparer.Ordinal);
        IReadOnlyList<RendererWindowSpec> snapshot = specs.ToArray();
        try
        {
            foreach (string groupName in snapshot.Select(spec => spec.ThreadGroup).Distinct(StringComparer.Ordinal))
            {
                var group = new RuntimeGroupHost(groupName, _options, () => snapshot, _logger, _backendManager, _controlChannel);
                group.Start();
                result.Add(groupName, group);
            }
            return result;
        }
        catch
        {
            foreach (RuntimeGroupHost group in result.Values)
                group.Dispose();
            throw;
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show menu", null, (_, _) => ExecuteUiCommand(new(RendererCommandKind.ShowMenu)));
        menu.Items.Add("Hide menu", null, (_, _) => ExecuteUiCommand(new(RendererCommandKind.HideMenu)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Pause all groups", null, (_, _) => ExecuteUiCommand(new(RendererCommandKind.Pause, All: true)));
        menu.Items.Add("Resume all groups", null, (_, _) => ExecuteUiCommand(new(RendererCommandKind.Resume, All: true)));
        menu.Items.Add("Reload all groups", null, (_, _) => ExecuteUiCommand(new(RendererCommandKind.Reload, All: true)));
        menu.Items.Add("Print status", null, (_, _) => ExecuteUiCommand(new(RendererCommandKind.Status, All: true)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExecuteUiCommand(new(RendererCommandKind.Exit)));
        return menu;
    }

    private void ExecuteUiCommand(RendererCommand command)
    {
        try
        {
            string response = HandleCommand(command);
            if (response.StartsWith("ERR ", StringComparison.Ordinal))
                MessageBox.Show(response[4..], "Aether", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else if (command.Kind == RendererCommandKind.Status)
                MessageBox.Show(response.StartsWith("OK ", StringComparison.Ordinal) ? response[3..] : response,
                    "Aether status", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Aether", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string HandleCommand(RendererCommand command)
    {
        string? identityError = ValidateThemeIdentity(command);
        if (identityError != null)
            return identityError;

        _logger.Info("command.received", new Dictionary<string, string?>
        {
            ["command"] = command.Kind.ToString(),
            ["all"] = command.All.ToString().ToLowerInvariant(),
            ["hasGroup"] = (command.Group != null).ToString().ToLowerInvariant(),
            ["hasTarget"] = (command.TargetId != null).ToString().ToLowerInvariant()
        });
        try
        {
            string response = command.Kind switch
            {
                RendererCommandKind.ApplyConfig => ApplyConfiguration(command.ConfigPaths ?? Array.Empty<string>(), command.ThemeId, command.ThemeActivationId, command.ThemeGeneration),
                RendererCommandKind.SystemTray => SetSystemTrayVisibility(command.TrayAction),
                RendererCommandKind.ShowMenu => SetMenuVisibility(true),
                RendererCommandKind.HideMenu => SetMenuVisibility(false),
                RendererCommandKind.ToggleMenu => ToggleMenuVisibility(),
                RendererCommandKind.HideWindow => SetWindowVisibility(command.TargetId, false),
                RendererCommandKind.RevealWindow => SetWindowVisibility(command.TargetId, true),
                RendererCommandKind.Show => ShowTopic(command.Topic),
                RendererCommandKind.Exit => RequestExit(),
                RendererCommandKind.Pause => ApplyPause(command, true),
                RendererCommandKind.Resume => ApplyPause(command, false),
                RendererCommandKind.Status => GetStatus(command),
                RendererCommandKind.Reload => ReloadConfiguration(command),
                _ => "ERR Unsupported runtime command."
            };
            _logger.Log(
                response.StartsWith("ERR ", StringComparison.Ordinal) ? RendererLogLevel.Warn : RendererLogLevel.Info,
                response.StartsWith("ERR ", StringComparison.Ordinal) ? "command.failed" : "command.succeeded",
                new Dictionary<string, string?>
                {
                    ["command"] = command.Kind.ToString(),
                    ["error"] = response.StartsWith("ERR ", StringComparison.Ordinal).ToString().ToLowerInvariant()
                });
            return response;
        }
        catch (Exception ex)
        {
            _logger.Error("command.failed", new Dictionary<string, string?>
            {
                ["command"] = command.Kind.ToString(),
                ["exceptionType"] = ex.GetType().Name
            });
            return $"ERR {ex.Message}";
        }
    }

    private string? ValidateThemeIdentity(RendererCommand command)
    {
        if (_themeGeneration == 0 || command.ThemeGeneration == null)
            return null;

        if (command.Kind == RendererCommandKind.ApplyConfig)
        {
            if (command.ThemeGeneration <= _themeGeneration)
                return "ERR Theme session is stale.";
            return null;
        }

        if (command.ThemeGeneration != _themeGeneration ||
            (command.ThemeActivationId is { } activationId && activationId != _themeActivationId) ||
            (command.ThemeId is { } themeId && themeId != _themeId))
            return "ERR Theme session is stale.";
        return null;
    }

    private string ApplyConfiguration(IReadOnlyList<string> paths, Guid? themeId = null, Guid? activationId = null, long? generation = null)
    {
        if (paths.Count == 0)
            return "ERR At least one --config path is required.";

        RendererConfiguration candidate = RendererConfiguration.Load(paths);
        IReadOnlyList<RendererWindowSpec> candidateSpecs = candidate.ResolveWindowSpecs();
        IReadOnlyList<RendererWindowSpec> oldSpecs;
        RendererConfiguration oldConfiguration;
        IReadOnlyList<string> oldPaths;
        Guid? oldThemeId;
        Guid oldActivationId;
        long oldGeneration;
        lock (_sync)
        {
            oldSpecs = _specs.ToArray();
            oldConfiguration = _configuration;
            oldPaths = _configPaths;
            oldThemeId = _themeId;
            oldActivationId = _themeActivationId;
            oldGeneration = _themeGeneration;
        }

        StopGroups();
        long candidateGeneration = generation ?? checked(_themeGeneration + 1);
        _backendManager?.RotateThemeScope(themeId, candidateGeneration);
        Dictionary<string, RuntimeGroupHost> candidateGroups;
        try
        {
            candidateGroups = CreateGroups(candidateSpecs);
        }
        catch
        {
            try
            {
                _backendManager?.RotateThemeScope(oldThemeId, oldGeneration == 0 ? null : oldGeneration);
                Dictionary<string, RuntimeGroupHost> restoredGroups = CreateGroups(oldSpecs);
                lock (_sync)
                {
                    foreach (KeyValuePair<string, RuntimeGroupHost> pair in restoredGroups)
                        _groups.Add(pair.Key, pair.Value);
                    _configuration = oldConfiguration;
                    _specs = oldSpecs;
                    _configPaths = oldPaths;
                    _themeId = oldThemeId;
                    _themeActivationId = oldActivationId;
                    _themeGeneration = oldGeneration;
                }
            }
            catch (Exception restoreError)
            {
                Console.Error.WriteLine($"[Reload] old configuration restore failed: {restoreError.Message}");
            }
            throw;
        }

        lock (_sync)
        {
            foreach (KeyValuePair<string, RuntimeGroupHost> pair in candidateGroups)
                _groups.Add(pair.Key, pair.Value);
            _configuration = candidate;
            _specs = candidateSpecs;
            _configPaths = paths.ToArray();
            _themeId = themeId;
            _themeActivationId = activationId ?? Guid.NewGuid();
            _themeGeneration = candidateGeneration;
        }

        _logger.Info("config.reload.succeeded", new Dictionary<string, string?>
        {
            ["windowCount"] = candidateSpecs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        _controlChannel?.TrySendEvent("config.changed", new
        {
            files = _configPaths,
            themeId = _themeId,
            themeActivationId = ActiveThemeActivationId,
            themeGeneration = ActiveThemeGeneration
        });
        return "OK configuration applied";
    }

    private string ReloadConfiguration(RendererCommand command)
    {
        if (_configPaths.Count == 0)
            return "ERR No active configuration.";
        if (command.All)
            return ApplyConfiguration(_configPaths, _themeId, _themeActivationId, _themeGeneration);

        IReadOnlyList<RuntimeGroupHost> targets = ResolveTargets(command);
        foreach (RuntimeGroupHost group in targets)
            group.InvokeReload();
        return $"OK reloaded {targets.Count} group(s)";
    }

    private string SetMenuVisibility(bool visible)
    {
        RendererWindowSpec? spec = _specs.FirstOrDefault(item => item.Kind == WindowKind.StartMenu);
        if (spec == null)
            return "ERR No menu window is configured.";
        if (!_groups.TryGetValue(spec.ThreadGroup, out RuntimeGroupHost? host))
            return $"ERR Menu group '{spec.ThreadGroup}' is not running.";
        if (visible) host.InvokeShowMenu(); else host.InvokeHideMenu();
        return $"OK {(visible ? "showed" : "hid")} menu";
    }

    private string ToggleMenuVisibility()
    {
        RendererWindowSpec? spec = _specs.FirstOrDefault(item => item.Kind == WindowKind.StartMenu);
        if (spec == null)
            return "ERR No menu window is configured.";
        if (!_groups.TryGetValue(spec.ThreadGroup, out RuntimeGroupHost? host))
            return $"ERR Menu group '{spec.ThreadGroup}' is not running.";
        bool visible = host.InvokeToggleMenu();
        return $"OK {(visible ? "showed" : "hid")} menu";
    }

    private string SetSystemTrayVisibility(SystemTrayAction action)
    {
        bool visible = false;
        _dispatcher.Invoke(() =>
        {
            if (_tray == null)
                throw new InvalidOperationException("Aether tray is disabled for this process.");
            visible = action switch
            {
                SystemTrayAction.Show => true,
                SystemTrayAction.Hide => false,
                SystemTrayAction.Toggle => !_tray.Visible,
                _ => throw new ArgumentOutOfRangeException(nameof(action))
            };
            _tray.Visible = visible;
        });
        return $"OK system tray {(visible ? "shown" : "hidden")}";
    }

    private string SetWindowVisibility(string? id, bool visible)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "ERR Window ID is required.";
        RendererWindowSpec? spec = _specs.FirstOrDefault(item => string.Equals(item.LogicalId, id, StringComparison.Ordinal));
        if (spec == null)
            return $"ERR Unknown window ID: {id}";
        if (!_groups.TryGetValue(spec.ThreadGroup, out RuntimeGroupHost? host))
            return $"ERR Window group '{spec.ThreadGroup}' is not running.";
        if (visible) host.InvokeRevealWindow(id); else host.InvokeHideWindow(id);
        _controlChannel?.TrySendEvent("window.changed", new
        {
            id,
            visible,
            themeId = _themeId,
            themeActivationId = ActiveThemeActivationId,
            themeGeneration = ActiveThemeGeneration
        });
        return $"OK {(visible ? "revealed" : "hid")} {id}";
    }

    private string ShowTopic(string? topic)
    {
        return topic switch
        {
            "config" => ControlPipeCodec.SerializeJson(BuildConfigPayload()),
            "windows" => ControlPipeCodec.SerializeJson(new { windows = GetWindowSnapshots() }),
            "paths" => ControlPipeCodec.SerializeJson(RendererPaths.Resolve(_options.UltralightRoot)),
            "pipes" => ControlPipeCodec.SerializeJson(new
            {
                control = _controlChannel?.State ?? "standalone",
                backends = _backendManager?.Snapshot() ?? Array.Empty<BackendPipeSnapshot>(),
                themeId = _themeId,
                themeActivationId = ActiveThemeActivationId,
                themeGeneration = ActiveThemeGeneration
            }),
            _ => "ERR Show topic must be pipes, config, windows, or paths."
        };
    }

    private string RequestExit()
    {
        _dispatcher.BeginInvoke((Action)ExitHost);
        return "OK exit requested";
    }

    private void OnControlDisconnected()
    {
        if (_exiting)
            return;
        try { _dispatcher.BeginInvoke((Action)ExitHost); }
        catch (InvalidOperationException) { }
    }

    private ControlResponse HandleControlRequest(ControlRequest request)
    {
        if (request.Method == "GET" && request.Path == "/v1/pipes")
            return new ControlResponse(request.Id, 200, new
            {
                control = _controlChannel?.State ?? "standalone",
                backends = _backendManager?.Snapshot() ?? Array.Empty<BackendPipeSnapshot>(),
                themeId = _themeId,
                themeActivationId = ActiveThemeActivationId,
                themeGeneration = ActiveThemeGeneration
            });
        if (request.Method == "PUT" && request.Path == "/v1/logging/batch-limits")
        {
            if (_logger.TryApplyPolicy(request.Body, out RendererLogPolicy policy, out int status, out string error))
                return new ControlResponse(request.Id, status, policy.ToPayload());
            return new ControlResponse(request.Id, status,
                Error: new ControlError(status >= 500 ? "logging_policy_apply_failed" : "logging_policy_invalid", error));
        }
        return new ControlResponse(request.Id, 404, Error: new ControlError("route_not_found", "Unknown control route."));
    }

    private object BuildConfigPayload() => new
    {
        files = _configPaths,
        sections = new { wallpaper = _configuration.Wallpapers, widgets = _configuration.Widgets, menu = _configuration.Menu },
        resolvedWindows = _specs,
        themeId = _themeId,
        themeActivationId = ActiveThemeActivationId,
        themeGeneration = ActiveThemeGeneration
    };

    private Guid? ActiveThemeActivationId => _themeGeneration == 0 ? null : _themeActivationId;
    private long? ActiveThemeGeneration => _themeGeneration == 0 ? null : _themeGeneration;

    private string ApplyPause(RendererCommand command, bool paused)
    {
        IReadOnlyList<RuntimeGroupHost> targets = ResolveTargets(command);
        foreach (RuntimeGroupHost group in targets)
            group.InvokePause(paused);
        return $"OK {(paused ? "paused" : "resumed")} {targets.Count} group(s)";
    }

    private string GetStatus(RendererCommand command)
    {
        IReadOnlyList<RuntimeGroupHost> targets = ResolveTargets(command);
        string status = string.Join(Environment.NewLine, targets.Select(group => group.InvokeStatus()));
        return $"OK {status}";
    }

    private IReadOnlyList<RuntimeGroupHost> ResolveTargets(RendererCommand command)
    {
        lock (_sync)
        {
            if (command.All)
                return _groups.Values.ToArray();
            if (command.Group != null)
            {
                if (command.Group.Length == 0)
                    throw new InvalidOperationException("Group name must not be empty.");
                if (!_groups.TryGetValue(command.Group, out RuntimeGroupHost? host))
                    throw new InvalidOperationException($"Unknown runtime group: {command.Group}");
                return new[] { host };
            }
            if (_groups.Count != 1)
                throw new InvalidOperationException("A group name is required when more than one group is running.");
            return _groups.Values.ToArray();
        }
    }

    private IReadOnlyList<WallpaperWindow> GetDesktopWindows()
    {
        RuntimeGroupHost[] groups;
        lock (_sync) groups = _groups.Values.ToArray();
        return groups.SelectMany(group => group.GetDesktopWindows()).ToArray();
    }

    private IReadOnlyList<WindowSnapshot> GetWindowSnapshots()
    {
        RuntimeGroupHost[] groups;
        lock (_sync) groups = _groups.Values.ToArray();
        return groups.SelectMany(group => group.GetWindowSnapshots()).ToArray();
    }

    private void OnFullscreenChanged(IntPtr monitor)
    {
        RuntimeGroupHost[] groups;
        lock (_sync) groups = _groups.Values.ToArray();
        foreach (RuntimeGroupHost group in groups)
        {
            try { group.InvokeFullscreenMonitor(monitor); }
            catch (Exception ex) { Console.Error.WriteLine($"[Fullscreen] {ex.Message}"); }
        }
    }

    private void ExitHost()
    {
        if (_exiting)
            return;
        _exiting = true;
        _input.Dispose();
        _fullscreenMonitor.Dispose();
        _commandServer.Dispose();
        _logger.Info("renderer.stopping");
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _menu?.Dispose();
        StopGroups();
        _backendManager?.Dispose();
        _logger.Dispose();
        _controlChannel?.Dispose();
        _dispatcher.Dispose();
        Application.ExitThread();
    }

    private void StopGroups()
    {
        RuntimeGroupHost[] groups;
        lock (_sync)
        {
            groups = _groups.Values.ToArray();
            _groups.Clear();
        }
        foreach (RuntimeGroupHost group in groups)
            group.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_exiting)
            ExitHost();
        base.Dispose(disposing);
    }
}
