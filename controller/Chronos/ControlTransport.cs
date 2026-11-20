using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Chronos;

internal sealed class ControlChannel : IDisposable
{
    private readonly Stream _readStream;
    private readonly Stream _writeStream;
    private readonly BoundedLineReader _reader;
    private readonly StreamWriter _writer;
    private readonly Func<ControlRequest, Task<ControlResponse>> _handler;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ControlResponse>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<ControlResponse>> _inflight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly object _cacheSync = new();
    private int _disconnectSignaled;
    private int _disposed;
    private Task? _loop;

    public ControlChannel(Stream readStream, Stream writeStream, Func<ControlRequest, Task<ControlResponse>> handler)
    {
        _readStream = readStream;
        _writeStream = writeStream;
        _reader = new BoundedLineReader(readStream);
        _writer = new StreamWriter(writeStream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        _handler = handler;
    }

    public string State { get; private set; } = "connected";
    public event Action? Disconnected;
    public event Action<string, JsonElement?>? EventReceived;

    public void Start() => _loop = Task.Run(RunAsync);

    public async Task<ControlResponse> SendAsync(
        string method,
        string path,
        object? body,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (State != "connected")
            throw new IOException("Control channel is not connected.");

        string id = Guid.NewGuid().ToString("D");
        var request = new ControlRequest(
            id,
            method.ToUpperInvariant(),
            path,
            body == null ? null : ControlCodec.SerializeBody(body));
        var completion = new TaskCompletionSource<ControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            throw new InvalidOperationException("Could not allocate control request ID.");

        try
        {
            await WriteAsync(ControlCodec.SerializeRequest(request), cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        State = "closed";
        FailPending(new IOException("Control channel closed."));
        _shutdown.Cancel();
        try { _readStream.Dispose(); } catch { }
        try { _writeStream.Dispose(); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        DisposeQuietly(_writer);
        DisposeQuietly(_writeLock);
        DisposeQuietly(_shutdown);
    }

    private static void DisposeQuietly(IDisposable disposable)
    {
        try { disposable.Dispose(); }
        catch { }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                BoundedLine frame = await _reader.ReadAsync(ControlCodec.MaxFrameBytes, _shutdown.Token).ConfigureAwait(false);
                if (frame.EndOfStream)
                    break;
                if (frame.TooLarge)
                {
                    await WriteAsync(ControlCodec.SerializeResponse(
                        ControlResponse.Failure(null, 400, "frame_too_large", "Control frame exceeds 1 MiB.")), _shutdown.Token).ConfigureAwait(false);
                    continue;
                }

                string line = frame.Text!;

                if (ControlCodec.TryParseResponse(line, out ControlResponse? response))
                {
                    if (response?.Id is { } responseId && _pending.TryGetValue(responseId, out TaskCompletionSource<ControlResponse>? completion))
                        completion.TrySetResult(response);
                    continue;
                }

                if (ControlCodec.TryParseEvent(line, out ControlEvent? controlEvent))
                {
                    EventReceived?.Invoke(controlEvent!.Name, controlEvent.Body);
                    continue;
                }

                if (!ControlCodec.TryParseRequest(line, out ControlRequest? request, out ControlError? parseError, out string? requestId))
                {
                    await WriteAsync(ControlCodec.SerializeResponse(
                        new ControlResponse(requestId, 400, Error: parseError)), _shutdown.Token).ConfigureAwait(false);
                    continue;
                }

                Task<ControlResponse> responseTask = GetRequestTask(request!);
                _ = WriteRequestResponseAsync(request!.Id, responseTask);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            if (State != "closed")
            {
                State = "disconnected";
                FailPending(new IOException("Control channel disconnected."));
                if (Interlocked.Exchange(ref _disconnectSignaled, 1) == 0)
                    Disconnected?.Invoke();
            }
        }
    }

    private Task<ControlResponse> GetRequestTask(ControlRequest request)
    {
        string fingerprint = ControlCodec.SerializeRequest(request);
        return _inflight.GetOrAdd(request.Id, _ => Task.Run(() => ExecuteRequest(request, fingerprint), _shutdown.Token));
    }

    private async Task<ControlResponse> ExecuteRequest(ControlRequest request, string fingerprint)
    {
        if (TryGetCached(request.Id, fingerprint, out ControlResponse? cached, out bool reused))
        {
            return reused
                ? ControlResponse.Failure(request.Id, 409, "request_id_reused", "Request ID was reused with different content.")
                : cached!;
        }

        ControlResponse response;
        try
        {
            response = await _handler(request).ConfigureAwait(false);
        }
        catch (BackendException ex)
        {
            response = ControlResponse.Failure(request.Id, ex.Status, ex.Code, ex.Message);
        }
        catch (LogStoreException ex)
        {
            response = ControlResponse.Failure(request.Id, ex.Status, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            response = ControlResponse.Failure(request.Id, 500, "internal_error", ex.Message);
        }

        Cache(request.Id, fingerprint, response);
        return response;
    }

    private async Task WriteRequestResponseAsync(string requestId, Task<ControlResponse> responseTask)
    {
        try
        {
            ControlResponse response = await responseTask.ConfigureAwait(false);
            await WriteAsync(ControlCodec.SerializeResponse(response), _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            _inflight.TryRemove(requestId, out _);
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (TaskCompletionSource<ControlResponse> completion in _pending.Values)
            completion.TrySetException(exception);
    }

    private async Task WriteAsync(string line, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    private bool TryGetCached(string id, string fingerprint, out ControlResponse? response, out bool reused)
    {
        lock (_cacheSync)
        {
            PruneCacheLocked();
            if (!_cache.TryGetValue(id, out CacheEntry? entry))
            {
                response = null;
                reused = false;
                return false;
            }

            response = entry.Response;
            reused = !string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal);
            return true;
        }
    }

    private void Cache(string id, string fingerprint, ControlResponse response)
    {
        lock (_cacheSync)
        {
            PruneCacheLocked();
            if (_cache.Count >= 256)
                _cache.Remove(_cache.Keys.First());
            _cache[id] = new CacheEntry(fingerprint, response, DateTimeOffset.UtcNow.AddMinutes(5));
        }
    }

    private void PruneCacheLocked()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (string key in _cache.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
            _cache.Remove(key);
    }

    private sealed record CacheEntry(string Fingerprint, ControlResponse Response, DateTimeOffset Expires);
}

internal static class AnonymousPipeFactory
{
    public static (AnonymousPipeServerStream ToChild, AnonymousPipeServerStream FromChild, string ChildReadHandle, string ChildWriteHandle) Create()
    {
        var toChild = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var fromChild = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        return (toChild, fromChild, toChild.GetClientHandleAsString(), fromChild.GetClientHandleAsString());
    }
}
