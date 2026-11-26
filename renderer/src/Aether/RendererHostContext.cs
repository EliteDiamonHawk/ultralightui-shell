using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace Aether;

public sealed class RendererHostContext : ApplicationContext
{
    private HostOptions _options;
    private readonly SingleInstanceLease _lease;
    private readonly Control _dispatcher = new();
    private readonly NotifyIcon? _tray;
    private readonly CommandPipeServer _commandServer;
    private readonly InputRouter _input;
    private readonly FullscreenMonitor _fullscreenMonitor;
    private readonly WindowFocusMonitor _focusMonitor;
    private readonly RendererLogger _logger;
    private BackendConnectionManager? _backendManager;
    private readonly object _sync = new();
    // Config swaps must not overlap across control and command pipes.
    private readonly object _configurationSync = new();
    private readonly Dictionary<string, RuntimeGroupHost> _groups = new(StringComparer.Ordinal);
    private ControlPipeChannel? _controlChannel;
    private IReadOnlyList<string> _configPaths;
    private string? _configJson;
    private bool _exiting;
    private readonly bool _managedStartup;
    private bool _startupConfigured;
    private RendererConfiguration _configuration;
    private IReadOnlyList<RendererWindowSpec> _specs;
    private Guid? _themeId;
    private Guid _themeActivationId;
    private long _themeGeneration;

