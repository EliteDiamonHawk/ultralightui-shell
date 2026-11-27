using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using KhaosShell.Contracts;

namespace KhaosShell.MinimalBackend;

internal sealed class BackendServer : IAsyncDisposable
{
    private readonly BackendOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<ClientSession, byte> _clients = new();
    private readonly ConcurrentDictionary<string, StreamSubscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly MinimalValueState _valueState = new();
    private readonly object _listenerSync = new();
    private NamedPipeServerStream? _listener;
    private int _disposed;

    public BackendServer(BackendOptions options) => _options = options;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        CancellationToken token = linked.Token;
        NamedPipeServerStream listener = CreatePipe();
        lock (_listenerSync) _listener = listener;
        WriteReadiness();

        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await listener.WaitForConnectionAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (token.IsCancellationRequested)
                {
                    break;
                }

                if (!IsAuthorizedClient(listener))
                {
                    listener.Dispose();
                    listener = CreatePipe();
                    lock (_listenerSync) _listener = listener;
                    continue;
                }

                ClientSession client = new(this, listener, token);
                _clients.TryAdd(client, 0);
                _ = RunClientAsync(client);

                listener = CreatePipe();
                lock (_listenerSync) _listener = listener;
            }
        }
        finally
        {
            listener.Dispose();
            lock (_listenerSync) _listener = null;
            await DisposeClientsAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdown.Cancel();
        lock (_listenerSync)
        {
            try { _listener?.Dispose(); } catch { }
            _listener = null;
        }

        foreach (StreamSubscription subscription in _subscriptions.Values.ToArray())
            await RemoveSubscriptionAsync(subscription.StreamId, subscription.Owner).ConfigureAwait(false);
        await DisposeClientsAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task RunClientAsync(ClientSession client)
    {
        try
        {
            await client.RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Console.Error.WriteLine($"[MinimalBackend] client disconnected: {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(client, out _);
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DisposeClientsAsync()
    {
        ClientSession[] clients = _clients.Keys.ToArray();
        foreach (ClientSession client in clients)
            await client.DisposeAsync().ConfigureAwait(false);
        _clients.Clear();
    }

    private NamedPipeServerStream CreatePipe()
    {
        string currentSid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        if (!string.Equals(currentSid, _options.Sid, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Supplied SID does not match backend process identity.");

        const uint pipeAccessDuplex = 0x00000003;
        const uint fileFlagOverlapped = 0x40000000;
        const uint pipeTypeByte = 0x00000000;
        const uint pipeReadModeByte = 0x00000000;
        const uint pipeWait = 0x00000000;
        const uint securityDescriptorRevision = 1;

        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                // NamedPipeClientStream needs the pipe to be openable before the
                // server can inspect the client process token. Anonymous clients
                // are denied at the ACL, and authenticated clients are checked
                // against the launch SID immediately after connection.
                $"O:{_options.Sid}G:{_options.Sid}D:P(D;;GA;;;AN)(A;;GA;;;WD)",
                securityDescriptorRevision,
                out IntPtr securityDescriptor,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the backend pipe security descriptor.");
        }

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = securityDescriptor,
                InheritHandle = 0
            };
            IntPtr attributesPointer = Marshal.AllocHGlobal(attributes.Length);
            try
            {
                Marshal.StructureToPtr(attributes, attributesPointer, fDeleteOld: false);
                SafePipeHandle handle = CreateNamedPipe(
                    $"\\\\.\\pipe\\{_options.PipeName}",
                    pipeAccessDuplex | fileFlagOverlapped,
                    pipeTypeByte | pipeReadModeByte | pipeWait,
                    maxInstances: 255,
                    outBufferSize: 0,
                    inBufferSize: 0,
                    defaultTimeOut: 0,
                    attributesPointer);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new Win32Exception(error, "Could not create the backend named pipe.");
                }

                return new NamedPipeServerStream(
                    PipeDirection.InOut,
                    isAsync: true,
                    isConnected: false,
                    handle);
            }
            finally
            {
                Marshal.FreeHGlobal(attributesPointer);
            }
        }
        finally
        {
            LocalFree(securityDescriptor);
        }
    }

    private bool IsAuthorizedClient(NamedPipeServerStream pipe)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint processId) || processId == 0)
                return false;
            IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero)
                return false;
            try
            {
                if (!OpenProcessToken(process, TokenQuery, out SafeAccessTokenHandle token))
                    return false;
                using (token)
                using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                    return string.Equals(identity.User?.Value, _options.Sid, StringComparison.OrdinalIgnoreCase);
            }
            finally { CloseHandle(process); }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSDRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(
        string pipeName,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        uint outBufferSize,
        uint inBufferSize,
        uint defaultTimeOut,
        IntPtr securityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);

    private void WriteReadiness()
    {
        string readiness = BackendCodec.SerializeJson(new
        {
            ready = true,
            protocol = _options.Protocol,
            backendId = _options.BackendId,
            sid = _options.Sid,
            pipeName = _options.PipeName
        });
        Console.Out.WriteLine(readiness);
        Console.Out.Flush();
    }

    private async Task<(BackendResponse Response, StreamSubscription? Subscription)> HandleAsync(BackendRequest request, ClientSession client)
    {
        if (request.Method == "POST" && request.Path == "/v1/request")
            return (HandleRequest(request), null);
        if (request.Method == "POST" && request.Path == "/v1/streams")
            return await OpenStreamAsync(request, client).ConfigureAwait(false);
        if (request.Method == "DELETE" && request.Path.StartsWith("/v1/streams/", StringComparison.Ordinal))
            return (await CloseStreamAsync(request, client).ConfigureAwait(false), null);
        return (Failure(request.Id, 404, "route_not_found", "Backend route was not found."), null);
    }

    private BackendResponse HandleRequest(BackendRequest request)
    {
        if (!BackendCodec.TryGetObject(request.Body, out JsonElement body))
            return Failure(request.Id, 400, "request_body_invalid", "Request body must be an object.");

        string method = (BackendCodec.GetString(body, "method") ?? "").ToUpperInvariant();
        string path = BackendCodec.GetString(body, "path") ?? "";
        if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE"))
            return Failure(request.Id, 400, "method_invalid", "Nested method is unsupported.");
        if (!path.StartsWith("/v1/", StringComparison.Ordinal))
            return Failure(request.Id, 400, "path_invalid", "Nested path must begin with /v1/.");

        if (method == "GET" && path == "/v1/minimal/health")
            return Success(request.Id, new
            {
                ok = true,
                backendId = _options.BackendId,
                protocol = _options.Protocol
            });

        if (method == "GET" && path == "/v1/minimal/value")
            return Success(request.Id, ValueBody(_valueState.Read()));

        if (method == "PUT" && path == "/v1/minimal/value")
        {
            if (!TryGetFiniteNumber(requestBody: GetNestedBody(body), "value", out double value, out BackendResponse? error))
                return error! with { Id = request.Id };
            return Success(request.Id, ValueBody(SetValue(value)));
        }

        if (method == "POST" && path == "/v1/minimal/commands/increment")
        {
            if (!TryGetFiniteNumber(requestBody: GetNestedBody(body), "delta", out double delta, out BackendResponse? error))
                return error! with { Id = request.Id };
            if (!_valueState.TryAdd(delta, out double result, out bool changed))
                return Failure(request.Id, 400, "value_out_of_range", "The resulting value must be finite.");
            if (changed)
                PublishValue(result);
            return Success(request.Id, ValueBody(result));
        }

        if (path is "/v1/minimal/health" or "/v1/minimal/value" or "/v1/minimal/commands/increment")
            return Failure(request.Id, 405, "method_not_allowed", "The requested route does not allow this method.");
        return Failure(request.Id, 404, "route_not_found", "Reference route was not found.");
    }

    private Task<(BackendResponse Response, StreamSubscription? Subscription)> OpenStreamAsync(BackendRequest request, ClientSession client)
    {
        if (!BackendCodec.TryGetObject(request.Body, out JsonElement body))
            return Task.FromResult((Failure(request.Id, 400, "stream_body_invalid", "Stream body must be an object."), (StreamSubscription?)null));

        string dataId = BackendCodec.GetString(body, "dataId") ?? "";
        string type = BackendCodec.GetString(body, "type") ?? "";
        if (type != "json")
            return Task.FromResult((Failure(request.Id, 400, "stream_type_unsupported", "Only json streams are supported."), (StreamSubscription?)null));
        if (dataId != "minimal.value")
            return Task.FromResult((Failure(request.Id, 404, "data_not_found", "Reference data ID was not found."), (StreamSubscription?)null));

        string streamId = Guid.NewGuid().ToString("D");
        var subscription = new StreamSubscription(streamId, dataId, client, _shutdown.Token);
        if (!_subscriptions.TryAdd(streamId, subscription))
            return Task.FromResult((Failure(request.Id, 500, "stream_create_failed", "Could not allocate stream."), (StreamSubscription?)null));
        client.AddSubscription(subscription);

        return Task.FromResult((Success(request.Id, new
        {
            streamId,
            type,
            schema = new
            {
                kind = "object",
                fields = new { value = new { kind = "number" } }
            }
        }), (StreamSubscription?)subscription));
    }

    private async Task<BackendResponse> CloseStreamAsync(BackendRequest request, ClientSession client)
    {
        string streamId = Uri.UnescapeDataString(request.Path["/v1/streams/".Length..]);
        if (streamId.Length == 0 || !_subscriptions.TryGetValue(streamId, out StreamSubscription? subscription) || subscription.Owner != client)
            return Failure(request.Id, 404, "stream_not_found", "Stream was not found.");

        await RemoveSubscriptionAsync(streamId, client).ConfigureAwait(false);
        return Success(request.Id, null);
    }

    private async Task RemoveSubscriptionAsync(string streamId, ClientSession owner)
    {
        if (_subscriptions.TryRemove(streamId, out StreamSubscription? subscription) && subscription.Owner == owner)
        {
            owner.RemoveSubscription(streamId);
            await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static BackendResponse Success(string id, object? body) => BackendResponse.FromObject(id, 200, body);

    private static BackendResponse Failure(string id, int status, string code, string message) =>
        new(id, status, Error: new BackendError(code, message));

    private static object ValueBody(double value) => new { value };

    private double SetValue(double value)
    {
        double normalized = Math.Round(value, 3);
        double result = _valueState.Set(normalized, out bool changed);
        if (changed)
            PublishValue(result);
        return result;
    }

    private void PublishValue(double value)
    {
        object body = ValueBody(value);
        foreach (StreamSubscription subscription in _subscriptions.Values.ToArray())
            if (subscription.DataId == "minimal.value")
                subscription.Publish(body);
    }

    private static JsonElement? GetNestedBody(JsonElement body) =>
        body.TryGetProperty("body", out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    private static bool TryGetFiniteNumber(
        JsonElement? requestBody,
        string field,
        out double value,
        out BackendResponse? error)
    {
        value = 0;
        error = null;
        if (requestBody is not JsonElement body || body.ValueKind != JsonValueKind.Object)
        {
            error = Failure("", 400, "request_body_invalid", "Request body must be an object.");
            return false;
        }
        if (!body.TryGetProperty(field, out JsonElement property) || property.ValueKind != JsonValueKind.Number ||
            !property.TryGetDouble(out value) || !double.IsFinite(value))
        {
            error = Failure("", 400, "field_invalid", $"Field '{field}' must be a finite number.");
            return false;
        }
        return true;
    }

    private sealed class ClientSession : IAsyncDisposable
    {
        private readonly BackendServer _server;
        private readonly Stream _stream;
        private readonly BoundedLineReader _reader;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _shutdown;
        private readonly ConcurrentDictionary<string, StreamSubscription> _subscriptions = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _inflightRequests = new(StringComparer.Ordinal);
        private int _disposed;

        public ClientSession(BackendServer server, NamedPipeServerStream stream, CancellationToken serverToken)
        {
            _server = server;
            _stream = stream;
            _reader = new BoundedLineReader(stream);
            _writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            _shutdown = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        }

        public IReadOnlyCollection<StreamSubscription> Subscriptions => _subscriptions.Values.ToArray();
        public Task RunAsync() => ReadLoopAsync();

        public void AddSubscription(StreamSubscription subscription) => _subscriptions[subscription.StreamId] = subscription;
        public void RemoveSubscription(string streamId) => _subscriptions.TryRemove(streamId, out _);

        public async Task SendEventAsync(BackendEventFrame value, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            await WriteLineAsync(BackendCodec.SerializeEvent(value), cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _shutdown.Cancel();
            foreach (StreamSubscription subscription in _subscriptions.Values.ToArray())
                await _server.RemoveSubscriptionAsync(subscription.StreamId, this).ConfigureAwait(false);
            _subscriptions.Clear();
            try { _stream.Dispose(); } catch { }
            try { _writer.Dispose(); } catch { }
            _writeLock.Dispose();
            _shutdown.Dispose();
        }

        private async Task ReadLoopAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                BoundedLine frame = await _reader.ReadAsync(BackendCodec.MaxFrameBytes, _shutdown.Token).ConfigureAwait(false);
                if (frame.EndOfStream)
                    break;
                if (frame.TooLarge)
                {
                    await WriteLineAsync(BackendCodec.SerializeResponse(new BackendResponse(null, 400, Error: new BackendError("frame_too_large", "Backend frame exceeds 1 MiB."))), _shutdown.Token).ConfigureAwait(false);
                    continue;
                }

                if (!BackendCodec.TryParseRequest(frame.Text!, out BackendRequest? request, out BackendError? error))
                {
                    await WriteLineAsync(BackendCodec.SerializeResponse(new BackendResponse(null, 400, Error: error)), _shutdown.Token).ConfigureAwait(false);
                    continue;
                }

                _ = CompleteRequestAsync(request!);
            }
        }

        private async Task CompleteRequestAsync(BackendRequest request)
        {
            if (!_inflightRequests.TryAdd(request.Id, 0))
            {
                try
                {
                    await WriteLineAsync(BackendCodec.SerializeResponse(
                        Failure(request.Id, 409, "request_id_in_flight", "Request ID is already in flight.")),
                        _shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
                return;
            }

            BackendResponse response;
            StreamSubscription? subscription = null;
            try
            {
                (response, subscription) = await _server.HandleAsync(request, this).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MinimalBackend] request failed: {ex.Message}");
                response = new BackendResponse(request.Id, 500, Error: new BackendError("internal_error", "Backend request failed."));
            }

            try
            {
                await WriteLineAsync(BackendCodec.SerializeResponse(response), _shutdown.Token).ConfigureAwait(false);
                subscription?.Start(ValueBody(_server._valueState.Read()));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
            finally
            {
                _inflightRequests.TryRemove(request.Id, out _);
            }
        }

        private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false); }
            finally { _writeLock.Release(); }
        }
    }

    private sealed class MinimalValueState
    {
        private readonly object _sync = new();
        private double _value = 20.0;

        public double Read()
        {
            lock (_sync)
                return _value;
        }

        public double Set(double value, out bool changed)
        {
            lock (_sync)
            {
                changed = _value != value;
                _value = value;
                return _value;
            }
        }

        public bool TryAdd(double delta, out double result, out bool changed)
        {
            lock (_sync)
            {
                result = _value + delta;
                changed = _value != result;
                if (!double.IsFinite(result))
                    return false;
                _value = result;
                return true;
            }
        }
    }

    private sealed class StreamSubscription : IAsyncDisposable
    {
        private readonly CancellationTokenSource _shutdown;
        private readonly SemaphoreSlim _signal = new(0, 1);
        private readonly object _pendingSync = new();
        private Task? _producer;
        private object? _pending;
        private bool _started;
        private int _disposed;

        public StreamSubscription(string streamId, string dataId, ClientSession owner, CancellationToken serverToken)
        {
            StreamId = streamId;
            DataId = dataId;
            Owner = owner;
            _shutdown = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        }

        public string StreamId { get; }
        public string DataId { get; }
        public ClientSession Owner { get; }

        public void Start(object snapshot)
        {
            lock (_pendingSync)
            {
                if (_started || Volatile.Read(ref _disposed) != 0)
                    return;
                _started = true;
                _pending = snapshot;
            }
            _producer = Task.Run(ProduceAsync);
            Signal();
        }

        public void Publish(object data)
        {
            lock (_pendingSync)
            {
                if (!_started || Volatile.Read(ref _disposed) != 0)
                    return;
                _pending = data;
            }
            Signal();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _shutdown.Cancel();
            Signal();
            if (_producer != null && Task.CurrentId != _producer.Id)
            {
                try { await _producer.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            _signal.Dispose();
            _shutdown.Dispose();
        }

        private async Task ProduceAsync()
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                    object? data;
                    lock (_pendingSync)
                    {
                        data = _pending;
                        _pending = null;
                    }
                    if (data != null)
                        await Owner.SendEventAsync(BackendEventFrame.FromObject("stream.data", StreamId, "json", data), _shutdown.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        private void Signal()
        {
            try { _signal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
