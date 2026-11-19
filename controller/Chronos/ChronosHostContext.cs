using System.Globalization;
using System.Windows.Forms;

namespace Chronos;

internal sealed class ChronosHostContext : ApplicationContext
{
    private readonly ControllerOptions _options;
    private readonly ChronosPaths _paths;
    private readonly PersistenceStore _persistence;
    private readonly LogStore _logs;
    private readonly BackendManager _backends;
    private readonly Control _dispatcher = new();
    private readonly RendererCommandService _commands;
    private readonly RendererSession _renderer;
    private readonly WinMenuToggleController _winToggle;
    private readonly TrayController? _tray;
    private bool _exiting;

    public ChronosHostContext(ControllerOptions options)
    {
        _options = options;
        _paths = ChronosPaths.Resolve();
        _persistence = new PersistenceStore(_paths, Diagnostic);
        _logs = new LogStore(_paths.LogsDirectory);
        BackendRegistry registry = BackendRegistry.Load(_paths.BackendRegistryFile, Diagnostic);
        _backends = new BackendManager(registry);
        _dispatcher.CreateControl();

        PersistentState state = _persistence.ReadState();
        IReadOnlyList<string> configPaths = options.ConfigPaths.Count > 0
            ? options.ConfigPaths
            : options.NoLastConfig ? Array.Empty<string>() : state.Files;
        WrapperSettings settings = _persistence.ReadSettings();
        bool winToggleEnabled = options.WinMenuToggle ?? settings.WinMenuToggleEnabled;
        string rendererPath = RendererLocator.Resolve(options.RendererPath);

        RendererSession? startedRenderer = null;
        try
        {
            startedRenderer = RendererSession.Start(
                rendererPath,
                configPaths,
                HandleControlRequestAsync,
                line => Diagnostic($"[Aether] {line}"),
                line => Diagnostic($"[Aether] {line}"));
            _renderer = startedRenderer;
            _commands = new RendererCommandService(new AetherCommandClient());
            _renderer.Control.Disconnected += OnControlDisconnected;
            _renderer.Control.EventReceived += OnRendererEvent;
            _renderer.ProcessExited += OnRendererExited;
            WaitForRenderer(configPaths);

            if (configPaths.Count > 0)
                PersistAcceptedConfig(configPaths);

            ConfigureRendererLogging();
            _winToggle = new WinMenuToggleController(_commands, winToggleEnabled, Diagnostic);
            _winToggle.StateChanged += OnWinToggleChanged;
            _tray = options.NoTray ? null : new TrayController(_commands, _winToggle, RequestExit, Diagnostic);
        }
        catch
        {
            try { startedRenderer?.Dispose(); } catch { }
            _backends.Dispose();
            _dispatcher.Dispose();
            throw;
        }
    }

    public RendererCapabilities Capabilities => _commands.Capabilities;

    private void WaitForRenderer(IReadOnlyList<string> expectedConfig)
    {
        RendererCapabilities capabilities = _commands.RefreshAsync().GetAwaiter().GetResult();
        if (!_commands.IsConnected)
        {
            if (_renderer.Process.HasExited && _renderer.Process.ExitCode == 3)
                throw new InvalidOperationException("Aether is already running outside Chronos; attach/adopt is not supported.");
            throw new InvalidOperationException("Aether did not accept commands during startup.");
        }

        if (expectedConfig.Count > 0 && !PathsEqual(expectedConfig, capabilities.ConfigFiles))
            throw new InvalidOperationException("Aether accepted a configuration different from requested startup paths.");
    }

    private void PersistAcceptedConfig(IReadOnlyList<string> configPaths)
    {
        try { _persistence.WriteState(configPaths); }
        catch (Exception ex) { Diagnostic($"Could not persist accepted configuration: {ex.Message}"); }
    }

