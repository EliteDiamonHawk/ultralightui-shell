using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using KhaosShell.Contracts;

namespace Chronos;

internal sealed record AetherCommandResult(bool Success, string Payload, string? Error = null, bool TransportFailure = false)
{
    public static AetherCommandResult Failure(string message) => new(false, $"ERR {message}", message);
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
            return new AetherCommandResult(false, "ERR Aether command timed out.", "Aether command timed out.", true);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            return new AetherCommandResult(false, $"ERR {ex.Message}", ex.Message, true);
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

        return new AetherCommandResult(false, $"ERR Invalid response from Aether: {response}", response, true);
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

internal sealed record RendererWindowCapability(string Id, string? Group, string? Kind, bool? Visible);

internal sealed record RendererCapabilities(
    IReadOnlyList<string> ConfigFiles,
    IReadOnlyList<RendererWindowCapability> Windows,
    IReadOnlyList<string> Groups,
    bool HasMenu,
    string PipeState,
    Guid? ThemeId = null,
    Guid? ThemeActivationId = null,
    long? ThemeGeneration = null)
{
    public static RendererCapabilities Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<RendererWindowCapability>(),
        Array.Empty<string>(),
        false,
        "disconnected");
}

internal sealed class RendererCommandService
{
    private readonly AetherCommandClient _client;
    private readonly object _sync = new();
    private RendererCapabilities _capabilities = RendererCapabilities.Empty;
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
            _capabilities = next;
            _connected = true;
        }
        return next;
    }

    public Task<AetherCommandResult> ShowMenuAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new(AetherCommandKind.ShowMenu), cancellationToken);

    public Task<AetherCommandResult> HideMenuAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new(AetherCommandKind.HideMenu), cancellationToken);

    public Task<AetherCommandResult> SetWindowVisibilityAsync(string id, bool visible, CancellationToken cancellationToken = default) =>
        SendAsync(new(visible ? AetherCommandKind.RevealWindow : AetherCommandKind.HideWindow, TargetId: id), cancellationToken);

    public async Task<AetherCommandResult> ApplyConfigAsync(
        IReadOnlyList<string> paths,
        Guid? themeId = null,
        long? generation = null,
        string? configJson = null,
        IReadOnlyList<string>? sourcePaths = null,
        CancellationToken cancellationToken = default)
    {
        Guid activationId = Guid.NewGuid();
        AetherCommandResult result = await SendAsync(
            new(AetherCommandKind.ApplyConfig, ConfigPaths: paths, ThemeId: themeId,
                ThemeActivationId: activationId, ThemeGeneration: generation,
                ConfigJson: configJson, ConfigSourcePaths: sourcePaths),
            cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            lock (_sync)
                _capabilities = _capabilities with
                {
                    ThemeId = themeId ?? _capabilities.ThemeId,
                    ThemeActivationId = activationId,
                    ThemeGeneration = generation ?? (_capabilities.ThemeGeneration ?? 0) + 1
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

    public async Task<AetherCommandResult> ToggleMenuAsync(CancellationToken cancellationToken = default)
    {
        RendererCapabilities capabilities = Capabilities;
        RendererWindowCapability? menu = capabilities.Windows.FirstOrDefault(item =>
            string.Equals(item.Kind, "menu", StringComparison.OrdinalIgnoreCase));
        bool show = menu?.Visible != true;
        AetherCommandResult result = capabilities.HasMenu
            ? await SendAsync(new(show ? AetherCommandKind.ShowMenu : AetherCommandKind.HideMenu), cancellationToken).ConfigureAwait(false)
            : new AetherCommandResult(false, "ERR No menu window is configured.", "No menu window is configured.");
        if (result.Success && menu != null)
        {
            lock (_sync)
                _capabilities = _capabilities with
                {
                    Windows = _capabilities.Windows.Select(item => item.Id == menu.Id ? item with { Visible = show } : item).ToArray()
                };
        }
        return result;
    }

    public void MarkDisconnected()
    {
        lock (_sync)
        {
            _transportAvailable = false;
            _connected = false;
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
        lock (_sync)
        {
            RendererCapabilities current = _capabilities;
            request = command with
            {
                ThemeId = command.ThemeId ?? current.ThemeId,
                ThemeActivationId = command.ThemeActivationId ?? current.ThemeActivationId,
                ThemeGeneration = command.ThemeGeneration ?? current.ThemeGeneration
            };
        }

        AetherCommandResult result = await _client.SendAsync(request, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (!result.Success && result.TransportFailure)
            MarkDisconnected();
        return result;
    }

    private static RendererCapabilities ParseCapabilities(string configJson, string windowsJson, string pipesJson)
    {
        using JsonDocument config = JsonDocument.Parse(configJson);
        using JsonDocument windows = JsonDocument.Parse(windowsJson);
        using JsonDocument pipes = JsonDocument.Parse(pipesJson);
        JsonElement configRoot = config.RootElement;
        JsonElement windowsRoot = windows.RootElement;
        JsonElement pipesRoot = pipes.RootElement;

        var files = new List<string>();
        if (configRoot.TryGetProperty("files", out JsonElement fileValues) && fileValues.ValueKind == JsonValueKind.Array)
            files.AddRange(fileValues.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Where(item => item != null));

        var windowValues = new List<RendererWindowCapability>();
        if (windowsRoot.TryGetProperty("windows", out JsonElement windowsArray) && windowsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in windowsArray.EnumerateArray())
            {
                string? id = ControlCodec.GetString(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                string? kind = item.TryGetProperty("kind", out JsonElement kindElement)
                    ? kindElement.ValueKind == JsonValueKind.String
                        ? kindElement.GetString()
                        : kindElement.ValueKind == JsonValueKind.Number && kindElement.TryGetInt32(out int kindNumber)
                            ? kindNumber switch { 0 => "wallpaper", 1 => "widget", 2 => "menu", _ => null }
                            : null
                    : null;
                windowValues.Add(new RendererWindowCapability(
                    id,
                    ControlCodec.GetString(item, "group"),
                    kind,
                    item.TryGetProperty("visible", out JsonElement visible) && visible.ValueKind is JsonValueKind.True or JsonValueKind.False ? visible.GetBoolean() : null));
            }
        }

        var groups = windowValues.Select(item => item.Group).Where(item => item != null).Distinct(StringComparer.Ordinal).Cast<string>().ToArray();
        bool hasMenu = windowValues.Any(item => string.Equals(item.Kind, "menu", StringComparison.OrdinalIgnoreCase));
        string pipeState = ControlCodec.GetString(pipesRoot, "control") ?? "unknown";
        Guid? themeId = TryGetGuid(configRoot, "themeId");
        Guid? activationId = TryGetGuid(configRoot, "themeActivationId");
        long? generation = configRoot.TryGetProperty("themeGeneration", out JsonElement generationValue) &&
            generationValue.ValueKind == JsonValueKind.Number &&
            generationValue.TryGetInt64(out long parsedGeneration) ? parsedGeneration : null;
        return new RendererCapabilities(files, windowValues, groups, hasMenu, pipeState, themeId, activationId, generation);
    }

    private static Guid? TryGetGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        Guid.TryParse(value.GetString(), out Guid parsed) ? parsed : null;
}
