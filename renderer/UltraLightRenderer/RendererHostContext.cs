using System.Text.Json;
using System.Windows.Forms;

namespace UltraLightRenderer;

public sealed class RendererHostContext : ApplicationContext
{
    private readonly HostOptions _options;
    private readonly SingleInstanceLease _lease;
    private readonly Control _dispatcher = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly CommandPipeServer _commandServer;
    private readonly InputRouter _input;
    private readonly FullscreenMonitor _fullscreenMonitor;
    private BackendConnectionManager? _backendManager;
    private readonly object _sync = new();
    private readonly Dictionary<string, RuntimeGroupHost> _groups = new(StringComparer.Ordinal);
    private ControlPipeChannel? _controlChannel;
    private IReadOnlyList<string> _configPaths;
    private bool _exiting;
    private RendererConfiguration _configuration;
    private IReadOnlyList<RendererWindowSpec> _specs;

    public RendererHostContext(HostOptions options, SingleInstanceLease lease)
    {
        _options = options;
        _lease = lease;
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
            foreach (KeyValuePair<string, RuntimeGroupHost> pair in CreateGroups(_specs))
                _groups.Add(pair.Key, pair.Value);

            _input = new InputRouter(GetDesktopWindows);
            _fullscreenMonitor = new FullscreenMonitor(OnFullscreenChanged);
            _fullscreenMonitor.Start();
            _commandServer = new CommandPipeServer(HandleCommand);
            _commandServer.Start();
            if (options.ControlReadHandle is { } readHandle && options.ControlWriteHandle is { } writeHandle)
            {
                _controlChannel = new ControlPipeChannel(readHandle, writeHandle, HandleControlRequest);
                _controlChannel.Start();
            }
            _backendManager = new BackendConnectionManager(_controlChannel);
            _menu = BuildMenu();
            _tray = new NotifyIcon
            {
                Text = "UltraLightRenderer",
                Icon = SystemIcons.Application,
                ContextMenuStrip = _menu,
                Visible = true
            };
        }
        catch
        {
            StopGroups();
            _controlChannel?.Dispose();
            _backendManager?.Dispose();
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
                var group = new RuntimeGroupHost(groupName, _options, () => snapshot);
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
                MessageBox.Show(response[4..], "UltraLightRenderer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else if (command.Kind == RendererCommandKind.Status)
                MessageBox.Show(response.StartsWith("OK ", StringComparison.Ordinal) ? response[3..] : response,
                    "UltraLightRenderer status", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "UltraLightRenderer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string HandleCommand(RendererCommand command)
    {
        try
        {
            return command.Kind switch
            {
                RendererCommandKind.ApplyConfig => ApplyConfiguration(command.ConfigPaths ?? Array.Empty<string>()),
                RendererCommandKind.ShowMenu => SetMenuVisibility(true),
                RendererCommandKind.HideMenu => SetMenuVisibility(false),
                RendererCommandKind.HideWindow => SetWindowVisibility(command.TargetId, false),
                RendererCommandKind.RevealWindow => SetWindowVisibility(command.TargetId, true),
                RendererCommandKind.Show => ShowTopic(command.Topic),
                RendererCommandKind.Exit => RequestExit(),
                RendererCommandKind.Pause => ApplyPause(command, true),
                RendererCommandKind.Resume => ApplyPause(command, false),
                RendererCommandKind.Status => GetStatus(command),
                RendererCommandKind.Reload => ReloadConfiguration(),
                _ => "ERR Unsupported runtime command."
            };
        }
        catch (Exception ex)
        {
            return $"ERR {ex.Message}";
        }
    }

    private string ApplyConfiguration(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return "ERR At least one --config path is required.";

        RendererConfiguration candidate = RendererConfiguration.Load(paths);
        IReadOnlyList<RendererWindowSpec> candidateSpecs = candidate.ResolveWindowSpecs();
        Dictionary<string, RuntimeGroupHost> candidateGroups = CreateGroups(candidateSpecs);

        RuntimeGroupHost[] oldGroups;
        lock (_sync)
        {
            oldGroups = _groups.Values.ToArray();
            _groups.Clear();
            foreach (KeyValuePair<string, RuntimeGroupHost> pair in candidateGroups)
                _groups.Add(pair.Key, pair.Value);
            _configuration = candidate;
            _specs = candidateSpecs;
            _configPaths = paths.ToArray();
        }

        foreach (RuntimeGroupHost group in oldGroups)
            group.Dispose();
        return "OK configuration applied";
    }

    private string ReloadConfiguration()
    {
        if (_configPaths.Count == 0)
            return "ERR No active configuration.";
        return ApplyConfiguration(_configPaths);
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
        return $"OK {(visible ? "revealed" : "hid")} {id}";
    }

    private string ShowTopic(string? topic)
    {
        return topic switch
        {
            "config" => ControlPipeCodec.SerializeJson(BuildConfigPayload()),
            "windows" => ControlPipeCodec.SerializeJson(new { windows = GetWindowSnapshots() }),
            "paths" => ControlPipeCodec.SerializeJson(RendererPaths.Resolve(_options.UltralightRoot)),
            "pipes" => ControlPipeCodec.SerializeJson(new { control = _controlChannel?.State ?? "standalone", backends = _backendManager?.Snapshot() ?? Array.Empty<BackendPipeSnapshot>() }),
            _ => "ERR Show topic must be pipes, config, windows, or paths."
        };
    }

    private string RequestExit()
    {
        _dispatcher.BeginInvoke((Action)ExitHost);
        return "OK exit requested";
    }

    private ControlResponse HandleControlRequest(ControlRequest request)
    {
        string[] parts = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (request.Method == "GET" && request.Path == "/v1/config")
            return new ControlResponse(request.Id, 200, BuildConfigPayload());
        if (request.Method == "POST" && request.Path == "/v1/config")
        {
            if (!TryGetFiles(request.Body, out IReadOnlyList<string> files, out string? error))
                return new ControlResponse(request.Id, 400, Error: new ControlError("config_body_invalid", error!));
            string result = ApplyConfiguration(files);
            return result.StartsWith("OK ", StringComparison.Ordinal)
                ? new ControlResponse(request.Id, 200, new { applied = true, files })
                : new ControlResponse(request.Id, 422, Error: new ControlError("config_rejected", result[4..]));
        }
        if (request.Method == "GET" && request.Path == "/v1/windows")
            return new ControlResponse(request.Id, 200, new { windows = GetWindowSnapshots() });
        if (request.Method == "GET" && request.Path == "/v1/paths")
            return new ControlResponse(request.Id, 200, RendererPaths.Resolve(_options.UltralightRoot));
        if (request.Method == "GET" && request.Path == "/v1/pipes")
            return new ControlResponse(request.Id, 200, new
            {
                control = _controlChannel?.State ?? "standalone",
                backends = _backendManager?.Snapshot() ?? Array.Empty<BackendPipeSnapshot>()
            });
        if (parts.Length == 4 && parts[0] == "v1" && parts[1] == "windows" && request.Method == "POST")
        {
            string id = Uri.UnescapeDataString(parts[2]);
            bool visible = parts[3] == "reveal";
            if (parts[3] is not ("hide" or "reveal"))
                return new ControlResponse(request.Id, 404, Error: new ControlError("route_not_found", "Unknown window action."));
            string result = SetWindowVisibility(id, visible);
            return result.StartsWith("OK ", StringComparison.Ordinal)
                ? new ControlResponse(request.Id, 200, new { id, visible })
                : new ControlResponse(request.Id, 404, Error: new ControlError("window_not_found", result[4..]));
        }
        if (request.Method == "POST" && request.Path == "/v1/menu/show")
            return ControlMenuResponse(request, true);
        if (request.Method == "POST" && request.Path == "/v1/menu/hide")
            return ControlMenuResponse(request, false);
        if (parts.Length == 4 && parts[0] == "v1" && parts[1] == "backends" && parts[3] == "connect")
            return new ControlResponse(request.Id, 501, Error: new ControlError("backend_transport_unavailable", "Backend connection manager is not initialized."));

        return new ControlResponse(request.Id, 404, Error: new ControlError("route_not_found", "Unknown control route."));
    }

    private ControlResponse ControlMenuResponse(ControlRequest request, bool visible)
    {
        string result = SetMenuVisibility(visible);
        return result.StartsWith("OK ", StringComparison.Ordinal)
            ? new ControlResponse(request.Id, 200, new { visible })
            : new ControlResponse(request.Id, 404, Error: new ControlError("menu_not_found", result[4..]));
    }

    private static bool TryGetFiles(JsonElement? body, out IReadOnlyList<string> files, out string? error)
    {
        files = Array.Empty<string>();
        error = null;
        if (body is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("files", out JsonElement fileArray) || fileArray.ValueKind != JsonValueKind.Array)
        {
            error = "Body must contain a files array.";
            return false;
        }

        var paths = new List<string>();
        foreach (JsonElement item in fileArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                error = "Each config file path must be a non-empty string.";
                return false;
            }
            paths.Add(Path.GetFullPath(item.GetString()!));
        }
        if (paths.Count == 0)
        {
            error = "At least one config file path is required.";
            return false;
        }
        files = paths;
        return true;
    }

    private object BuildConfigPayload() => new
    {
        files = _configPaths,
        sections = new { wallpaper = _configuration.Wallpapers, widgets = _configuration.Widgets, menu = _configuration.Menu },
        resolvedWindows = _specs
    };

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
        Console.WriteLine(status);
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
        _backendManager?.Dispose();
        _controlChannel?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        StopGroups();
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
