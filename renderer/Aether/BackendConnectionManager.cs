using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Aether;

internal sealed record BackendPipeSnapshot(string BackendId, string State, int QueueDepth);

internal sealed class BackendConnectionManager : IDisposable
{
    private readonly ControlPipeChannel? _control;
    private readonly ConcurrentDictionary<string, BackendPipeConnection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JsonSchemaDescriptor> _schemas = new(StringComparer.Ordinal);
    private readonly string _sid = WindowsIdentityHelper.GetCurrentSid();
    private readonly string _sessionId = Guid.NewGuid().ToString("D");
    private int _disposed;

    public BackendConnectionManager(ControlPipeChannel? control)
    {
        _control = control;
        if (_control != null)
            _control.Disconnected += OnControlDisconnected;
    }

    public event Action<BackendEvent>? StreamEventReceived;

    public async Task<BackendPipeConnection> GetOrConnectAsync(string backendId, CancellationToken cancellationToken = default)
    {
        if (_connections.TryGetValue(backendId, out BackendPipeConnection? existing) && existing.State == "connected")
            return existing;
        if (_control == null || _control.State != "connected")
            throw new InvalidOperationException("Control channel is unavailable.");

        ControlResponse response = await _control.SendAsync(
            "POST",
            $"/v1/backends/{Uri.EscapeDataString(backendId)}/connect",
            new
            {
                protocol = 1,
                renderer = new { pid = Environment.ProcessId, sid = _sid, sessionId = _sessionId }
            },
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);

        if (response.Error != null || response.Status >= 300)
            throw new InvalidOperationException(response.Error?.Message ?? $"Backend connection failed with status {response.Status}.");
        if (response.Body is not JsonElement body ||
            !body.TryGetProperty("protocol", out JsonElement protocol) || !protocol.TryGetInt32(out int protocolVersion) || protocolVersion != 1 ||
            !body.TryGetProperty("backendId", out JsonElement returnedId) || returnedId.GetString() != backendId ||
            !body.TryGetProperty("expiresAt", out JsonElement expiry) || !DateTimeOffset.TryParse(expiry.GetString(), out DateTimeOffset expiresAt) || expiresAt <= DateTimeOffset.UtcNow ||
            !body.TryGetProperty("pipeName", out JsonElement pipeNameElement))
            throw new InvalidOperationException("Control returned no backend pipe name.");

        string pipeName = pipeNameElement.GetString() ?? "";
        var connection = await BackendPipeConnection.ConnectAsync(backendId, pipeName, cancellationToken).ConfigureAwait(false);
        connection.EventReceived += OnBackendEvent;
        if (_connections.TryGetValue(backendId, out BackendPipeConnection? previous))
            previous.Dispose();
        _connections[backendId] = connection;
        return connection;
    }

    public async Task<ControlResponse> RequestAsync(string backendId, string method, string path, object? body, CancellationToken cancellationToken = default)
    {
        BackendPipeConnection connection = await GetOrConnectAsync(backendId, cancellationToken).ConfigureAwait(false);
        try
        {
            return await connection.SendAsync("POST", "/v1/request", new { method, path, body }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _connections.TryRemove(new KeyValuePair<string, BackendPipeConnection>(backendId, connection));
            connection.Dispose();
            throw;
        }
    }

    public async Task<BackendStreamDescriptor> OpenStreamAsync(string backendId, string dataId, string type, CancellationToken cancellationToken = default)
    {
        if (type != "json" || string.IsNullOrWhiteSpace(dataId))
            throw new InvalidOperationException("Only non-empty json streams are supported.");
        BackendPipeConnection connection = await GetOrConnectAsync(backendId, cancellationToken).ConfigureAwait(false);
        ControlResponse response = await connection.SendAsync("POST", "/v1/streams", new { dataId, type }, cancellationToken).ConfigureAwait(false);
        if (response.Error != null || response.Status >= 300 || response.Body is not JsonElement body ||
            !body.TryGetProperty("streamId", out JsonElement streamId) || streamId.ValueKind != JsonValueKind.String ||
            !body.TryGetProperty("type", out JsonElement returnedType) || returnedType.GetString() != type ||
            !body.TryGetProperty("schema", out JsonElement schema))
            throw new InvalidOperationException(response.Error?.Message ?? "Backend returned an invalid stream descriptor.");
        BackendStreamDescriptor descriptor = new(streamId.GetString()!, type, JsonSchemaDescriptor.Parse(schema));
        _schemas[descriptor.StreamId] = descriptor.Schema;
        return descriptor;
    }

    public async Task CloseStreamAsync(string backendId, string streamId, CancellationToken cancellationToken = default)
    {
        BackendPipeConnection connection = await GetOrConnectAsync(backendId, cancellationToken).ConfigureAwait(false);
        ControlResponse response = await connection.SendAsync("DELETE", $"/v1/streams/{Uri.EscapeDataString(streamId)}", null, cancellationToken).ConfigureAwait(false);
        if (response.Error != null || response.Status >= 300)
            throw new InvalidOperationException(response.Error?.Message ?? "Backend stream close failed.");
        _schemas.TryRemove(streamId, out _);
    }

    public IReadOnlyList<BackendPipeSnapshot> Snapshot() => _connections.Values
        .Select(connection => new BackendPipeSnapshot(connection.BackendId, connection.State, connection.QueueDepth))
        .ToArray();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_control != null)
            _control.Disconnected -= OnControlDisconnected;
        foreach (KeyValuePair<string, BackendPipeConnection> pair in _connections)
        {
            try
            {
                if (_control?.State == "connected")
                    _control.SendAsync("DELETE", $"/v1/backends/{Uri.EscapeDataString(pair.Key)}/connect", new { sessionId = _sessionId }, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch { }
            pair.Value.Dispose();
        }
        _connections.Clear();
        _schemas.Clear();
        StreamEventReceived = null;
    }

    private void OnControlDisconnected()
    {
        foreach (KeyValuePair<string, BackendPipeConnection> pair in _connections.ToArray())
        {
            if (_connections.TryRemove(pair.Key, out BackendPipeConnection? connection))
                connection.Dispose();
        }
    }

    private void OnBackendEvent(BackendEvent value)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (value.Name == "stream.overflow")
        {
            if (Volatile.Read(ref _disposed) == 0)
                StreamEventReceived?.Invoke(value);
            return;
        }
        if (value.Name != "stream.data" || value.Type != "json" || value.Data is not JsonElement data ||
            !_schemas.TryGetValue(value.StreamId, out JsonSchemaDescriptor? schema) || !schema.Matches(data))
            return;
        if (Volatile.Read(ref _disposed) == 0)
            StreamEventReceived?.Invoke(value);
    }
}

