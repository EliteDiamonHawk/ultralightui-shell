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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectionGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<BackendStreamKey, JsonSchemaDescriptor> _schemas = new();
    private readonly ConcurrentDictionary<BackendStreamKey, JsonElement> _latestStreamData = new();
    private readonly ConcurrentDictionary<string, SharedBackendStream> _streams = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _streamGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<BackendPipeConnection, Action<BackendEvent>> _eventHandlers = new();
    private readonly BackendEventQueue _eventQueue = new();
    private readonly SemaphoreSlim _eventSignal = new(0);
    private readonly Task _eventDispatcher;
    private readonly System.Threading.Timer _expiryTimer;
    private readonly string _sid = WindowsIdentityHelper.GetCurrentSid();
    private string _sessionId = Guid.NewGuid().ToString("D");
    private Guid? _themeId;
    private long? _themeGeneration;
    private int _disposed;

    public BackendConnectionManager(ControlPipeChannel? control)
    {
        _control = control;
        if (_control != null)
            _control.Disconnected += OnControlDisconnected;
        _eventDispatcher = Task.Run(DispatchEventsAsync);
        _expiryTimer = new System.Threading.Timer(_ => ExpireConnections(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public event Action<BackendEvent>? StreamEventReceived;

    public async Task<BackendPipeConnection> GetOrConnectAsync(string backendId, CancellationToken cancellationToken = default)
    {
        if (_connections.TryGetValue(backendId, out BackendPipeConnection? existing) && existing.IsUsable)
            return existing;
        RemoveStaleConnection(backendId, existing);
        if (_control == null || _control.State != "connected")
            throw new InvalidOperationException("Control channel is unavailable.");

        SemaphoreSlim gate = _connectionGates.GetOrAdd(backendId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connections.TryGetValue(backendId, out existing) && existing.IsUsable)
                return existing;
            RemoveStaleConnection(backendId, existing);

            ControlResponse response = await _control.SendAsync(
                "POST",
                $"/v1/backends/{Uri.EscapeDataString(backendId)}/connect",
                new
                {
                    protocol = 1,
                    renderer = new
                    {
                        pid = Environment.ProcessId,
                        sid = _sid,
                        sessionId = _sessionId,
                        themeId = _themeId,
                        themeGeneration = _themeGeneration
                    }
                },
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);

            if (response.Error != null || response.Status >= 300)
                throw new InvalidOperationException(response.Error?.Message ?? $"Backend connection failed with status {response.Status}.");
            if (response.Body is not JsonElement body ||
                !body.TryGetProperty("protocol", out JsonElement protocol) || protocol.ValueKind != JsonValueKind.Number || !protocol.TryGetInt32(out int protocolVersion) || protocolVersion != 1 ||
                !body.TryGetProperty("backendId", out JsonElement returnedId) || returnedId.GetString() != backendId ||
                !body.TryGetProperty("expiresAt", out JsonElement expiry) || !DateTimeOffset.TryParse(expiry.GetString(), out DateTimeOffset expiresAt) || expiresAt <= DateTimeOffset.UtcNow ||
                !body.TryGetProperty("pipeName", out JsonElement pipeNameElement))
                throw new InvalidOperationException("Control returned no backend pipe name.");

            string pipeName = pipeNameElement.GetString() ?? "";
            BackendPipeConnection connection = await BackendPipeConnection.ConnectAsync(backendId, pipeName, expiresAt, cancellationToken).ConfigureAwait(false);
            if (connection.State != "connected")
            {
                connection.Dispose();
                throw new IOException("Backend pipe disconnected during setup.");
            }
            connection.Disconnected += () => OnBackendDisconnected(backendId, connection);
            Action<BackendEvent> eventHandler = value => OnBackendEvent(backendId, connection, value);
            connection.EventReceived += eventHandler;
            _eventHandlers[connection] = eventHandler;
            _connections[backendId] = connection;
            return connection;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ControlResponse> RequestAsync(string backendId, string method, string path, object? body, CancellationToken cancellationToken = default)
    {
        BackendPipeConnection connection = await GetOrConnectAsync(backendId, cancellationToken).ConfigureAwait(false);
        try
        {
            return await connection.SendAsync("POST", "/v1/request", new { method, path, body }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            RemoveConnection(backendId, connection);
            throw;
        }
    }

    public async Task<BackendStreamDescriptor> OpenStreamAsync(string backendId, string dataId, string type, CancellationToken cancellationToken = default)
    {
        if (type != "json" || string.IsNullOrWhiteSpace(dataId))
            throw new InvalidOperationException("Only non-empty json streams are supported.");
        BackendPipeConnection connection = await GetOrConnectAsync(backendId, cancellationToken).ConfigureAwait(false);
        string streamKey = StreamKey(backendId, dataId, type);
        SemaphoreSlim streamGate = _streamGates.GetOrAdd(streamKey, _ => new SemaphoreSlim(1, 1));
        await streamGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_streams.TryGetValue(streamKey, out SharedBackendStream? existing) && existing.Connection == connection)
            {
                Interlocked.Increment(ref existing.LeaseCount);
                return existing.Descriptor;
            }

            ControlResponse response = await connection.SendAsync("POST", "/v1/streams", new { dataId, type }, cancellationToken).ConfigureAwait(false);
            if (response.Error != null || response.Status >= 300 || response.Body is not JsonElement body ||
                !body.TryGetProperty("streamId", out JsonElement streamId) || streamId.ValueKind != JsonValueKind.String ||
                !body.TryGetProperty("type", out JsonElement returnedType) || returnedType.GetString() != type ||
                !body.TryGetProperty("schema", out JsonElement schema))
                throw new InvalidOperationException(response.Error?.Message ?? "Backend returned an invalid stream descriptor.");
            string returnedStreamId = streamId.GetString()!;
            BackendStreamDescriptor descriptor = new(returnedStreamId, type, JsonSchemaDescriptor.Parse(schema));
            _streams[streamKey] = new SharedBackendStream(streamKey, backendId, connection, descriptor);
            _schemas[new BackendStreamKey(connection, descriptor.StreamId)] = descriptor.Schema;
            return descriptor;
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            RemoveConnection(backendId, connection);
            throw;
        }
        finally
        {
            streamGate.Release();
        }
    }

    public async Task CloseStreamAsync(string backendId, string streamId, CancellationToken cancellationToken = default)
    {
        if (await TryReleaseSharedStreamAsync(backendId, streamId, cancellationToken).ConfigureAwait(false))
            return;

        BackendPipeConnection connection = await GetOrConnectAsync(backendId, cancellationToken).ConfigureAwait(false);
        try
        {
            ControlResponse response = await connection.SendAsync("DELETE", $"/v1/streams/{Uri.EscapeDataString(streamId)}", null, cancellationToken).ConfigureAwait(false);
            if (response.Error != null || response.Status >= 300)
                throw new InvalidOperationException(response.Error?.Message ?? "Backend stream close failed.");
            _schemas.TryRemove(new BackendStreamKey(connection, streamId), out _);
            _latestStreamData.TryRemove(new BackendStreamKey(connection, streamId), out _);
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            RemoveConnection(backendId, connection);
            throw;
        }
    }

    public IReadOnlyList<BackendPipeSnapshot> Snapshot() => _connections.Values
        .Select(connection => new BackendPipeSnapshot(connection.BackendId, connection.State, connection.QueueDepth))
        .ToArray();

    public void RotateThemeScope(Guid? themeId = null, long? themeGeneration = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        RotateThemeScopeCore(themeId, themeGeneration);
    }

    private void RotateThemeScopeCore(Guid? themeId = null, long? themeGeneration = null)
    {

        foreach (KeyValuePair<string, BackendPipeConnection> pair in _connections.ToArray())
        {
            if (_connections.ContainsKey(pair.Key))
            {
                try
                {
                    if (_control?.State == "connected")
                    {
                        _control.SendAsync(
                            "DELETE",
                            $"/v1/backends/{Uri.EscapeDataString(pair.Key)}/connect",
                            new { sessionId = _sessionId },
                            TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    }
                }
                catch { }
                RemoveConnection(pair.Key, pair.Value);
            }
        }

        _schemas.Clear();
        _latestStreamData.Clear();
        _sessionId = Guid.NewGuid().ToString("D");
        _themeId = themeId;
        _themeGeneration = themeGeneration;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_control != null)
            _control.Disconnected -= OnControlDisconnected;
        _expiryTimer.Dispose();
        RotateThemeScopeCore();
        _schemas.Clear();
        _latestStreamData.Clear();
        _streams.Clear();
        _eventSignal.Release();
        if (Task.CurrentId != _eventDispatcher.Id)
        {
            try { _eventDispatcher.Wait(TimeSpan.FromSeconds(1)); } catch { }
        }
        _eventSignal.Dispose();
        StreamEventReceived = null;
    }

    private void OnControlDisconnected()
    {
        foreach (KeyValuePair<string, BackendPipeConnection> pair in _connections.ToArray())
            RemoveConnection(pair.Key, pair.Value);
        _schemas.Clear();
        _latestStreamData.Clear();
        _streams.Clear();
    }

    private void OnBackendDisconnected(string backendId, BackendPipeConnection connection) => RemoveConnection(backendId, connection);

    private void RemoveConnection(string backendId, BackendPipeConnection connection)
    {
        if (_connections.TryRemove(new KeyValuePair<string, BackendPipeConnection>(backendId, connection)))
        {
            foreach (SharedBackendStream stream in _streams.Values.Where(value => ReferenceEquals(value.Connection, connection)).ToArray())
                _streams.TryRemove(stream.Key, out _);
            foreach (BackendStreamKey key in _schemas.Keys.Where(key => ReferenceEquals(key.Connection, connection)).ToArray())
                _schemas.TryRemove(key, out _);
            foreach (BackendStreamKey key in _latestStreamData.Keys.Where(key => ReferenceEquals(key.Connection, connection)).ToArray())
                _latestStreamData.TryRemove(key, out _);
            if (_eventHandlers.TryRemove(connection, out Action<BackendEvent>? eventHandler))
                connection.EventReceived -= eventHandler;
            connection.Dispose();
        }
    }

    private static bool IsConnectionFailure(Exception exception) => exception is IOException or InvalidDataException or TimeoutException or ObjectDisposedException;

    private void OnBackendEvent(string backendId, BackendPipeConnection connection, BackendEvent value)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            !_connections.TryGetValue(backendId, out BackendPipeConnection? current) ||
            !ReferenceEquals(current, connection))
            return;

        SharedBackendStream? stream = _streams.Values.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.Connection, connection) && candidate.Descriptor.StreamId == value.StreamId);
        if (stream == null)
            return;
        BackendEvent scopedValue = value with { BackendId = backendId };

        if (scopedValue.Name == "stream.overflow")
        {
            if (Volatile.Read(ref _disposed) == 0)
                StreamEventReceived?.Invoke(scopedValue);
            return;
        }
        if (scopedValue.Name != "stream.data" || scopedValue.Type != "json" || scopedValue.Data is not JsonElement data ||
            !_schemas.TryGetValue(new BackendStreamKey(connection, scopedValue.StreamId), out JsonSchemaDescriptor? schema) || !schema.Matches(data))
            return;
        if (Volatile.Read(ref _disposed) == 0)
        {
            _latestStreamData[new BackendStreamKey(connection, scopedValue.StreamId)] = data.Clone();
            _eventQueue.Enqueue(scopedValue, overflow => StreamEventReceived?.Invoke(overflow));
            _eventSignal.Release();
        }
    }

    private async Task DispatchEventsAsync()
    {
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                await _eventSignal.WaitAsync().ConfigureAwait(false);
                foreach (BackendEvent value in _eventQueue.Drain())
                    if (Volatile.Read(ref _disposed) == 0)
                        StreamEventReceived?.Invoke(value);
            }
        }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
    }

    private void ExpireConnections()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        foreach (KeyValuePair<string, BackendPipeConnection> pair in _connections.ToArray())
            if (!pair.Value.IsUsable)
                RemoveConnection(pair.Key, pair.Value);
    }

    private void RemoveStaleConnection(string backendId, BackendPipeConnection? connection)
    {
        if (connection != null && !connection.IsUsable)
            RemoveConnection(backendId, connection);
    }

    public bool TryGetLatestStreamData(string backendId, string streamId, out JsonElement data)
    {
        SharedBackendStream? shared = _streams.Values.FirstOrDefault(value =>
            value.BackendId == backendId && value.Descriptor.StreamId == streamId);
        if (shared != null &&
            _latestStreamData.TryGetValue(new BackendStreamKey(shared.Connection, streamId), out JsonElement latest))
        {
            data = latest.Clone();
            return true;
        }

        data = default;
        return false;
    }

    private async Task<bool> TryReleaseSharedStreamAsync(string backendId, string streamId, CancellationToken cancellationToken)
    {
        SharedBackendStream? shared = _streams.Values.FirstOrDefault(value => value.BackendId == backendId && value.Descriptor.StreamId == streamId);
        if (shared == null)
            return false;

        SemaphoreSlim streamGate = _streamGates.GetOrAdd(shared.Key, _ => new SemaphoreSlim(1, 1));
        await streamGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_streams.TryGetValue(shared.Key, out shared))
                return false;
            if (Interlocked.Decrement(ref shared.LeaseCount) > 0)
                return true;
            _streams.TryRemove(shared.Key, out _);
            _schemas.TryRemove(new BackendStreamKey(shared.Connection, streamId), out _);
            _latestStreamData.TryRemove(new BackendStreamKey(shared.Connection, streamId), out _);
            // The local lease is released even if the backend has already disconnected or
            // forgot the stream. The connection/session cleanup path owns stale remote state.
            await CloseSharedStreamAsync(shared).ConfigureAwait(false);
            return true;
        }
        finally
        {
            streamGate.Release();
        }
    }

    private async Task<bool> CloseSharedStreamAsync(SharedBackendStream shared)
    {
        try
        {
            ControlResponse response = await shared.Connection.SendAsync(
                "DELETE",
                $"/v1/streams/{Uri.EscapeDataString(shared.Descriptor.StreamId)}",
                null,
                CancellationToken.None).ConfigureAwait(false);
            return response.Error == null && response.Status < 300;
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            RemoveConnection(shared.BackendId, shared.Connection);
            return false;
        }
    }

    private static string StreamKey(string backendId, string dataId, string type) =>
        $"{backendId}\n{dataId}\n{type}";

    private sealed class SharedBackendStream
    {
        public SharedBackendStream(string key, string backendId, BackendPipeConnection connection, BackendStreamDescriptor descriptor)
        {
            Key = key;
            BackendId = backendId;
            Connection = connection;
            Descriptor = descriptor;
            LeaseCount = 1;
        }

        public string Key { get; }
        public string BackendId { get; }
        public BackendPipeConnection Connection { get; }
        public BackendStreamDescriptor Descriptor { get; }
        public int LeaseCount;
    }
}

