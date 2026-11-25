using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows.Forms;
using KhaosShell.Contracts;

namespace Chronos;

internal sealed class ChronosHostContext : ApplicationContext
{
    private readonly ControllerOptions _options;
    private readonly ChronosPaths _paths;
    private readonly PersistenceStore _persistence;
    private readonly LogStore _logs;
    private readonly BackendManager _backends;
    private readonly ThemeHookRunner _themeHooks;
    private readonly Control _dispatcher = new();
    private readonly RendererCommandService _commands;
    private RendererSession? _renderer;
    private readonly WinMenuToggleController _winToggle;
    private readonly EscapePanicController _escapePanic;
    private readonly KeybindController _keybinds;
    private readonly TrayController? _tray;
    private bool _statusPollingEnabled;
    private PersistentSettings _settings;
    private ThemeRegistryState _themes;
    private string _themeOrigin;
    private Guid? _activeThemeId;
    private PreparedTheme? _activeTheme;
    private long _activeThemeGeneration;
    private Guid? _pendingThemeId;
    private long? _pendingThemeGeneration;
    private ShellForm? _shellForm;
    private string? _rendererFailure;
    private bool _rendererStarting;
    private bool _exiting;
    private string? _managedMenuId;
    private bool _managedMenuAutoHide;

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
        _themeHooks = new ThemeHookRunner(Diagnostic);
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
        _winToggle = new WinMenuToggleController(ExecuteWindowsKeyMenuToggleAsync, winToggleEnabled, Diagnostic);
        _winToggle.StateChanged += OnWinToggleChanged;
        _escapePanic = new EscapePanicController(_settings.PanicEscapeEnabled, QueuePanicTrigger, Diagnostic);
        _escapePanic.StateChanged += OnEscapePanicChanged;
        _keybinds = new KeybindController(_settings.Keybinds ?? KeybindCatalog.DefaultBindings, DispatchKeybindAsync, Diagnostic);
        _tray = trayEnabled ? new TrayController(_commands, ShowMenuAsync, HideMenuAsync, _winToggle, ShowShellForm, RequestExit, Diagnostic) : null;