internal sealed class BackendPipeConnection : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ControlResponse>> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _readerTask;

    private BackendPipeConnection(string backendId, NamedPipeClientStream pipe)
    {
        BackendId = backendId;
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        State = "connected";
        _readerTask = Task.Run(ReadAsync);
    }

    public string BackendId { get; }
    public string State { get; private set; }
    public int QueueDepth => _pending.Count;
    public event Action<BackendEvent>? EventReceived;

    public static async Task<BackendPipeConnection> ConnectAsync(string backendId, string pipeName, CancellationToken cancellationToken)
    {
        const string prefix = "\\\\.\\pipe\\";
        string name = pipeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? pipeName[prefix.Length..]
            : pipeName;
        if (name.Length == 0 || name.Contains('\\') || name.Contains('/'))
            throw new InvalidOperationException("Control returned an invalid backend pipe name.");

        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, cancellationToken).ConfigureAwait(false);
        return new BackendPipeConnection(backendId, pipe);
    }

    public async Task<ControlResponse> SendAsync(string method, string path, object? body, CancellationToken cancellationToken)
    {
        if (State != "connected")
            throw new IOException("Backend pipe is not connected.");
        string id = Guid.NewGuid().ToString("D");
        JsonElement? bodyElement = body == null ? null : ControlPipeCodec.SerializeBody(body);
        var request = new ControlRequest(id, method, path, bodyElement);
        var completion = new TaskCompletionSource<ControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await _writer.WriteLineAsync(ControlPipeCodec.SerializeRequest(request).AsMemory(), cancellationToken).ConfigureAwait(false); }
            finally { _writeLock.Release(); }
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public void Dispose()
    {
        if (State == "closed")
            return;
        State = "closed";
        _shutdown.Cancel();
        foreach (TaskCompletionSource<ControlResponse> completion in _pending.Values)
            completion.TrySetException(new IOException("Backend pipe closed."));
        try { _pipe.Dispose(); } catch { }
        try { _readerTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _reader.Dispose();
        _writer.Dispose();
        _writeLock.Dispose();
        _shutdown.Dispose();
    }

    private async Task ReadAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                string? line = await _reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
                if (line == null)
                    break;
                if (!ControlPipeCodec.TryParseResponse(line, out ControlResponse? response) || response?.Id == null)
                {
                    if (TryParseEvent(line, out BackendEvent? backendEvent))
                        EventReceived?.Invoke(backendEvent!);
                    continue;
                }
                if (_pending.TryGetValue(response.Id, out TaskCompletionSource<ControlResponse>? completion))
                    completion.TrySetResult(response);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            State = "disconnected";
            foreach (TaskCompletionSource<ControlResponse> completion in _pending.Values)
                completion.TrySetException(ex);
        }
    }

    private static bool TryParseEvent(string line, out BackendEvent? backendEvent)
    {
        backendEvent = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.Null ||
                !root.TryGetProperty("event", out JsonElement name) || name.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("streamId", out JsonElement stream) || stream.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String)
                return false;
            backendEvent = new BackendEvent(name.GetString()!, stream.GetString()!, type.GetString()!, root.TryGetProperty("data", out JsonElement data) ? data.Clone() : null);
            return true;
        }
        catch (JsonException) { return false; }
    }
}

internal static class WindowsIdentityHelper
{
    public static string GetCurrentSid()
    {
        try
        {
            using System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
