using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace KhaosShell.MinimalBackend;

internal sealed class BackendServer : IAsyncDisposable
{
    private readonly BackendOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<ClientSession, byte> _clients = new();
    private readonly ConcurrentDictionary<string, StreamSubscription> _subscriptions = new(StringComparer.Ordinal);
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
                // NamedPipeClientStream opens the local client handle anonymously on
                // this Windows/.NET combination. The pipe name is the capability;
                // Chronos supplies a fresh unpredictable name for every launch and
                // the backend still verifies the launch SID above.
                $"O:{_options.Sid}G:{_options.Sid}D:P(A;;GA;;;WD)",
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSDRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

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

    private async Task<BackendResponse> HandleAsync(BackendRequest request, ClientSession client)
    {
        if (request.Method == "POST" && request.Path == "/v1/request")
            return HandleRequest(request);
        if (request.Method == "POST" && request.Path == "/v1/streams")
            return await OpenStreamAsync(request, client).ConfigureAwait(false);
        if (request.Method == "DELETE" && request.Path.StartsWith("/v1/streams/", StringComparison.Ordinal))
            return await CloseStreamAsync(request, client).ConfigureAwait(false);
        return Failure(request.Id, 404, "route_not_found", "Backend route was not found.");
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

        if (method != "GET")
            return Failure(request.Id, 405, "method_not_allowed", "Reference routes allow GET only.");

        return path switch
        {
            "/v1/minimal/health" => Success(request.Id, new
            {
                ok = true,
                backendId = _options.BackendId,
                protocol = _options.Protocol
            }),
            "/v1/minimal/value" => Success(request.Id, new { value = CurrentValue() }),
            _ => Failure(request.Id, 404, "route_not_found", "Reference route was not found.")
        };
    }

    private Task<BackendResponse> OpenStreamAsync(BackendRequest request, ClientSession client)
    {
        if (!BackendCodec.TryGetObject(request.Body, out JsonElement body))
            return Task.FromResult(Failure(request.Id, 400, "stream_body_invalid", "Stream body must be an object."));

        string dataId = BackendCodec.GetString(body, "dataId") ?? "";
        string type = BackendCodec.GetString(body, "type") ?? "";
        if (type != "json")
            return Task.FromResult(Failure(request.Id, 400, "stream_type_unsupported", "Only json streams are supported."));
        if (dataId != "minimal.value")
            return Task.FromResult(Failure(request.Id, 404, "data_not_found", "Reference data ID was not found."));

        string streamId = Guid.NewGuid().ToString("D");
        var subscription = new StreamSubscription(streamId, client, _shutdown.Token);
        if (!_subscriptions.TryAdd(streamId, subscription))
            return Task.FromResult(Failure(request.Id, 500, "stream_create_failed", "Could not allocate stream."));
        client.AddSubscription(subscription);
        subscription.Start();

        return Task.FromResult(Success(request.Id, new
        {
            streamId,
            type,
            schema = new
            {
                kind = "object",
                fields = new { value = new { kind = "number" } }
            }
        }));
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

    private static BackendResponse Success(string id, object? body) => new(id, 200, body);

    private static BackendResponse Failure(string id, int status, string code, string message) =>
        new(id, status, Error: new BackendError(code, message));

    private static double CurrentValue() => Math.Round(20.0 + (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 10000) / 1000.0, 3);

    private sealed class ClientSession : IAsyncDisposable
    {
        private readonly BackendServer _server;
        private readonly Stream _stream;
        private readonly BoundedLineReader _reader;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _shutdown;
        private readonly ConcurrentDictionary<string, StreamSubscription> _subscriptions = new(StringComparer.Ordinal);
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

        public async Task SendEventAsync(BackendEvent value, CancellationToken cancellationToken = default)
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
            BackendResponse response;
            try
            {
                response = await _server.HandleAsync(request, this).ConfigureAwait(false);
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

            try { await WriteLineAsync(BackendCodec.SerializeResponse(response), _shutdown.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        }

        private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false); }
            finally { _writeLock.Release(); }
        }
    }

    private sealed class StreamSubscription : IAsyncDisposable
    {
        private readonly CancellationTokenSource _shutdown;
        private Task? _producer;
        private int _disposed;

        public StreamSubscription(string streamId, ClientSession owner, CancellationToken serverToken)
        {
            StreamId = streamId;
            Owner = owner;
            _shutdown = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        }

        public string StreamId { get; }
        public ClientSession Owner { get; }

        public void Start() => _producer = Task.Run(ProduceAsync);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _shutdown.Cancel();
            if (_producer != null && Task.CurrentId != _producer.Id)
            {
                try { await _producer.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            _shutdown.Dispose();
        }

        private async Task ProduceAsync()
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    await Owner.SendEventAsync(new BackendEvent(StreamId, "json", new { value = CurrentValue() }), _shutdown.Token).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _shutdown.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