        PreparedTheme? preparedTheme = configPaths.Count == 0 ? null : ThemeConfigurationLoader.Load(configPaths);
        StartRenderer(configPaths, options.RendererPath, _settings.RendererPath, preparedTheme: preparedTheme);
    }

    public RendererCapabilities Capabilities => _commands.Capabilities;
    public bool StatusPollingEnabled => _statusPollingEnabled;
    public bool TrayEnabled => _tray != null;
    public bool TrayVisible => _tray?.Visible == true;
    public IReadOnlyList<ThemeDefinition> Themes => _themes.Themes;
    public string ThemesDirectory => _paths.ThemesDirectory;

    public ShellUiSnapshot GetUiSnapshot()
    {
        IReadOnlyList<KeybindDefinition> bindings = KeybindCatalog.SetWindowsBindingEnabled(
            _settings.Keybinds ?? KeybindCatalog.DefaultBindings,
            _winToggle.Enabled);
        return new(
            _commands.Capabilities,
            _commands.IsConnected,
            _tray != null,
            _tray?.Visible == true,
            _winToggle.Enabled,
            _escapePanic.Enabled,
            _themeOrigin,
            _rendererFailure,
            _statusPollingEnabled,
            _exiting,
            _settings with { Keybinds = bindings });
    }

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
        PreparedTheme preparedTheme = ThemeConfigurationLoader.Load(files);
        PreparedTheme? outgoingTheme = _activeTheme;
        _themeHooks.TerminateTrackedBefore();

        if (!_commands.IsTransportAvailable)
        {
            if (_renderer != null)
                StopRenderer(requestExit: false);
            long restartGeneration = checked(_activeThemeGeneration + 1);
            bool started = StartRenderer(
                files,
                _options.RendererPath,
                _settings.RendererPath,
                theme.Id,
                restartGeneration,
                preparedTheme);
            if (outgoingTheme != null)
                await RunAfterAsync(outgoingTheme).ConfigureAwait(true);
            if (!started)
            {
                _activeTheme = null;
                return AetherCommandResult.Failure("Renderer is unavailable; theme was not applied.");
            }

            _activeThemeId = theme.Id;
            _activeThemeGeneration = restartGeneration;
            _themeOrigin = "UI-applied";
            PersistAcceptedConfig(files, _activeThemeId);
            NotifyUiStateChanged();
            return new AetherCommandResult(true, "OK configuration applied");
        }

        long generation = checked(_activeThemeGeneration + 1);
        _pendingThemeId = theme.Id;
        _pendingThemeGeneration = generation;
        try
        {
            await _themeHooks.RunBeforeAsync(preparedTheme.Before).ConfigureAwait(true);
            await DeleteManagedMenuAsync().ConfigureAwait(true);
            AetherCommandResult result = await _commands.ApplyConfigAsync(
                Array.Empty<string>(),
                theme.Id,
                generation,
                preparedTheme.AetherJson,
                files).ConfigureAwait(true);
            if (!result.Success)
            {
                if (outgoingTheme != null)
                {
                    AetherCommandResult restoreResult = await CreateManagedMenuAsync(outgoingTheme).ConfigureAwait(true);
                    if (!restoreResult.Success)
                        Diagnostic($"Could not restore the previous managed menu: {restoreResult.Error ?? restoreResult.Payload}");
                }
                _themeHooks.TerminateTrackedBefore();
                return result;
            }

            if (preparedTheme.MenuJson != null)
            {
                AetherCommandResult menuResult = await CreateManagedMenuAsync(preparedTheme).ConfigureAwait(true);
                if (!menuResult.Success)
                    return menuResult;
            }
                RendererCapabilities capabilities = await _commands.RefreshAsync().ConfigureAwait(true);
                if (PathsEqual(files, capabilities.ConfigFiles))
                {
                    _activeTheme = preparedTheme;
                    _activeThemeId = theme.Id;
                    _activeThemeGeneration = capabilities.ThemeGeneration ?? generation;
                    PersistAcceptedConfig(capabilities.ConfigFiles, _activeThemeId);
                    _themeOrigin = "UI-applied";
                }
                NotifyUiStateChanged();
                if (outgoingTheme != null)
                    await RunAfterAsync(outgoingTheme).ConfigureAwait(true);
            return result;
        }
        finally
        {
            _pendingThemeId = null;
            _pendingThemeGeneration = null;
        }
    }

    public async Task<AetherCommandResult> UnloadThemeAsync()
    {
        PreparedTheme? outgoingTheme = _activeTheme;
        if (_renderer != null && _commands.IsTransportAvailable)
        {
            try
            {
                await DeleteManagedMenuAsync().ConfigureAwait(true);
                ControlResponse response = await _renderer.Control.SendAsync(
                    "POST",
                    "/v1/renderer/configure",
                    new
                    {
                        protocolVersion = 1,
                        ultralightRoot = _settings.UltralightRoot,
                        noTray = true,
                        config = (object?)null,
                        themeId = (Guid?)null,
                        themeGeneration = (long?)null
                    },
                    TimeSpan.FromSeconds(30)).ConfigureAwait(true);
                if (response.Status is < 200 or >= 300)
                    return AetherCommandResult.Failure(response.Error?.Message ?? $"Aether unload failed with status {response.Status}.");
            }
            catch (Exception ex)
            {
                return AetherCommandResult.Failure(ex.Message);
            }
        }
        _themeHooks.TerminateTrackedBefore();
        _activeThemeId = null;
        _activeTheme = null;
        _managedMenuId = null;
        _commands.ClearManagedMenu();
        _activeThemeGeneration = 0;
        _pendingThemeId = null;
        _pendingThemeGeneration = null;
        _themeOrigin = "None";
        _rendererFailure = null;
        PersistAcceptedConfig(Array.Empty<string>(), null);
        NotifyUiStateChanged();
        if (outgoingTheme != null)
            await RunAfterAsync(outgoingTheme).ConfigureAwait(true);
        return new AetherCommandResult(true, "OK theme unloaded");
    }

    public void SetCurrentWinToggle(bool enabled)
    {
        _winToggle.SetEnabled(enabled);
        IReadOnlyList<KeybindDefinition> bindings = KeybindCatalog.SetWindowsBindingEnabled(
            _settings.Keybinds ?? KeybindCatalog.DefaultBindings,
            _winToggle.Enabled);
        UpdateSettings(_settings with { WindowsKeyOverride = _winToggle.Enabled, Keybinds = bindings });
    }

    public void SetEscapePanicEnabled(bool enabled)
    {
        _escapePanic.SetEnabled(enabled);
        UpdateSettings(_settings with { PanicEscapeEnabled = enabled });
    }

    public void SetKeybinds(IReadOnlyList<KeybindDefinition> bindings)
    {
        IReadOnlyList<KeybindDefinition> normalized = KeybindCatalog.EnsureWindowsBinding(
            bindings,
            _winToggle.Enabled);
        KeybindDefinition windows = normalized.First(binding => binding.SystemBinding);
        _keybinds.SetBindings(normalized);
        _winToggle.SetEnabled(windows.Enabled);
        UpdateSettings(_settings with { WindowsKeyOverride = windows.Enabled, Keybinds = normalized });
    }

    public void SetWindowsKeyMenuPolicy(
        WindowsKeyMenuHideMode hideMode,
        string? hideCue,
        bool showCueEnabled,
        string? showCue)
    {
        UpdateSettings(_settings with
        {
            WindowsKeyMenuHideMode = hideMode is WindowsKeyMenuHideMode.Cue
                ? WindowsKeyMenuHideMode.Cue
                : WindowsKeyMenuHideMode.Direct,
            WindowsKeyMenuHideCue = NormalizeCue(hideCue, "hide-menu"),
            WindowsKeyMenuShowCueEnabled = showCueEnabled,
            WindowsKeyMenuShowCue = NormalizeCue(showCue, "show-menu")
        });
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

    public void SetUltralightRoot(string? path)
    {
        string? normalized = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path.Trim());
        UpdateSettings(_settings with { UltralightRoot = normalized });
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
    public Task<AetherCommandResult> ShowMenuAsync() => SetManagedMenuVisibilityAsync(true);
    public Task<AetherCommandResult> HideMenuAsync() => SetManagedMenuVisibilityAsync(false);
    public async Task<AetherCommandResult> ToggleMenuAsync()
    {
        (AetherCommandResult result, RendererMenuVisibility? visibility) = await QueryManagedMenuVisibilityAsync().ConfigureAwait(false);
        return !result.Success || visibility == null
            ? result
            : await SetManagedMenuVisibilityAsync(!visibility.Visible).ConfigureAwait(false);
    }
    public Task<AetherCommandResult> SetWindowVisibilityAsync(string id, bool visible) => _commands.SetWindowVisibilityAsync(id, visible);
    public void RequestExitFromUi() => RequestExit();

    private async Task DispatchKeybindAsync(KeybindDefinition binding)
    {
        KeybindAction action = binding.EffectiveAction;
        if (action.Kind == KeybindActionKind.ManagedMenu)
        {
            AetherCommandResult result = action.MenuAction switch
            {
                ManagedMenuAction.Show => await ShowMenuAsync().ConfigureAwait(false),
                ManagedMenuAction.Hide => await HideMenuAsync().ConfigureAwait(false),
                _ => await ToggleMenuAsync().ConfigureAwait(false)
            };
            if (!result.Success)
                Diagnostic(result.Error ?? result.Payload);
            return;
        }

        if (action.Kind == KeybindActionKind.AetherCommand)
        {
            AetherCommandResult result = await _commands.ExecuteAsync(action.ToRendererCommand()).ConfigureAwait(false);
            if (!result.Success)
                Diagnostic(result.Error ?? result.Payload);
            return;
        }

        if (_renderer?.Control is not { } control)
        {
            Diagnostic($"Keybind '{binding.DisplayName}' failed: renderer is not running.");
            return;
        }

        try
        {
            foreach (string cueName in action.EffectiveCueNames)
                await control.SendCueAsync(cueName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic($"Keybind '{binding.DisplayName}' cue failed: {ex.Message}");
        }
    }

    private async Task<AetherCommandResult> ExecuteWindowsKeyMenuToggleAsync()
    {
        (AetherCommandResult queryResult, RendererMenuVisibility? visibility) =
            await QueryManagedMenuVisibilityAsync().ConfigureAwait(false);
        if (!queryResult.Success || visibility == null)
            return queryResult;

        if (visibility.Visible)
        {
            if (_settings.WindowsKeyMenuHideMode == WindowsKeyMenuHideMode.Cue)
                return await SendWindowsKeyCueAsync(_settings.WindowsKeyMenuHideCue).ConfigureAwait(false);
            return await SetManagedMenuVisibilityAsync(false).ConfigureAwait(false);
        }

        AetherCommandResult showResult = await SetManagedMenuVisibilityAsync(true).ConfigureAwait(false);
        if (!showResult.Success || !_settings.WindowsKeyMenuShowCueEnabled)
            return showResult;
        return await SendWindowsKeyCueAsync(_settings.WindowsKeyMenuShowCue).ConfigureAwait(false);
    }

    private async Task<(AetherCommandResult Result, RendererMenuVisibility? Visibility)> QueryManagedMenuVisibilityAsync()
    {
        RendererSession? renderer = _renderer;
        string? menuId = _managedMenuId;
        if (renderer?.Control is not { State: "connected" } control || string.IsNullOrWhiteSpace(menuId))
            return (AetherCommandResult.Failure("Renderer is not running."), null);
        try
        {
            ControlResponse response = await control.SendAsync(
                "GET", $"/v1/renderer/windows/{Uri.EscapeDataString(menuId)}", null, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (response.Status is < 200 or >= 300)
                return (AetherCommandResult.Failure(response.Error?.Message ?? "Menu query failed."), null);
            if (response.Body is not { } body || body.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !body.TryGetProperty("id", out System.Text.Json.JsonElement idValue) ||
                idValue.ValueKind != System.Text.Json.JsonValueKind.String ||
                string.IsNullOrWhiteSpace(idValue.GetString()))
            {
                return (AetherCommandResult.Failure("No menu window is configured."), null);
            }

            bool visible = body.TryGetProperty("visible", out System.Text.Json.JsonElement visibleValue) &&
                visibleValue.ValueKind == System.Text.Json.JsonValueKind.True;
            return (new AetherCommandResult(true, "OK menu state queried"), new RendererMenuVisibility(idValue.GetString()!, visible));
        }
        catch (Exception ex)
        {
            return (AetherCommandResult.Failure(ex.Message), null);
        }
    }

    private async Task<AetherCommandResult> SetManagedMenuVisibilityAsync(bool visible)
    {
        RendererSession? renderer = _renderer;
        string? menuId = _managedMenuId;
        if (renderer?.Control is not { State: "connected" } control || string.IsNullOrWhiteSpace(menuId))
            return AetherCommandResult.Failure("Renderer is not running.");
        try
        {
            ControlResponse response = await control.SendAsync(
                "POST",
                $"/v1/renderer/windows/{Uri.EscapeDataString(menuId)}/actions",
                new { action = visible ? "show" : "hide" },
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (response.Status == 404 && visible && _activeTheme?.MenuJson != null)
            {
                AetherCommandResult recreated = await CreateManagedMenuAsync(_activeTheme).ConfigureAwait(false);
                if (recreated.Success)
                    return recreated;
            }
            if (response.Status is < 200 or >= 300)
                return AetherCommandResult.Failure(response.Error?.Message ?? "Menu command failed.");
            _commands.SetManagedMenu(menuId, visible);
            return new AetherCommandResult(true, $"OK {(visible ? "showed" : "hid")} menu");
        }
        catch (Exception ex)
        {
            return AetherCommandResult.Failure(ex.Message);
        }
    }

    private async Task<AetherCommandResult> CreateManagedMenuAsync(PreparedTheme theme)
    {
        if (theme.MenuJson == null)
        {
            _managedMenuId = null;
            _commands.ClearManagedMenu();
            return new AetherCommandResult(true, "OK no managed menu");
        }

        RendererSession? renderer = _renderer;
        if (renderer?.Control is not { State: "connected" } control)
            return AetherCommandResult.Failure("Renderer is not running.");
        try
        {
            using JsonDocument definition = JsonDocument.Parse(theme.MenuJson);
            ControlResponse response = await control.SendAsync(
                "POST", "/v1/renderer/windows", definition.RootElement, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (response.Status is < 200 or >= 300)
                return AetherCommandResult.Failure(response.Error?.Message ?? "Menu window creation failed.");
            if (response.Body is not { } window || window.ValueKind != JsonValueKind.Object ||
                !window.TryGetProperty("id", out JsonElement id) ||
                id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                return AetherCommandResult.Failure("Aether returned an invalid managed menu window.");

            _managedMenuId = id.GetString();
            _managedMenuAutoHide = theme.MenuAutoHide;
            _commands.SetManagedMenu(_managedMenuId!, true);
            return new AetherCommandResult(true, "OK menu window created");
        }
        catch (Exception ex)
        {
            return AetherCommandResult.Failure(ex.Message);
        }
    }

    private async Task DeleteManagedMenuAsync()
    {
        string? menuId = _managedMenuId;
        _managedMenuId = null;
        _managedMenuAutoHide = false;
        _commands.ClearManagedMenu();
        if (string.IsNullOrWhiteSpace(menuId) || _renderer?.Control is not { State: "connected" } control)
            return;
        try
        {
            await control.SendAsync(
                "DELETE", $"/v1/renderer/windows/{Uri.EscapeDataString(menuId)}", null, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic($"Managed menu cleanup failed: {ex.Message}");
        }
    }

    private async Task<AetherCommandResult> SendWindowsKeyCueAsync(string cueName)
    {
        if (_renderer?.Control is not { } control)
            return AetherCommandResult.Failure("Renderer is not running.");
        try
        {
            await control.SendCueAsync(cueName).ConfigureAwait(false);
            return new AetherCommandResult(true, "OK cue sent");
        }
        catch (Exception ex)
        {
            return AetherCommandResult.Failure(ex.Message);
        }
    }

    private static string NormalizeCue(string? cue, string fallback) =>
        string.IsNullOrWhiteSpace(cue) ? fallback : cue.Trim();

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
        string? persistedRendererPath,
        Guid? startupThemeId = null,
        long? startupThemeGeneration = null,
        PreparedTheme? preparedTheme = null)
    {
        if (_renderer != null)
            return _commands.IsTransportAvailable;

        RendererSession? startedRenderer = null;
        JsonDocument? inlineDocument = null;
        var stderr = new List<string>();
        var transportReady = new ManualResetEventSlim(false);
        Action<string, System.Text.Json.JsonElement?>? startupEvent = null;
        _rendererStarting = true;
        try
        {
            string rendererPath = ResolveRendererPath(explicitRendererPath, persistedRendererPath);
            _commands.BeginConnection();
            startedRenderer = RendererSession.Start(
                rendererPath,
                HandleControlRequestAsync,
                line => Diagnostic($"[Aether] {line}"),
                line =>
                {
                    lock (stderr) stderr.Add(line);
                    Diagnostic($"[Aether] {line}");
                });
            _renderer = startedRenderer;
            startedRenderer.Control.Disconnected += OnControlDisconnected;
            startedRenderer.Control.EventReceived += OnRendererEvent;
            startedRenderer.ProcessExited += OnRendererExited;
            startupEvent = (name, _) =>
            {
                if (name == "renderer.transport.ready")
                    transportReady.Set();
            };
            startedRenderer.Control.EventReceived += startupEvent;
            startedRenderer.StartControl();

            DateTime transportDeadline = DateTime.UtcNow.AddSeconds(15);
            while (!transportReady.IsSet && DateTime.UtcNow < transportDeadline && !startedRenderer.Process.HasExited)
                transportReady.Wait(TimeSpan.FromMilliseconds(100));
            if (!transportReady.IsSet)
                throw new InvalidOperationException(DescribeRendererProcessFailure(startedRenderer.Process, stderr, "Aether control channel did not become ready."));

            if (preparedTheme != null)
                _themeHooks.RunBeforeAsync(preparedTheme.Before).GetAwaiter().GetResult();

            inlineDocument = preparedTheme == null
                ? null
                : JsonDocument.Parse(preparedTheme.AetherJson);
            object? config = preparedTheme == null
                ? null
                : new
                {
                    mode = "json",
                    document = inlineDocument!.RootElement,
                    sourcePaths = preparedTheme.SourcePaths
                };
            ControlResponse startup = startedRenderer.Control.SendAsync(
                    "POST",
                    "/v1/renderer/configure",
                    new
                    {
                        protocolVersion = 1,
                        ultralightRoot = _settings.UltralightRoot,
                        noTray = true,
                        config,
                        themeId = startupThemeId ?? _activeThemeId,
                        themeGeneration = startupThemeGeneration ?? (_activeThemeGeneration == 0 ? (long?)null : _activeThemeGeneration)
                    },
                    TimeSpan.FromSeconds(30))
                .GetAwaiter()
                .GetResult();
            if (startup.Status is < 200 or >= 300)
                throw new InvalidOperationException(startup.Error?.Message ?? $"Aether startup request failed with status {startup.Status}.");

            WaitForRenderer(configPaths);

            if (preparedTheme != null)
            {
                AetherCommandResult menuResult = CreateManagedMenuAsync(preparedTheme).GetAwaiter().GetResult();
                if (!menuResult.Success)
                    throw new InvalidOperationException(menuResult.Error ?? menuResult.Payload);
            }

            if (configPaths.Count > 0)
                PersistAcceptedConfig(configPaths);

            ConfigureRendererLogging();
            _activeTheme = preparedTheme;
            _rendererFailure = null;
            NotifyUiStateChanged();
            return true;
        }
        catch (Exception ex)
        {
            _themeHooks.TerminateTrackedBefore();
            string message = ex.Message;
            if (startedRenderer != null)
                message = DescribeRendererProcessFailure(startedRenderer.Process, stderr, message);
            _rendererFailure = message;
            Diagnostic($"Aether startup failed; Chronos remains available: {message}");
            if (startedRenderer != null)
            {
                startedRenderer.Control.Disconnected -= OnControlDisconnected;
                startedRenderer.Control.EventReceived -= OnRendererEvent;
                startedRenderer.ProcessExited -= OnRendererExited;
                try { startedRenderer.Dispose(); } catch { }
            }
            _renderer = null;
            _commands.MarkDisconnected();
            _managedMenuId = null;
            _managedMenuAutoHide = false;
            NotifyUiStateChanged();
            ShowRendererFailure(message);
            return false;
        }
        finally
        {
            inlineDocument?.Dispose();
            _rendererStarting = false;
            if (startedRenderer != null && startupEvent != null)
                startedRenderer.Control.EventReceived -= startupEvent;
            transportReady.Dispose();
        }
    }

    private static string DescribeRendererProcessFailure(Process process, IReadOnlyList<string> stderr, string fallback)
    {
        if (process.HasExited && process.ExitCode == 3)
            return "Aether is already running outside Chronos; close it before starting Chronos.";
        if (stderr.Count > 0)
            return process.HasExited
                ? $"{fallback} {stderr[^1]} (exit code {process.ExitCode})."
                : $"{fallback} {stderr[^1]}";
        return process.HasExited ? $"{fallback} (exit code {process.ExitCode})." : fallback;
    }

    private void ShowRendererFailure(string message)
    {
        try
        {
            MessageBox.Show(_shellForm, message, "Aether startup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (InvalidOperationException) { }
    }

    private void StopRenderer(bool requestExit)
    {
        RendererSession? renderer = _renderer;
        _renderer = null;
        _managedMenuId = null;
        _managedMenuAutoHide = false;
        _commands.ClearManagedMenu();
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
            protocol.ValueKind != System.Text.Json.JsonValueKind.Number ||
            !protocol.TryGetInt32(out int protocolVersion) || protocolVersion != 1 ||
            !body.TryGetProperty("renderer", out System.Text.Json.JsonElement rendererBody) || rendererBody.ValueKind != System.Text.Json.JsonValueKind.Object)
            return ControlResponse.Failure(request.Id, 400, "backend_request_invalid", "Backend connect body is invalid.");

        int pid = rendererBody.TryGetProperty("pid", out System.Text.Json.JsonElement pidValue) &&
            pidValue.ValueKind == System.Text.Json.JsonValueKind.Number &&
            pidValue.TryGetInt32(out int parsedPid)
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
            generationValue.ValueKind == System.Text.Json.JsonValueKind.Number &&
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
        if (_rendererStarting)
            return;
        RequestRendererUnavailable("Renderer control channel disconnected.");
    }

    private void OnRendererEvent(string eventName, System.Text.Json.JsonElement? body)
    {
        if (eventName == "window.focusChanged" && body is { } focusBody)
        {
            string? id = focusBody.TryGetProperty("window", out JsonElement focusWindow) &&
                         focusWindow.ValueKind == JsonValueKind.Object
                ? ControlCodec.GetString(focusWindow, "id")
                : null;
            bool focused = focusWindow.ValueKind == JsonValueKind.Object &&
                           focusWindow.TryGetProperty("focused", out JsonElement focusedValue) &&
                           focusedValue.ValueKind == JsonValueKind.True;
            if (_managedMenuAutoHide && !focused && string.Equals(id, _managedMenuId, StringComparison.Ordinal))
            {
                try
                {
                    _dispatcher.BeginInvoke((Action)(() =>
                    {
                        _ = SetManagedMenuVisibilityAsync(false).ContinueWith(
                            task =>
                            {
                                if (task.IsFaulted)
                                    Diagnostic($"Managed menu auto-hide failed: {task.Exception?.GetBaseException().Message}");
                            },
                            CancellationToken.None,
                            TaskContinuationOptions.None,
                            TaskScheduler.Default);
                    }));
                }
                catch (InvalidOperationException) { }
            }
            NotifyUiStateChanged();
            return;
        }

        if (eventName is not ("config.changed" or "window.changed"))
            return;
        if (body is { } eventBody &&
            eventBody.TryGetProperty("themeGeneration", out System.Text.Json.JsonElement generationValue) &&
            generationValue.ValueKind == System.Text.Json.JsonValueKind.Number &&
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

    public void EmitCue(string name, object? data = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Cue name must not be empty.", nameof(name));
        RendererSession? renderer = _renderer;
        if (renderer == null)
            return;
        renderer.Control.SendCueAsync(name, data).ContinueWith(
            _ => { },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private void OnRendererExited(RendererSession exitedRenderer)
    {
        if (_rendererStarting)
            return;
        if (!ReferenceEquals(_renderer, exitedRenderer))
            return;
        RequestRendererUnavailable("Aether process exited.");
    }

    private void RequestRendererUnavailable(string message)
    {
        try
        {
            _dispatcher.BeginInvoke((Action)(() =>
            {
                PreparedTheme? outgoingTheme = _activeTheme;
                StopRenderer(requestExit: false);
                _themeHooks.TerminateTrackedBefore();
                _activeTheme = null;
                if (outgoingTheme != null)
                    RunAfterAsync(outgoingTheme).GetAwaiter().GetResult();
                _rendererFailure = message;
                NotifyUiStateChanged();
                Diagnostic(message);
                ShowRendererFailure(message);
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void OnWinToggleChanged(bool enabled)
    {
        NotifyUiStateChanged();
    }

    private void OnEscapePanicChanged(bool enabled)
    {
        NotifyUiStateChanged();
    }

    private void QueuePanicTrigger()
    {
        if (_exiting)
            return;
        try { _dispatcher.BeginInvoke((Action)TriggerPanic); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    private void TriggerPanic()
    {
        if (_exiting)
            return;
        RequestExit();
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

    private Task RunAfterAsync(PreparedTheme theme) => _themeHooks.RunAfterAsync(theme.After);

    private void RequestExit()
    {
        if (_exiting)
            return;
        _exiting = true;
        PreparedTheme? outgoingTheme = _activeTheme;
        try
        {
            try { _shellForm?.CloseForShutdown(); } catch { }
            try { _winToggle.Dispose(); } catch { }
            try { _escapePanic.Dispose(); } catch { }
            try { _keybinds.Dispose(); } catch { }
            try { _tray?.Dispose(); } catch { }
            try { StopRenderer(requestExit: true); } catch (Exception ex) { Diagnostic($"Renderer shutdown failed: {ex.Message}"); }
            try { _backends.Dispose(); } catch (Exception ex) { Diagnostic($"Backend shutdown failed: {ex.Message}"); }
        }
        finally
        {
            _themeHooks.TerminateTrackedBefore();
            _activeTheme = null;
            if (outgoingTheme != null)
                RunAfterAsync(outgoingTheme).GetAwaiter().GetResult();
            _dispatcher.Dispose();
            Application.ExitThread();
        }
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