    public RendererHostContext(HostOptions options, SingleInstanceLease lease)
    {
        _options = options;
        _lease = lease;
        _managedStartup = options.HasInheritedControlChannel;
        _logger = new RendererLogger();
        _configPaths = Array.Empty<string>();
        _configJson = null;
        _configuration = RendererConfiguration.Empty;
        _specs = Array.Empty<RendererWindowSpec>();
        _dispatcher.CreateControl();

        try
        {
            if (_managedStartup && options.ControlReadHandle is { } readHandle && options.ControlWriteHandle is { } writeHandle)
            {
                _controlChannel = new ControlPipeChannel(readHandle, writeHandle, HandleControlRequest);
                _controlChannel.Disconnected += OnControlDisconnected;
                _logger.AttachControlChannel(_controlChannel);
                _controlChannel.Start();
                _controlChannel.TrySendEvent("renderer.transport.ready", new { protocolVersion = 1 });
            }
            _logger.Info("renderer.starting");

            _backendManager = new BackendConnectionManager(_controlChannel);

            if (!_managedStartup)
            {
                RendererPaths.Resolve(options.UltralightRoot).EnsureWritableRoot();
                _configPaths = options.ConfigPaths.ToArray();
                _configuration = _configPaths.Count == 0
                    ? RendererConfiguration.Empty
                    : RendererConfiguration.Load(_configPaths);
                _specs = _configPaths.Count == 0
                    ? Array.Empty<RendererWindowSpec>()
                    : _configuration.ResolveWindowSpecs();
            }

            _input = new InputRouter(GetDesktopWindows);
            _fullscreenMonitor = new FullscreenMonitor(OnFullscreenChanged);
            _fullscreenMonitor.Start();
            _focusMonitor = new WindowFocusMonitor(OnForegroundChanged);
            _focusMonitor.Start();
            _commandServer = new CommandPipeServer(HandleCommand);
            _commandServer.Start();

            foreach (KeyValuePair<string, RuntimeGroupHost> pair in CreateGroups(_specs))
                _groups.Add(pair.Key, pair.Value);

            if (!options.NoTray && !_managedStartup)
            {
                _tray = new NotifyIcon
                {
                    Text = "Aether",
                    Icon = SystemIcons.Application,
                    Visible = options.InitialSystemTrayAction is not (SystemTrayAction.Hide or SystemTrayAction.Toggle)
                };
            }
            if (!_managedStartup)
            {
                _startupConfigured = true;
                _logger.Info("renderer.ready", new Dictionary<string, string?>
                {
                    ["configuredWindows"] = _specs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["controllerConnected"] = (_controlChannel != null).ToString().ToLowerInvariant()
                });
            }
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
                var group = new RuntimeGroupHost(groupName, _options, () => snapshot, _logger, _backendManager, _controlChannel, HandleCommand);
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
                RendererCommandKind.ApplyConfig => ApplyConfiguration(
                    command.ConfigPaths ?? Array.Empty<string>(),
                    command.ThemeId,
                    command.ThemeActivationId,
                    command.ThemeGeneration,
                    command.ConfigJson,
                    command.ConfigSourcePaths),
                RendererCommandKind.SystemTray => SetSystemTrayVisibility(command.TrayAction),
                RendererCommandKind.HideWindow => SetWindowVisibility(command.TargetId, false),
                RendererCommandKind.RevealWindow => SetWindowVisibility(command.TargetId, true),
                RendererCommandKind.UnloadWindow => SetWindowLifecycle(command.TargetId, reload: false),
                RendererCommandKind.ReloadWindow => SetWindowLifecycle(command.TargetId, reload: true),
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

    private string ApplyConfiguration(
        IReadOnlyList<string> paths,
        Guid? themeId = null,
        Guid? activationId = null,
        long? generation = null,
        string? configJson = null,
        IReadOnlyList<string>? sourcePaths = null)
    {
        lock (_configurationSync)
        {
            return ApplyConfigurationCore(paths, themeId, activationId, generation, configJson, sourcePaths);
        }
    }

    private string ApplyConfigurationCore(
        IReadOnlyList<string> paths,
        Guid? themeId = null,
        Guid? activationId = null,
        long? generation = null,
        string? configJson = null,
        IReadOnlyList<string>? sourcePaths = null)
    {
        if (paths.Count > 0 && !string.IsNullOrWhiteSpace(configJson))
            return "ERR Configuration paths and inline JSON cannot be combined.";
        if (paths.Count == 0 && string.IsNullOrWhiteSpace(configJson))
            return "ERR At least one --config path is required.";

        RendererConfiguration candidate;
        if (!string.IsNullOrWhiteSpace(configJson))
        {
            if (Encoding.UTF8.GetByteCount(configJson) > ControlPipeCodec.MaxFrameBytes)
                return "ERR Inline configuration exceeds 1 MiB.";
            using JsonDocument document = JsonDocument.Parse(configJson);
            candidate = RendererConfiguration.LoadJson(document.RootElement, "inline");
            paths = sourcePaths?.ToArray() ?? Array.Empty<string>();
        }
        else
        {
            candidate = RendererConfiguration.Load(paths);
            paths = sourcePaths?.ToArray() ?? paths;
        }
        IReadOnlyList<RendererWindowSpec> candidateSpecs = candidate.ResolveWindowSpecs();
        IReadOnlyList<RendererWindowSpec> oldSpecs;
        RendererConfiguration oldConfiguration;
        IReadOnlyList<string> oldPaths;
        string? oldConfigJson;
        Guid? oldThemeId;
        Guid oldActivationId;
        long oldGeneration;
        lock (_sync)
        {
            oldSpecs = _specs.ToArray();
            oldConfiguration = _configuration;
            oldPaths = _configPaths;
            oldConfigJson = _configJson;
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
                    _configJson = oldConfigJson;
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
            _configJson = string.IsNullOrWhiteSpace(configJson) ? null : configJson;
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
        lock (_configurationSync)
        {
            if (_configPaths.Count == 0 && string.IsNullOrWhiteSpace(_configJson))
                return "ERR No active configuration.";
            if (command.All)
                return ApplyConfigurationCore(
                    string.IsNullOrWhiteSpace(_configJson) ? _configPaths : Array.Empty<string>(),
                    _themeId,
                    _themeActivationId,
                    _themeGeneration,
                    _configJson,
                    _configPaths);

            IReadOnlyList<RuntimeGroupHost> targets = ResolveTargets(command);
            foreach (RuntimeGroupHost group in targets)
                group.InvokeReload();
            return $"OK reloaded {targets.Count} group(s)";
        }
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
        RuntimeGroupHost? host = FindGroupForWindow(id);
        if (host == null)
            return $"ERR Unknown window ID: {id}";
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

    private string SetWindowLifecycle(string? id, bool reload)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "ERR Window ID is required.";
        RuntimeGroupHost? host = FindGroupForWindow(id);
        if (host == null)
            return $"ERR Unknown window ID: {id}";
        WindowSnapshot snapshot = reload
            ? host.InvokeReloadWindow(id)
            : host.InvokeUnloadWindow(id);
        EmitWindowEvent(reload ? "window.reloaded" : "window.unloaded", snapshot);
        EmitWindowEvent("window.changed", snapshot);
        return $"OK {(reload ? "reloaded" : "unloaded")} {id}";
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
        if (request.Method == "POST" && request.Path == "/v1/renderer/configure")
            return ConfigureManagedRenderer(request);
        if (request.Path == "/v1/renderer/windows" || request.Path.StartsWith("/v1/renderer/windows/", StringComparison.Ordinal))
            return HandleWindowRequest(request);
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

    private ControlResponse HandleWindowRequest(ControlRequest request)
    {
        const string prefix = "/v1/renderer/windows";
        string suffix = request.Path.Length == prefix.Length ? "" : request.Path[prefix.Length..].Trim('/');
        string? id = suffix.Length == 0 ? null : suffix.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
        bool actionRoute = suffix.EndsWith("/actions", StringComparison.OrdinalIgnoreCase);
        if (actionRoute)
            id = suffix[..^"/actions".Length].Trim('/');

        if (request.Method == "GET")
        {
            if (id == null)
                return new ControlResponse(request.Id, 200, new { windows = GetWindowSnapshots() });
            WindowSnapshot? snapshot = GetWindowSnapshots().FirstOrDefault(window => window.Id == id);
            return snapshot == null
                ? new ControlResponse(request.Id, 404, Error: new ControlError("window_not_found", $"Unknown window ID: {id}"))
                : new ControlResponse(request.Id, 200, snapshot);
        }

        if (request.Method == "POST" && id == null)
        {
            if (!TryResolveDynamicSpec(request.Body, out RendererWindowSpec? spec, out string error))
                return new ControlResponse(request.Id, 400, Error: new ControlError("window_definition_invalid", error));
            try
            {
                RuntimeGroupHost host = EnsureGroup(spec!.ThreadGroup);
                WindowSnapshot snapshot = host.InvokeCreateWindow(spec);
                EmitWindowEvent("window.created", snapshot);
                return new ControlResponse(request.Id, 201, snapshot);
            }
            catch (Exception ex)
            {
                return new ControlResponse(request.Id, 409, Error: new ControlError("window_create_failed", ex.Message));
            }
        }

        if (id == null)
            return new ControlResponse(request.Id, 400, Error: new ControlError("window_id_required", "A window ID is required."));
        RuntimeGroupHost? group = FindGroupForWindow(id);

        if (request.Method == "DELETE")
        {
            if (group == null)
                return new ControlResponse(request.Id, 404, Error: new ControlError("window_not_found", $"Unknown window ID: {id}"));
            group.InvokeDestroyWindow(id);
            EmitWindowEvent("window.destroyed", new { id });
            return new ControlResponse(request.Id, 200, new { id, destroyed = true });
        }

        if (request.Method == "PUT")
        {
            if (!TryResolveDynamicSpec(request.Body, out RendererWindowSpec? replacement, out string error))
                return new ControlResponse(request.Id, 400, Error: new ControlError("window_definition_invalid", error));
            if (!string.Equals(replacement!.LogicalId, id, StringComparison.Ordinal))
                return new ControlResponse(request.Id, 400, Error: new ControlError("window_id_mismatch", "Definition ID must match the URL ID."));
            if (group != null)
                group.InvokeDestroyWindow(id);
            RuntimeGroupHost replacementGroup = EnsureGroup(replacement.ThreadGroup);
            WindowSnapshot snapshot = replacementGroup.InvokeCreateWindow(replacement);
            EmitWindowEvent("window.updated", snapshot);
            return new ControlResponse(request.Id, 200, snapshot);
        }

        if (request.Method == "POST" && actionRoute)
        {
            if (group == null)
                return new ControlResponse(request.Id, 404, Error: new ControlError("window_not_found", $"Unknown window ID: {id}"));
            if (request.Body is not { } body || body.ValueKind != JsonValueKind.Object ||
                !body.TryGetProperty("action", out JsonElement actionValue) || actionValue.ValueKind != JsonValueKind.String)
                return new ControlResponse(request.Id, 400, Error: new ControlError("window_action_invalid", "Window action must be a string."));
            try
            {
                string action = actionValue.GetString() ?? "";
                WindowSnapshot snapshot = group.InvokeWindowAction(id, action);
                EmitWindowEvent("window.changed", snapshot);
                if (snapshot.State == AppWindowState.Closed)
                    EmitWindowEvent("window.destroyed", new { id });
                if (action.Trim().Equals("unload", StringComparison.OrdinalIgnoreCase))
                    EmitWindowEvent("window.unloaded", snapshot);
                else if (action.Trim().Equals("reload", StringComparison.OrdinalIgnoreCase))
                    EmitWindowEvent("window.reloaded", snapshot);
                if (action.Trim().Equals("show", StringComparison.OrdinalIgnoreCase) ||
                    action.Trim().Equals("hide", StringComparison.OrdinalIgnoreCase) ||
                    action.Trim().Equals("toggle", StringComparison.OrdinalIgnoreCase))
                    EmitWindowEvent("window.visibilityChanged", snapshot);
                return new ControlResponse(request.Id, 200, snapshot);
            }
            catch (Exception ex)
            {
                return new ControlResponse(request.Id, 409, Error: new ControlError("window_action_failed", ex.Message));
            }
        }

        return new ControlResponse(request.Id, 405, Error: new ControlError("method_not_allowed", "Unsupported window operation."));
    }

    private bool TryResolveDynamicSpec(JsonElement? body, out RendererWindowSpec? spec, out string error)
    {
        spec = null;
        error = "Window definition must be an object.";
        if (body is not { } value || value.ValueKind != JsonValueKind.Object)
            return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse($"{{\"windows\":[{value.GetRawText()}]}}");
            IReadOnlyList<RendererWindowSpec> specs = RendererConfiguration.LoadJson(document.RootElement, "window request").ResolveWindowSpecs();
            if (specs.Count != 1)
            {
                error = "Window definition must resolve to exactly one window.";
                return false;
            }
            spec = specs[0];
            return true;
        }
        catch (RendererConfigurationException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private RuntimeGroupHost EnsureGroup(string name)
    {
        lock (_sync)
        {
            if (_groups.TryGetValue(name, out RuntimeGroupHost? existing))
                return existing;
            RuntimeGroupHost group = new(name, _options, () => _specs, _logger, _backendManager, _controlChannel, HandleCommand);
            group.Start();
            _groups.Add(name, group);
            return group;
        }
    }

    private RuntimeGroupHost? FindGroupForWindow(string id)
    {
        lock (_sync)
        {
            foreach (RuntimeGroupHost group in _groups.Values)
                if (group.GetWindowSnapshots().Any(window => string.Equals(window.Id, id, StringComparison.Ordinal)))
                    return group;
            return null;
        }
    }

    private void EmitWindowEvent(string name, object body) => _controlChannel?.TrySendEvent(name, new
    {
        window = body,
        themeId = _themeId,
        themeActivationId = ActiveThemeActivationId,
        themeGeneration = ActiveThemeGeneration
    });

    private object BuildConfigPayload() => new
    {
        files = _configPaths,
        cues = _configuration.Cues,
        sections = new { windows = _configuration.Windows, wallpaper = _configuration.Wallpapers, widgets = _configuration.Widgets },
        resolvedWindows = _specs,
        themeId = _themeId,
        themeActivationId = ActiveThemeActivationId,
        themeGeneration = ActiveThemeGeneration
    };

    private Guid? ActiveThemeActivationId => _themeGeneration == 0 ? null : _themeActivationId;
    private long? ActiveThemeGeneration => _themeGeneration == 0 ? null : _themeGeneration;

    private ControlResponse ConfigureManagedRenderer(ControlRequest request)
    {
        if (!_managedStartup)
            return new ControlResponse(request.Id, 403,
                Error: new ControlError("managed_startup_required", "Renderer configuration route requires inherited control handles."));
        if (request.Body is not { } body || body.ValueKind != JsonValueKind.Object)
            return new ControlResponse(request.Id, 400,
                Error: new ControlError("startup_body_invalid", "Renderer startup body must be an object."));
        if (!body.TryGetProperty("protocolVersion", out JsonElement protocolVersion) ||
            protocolVersion.ValueKind != JsonValueKind.Number ||
            !protocolVersion.TryGetInt32(out int version) || version != 1)
            return new ControlResponse(request.Id, 400,
                Error: new ControlError("startup_protocol_invalid", "Renderer startup protocol version must be 1."));

        string? ultralightRoot = GetOptionalString(body, "ultralightRoot");
        bool noTray = !body.TryGetProperty("noTray", out JsonElement noTrayElement) ||
                      noTrayElement.ValueKind != JsonValueKind.False;
        Guid? themeId = TryGetGuid(body, "themeId");
        long? themeGeneration = TryGetLong(body, "themeGeneration");
        JsonElement? configDocument = null;
        string[] configPaths = Array.Empty<string>();
        string[] sourcePaths = Array.Empty<string>();

        if (body.TryGetProperty("config", out JsonElement config) && config.ValueKind != JsonValueKind.Null)
        {
            if (config.ValueKind != JsonValueKind.Object)
                return new ControlResponse(request.Id, 400,
                    Error: new ControlError("startup_config_invalid", "Startup config must be an object."));

            string mode = GetOptionalString(config, "mode")?.ToLowerInvariant() ?? "";
            sourcePaths = GetStringArray(config, "sourcePaths");
            if (mode == "paths")
            {
                configPaths = GetStringArray(config, "paths");
                if (configPaths.Length == 0)
                    return new ControlResponse(request.Id, 400,
                        Error: new ControlError("startup_config_invalid", "Path config requires at least one path."));
            }
            else if (mode == "json")
            {
                if (!config.TryGetProperty("document", out JsonElement document) || document.ValueKind != JsonValueKind.Object)
                    return new ControlResponse(request.Id, 400,
                        Error: new ControlError("startup_config_invalid", "JSON config requires object document."));
                configDocument = document.Clone();
            }
            else
            {
                return new ControlResponse(request.Id, 400,
                    Error: new ControlError("startup_config_invalid", "Startup config mode must be paths or json."));
            }
        }

        try
        {
            ControlResponse response = InvokeOnDispatcher(() => ApplyManagedConfiguration(
                ultralightRoot,
                noTray,
                configPaths,
                configDocument,
                sourcePaths,
                themeId,
                themeGeneration,
                request.Id));
            if (response.Status is >= 200 and < 300)
                _controlChannel?.TrySendEvent("renderer.ready", new
                {
                    configuredWindows = _specs.Count,
                    files = _configPaths,
                    themeId = _themeId,
                    themeGeneration = ActiveThemeGeneration
                });
            return response;
        }
        catch (RendererConfigurationException ex)
        {
            return new ControlResponse(request.Id, 422,
                Error: new ControlError("configuration_invalid", ex.Message));
        }
        catch (Exception ex)
        {
            _logger.Error("renderer.startup.failed", new Dictionary<string, string?>
            {
                ["exceptionType"] = ex.GetType().Name,
                ["message"] = ex.Message
            });
            return new ControlResponse(request.Id, 500,
                Error: new ControlError("renderer_startup_failed", ex.Message));
        }
    }

    private ControlResponse ApplyManagedConfiguration(
        string? ultralightRoot,
        bool noTray,
        IReadOnlyList<string> configPaths,
        JsonElement? configDocument,
        IReadOnlyList<string> sourcePaths,
        Guid? themeId,
        long? themeGeneration,
        string requestId)
    {
        lock (_configurationSync)
        {
            return ApplyManagedConfigurationCore(
                ultralightRoot,
                noTray,
                configPaths,
                configDocument,
                sourcePaths,
                themeId,
                themeGeneration,
                requestId);
        }
    }

    private ControlResponse ApplyManagedConfigurationCore(
        string? ultralightRoot,
        bool noTray,
        IReadOnlyList<string> configPaths,
        JsonElement? configDocument,
        IReadOnlyList<string> sourcePaths,
        Guid? themeId,
        long? themeGeneration,
        string requestId)
    {
        _options = _options.WithManagedRuntime(ultralightRoot, noTray);
        RendererPaths.Resolve(ultralightRoot).EnsureWritableRoot();

        if (configPaths.Count == 0 && configDocument == null)
        {
            StopGroups();
            _backendManager?.RotateThemeScope();
            lock (_sync)
            {
                _configuration = RendererConfiguration.Empty;
                _specs = Array.Empty<RendererWindowSpec>();
                _configPaths = Array.Empty<string>();
                _configJson = null;
                _themeId = themeId;
                _themeGeneration = themeGeneration ?? 0;
            }
            _startupConfigured = true;
            return new ControlResponse(requestId, 200, new { state = "idle", files = Array.Empty<string>() });
        }

        RendererConfiguration candidate = configDocument is { } document
            ? RendererConfiguration.LoadJson(document, "inline")
            : RendererConfiguration.Load(configPaths);
        IReadOnlyList<RendererWindowSpec> candidateSpecs = candidate.ResolveWindowSpecs();
        if (_startupConfigured)
        {
            string result = ApplyConfiguration(
                configPaths,
                themeId,
                null,
                themeGeneration,
                configDocument is { } inline ? inline.GetRawText() : null,
                sourcePaths.Count == 0 ? configPaths : sourcePaths);
            if (result.StartsWith("ERR ", StringComparison.Ordinal))
                return new ControlResponse(requestId, 422,
                    Error: new ControlError("configuration_invalid", result[4..]));
            return new ControlResponse(requestId, 200, new { state = "ready", files = _configPaths, windows = _specs.Count });
        }

        Dictionary<string, RuntimeGroupHost> groups = CreateGroups(candidateSpecs);
        lock (_sync)
        {
            foreach (KeyValuePair<string, RuntimeGroupHost> pair in groups)
                _groups.Add(pair.Key, pair.Value);
            _configuration = candidate;
            _specs = candidateSpecs;
            _configPaths = (sourcePaths.Count == 0 ? configPaths : sourcePaths).ToArray();
            _configJson = configDocument?.GetRawText();
            _themeId = themeId;
            _themeGeneration = themeGeneration ?? 0;
        }
        _startupConfigured = true;
        _logger.Info("renderer.ready", new Dictionary<string, string?>
        {
            ["configuredWindows"] = _specs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["controllerConnected"] = "true"
        });
        return new ControlResponse(requestId, 200, new { state = "ready", files = _configPaths, windows = _specs.Count });
    }

    private T InvokeOnDispatcher<T>(Func<T> action)
    {
        if (_dispatcher.InvokeRequired)
            return (T)_dispatcher.Invoke(action);
        return action();
    }

    private static string? GetOptionalString(JsonElement objectValue, string name) =>
        objectValue.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string[] GetStringArray(JsonElement objectValue, string name)
    {
        if (!objectValue.TryGetProperty(name, out JsonElement values) || values.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return values.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static Guid? TryGetGuid(JsonElement objectValue, string name) =>
        GetOptionalString(objectValue, name) is { } value && Guid.TryParse(value, out Guid parsed) ? parsed : null;

    private static long? TryGetLong(JsonElement objectValue, string name) =>
        objectValue.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out long parsed) ? parsed : null;

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

    private void OnForegroundChanged(IntPtr foreground)
    {
        RuntimeGroupHost[] groups;
        lock (_sync) groups = _groups.Values.ToArray();
        foreach (RuntimeGroupHost group in groups)
        {
            try
            {
                foreach (WindowSnapshot snapshot in group.InvokeForegroundChanged(foreground))
                    EmitWindowEvent("window.focusChanged", snapshot);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[Focus] {ex.Message}"); }
        }
    }

    private void ExitHost()
    {
        if (_exiting)
            return;
        _exiting = true;
        _input.Dispose();
        _fullscreenMonitor.Dispose();
        _focusMonitor.Dispose();
        _commandServer.Dispose();
        _logger.Info("renderer.stopping");
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
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
