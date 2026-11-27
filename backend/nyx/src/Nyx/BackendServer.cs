using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using KhaosShell.Contracts;
using Microsoft.Win32.SafeHandles;

namespace KhaosShell.Nyx;

internal sealed class BackendServer : IAsyncDisposable
{
    private readonly BackendOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<ClientSession, byte> _clients = new();
    private readonly ConcurrentDictionary<string, StreamSubscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly Lazy<NyxRuntime> _runtime;
    private readonly object _listenerGate = new();
    private NamedPipeServerStream? _listener;
    private int _disposed;

    public BackendServer(BackendOptions options)
    {
        _options = options;
        _runtime = new Lazy<NyxRuntime>(() => new NyxRuntime(options), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        CancellationToken token = linked.Token;
        NamedPipeServerStream listener = CreatePipe();
        lock (_listenerGate) _listener = listener;
        WriteReadiness();

        try
        {
            while (!token.IsCancellationRequested)
            {
                try { await listener.WaitForConnectionAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (token.IsCancellationRequested) { break; }

                if (!IsAuthorizedClient(listener))
                {
                    listener.Dispose();
                    listener = CreatePipe();
                    lock (_listenerGate) _listener = listener;
                    continue;
                }

                var client = new ClientSession(this, listener, token);
                _clients.TryAdd(client, 0);
                _ = RunClientAsync(client);
                listener = CreatePipe();
                lock (_listenerGate) _listener = listener;
            }
        }
        finally
        {
            listener.Dispose();
            lock (_listenerGate) _listener = null;
            await DisposeClientsAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        lock (_listenerGate)
        {
            try { _listener?.Dispose(); } catch { }
            _listener = null;
        }
        foreach (StreamSubscription subscription in _subscriptions.Values.ToArray())
            await RemoveSubscriptionAsync(subscription.StreamId, subscription.Owner).ConfigureAwait(false);
        await DisposeClientsAsync().ConfigureAwait(false);
        if (_runtime.IsValueCreated) _runtime.Value.Dispose();
        _shutdown.Dispose();
    }

    internal async Task<(BackendResponse Response, StreamSubscription? Subscription)> HandleAsync(BackendRequest request, ClientSession client)
    {
        if (request.Method == "POST" && request.Path == "/v1/request")
            return (await _runtime.Value.HandleRequestAsync(request, client.Token).ConfigureAwait(false), null);
        if (request.Method == "POST" && request.Path == "/v1/streams")
            return await OpenStreamAsync(request, client).ConfigureAwait(false);
        if (request.Method == "DELETE" && request.Path.StartsWith("/v1/streams/", StringComparison.Ordinal))
            return (await CloseStreamAsync(request, client).ConfigureAwait(false), null);
        return (Failure(request.Id, 404, "route_not_found", "Backend route was not found."), null);
    }

    private async Task<(BackendResponse Response, StreamSubscription? Subscription)> OpenStreamAsync(BackendRequest request, ClientSession client)
    {
        if (!BackendCodec.TryGetObject(request.Body, out JsonElement body))
            return (Failure(request.Id, 400, "stream_body_invalid", "Stream body must be an object."), null);
        string dataId = BackendCodec.GetString(body, "dataId") ?? "";
        string type = BackendCodec.GetString(body, "type") ?? "";
        if (dataId.Length == 0)
            return (Failure(request.Id, 400, "data_id_invalid", "Stream data ID must not be empty."), null);

        (BackendStreamDescriptor? descriptor, NyxStreamProducer? producer, BackendResponse? error) = await _runtime.Value.OpenStreamAsync(request.Id, dataId, type).ConfigureAwait(false);
        if (error is not null || descriptor is null || producer is null)
            return (error ?? Failure(request.Id, 500, "stream_create_failed", "Could not create stream."), null);

        var subscription = new StreamSubscription(descriptor.StreamId, descriptor.Type, client, producer, _shutdown.Token);
        if (!_subscriptions.TryAdd(descriptor.StreamId, subscription))
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
            return (Failure(request.Id, 500, "stream_create_failed", "Could not allocate stream."), null);
        }
        client.AddSubscription(subscription);
        return (Success(request.Id, descriptor), subscription);
    }

    private async Task<BackendResponse> CloseStreamAsync(BackendRequest request, ClientSession client)
    {
        string streamId = Uri.UnescapeDataString(request.Path["/v1/streams/".Length..]);
        if (streamId.Length == 0 || !_subscriptions.TryGetValue(streamId, out StreamSubscription? subscription) || subscription.Owner != client)
            return Failure(request.Id, 404, "stream_not_found", "Stream was not found.");
        await RemoveSubscriptionAsync(streamId, client).ConfigureAwait(false);
        return Success(request.Id, null);
    }

    internal async Task RemoveSubscriptionAsync(string streamId, ClientSession owner)
    {
        if (_subscriptions.TryRemove(streamId, out StreamSubscription? subscription) && subscription.Owner == owner)
        {
            owner.RemoveSubscription(streamId);
            await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RunClientAsync(ClientSession client)
    {
        try { await client.RunAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) when (client.Token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { Console.Error.WriteLine($"[Nyx] client disconnected: {ex.Message}"); }
        finally
        {
            _clients.TryRemove(client, out _);
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DisposeClientsAsync()
    {
        foreach (ClientSession client in _clients.Keys.ToArray()) await client.DisposeAsync().ConfigureAwait(false);
        _clients.Clear();
    }

    private NamedPipeServerStream CreatePipe()
    {
        string currentSid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        if (!string.Equals(currentSid, _options.Sid, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Supplied SID does not match backend process identity.");

        const uint pipeAccessDuplex = 0x00000003;
        const uint fileFlagOverlapped = 0x40000000;
        const uint securityDescriptorRevision = 1;
        // NamedPipeClientStream needs the pipe to be openable before the server
        // can inspect the client process token. Anonymous clients are denied at
        // the ACL, and authenticated clients are checked against the launch SID
        // immediately after connection.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"O:{_options.Sid}G:{_options.Sid}D:P(D;;GA;;;AN)(A;;GA;;;WD)", securityDescriptorRevision, out IntPtr securityDescriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create backend pipe security descriptor.");
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = securityDescriptor };
            IntPtr pointer = Marshal.AllocHGlobal(attributes.Length);
            try
            {
                Marshal.StructureToPtr(attributes, pointer, false);
                SafePipeHandle handle = CreateNamedPipe($@"\\.\pipe\{_options.PipeName}", pipeAccessDuplex | fileFlagOverlapped, 0, 255, 0, 0, 0, pointer);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new Win32Exception(error, "Could not create backend named pipe.");
                }
                return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        finally { LocalFree(securityDescriptor); }
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

    private void WriteReadiness()
    {
        Console.Out.WriteLine(BackendCodec.SerializeJson(new { ready = true, protocol = _options.Protocol, backendId = _options.BackendId, sid = _options.Sid, pipeName = _options.PipeName }));
        Console.Out.Flush();
    }

    private static BackendResponse Success(string id, object? body) => BackendResponse.FromObject(id, 200, body);
    private static BackendResponse Failure(string id, int status, string code, string message) => new(id, status, Error: new BackendError(code, message));

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; public int InheritHandle; }
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBufferSize, uint inBufferSize, uint timeout, IntPtr attributes);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr LocalFree(IntPtr handle);

    internal sealed class ClientSession : IAsyncDisposable
    {
        private readonly BackendServer _server;
        private readonly Stream _stream;
        private readonly BoundedLineReader _reader;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly CancellationTokenSource _shutdown;
        private readonly ConcurrentDictionary<string, StreamSubscription> _subscriptions = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _inflight = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<Task, byte> _requestTasks = new();
        private readonly object _requestGate = new();
        private Task? _readLoop;
        private bool _requestIntakeClosed;
        private int _disposed;

        public ClientSession(BackendServer server, Stream stream, CancellationToken serverToken)
        {
            _server = server;
            _stream = stream;
            _reader = new BoundedLineReader(stream);
            _writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            _shutdown = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        }

        public CancellationToken Token => _shutdown.Token;
        public BackendServer Server => _server;
        public Task RunAsync() => _readLoop ??= ReadLoopAsync();
        public void AddSubscription(StreamSubscription subscription) => _subscriptions[subscription.StreamId] = subscription;
        public void RemoveSubscription(string streamId) => _subscriptions.TryRemove(streamId, out _);

        public async Task SendEventAsync(BackendEventFrame value, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
            await WriteLineAsync(BackendCodec.SerializeEvent(value), linked.Token).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _shutdown.Cancel();
            Task? readLoop;
            Task[] requestTasks;
            lock (_requestGate)
            {
                _requestIntakeClosed = true;
                readLoop = _readLoop;
                requestTasks = _requestTasks.Keys.ToArray();
            }
            if (readLoop is not null && Task.CurrentId != readLoop.Id)
            {
                try { await readLoop.ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
            }
            try { await Task.WhenAll(requestTasks).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
            foreach (StreamSubscription subscription in _subscriptions.Values.ToArray()) await _server.RemoveSubscriptionAsync(subscription.StreamId, this).ConfigureAwait(false);
            _subscriptions.Clear();
            try { _stream.Dispose(); } catch { }
            try { _writer.Dispose(); } catch { }
            _writeGate.Dispose();
            _shutdown.Dispose();
        }

        private async Task ReadLoopAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                BoundedLine frame = await _reader.ReadAsync(BackendCodec.MaxFrameBytes, _shutdown.Token).ConfigureAwait(false);
                if (frame.EndOfStream) break;
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
                StartRequest(request!);
            }
        }

        private void StartRequest(BackendRequest request)
        {
            var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_requestGate)
            {
                if (_requestIntakeClosed || Volatile.Read(ref _disposed) != 0)
                    return;
                _requestTasks.TryAdd(completion.Task, 0);
            }
            _ = RunTrackedRequestAsync(request, completion);
        }

        private async Task RunTrackedRequestAsync(BackendRequest request, TaskCompletionSource<object?> completion)
        {
            try { await CompleteRequestAsync(request).ConfigureAwait(false); }
            catch (Exception ex) { Console.Error.WriteLine($"[Nyx] request task failed: {ex}"); }
            finally
            {
                completion.TrySetResult(null);
                _requestTasks.TryRemove(completion.Task, out _);
            }
        }

        private async Task CompleteRequestAsync(BackendRequest request)
        {
            if (!_inflight.TryAdd(request.Id, 0))
            {
                await WriteLineAsync(BackendCodec.SerializeResponse(Failure(request.Id, 409, "request_id_in_flight", "Request ID is already in flight.")), _shutdown.Token).ConfigureAwait(false);
                return;
            }
            try
            {
                (BackendResponse response, StreamSubscription? subscription) = await _server.HandleAsync(request, this).ConfigureAwait(false);
                await WriteLineAsync(BackendCodec.SerializeResponse(response), _shutdown.Token).ConfigureAwait(false);
                if (subscription is not null) await subscription.StartAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Nyx] request failed: {ex}");
                try { await WriteLineAsync(BackendCodec.SerializeResponse(Failure(request.Id, 500, "backend_error", "Nyx request failed.")), _shutdown.Token).ConfigureAwait(false); } catch { }
            }
            finally { _inflight.TryRemove(request.Id, out _); }
        }

        private async Task WriteLineAsync(string value, CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);
                await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { _writeGate.Release(); }
        }
    }
}

internal sealed class StreamSubscription : IAsyncDisposable
{
    private readonly NyxStreamProducer _producer;
    private readonly Func<BackendEventFrame, CancellationToken, Task> _send;
    private readonly CancellationTokenSource _shutdown;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly object _pendingSync = new();
    private Task? _worker;
    private object? _pending;
    private bool _hasPending;
    private bool _started;
    private int _disposed;

    public StreamSubscription(
        string streamId,
        string type,
        BackendServer.ClientSession owner,
        NyxStreamProducer producer,
        CancellationToken serverToken,
        Func<BackendEventFrame, CancellationToken, Task>? send = null)
    {
        StreamId = streamId;
        Type = type;
        Owner = owner;
        _producer = producer;
        _send = send ?? owner.SendEventAsync;
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
    }

    public string StreamId { get; }
    public string Type { get; }
    public BackendServer.ClientSession Owner { get; }

    public async Task StartAsync()
    {
        lock (_pendingSync)
        {
            if (_started || Volatile.Read(ref _disposed) != 0)
                return;
            _started = true;
        }

        _worker = Task.Run(SendLoopAsync);
        try { await _producer.StartAsync(Publish, _shutdown.Token).ConfigureAwait(false); }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        Signal();
        try { await _producer.DisposeAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        if (_worker is not null && Task.CurrentId != _worker.Id)
        {
            try { await _worker.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
        _signal.Dispose();
        _shutdown.Dispose();
    }

    private void Publish(object? data)
    {
        lock (_pendingSync)
        {
            if (!_started || Volatile.Read(ref _disposed) != 0) return;
            _pending = data;
            _hasPending = true;
        }
        Signal();
    }

    private async Task SendLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                object? data;
                lock (_pendingSync)
                {
                    if (!_hasPending)
                        continue;
                    data = _pending;
                    _pending = null;
                    _hasPending = false;
                }

                await _send(BackendEventFrame.FromObject("stream.data", StreamId, Type, data), _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private void Signal()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }
}