    private void ConfigureRendererLogging()
    {
        try
        {
            _renderer.Control.SendAsync(
                    "PUT",
                    "/v1/logging/batch-limits",
                    new { enabled = true, maxEvents = 32, maxBytes = 65536, flushIntervalMs = 250 },
                    TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            Diagnostic($"Renderer logging policy was not applied: {ex.Message}");
        }
    }

    private async Task<ControlResponse> HandleControlRequestAsync(ControlRequest request)
    {
        if (request.Method == "GET" && request.Path == "/v1/pipes")
        {
            return ControlResponse.Success(request.Id, 200, new
            {
                control = _renderer.Control.State,
                direction = "bidirectional",
                backends = _backends.Snapshot()
            });
        }

        if (request.Method == "POST" && request.Path == "/v1/logs")
        {
            LogBatchResult batch = _logs.Accept(request.Body);
            return ControlResponse.Success(request.Id, 202, new
            {
                accepted = batch.Accepted,
                batchId = batch.BatchId,
                duplicate = batch.Duplicate
            });
        }

        if (request.Path.StartsWith("/v1/backends/", StringComparison.Ordinal) && request.Path.EndsWith("/connect", StringComparison.Ordinal))
        {
            string backendId = Uri.UnescapeDataString(request.Path["/v1/backends/".Length..^"/connect".Length]);
            return request.Method switch
            {
                "POST" => HandleBackendConnect(request, backendId),
                "DELETE" => HandleBackendDisconnect(request, backendId),
                _ => ControlResponse.Failure(request.Id, 404, "route_not_found", "Unknown control route.")
            };
        }

        await Task.CompletedTask;
        return ControlResponse.Failure(request.Id, 404, "route_not_found", "Unknown control route.");
    }

    private ControlResponse HandleBackendConnect(ControlRequest request, string backendId)
    {
        if (!ControlCodec.TryGetObject(request.Body, out System.Text.Json.JsonElement body) ||
            !body.TryGetProperty("protocol", out System.Text.Json.JsonElement protocol) ||
            !protocol.TryGetInt32(out int protocolVersion) || protocolVersion != 1 ||
            !body.TryGetProperty("renderer", out System.Text.Json.JsonElement renderer) || renderer.ValueKind != System.Text.Json.JsonValueKind.Object)
            return ControlResponse.Failure(request.Id, 400, "backend_request_invalid", "Backend connect body is invalid.");

        int pid = renderer.TryGetProperty("pid", out System.Text.Json.JsonElement pidValue) && pidValue.TryGetInt32(out int parsedPid)
            ? parsedPid
            : 0;
        string sid = ControlCodec.GetString(renderer, "sid") ?? "";
        string sessionId = ControlCodec.GetString(renderer, "sessionId") ?? "";
        if (pid != _renderer.Process.Id)
            return ControlResponse.Failure(request.Id, 403, "backend_renderer_unauthorized", "Renderer process identity is invalid.");

        BackendGrant grant = _backends.Connect(backendId, sid, pid, sessionId);
        return ControlResponse.Success(request.Id, 200, new
        {
            protocol = 1,
            backendId = grant.BackendId,
            pipeName = $"\\\\.\\pipe\\{grant.PipeName}",
            expiresAt = grant.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        });
    }

    private ControlResponse HandleBackendDisconnect(ControlRequest request, string backendId)
    {
        string sessionId = "";
        if (ControlCodec.TryGetObject(request.Body, out System.Text.Json.JsonElement body))
            sessionId = ControlCodec.GetString(body, "sessionId") ?? "";
        if (sessionId.Length > 0)
            _backends.Disconnect(backendId, sessionId);
        return ControlResponse.Success(request.Id, 200, null);
    }

    private void OnControlDisconnected()
    {
        _backends.RevokeAll();
        _commands.MarkDisconnected();
        Diagnostic("Renderer control channel disconnected.");
        RequestExitOnDispatcher();
    }

    private void OnRendererEvent(string eventName, System.Text.Json.JsonElement? body)
    {
        if (eventName is not ("config.changed" or "window.changed"))
            return;
        try
        {
            _dispatcher.BeginInvoke((Action)(() =>
            {
                try
                {
                    RendererCapabilities capabilities = _commands.RefreshAsync().GetAwaiter().GetResult();
                    if (eventName == "config.changed" && capabilities.ConfigFiles.Count > 0)
                        PersistAcceptedConfig(capabilities.ConfigFiles);
                }
                catch (Exception ex) { Diagnostic($"Could not refresh renderer state: {ex.Message}"); }
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void OnRendererExited()
    {
        try
        {
            _dispatcher.BeginInvoke((Action)(() =>
            {
                _commands.MarkDisconnected();
                Diagnostic("Aether process exited.");
                RequestExit();
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void RequestExitOnDispatcher()
    {
        try { _dispatcher.BeginInvoke((Action)RequestExit); }
        catch (InvalidOperationException) { }
    }

    private void OnWinToggleChanged(bool enabled)
    {
        try { _persistence.WriteSettings(enabled); }
        catch (Exception ex) { Diagnostic($"Could not persist Win-menu policy: {ex.Message}"); }
    }

    private void RequestExit()
    {
        if (_exiting)
            return;
        _exiting = true;
        try { _winToggle.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try
        {
            if (_commands.IsConnected)
                _commands.ExitAsync().GetAwaiter().GetResult();
        }
        catch { }
        _backends.Dispose();
        _renderer.Dispose();
        _dispatcher.Dispose();
        Application.ExitThread();
    }

    private void Diagnostic(string message)
    {
        if (message.StartsWith("[Aether]", StringComparison.Ordinal) || message.Contains("secret", StringComparison.OrdinalIgnoreCase))
            Console.Error.WriteLine(message);
        else
            Console.WriteLine($"[Chronos] {message}");
    }

    private static bool PathsEqual(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        return expected.Count == actual.Count && expected.Zip(actual).All(pair =>
            string.Equals(Path.GetFullPath(pair.First), Path.GetFullPath(pair.Second), StringComparison.OrdinalIgnoreCase));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_exiting)
            RequestExit();
        base.Dispose(disposing);
    }
}
