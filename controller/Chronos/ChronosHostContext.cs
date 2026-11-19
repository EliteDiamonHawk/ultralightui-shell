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
    private RendererSession? _renderer;
    private readonly WinMenuToggleController _winToggle;
    private readonly TrayController? _tray;
    private bool _statusPollingEnabled;
    private PersistentSettings _settings;
    private ThemeRegistryState _themes;
    private string _themeOrigin;
    private Guid? _activeThemeId;
    private long _activeThemeGeneration;
    private Guid? _pendingThemeId;
    private long? _pendingThemeGeneration;
    private ShellForm? _shellForm;
    private bool _exiting;

    public event Action? UiStateChanged;

    public ChronosHostContext(ControllerOptions options)
    {
        _options = options;
        _paths = ChronosPaths.Resolve();
        _persistence = new PersistenceStore(_paths, Diagnostic);
        _logs = new LogStore(_paths.LogsDirectory);
        _themes = _persistence.ReadThemes();
        _settings = _persistence.ReadSettings();
        _statusPollingEnabled = options.StatusPolling ?? _settings.StatusPollingEnabled;
        BackendRegistry registry = BackendRegistry.Load(_paths.BackendRegistryFile, Diagnostic);
        _backends = new BackendManager(registry);
        _dispatcher.CreateControl();

        PersistentState state = _persistence.ReadState();
        bool restoreState = options.RestoreState ?? _settings.RestoreState;
        ThemeDefinition? restoredTheme = restoreState && state.ActiveThemeId is { } activeThemeId
            ? _themes.Themes.FirstOrDefault(theme => theme.Id == activeThemeId)
            : restoreState
                ? _themes.Themes.FirstOrDefault(theme => PathsEqual(theme.Files, state.Files))
                : null;
        IReadOnlyList<string> configPaths = options.ConfigPaths.Count > 0
            ? options.ConfigPaths
            : restoredTheme?.Files ?? Array.Empty<string>();
        _activeThemeId = options.ConfigPaths.Count > 0 ? null : restoredTheme?.Id;
        _themeOrigin = options.ConfigPaths.Count > 0
            ? "Explicit CLI"
            : restoredTheme != null ? "Restored state" : "None";
        bool trayEnabled = options.Tray ?? _settings.TrayEnabled;
        bool winToggleEnabled = options.WindowsKeyOverride ?? _settings.WindowsKeyOverride;
        _commands = new RendererCommandService(new AetherCommandClient());
        _winToggle = new WinMenuToggleController(_commands, winToggleEnabled, Diagnostic);
        _winToggle.StateChanged += OnWinToggleChanged;
        _tray = trayEnabled ? new TrayController(_commands, _winToggle, ShowShellForm, RequestExit, Diagnostic) : null;

        if (configPaths.Count > 0)
            StartRenderer(configPaths, options.RendererPath, _settings.RendererPath);
        else
            NotifyUiStateChanged();
    }

    public RendererCapabilities Capabilities => _commands.Capabilities;
    public bool StatusPollingEnabled => _statusPollingEnabled;
    public bool TrayEnabled => _tray != null;
    public bool TrayVisible => _tray?.Visible == true;
    public IReadOnlyList<ThemeDefinition> Themes => _themes.Themes;
    public string ThemesDirectory => _paths.ThemesDirectory;

    public ShellUiSnapshot GetUiSnapshot() => new(
        _commands.Capabilities,
        _commands.IsConnected,
        _tray != null,
        _tray?.Visible == true,
        _winToggle.Enabled,
        _themeOrigin,
        _statusPollingEnabled,
        _exiting,
        _settings);

    public async Task RefreshUiAsync()
    {
        if (!_commands.IsTransportAvailable)
        {
            NotifyUiStateChanged();
            return;
        }
        await _commands.RefreshAsync().ConfigureAwait(true);
        NotifyUiStateChanged();
    }

    public void ImportTheme(string name, IReadOnlyList<string> files)
    {
        string[] normalized = NormalizeThemeFiles(files);
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Theme name must not be empty.");

        var next = _themes.Themes.ToList();
        int duplicate = next.FindIndex(theme => PathsEqual(theme.Files, normalized));
        Guid id = duplicate >= 0 ? next[duplicate].Id : Guid.NewGuid();
        var replacement = new ThemeDefinition(name.Trim(), normalized, id);
        if (duplicate >= 0)
            next[duplicate] = replacement;
        else
            next.Add(replacement);
        _persistence.WriteThemes(next);
        _themes = new ThemeRegistryState(2, next);
        NotifyUiStateChanged();
    }

    public void RenameTheme(ThemeDefinition theme, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Theme name must not be empty.");
        ReplaceTheme(theme, new ThemeDefinition(name.Trim(), theme.Files, theme.Id));
    }

    public void RemoveTheme(ThemeDefinition theme)
    {
        List<ThemeDefinition> next = _themes.Themes.Where(item => !Equals(item, theme)).ToList();
        _persistence.WriteThemes(next);
        _themes = new ThemeRegistryState(2, next);
        NotifyUiStateChanged();
    }

    public async Task<AetherCommandResult> ApplyThemeAsync(ThemeDefinition theme)
    {
        string[] files = NormalizeThemeFiles(theme.Files);
        bool startedForTheme = false;
        if (!_commands.IsTransportAvailable)
        {
            if (_renderer != null)
                StopRenderer(requestExit: false);
            if (!StartRenderer(Array.Empty<string>(), _options.RendererPath, _settings.RendererPath))
                return AetherCommandResult.Failure("Renderer is unavailable; theme was not applied.");
            startedForTheme = true;
        }

        long generation = checked(_activeThemeGeneration + 1);
        _pendingThemeId = theme.Id;
        _pendingThemeGeneration = generation;
        try
        {
            AetherCommandResult result = await _commands.ApplyConfigAsync(files, theme.Id, generation).ConfigureAwait(true);
            if (result.Success)
            {
                RendererCapabilities capabilities = await _commands.RefreshAsync().ConfigureAwait(true);
                if (PathsEqual(files, capabilities.ConfigFiles))
                {
                    _activeThemeId = theme.Id;
                    _activeThemeGeneration = capabilities.ThemeGeneration ?? generation;
                    PersistAcceptedConfig(capabilities.ConfigFiles, _activeThemeId);
                    _themeOrigin = "UI-applied";
                }
                else if (startedForTheme)
                {
                    StopRenderer(requestExit: false);
                }
                NotifyUiStateChanged();
            }
            else if (startedForTheme)
            {
                StopRenderer(requestExit: false);
            }
            return result;
        }
        catch
        {
            if (startedForTheme)
                StopRenderer(requestExit: false);
            throw;
        }
        finally
        {
            _pendingThemeId = null;
            _pendingThemeGeneration = null;
        }
    }

    public Task<AetherCommandResult> UnloadThemeAsync()
    {
        StopRenderer(requestExit: false);
        _activeThemeId = null;
        _activeThemeGeneration = 0;
        _pendingThemeId = null;
        _pendingThemeGeneration = null;
        _themeOrigin = "None";
        PersistAcceptedConfig(Array.Empty<string>(), null);
        NotifyUiStateChanged();
        return Task.FromResult(new AetherCommandResult(true, "OK theme unloaded"));
    }

    public void SetCurrentWinToggle(bool enabled)
    {
        _winToggle.SetEnabled(enabled);
        UpdateSettings(_settings with { WindowsKeyOverride = _winToggle.Enabled });
    }

    public void SetTrayVisible(bool visible)
    {
        _tray?.SetVisible(visible);
        UpdateSettings(_settings with { TrayEnabled = visible });
    }

    public void SetStatusPolling(bool enabled)
    {
        _statusPollingEnabled = enabled;
        UpdateSettings(_settings with { StatusPollingEnabled = enabled });
    }

    public void SetRestoreState(bool enabled)
    {
        UpdateSettings(_settings with { RestoreState = enabled });
    }

    public void SetRendererPath(string? path)
    {
        string? normalized = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path.Trim());
        UpdateSettings(_settings with { RendererPath = normalized });
    }

    private void UpdateSettings(PersistentSettings settings)
    {
        _persistence.WriteSettings(settings);
        _settings = settings;
        NotifyUiStateChanged();
    }

    public Task<AetherCommandResult> PauseAsync(string? group, bool all) => _commands.PauseAsync(group, all);
    public Task<AetherCommandResult> ResumeAsync(string? group, bool all) => _commands.ResumeAsync(group, all);
    public Task<AetherCommandResult> ReloadAsync(string? group, bool all) => _commands.ReloadAsync(group, all);
    public Task<AetherCommandResult> StatusAsync(string? group, bool all) => _commands.StatusAsync(group, all);
    public Task<AetherCommandResult> ShowMenuAsync() => _commands.ShowMenuAsync();
    public Task<AetherCommandResult> HideMenuAsync() => _commands.HideMenuAsync();
    public Task<AetherCommandResult> ToggleMenuAsync() => _commands.ToggleMenuAsync();
    public Task<AetherCommandResult> SetWindowVisibilityAsync(string id, bool visible) => _commands.SetWindowVisibilityAsync(id, visible);
    public void RequestExitFromUi() => RequestExit();

    private void WaitForRenderer(IReadOnlyList<string> expectedConfig)
    {
        RendererCapabilities capabilities = _commands.RefreshAsync().GetAwaiter().GetResult();
        RendererSession? renderer = _renderer;
        if (!_commands.IsConnected)
        {
            if (renderer?.Process.HasExited == true && renderer.Process.ExitCode == 3)
                throw new InvalidOperationException("Aether is already running outside Chronos; attach/adopt is not supported.");
            throw new InvalidOperationException("Aether did not accept commands during startup.");
        }

        if (expectedConfig.Count > 0 && !PathsEqual(expectedConfig, capabilities.ConfigFiles))
            throw new InvalidOperationException("Aether accepted a configuration different from requested startup paths.");

        _activeThemeGeneration = capabilities.ThemeGeneration ?? 0;
    }

    private bool StartRenderer(
        IReadOnlyList<string> configPaths,
        string? explicitRendererPath,
        string? persistedRendererPath)
    {
        if (_renderer != null)
            return _commands.IsTransportAvailable;

        RendererSession? startedRenderer = null;
        try
        {
            string rendererPath = ResolveRendererPath(explicitRendererPath, persistedRendererPath);
            _commands.BeginConnection();
            startedRenderer = RendererSession.Start(
                rendererPath,
                configPaths,
                HandleControlRequestAsync,
                line => Diagnostic($"[Aether] {line}"),
                line => Diagnostic($"[Aether] {line}"));
            _renderer = startedRenderer;
            startedRenderer.Control.Disconnected += OnControlDisconnected;
            startedRenderer.Control.EventReceived += OnRendererEvent;
            startedRenderer.ProcessExited += OnRendererExited;
            WaitForRenderer(configPaths);

            if (configPaths.Count > 0)
                PersistAcceptedConfig(configPaths);

            ConfigureRendererLogging();
            NotifyUiStateChanged();
            return true;
        }
        catch (Exception ex)
        {
            Diagnostic($"Aether startup failed; Chronos remains available: {ex.Message}");
            if (startedRenderer != null)
            {
                try { startedRenderer.Dispose(); } catch { }
            }
            _renderer = null;
            _commands.MarkDisconnected();
            NotifyUiStateChanged();
            return false;
        }
    }

    private void StopRenderer(bool requestExit)
    {
        RendererSession? renderer = _renderer;
        _renderer = null;
        if (renderer == null)
        {
            _commands.MarkDisconnected();
            return;
        }

        renderer.Control.Disconnected -= OnControlDisconnected;
        renderer.Control.EventReceived -= OnRendererEvent;
        renderer.ProcessExited -= OnRendererExited;
        try
        {
            if (requestExit && _commands.IsConnected)
                _commands.ExitAsync().GetAwaiter().GetResult();
        }
        catch { }
        if (requestExit && !renderer.WaitForExit(TimeSpan.FromSeconds(10)))
            Diagnostic("Aether did not exit gracefully; terminating it.");
        renderer.Dispose();
        _commands.MarkDisconnected();
        _backends.RevokeAll();
    }

    private void DiagnosticStaleRendererPath(string path) =>
        Diagnostic($"Persisted renderer path was not found; using normal discovery: {path}");

    private string ResolveRendererPath(string? explicitPath, string? persistedPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return RendererLocator.Resolve(explicitPath);
        if (string.IsNullOrWhiteSpace(persistedPath))
            return RendererLocator.Resolve(null);
        try { return RendererLocator.Resolve(persistedPath); }
        catch (FileNotFoundException)
        {
            DiagnosticStaleRendererPath(persistedPath);
            return RendererLocator.Resolve(null);
        }
    }

    private void PersistAcceptedConfig(IReadOnlyList<string> configPaths, Guid? themeId = null)
    {
        try { _persistence.WriteState(configPaths, themeId ?? _activeThemeId); }
        catch (Exception ex) { Diagnostic($"Could not persist accepted configuration: {ex.Message}"); }
    }

    private void ReplaceTheme(ThemeDefinition oldTheme, ThemeDefinition replacement)
    {
        List<ThemeDefinition> next = _themes.Themes.Select(item => Equals(item, oldTheme) ? replacement : item).ToList();
        _persistence.WriteThemes(next);
        _themes = new ThemeRegistryState(2, next);
        NotifyUiStateChanged();
    }

    private static string[] NormalizeThemeFiles(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            throw new InvalidOperationException("Theme requires at least one theme file.");
        string[] normalized = files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string? missing = normalized.FirstOrDefault(path => !File.Exists(path));
        if (missing != null)
            throw new FileNotFoundException($"Theme file does not exist: {missing}", missing);
        return normalized;
    }

    private void ConfigureRendererLogging()
    {
        RendererSession? session = _renderer;
        if (session == null)
            return;
        try
        {
            session.Control.SendAsync(
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
                control = _renderer?.Control.State ?? "disconnected",
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
        RendererSession? session = _renderer;
        if (session == null)
            return ControlResponse.Failure(request.Id, 503, "renderer_unavailable", "Renderer is not running.");

        if (!ControlCodec.TryGetObject(request.Body, out System.Text.Json.JsonElement body) ||
            !body.TryGetProperty("protocol", out System.Text.Json.JsonElement protocol) ||
            !protocol.TryGetInt32(out int protocolVersion) || protocolVersion != 1 ||
            !body.TryGetProperty("renderer", out System.Text.Json.JsonElement rendererBody) || rendererBody.ValueKind != System.Text.Json.JsonValueKind.Object)
            return ControlResponse.Failure(request.Id, 400, "backend_request_invalid", "Backend connect body is invalid.");

        int pid = rendererBody.TryGetProperty("pid", out System.Text.Json.JsonElement pidValue) && pidValue.TryGetInt32(out int parsedPid)
            ? parsedPid
            : 0;
        string sid = ControlCodec.GetString(rendererBody, "sid") ?? "";
        string sessionId = ControlCodec.GetString(rendererBody, "sessionId") ?? "";
        if (pid != session.Process.Id)
            return ControlResponse.Failure(request.Id, 403, "backend_renderer_unauthorized", "Renderer process identity is invalid.");

        Guid? themeId = Guid.TryParse(ControlCodec.GetString(rendererBody, "themeId"), out Guid parsedThemeId)
            ? parsedThemeId
            : null;
        long? generation = rendererBody.TryGetProperty("themeGeneration", out System.Text.Json.JsonElement generationValue) &&
            generationValue.TryGetInt64(out long parsedGeneration)
            ? parsedGeneration
            : null;
        bool matchesActive = themeId == _activeThemeId && generation == _activeThemeGeneration;
        bool matchesPending = themeId == _pendingThemeId && generation == _pendingThemeGeneration;
        if ((themeId != null || generation != null) &&
            (_activeThemeId != null || _pendingThemeId != null) && !matchesActive && !matchesPending)
            return ControlResponse.Failure(request.Id, 409, "theme_session_stale", "Backend request belongs to an inactive theme session.");

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
        RequestRendererUnavailable("Renderer control channel disconnected.");
    }

    private void OnRendererEvent(string eventName, System.Text.Json.JsonElement? body)
    {
        if (eventName is not ("config.changed" or "window.changed"))
            return;
        if (body is { } eventBody &&
            eventBody.TryGetProperty("themeGeneration", out System.Text.Json.JsonElement generationValue) &&
            generationValue.TryGetInt64(out long eventGeneration) && eventGeneration < _activeThemeGeneration)
            return;
        try
        {
            _dispatcher.BeginInvoke((Action)(() =>
            {
                try
                {
                    RendererCapabilities capabilities = _commands.RefreshAsync().GetAwaiter().GetResult();
                    if (eventName == "config.changed" && capabilities.ConfigFiles.Count > 0)
                    {
                        if (capabilities.ThemeGeneration is { } generation && generation >= _activeThemeGeneration)
                        {
                            _activeThemeGeneration = generation;
                            _activeThemeId = capabilities.ThemeId ?? _activeThemeId;
                        }
                        PersistAcceptedConfig(capabilities.ConfigFiles, _activeThemeId);
                        _themeOrigin = "Aether event";
                    }
                    NotifyUiStateChanged();
                }
                catch (Exception ex) { Diagnostic($"Could not refresh renderer state: {ex.Message}"); }
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void OnRendererExited()
    {
        RequestRendererUnavailable("Aether process exited.");
    }

    private void RequestRendererUnavailable(string message)
    {
        try
        {
            _dispatcher.BeginInvoke((Action)(() =>
            {
                StopRenderer(requestExit: false);
                NotifyUiStateChanged();
                Diagnostic(message);
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void OnWinToggleChanged(bool enabled)
    {
        NotifyUiStateChanged();
    }

    private void ShowShellForm()
    {
        if (_exiting || _tray == null)
            return;
        if (_shellForm == null || _shellForm.IsDisposed)
        {
            _shellForm = new ShellForm(this);
            _shellForm.FormClosed += (_, _) => _shellForm = null;
        }
        _shellForm.Show();
        _shellForm.WindowState = FormWindowState.Normal;
        _shellForm.Activate();
    }

    private void NotifyUiStateChanged()
    {
        try { UiStateChanged?.Invoke(); }
        catch (Exception ex) { Diagnostic($"UI state update failed: {ex.Message}"); }
    }

    private void RequestExit()
    {
        if (_exiting)
            return;
        _exiting = true;
        try { _shellForm?.CloseForShutdown(); } catch { }
        try { _winToggle.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        StopRenderer(requestExit: true);
        _backends.Dispose();
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
