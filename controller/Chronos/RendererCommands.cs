using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using KhaosShell.Contracts;

namespace Chronos;

internal sealed record AetherCommandResult(bool Success, string Payload, string? Error = null);

internal sealed class AetherCommandClient
{
    internal const string PipeName = "Aether.Commands";
    private readonly string _machine = ".";

    public async Task<AetherCommandResult> SendAsync(AetherCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var pipe = new NamedPipeClientStream(_machine, PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(RendererCommandCodec.Serialize(command).AsMemory(), timeoutSource.Token).ConfigureAwait(false);
            string response = await reader.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false) ?? "ERR No response from Aether.";
            return response.StartsWith("ERR ", StringComparison.Ordinal)
                ? new AetherCommandResult(false, response, response[4..])
                : new AetherCommandResult(true, response, response.StartsWith("OK ", StringComparison.Ordinal) ? response[3..] : response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AetherCommandResult(false, "ERR Aether command timed out.", "Aether command timed out.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            return new AetherCommandResult(false, $"ERR {ex.Message}", ex.Message);
        }
    }
}

internal sealed record RendererWindowCapability(string Id, string? Group, string? Kind, bool? Visible);

internal sealed record RendererCapabilities(
    IReadOnlyList<string> ConfigFiles,
    IReadOnlyList<RendererWindowCapability> Windows,
    IReadOnlyList<string> Groups,
    bool HasMenu,
    string PipeState)
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

    public RendererCommandService(AetherCommandClient client) => _client = client;

    public bool IsConnected
    {
        get { lock (_sync) return _connected; }
    }

    public RendererCapabilities Capabilities
    {
        get { lock (_sync) return _capabilities; }
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

    public Task<AetherCommandResult> ApplyConfigAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        SendAsync(new(AetherCommandKind.ApplyConfig, ConfigPaths: paths), cancellationToken);

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
            _connected = false;
            _capabilities = _capabilities with { PipeState = "disconnected" };
        }
    }

    private async Task<AetherCommandResult> SendAsync(AetherCommand command, CancellationToken cancellationToken)
    {
        AetherCommandResult result = await _client.SendAsync(command, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (!result.Success)
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
                        : kindElement.TryGetInt32(out int kindNumber)
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
        return new RendererCapabilities(files, windowValues, groups, hasMenu, pipeState);
    }
}
