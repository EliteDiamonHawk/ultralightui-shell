using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using KhaosShell.Contracts;

namespace Chronos;

internal sealed record AetherCommandResult(
    bool Success,
    string Payload,
    string? Error = null,
    bool TransportFailure = false,
    int? Status = null,
    string? ErrorCode = null)
{
    public static AetherCommandResult Failure(
        string message,
        int? status = null,
        string? errorCode = null,
        bool transportFailure = false) =>
        new(false, $"ERR {message}", message, transportFailure, status, errorCode);

    public static AetherCommandResult FromResponse(ControlResponse response, string fallback)
    {
        if (response.Status is >= 200 and < 300)
        {
            string payload = response.Body is { } body ? body.GetRawText() : "OK";
            return new(true, payload, payload, false, response.Status, response.Error?.Code);
        }

        string message = response.Error?.Message ?? $"{fallback} (status {response.Status}).";
        return new(false, $"ERR {message}", message, false, response.Status, response.Error?.Code);
    }

    public static AetherCommandResult Transport(string message) =>
        Failure(message, transportFailure: true);
}

internal sealed class AetherCommandClient
{
    internal const string PipeName = "Aether.Commands";
    private readonly string _machine = ".";

    public async Task<AetherCommandResult> SendAsync(RendererCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var pipe = new NamedPipeClientStream(_machine, PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(RendererCommandCodec.Serialize(command).AsMemory(), timeoutSource.Token).ConfigureAwait(false);
            BoundedLine frame = await new BoundedLineReader(pipe).ReadAsync(
                RendererCommandCodec.MaxFrameBytes,
                timeoutSource.Token).ConfigureAwait(false);
            string response = frame.EndOfStream
                ? "ERR No response from Aether."
                : frame.TooLarge
                    ? "ERR Aether response exceeded 1 MiB."
                    : frame.Text ?? "ERR No response from Aether.";
            return ParseResponse(command, response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AetherCommandResult.Transport("Aether command timed out.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            return AetherCommandResult.Transport(ex.Message);
        }
    }

    internal static AetherCommandResult ParseResponse(RendererCommand command, string response)
    {
        if (response.StartsWith("ERR ", StringComparison.Ordinal))
            return new AetherCommandResult(false, response, response[4..]);
        if (response.StartsWith("OK ", StringComparison.Ordinal))
            return new AetherCommandResult(true, response, response[3..]);

        if (command.Kind == RendererCommandKind.Show && IsJsonObject(response))
            return new AetherCommandResult(true, response, response);

        return AetherCommandResult.Transport($"Invalid response from Aether: {response}");
    }

    private static bool IsJsonObject(string response)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(response);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal sealed record RendererWindowCapability(string Id, string? Group, string? Kind, bool? Visible, bool? Loaded = null);

internal sealed record RendererMenuVisibility(string Id, bool Visible, bool Loaded);

internal sealed record RendererCapabilities(
    IReadOnlyList<string> ConfigFiles,
    IReadOnlyList<RendererWindowCapability> Windows,
    IReadOnlyList<string> Groups,
    bool HasMenu,
    string PipeState,
    IReadOnlyList<string> Cues,
    Guid? ThemeId = null,
    Guid? ThemeActivationId = null,
    long? ThemeGeneration = null,
    string? RendererSessionId = null)
{
    public static RendererCapabilities Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<RendererWindowCapability>(),
        Array.Empty<string>(),
        false,
        "disconnected",
        Array.Empty<string>());
}

internal sealed class RendererCommandService
{
    private readonly AetherCommandClient _client;
    private readonly object _sync = new();
    private RendererCapabilities _capabilities = RendererCapabilities.Empty;
    private RendererWindowCapability? _managedMenu;
    private ControlChannel? _managedControl;
    private bool _connected;
    private bool _transportAvailable;

    public RendererCommandService(AetherCommandClient client) => _client = client;

    public bool IsConnected
    {
        get { lock (_sync) return _connected; }
    }

    public bool IsTransportAvailable
    {
        get { lock (_sync) return _transportAvailable; }
    }

    public RendererCapabilities Capabilities
    {
        get { lock (_sync) return _capabilities; }
    }

    public void BeginConnection()
    {
        lock (_sync) _transportAvailable = true;
    }

    public void UseManagedControl(ControlChannel control)
    {
        lock (_sync)
        {
            _managedControl = control;
            _transportAvailable = true;
        }
    }

    public void ClearManagedControl()
    {
        lock (_sync) _managedControl = null;
    }

    public async Task<RendererCapabilities> RefreshAsync(CancellationToken cancellationToken = default)
    {
        AetherCommandResult config = await SendAsync(new(AetherCommandKind.Show, Topic: "config"), cancellationToken).ConfigureAwait(false);
        AetherCommandResult windows = await SendAsync(new(AetherCommandKind.Show, Topic: "windows"), cancellationToken).ConfigureAwait(false);
        AetherCommandResult pipes = await SendAsync(new(AetherCommandKind.Show, Topic: "pipes"), cancellationToken).ConfigureAwait(false);
        if (!config.Success || !windows.Success || !pipes.Success)
            return Capabilities;

        RendererCapabilities next = ParseCapabilities(config.Payload, windows.Payload, pipes.Payload);
        lock (_sync)
        {
            next = ReconcileManagedMenu(next, _managedMenu, keepWhenMissing: false);
            _capabilities = next;
            _connected = true;
        }
        return next;
    }

    public Task<AetherCommandResult> SetWindowVisibilityAsync(string id, bool visible, CancellationToken cancellationToken = default) =>
        SendAsync(new(visible ? AetherCommandKind.RevealWindow : AetherCommandKind.HideWindow, TargetId: id), cancellationToken);

    public Task<AetherCommandResult> UnloadWindowAsync(string id, CancellationToken cancellationToken = default) =>
        SendAsync(new(AetherCommandKind.UnloadWindow, TargetId: id), cancellationToken);

    public Task<AetherCommandResult> ReloadWindowAsync(string id, CancellationToken cancellationToken = default) =>
        SendAsync(new(AetherCommandKind.ReloadWindow, TargetId: id), cancellationToken);

    public void SetManagedMenu(string id, bool visible)
    {
        lock (_sync)
        {
            _managedMenu = new RendererWindowCapability(id, "chronos-menu", "menu", visible);
            _capabilities = ReconcileManagedMenu(_capabilities, _managedMenu, keepWhenMissing: true);
        }
    }

    public void ClearManagedMenu()
    {
        lock (_sync)
        {
            string? id = _managedMenu?.Id;
            _managedMenu = null;
            _capabilities = _capabilities with
            {
                Windows = _capabilities.Windows.Where(item =>
                    !string.Equals(item.Kind, "menu", StringComparison.OrdinalIgnoreCase) &&
                    (id == null || !string.Equals(item.Id, id, StringComparison.Ordinal))).ToArray(),
                HasMenu = false
            };
        }
    }

    internal static RendererCapabilities ReconcileManagedMenu(
        RendererCapabilities capabilities,
        RendererWindowCapability? managedMenu,
        bool keepWhenMissing)
    {
        if (managedMenu == null)
        {
            return capabilities with
            {
                Windows = capabilities.Windows
                    .Where(item => !string.Equals(item.Kind, "menu", StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
                HasMenu = false
            };
        }

        RendererWindowCapability? rendererWindow = capabilities.Windows.FirstOrDefault(item =>
            string.Equals(item.Id, managedMenu.Id, StringComparison.Ordinal) &&
            !string.Equals(item.Kind, "menu", StringComparison.OrdinalIgnoreCase));
        if (rendererWindow == null && !keepWhenMissing)
        {
            return capabilities with
            {
                Windows = capabilities.Windows
                    .Where(item => !string.Equals(item.Kind, "menu", StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
                HasMenu = false
            };
        }

        RendererWindowCapability classified = rendererWindow == null
            ? managedMenu
            : new RendererWindowCapability(
                rendererWindow.Id,
                rendererWindow.Group ?? managedMenu.Group,
                "menu",
                rendererWindow.Visible,
                rendererWindow.Loaded);
        bool inserted = false;
        var windows = new List<RendererWindowCapability>();
        foreach (RendererWindowCapability window in capabilities.Windows)
        {
            if (string.Equals(window.Kind, "menu", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(window.Id, managedMenu.Id, StringComparison.Ordinal))
            {
                if (!inserted)
                {
                    windows.Add(classified);
                    inserted = true;
                }
                continue;
            }
            windows.Add(window);
        }

        if (!inserted && keepWhenMissing)
            windows.Add(classified);
        return capabilities with { Windows = windows, HasMenu = inserted || keepWhenMissing };
    }

    public async Task<AetherCommandResult> ApplyConfigAsync(
        IReadOnlyList<string> paths,
        Guid? themeId = null,
        long? generation = null,
        string? configJson = null,
        IReadOnlyList<string>? sourcePaths = null,
        string? rendererSessionId = null,
        CancellationToken cancellationToken = default)
    {
        Guid activationId = Guid.NewGuid();
        AetherCommandResult result = await SendAsync(
            new(AetherCommandKind.ApplyConfig, ConfigPaths: paths, ThemeId: themeId,
                ThemeActivationId: activationId, ThemeGeneration: generation,
                ConfigJson: configJson, ConfigSourcePaths: sourcePaths,
                RendererSessionId: rendererSessionId),
            cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            lock (_sync)
                _capabilities = _capabilities with
                {
                    ThemeId = themeId ?? _capabilities.ThemeId,
                    ThemeActivationId = activationId,
                    ThemeGeneration = generation ?? (_capabilities.ThemeGeneration ?? 0) + 1,
                    RendererSessionId = rendererSessionId ?? _capabilities.RendererSessionId
                };
        }
        return result;
    }

    public Task<AetherCommandResult> PauseAsync(string? group, bool all, CancellationToken cancellationToken = default) =>
        SendAsync(new(AetherCommandKind.Pause, Group: group, All: all), cancellationToken);

    public Task<AetherCommandResult> ResumeAsync(string? group, bool all, CancellationToken cancellationToken = default) =>
        SendAsync(new(AetherCommandKind.Resume, Group: group, All: all), cancellationToken);

    public Task<AetherCommandResult> ReloadAsync(string? group, bool all, CancellationToken cancellationToken = default) =>
        SendAsync(new(AetherCommandKind.Reload, Group: group, All: all), cancellationToken);

    public Task<AetherCommandResult> StatusAsync(string? group, bool all, CancellationToken cancellationToken = default) =>
        SendAsync(new(AetherCommandKind.Status, Group: group, All: all), cancellationToken);

    public Task<AetherCommandResult> ExitAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new(AetherCommandKind.Exit), cancellationToken);

    public Task<AetherCommandResult> ExecuteAsync(RendererCommand command, CancellationToken cancellationToken = default) =>
        SendAsync(command, cancellationToken);

    public void MarkDisconnected()
    {
        lock (_sync)
        {
            _transportAvailable = false;
            _connected = false;
            _managedMenu = null;
            _capabilities = RendererCapabilities.Empty;
        }
    }

    private async Task<AetherCommandResult> SendAsync(AetherCommand command, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (!_transportAvailable)
                return AetherCommandResult.Failure("Renderer is not running.");
        }

        RendererCommand request;
        ControlChannel? managedControl;
        lock (_sync)
        {
            RendererCapabilities current = _capabilities;
            request = command with
            {
                ThemeId = command.ThemeId ?? current.ThemeId,
                ThemeActivationId = command.ThemeActivationId ?? current.ThemeActivationId,
                ThemeGeneration = command.ThemeGeneration ?? current.ThemeGeneration,
                RendererSessionId = command.RendererSessionId ?? current.RendererSessionId
            };
            managedControl = _managedControl;
        }

        AetherCommandResult result = managedControl == null
            ? await _client.SendAsync(request, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false)
            : await SendManagedAsync(managedControl, request, cancellationToken).ConfigureAwait(false);
        if (!result.Success && result.TransportFailure)
            MarkDisconnected();
        return result;
    }

    private static async Task<AetherCommandResult> SendManagedAsync(
        ControlChannel control,
        RendererCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            ControlResponse response;
            if (command.Kind == RendererCommandKind.Show && command.Topic is "config" or "windows" or "pipes")
            {
                string path = command.Topic switch
                {
                    "config" => "/v1/renderer/config",
                    "windows" => "/v1/renderer/windows",
                    _ => "/v1/pipes"
                };
                response = await control.SendAsync("GET", path, null, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                if (response.Status is >= 200 and < 300 && response.Body is { } body)
                    return new AetherCommandResult(true, body.GetRawText(), body.GetRawText(), false, response.Status);
            }
            else
            {
                response = await control.SendAsync(
                    "POST",
                    "/v1/renderer/command",
                    command,
                    TimeSpan.FromSeconds(30),
                    cancellationToken).ConfigureAwait(false);
                if (response.Status is >= 200 and < 300 && response.Body is { } body &&
                    body.ValueKind == JsonValueKind.Object &&
                    body.TryGetProperty("response", out JsonElement responseValue) &&
                    responseValue.ValueKind == JsonValueKind.String)
                    return AetherCommandClient.ParseResponse(command, responseValue.GetString() ?? "") with
                    {
                        Status = response.Status,
                        ErrorCode = response.Error?.Code
                    };
            }

            string message = response.Error?.Message ?? $"Aether control request failed with status {response.Status}.";
            return new AetherCommandResult(false, $"ERR {message}", message, false, response.Status, response.Error?.Code);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            return AetherCommandResult.Transport(ex.Message);
        }
    }

    private static RendererCapabilities ParseCapabilities(string configJson, string windowsJson, string pipesJson)
    {
        RendererConfigContract config = JsonSerializer.Deserialize<RendererConfigContract>(configJson, JsonOptions)
            ?? throw new InvalidOperationException("Aether returned an invalid renderer configuration payload.");
        RendererWindowListContract windows = JsonSerializer.Deserialize<RendererWindowListContract>(windowsJson, JsonOptions)
            ?? throw new InvalidOperationException("Aether returned an invalid renderer window payload.");
        using JsonDocument pipes = JsonDocument.Parse(pipesJson);
        JsonElement pipesRoot = pipes.RootElement;

        RendererWindowCapability[] windowValues = windows.Windows
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .Select(item => new RendererWindowCapability(item.Id, item.RuntimeGroup, item.Kind, item.Visible))
            .ToArray();

        var groups = windowValues.Select(item => item.Group).Where(item => item != null).Distinct(StringComparer.Ordinal).Cast<string>().ToArray();
        string pipeState = ControlCodec.GetString(pipesRoot, "control") ?? "unknown";
        return new RendererCapabilities(
            config.Files,
            windowValues,
            groups,
            false,
            pipeState,
            config.Cues,
            config.ThemeId,
            config.ThemeActivationId,
            config.ThemeGeneration,
            config.RendererSessionId);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