internal readonly record struct BackendStreamKey(BackendPipeConnection Connection, string StreamId);

internal sealed class BackendPipeConnection : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly BoundedLineReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ControlResponse>> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _readerTask;
    private int _state;

    private BackendPipeConnection(string backendId, NamedPipeClientStream pipe, DateTimeOffset expiresAt)
    {
        BackendId = backendId;
        _pipe = pipe;
        _reader = new BoundedLineReader(pipe);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        _state = 1;
        ExpiresAt = expiresAt;
        _readerTask = Task.Run(ReadAsync);
    }

    public string BackendId { get; }
    public DateTimeOffset ExpiresAt { get; }
    public bool IsUsable => State == "connected" && ExpiresAt > DateTimeOffset.UtcNow;
    public string State => Volatile.Read(ref _state) switch
    {
        1 => "connected",
        2 => "disconnected",
        _ => "closed"
    };
    public int QueueDepth => _pending.Count;
    public event Action<BackendEvent>? EventReceived;
    public event Action? Disconnected;

    public static async Task<BackendPipeConnection> ConnectAsync(string backendId, string pipeName, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        const string prefix = "\\\\.\\pipe\\";
        string name = pipeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? pipeName[prefix.Length..]
            : pipeName;
        if (name.Length == 0 || name.Contains('\\') || name.Contains('/'))
            throw new InvalidOperationException("Control returned an invalid backend pipe name.");

        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(5000, cancellationToken).ConfigureAwait(false);
            return new BackendPipeConnection(backendId, pipe, expiresAt);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
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
        if (Interlocked.Exchange(ref _state, 3) == 3)
            return;
        _shutdown.Cancel();
        foreach (TaskCompletionSource<ControlResponse> completion in _pending.Values)
            completion.TrySetException(new IOException("Backend pipe closed."));
        try { _pipe.Dispose(); } catch { }
        if (Task.CurrentId != _readerTask.Id)
        {
            try { _readerTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
        }
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
                BoundedLine frame = await _reader.ReadAsync(
                    ControlPipeCodec.MaxFrameBytes,
                    _shutdown.Token).ConfigureAwait(false);
                if (frame.EndOfStream)
                    break;
                if (frame.TooLarge)
                    throw new InvalidDataException("Backend frame exceeds 1 MiB.");
                string line = frame.Text!;
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
        catch (Exception ex) when (!_shutdown.IsCancellationRequested)
        {
            MarkDisconnected(ex);
        }
        finally
        {
            if (!_shutdown.IsCancellationRequested)
                MarkDisconnected(new IOException("Backend pipe disconnected."));
        }
    }

    private void MarkDisconnected(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _state, 2, 1) != 1)
            return;
        foreach (TaskCompletionSource<ControlResponse> completion in _pending.Values)
            completion.TrySetException(exception);
        Disconnected?.Invoke();
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
