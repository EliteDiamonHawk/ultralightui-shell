using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Text.Json;

namespace Aether;

internal sealed record ControlRequest(
    string Id,
    string Method,
    string Path,
    JsonElement? Body);

internal sealed record ControlError(string Code, string Message, object? Details = null);

internal sealed record ControlResponse(
    string? Id,
    int Status,
    object? Body = null,
    ControlError? Error = null);

internal sealed record ControlEvent(string Name, JsonElement? Body);

internal static class ControlPipeCodec
{
    public const int MaxFrameBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase
    };

    public static bool TryParseRequest(string line, out ControlRequest? request, out ControlError? error)
    {
        request = null;
        error = null;
        if (Encoding.UTF8.GetByteCount(line) > MaxFrameBytes)
        {
            error = new ControlError("frame_too_large", "Control frame exceeds 1 MiB.");
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = new ControlError("invalid_frame", "Control frame must be a JSON object.");
                return false;
            }

            string id = RequiredString(root, "id");
            if (!Guid.TryParse(id, out _))
                throw new ControlProtocolException("request_id_invalid", "Request id must be a UUID.");
            string method = RequiredString(root, "method").ToUpperInvariant();
            if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE"))
                throw new ControlProtocolException("method_invalid", "Unsupported HTTP method.");
            string path = RequiredString(root, "path");
            if (!path.StartsWith("/v1/", StringComparison.Ordinal))
                throw new ControlProtocolException("path_invalid", "Path must begin with /v1/.");

            JsonElement? body = root.TryGetProperty("body", out JsonElement bodyElement)
                && bodyElement.ValueKind != JsonValueKind.Null
                ? bodyElement.Clone()
                : null;
            request = new ControlRequest(id, method, path, body);
            return true;
        }
        catch (JsonException)
        {
            error = new ControlError("invalid_json", "Control frame is not valid JSON.");
            return false;
        }
        catch (ControlProtocolException ex)
        {
            error = new ControlError(ex.Code, ex.Message);
            return false;
        }
    }

    public static string Serialize(ControlResponse response)
    {
        object envelope = new
        {
            id = response.Id,
            status = response.Status,
            body = response.Error == null ? response.Body : null,
            error = response.Error
        };
        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    public static string SerializeRequest(ControlRequest request) => JsonSerializer.Serialize(new
    {
        id = request.Id,
        method = request.Method,
        path = request.Path,
        body = request.Body
    }, JsonOptions);

    public static string SerializeEvent(string name, object? body = null) => JsonSerializer.Serialize(new
    {
        id = (string?)null,
        @event = name,
        body = body == null ? (JsonElement?)null : SerializeBody(body)
    }, JsonOptions);

    public static JsonElement SerializeBody(object body) =>
        JsonSerializer.SerializeToElement(body, JsonOptions);

    public static string SerializeJson(object body) => SerializeBody(body).GetRawText();

    public static bool TryParseResponse(string line, out ControlResponse? response)
    {
        response = null;
        if (Encoding.UTF8.GetByteCount(line) > MaxFrameBytes)
            return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("status", out JsonElement status) || status.ValueKind != JsonValueKind.Number ||
                !root.TryGetProperty("id", out JsonElement idElement) || idElement.ValueKind != JsonValueKind.String)
                return false;
            string id = idElement.GetString() ?? "";
            if (!Guid.TryParse(id, out _) || !status.TryGetInt32(out int statusCode))
                return false;
            JsonElement? body = root.TryGetProperty("body", out JsonElement bodyElement) && bodyElement.ValueKind != JsonValueKind.Null
                ? bodyElement.Clone()
                : null;
            ControlError? error = null;
            if (root.TryGetProperty("error", out JsonElement errorElement) && errorElement.ValueKind == JsonValueKind.Object)
            {
                string code = errorElement.TryGetProperty("code", out JsonElement codeElement) ? codeElement.GetString() ?? "error" : "error";
                string message = errorElement.TryGetProperty("message", out JsonElement messageElement) ? messageElement.GetString() ?? "" : "";
                error = new ControlError(code, message);
            }
            response = new ControlResponse(id, statusCode, body, error);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParseEvent(string line, out ControlEvent? controlEvent)
    {
        controlEvent = null;
        if (Encoding.UTF8.GetByteCount(line) > MaxFrameBytes)
            return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.Null ||
                !root.TryGetProperty("event", out JsonElement name) || name.ValueKind != JsonValueKind.String)
                return false;
            controlEvent = new ControlEvent(name.GetString() ?? "", root.TryGetProperty("body", out JsonElement body) && body.ValueKind != JsonValueKind.Null ? body.Clone() : null);
            return controlEvent.Name.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new ControlProtocolException("field_invalid", $"Field '{name}' must be a string.");
        string result = value.GetString()?.Trim() ?? "";
        if (result.Length == 0)
            throw new ControlProtocolException("field_empty", $"Field '{name}' must not be empty.");
        return result;
    }

    private sealed class ControlProtocolException : Exception
    {
        public ControlProtocolException(string code, string message) : base(message) => Code = code;
        public string Code { get; }
    }
}

internal sealed class ControlPipeChannel : IDisposable, IRendererLogTransport
{
    private readonly Stream _readStream;
    private readonly Stream _writeStream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly Func<ControlRequest, ControlResponse> _handler;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ControlResponse>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<ControlResponse>> _inflightRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Fingerprint, string Response, DateTimeOffset Expires)> _idempotency = new(StringComparer.Ordinal);
    private readonly object _idempotencySync = new();
    private int _disconnectSignaled;
    private Task? _loop;

    public ControlPipeChannel(IntPtr readHandle, IntPtr writeHandle, Func<ControlRequest, ControlResponse> handler)
    {
        if (readHandle == IntPtr.Zero || writeHandle == IntPtr.Zero)
            throw new ArgumentException("Control handles must be non-zero.");

        _readStream = new FileStream(new SafeFileHandle(readHandle, ownsHandle: true), FileAccess.Read, 4096, isAsync: false);
        _writeStream = new FileStream(new SafeFileHandle(writeHandle, ownsHandle: true), FileAccess.Write, 4096, isAsync: false);
        _reader = new StreamReader(_readStream, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        _writer = new StreamWriter(_writeStream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        _handler = handler;
    }

    public string State { get; private set; } = "connected";
    public event Action? Disconnected;

    public void Start() => _loop = Task.Run(RunAsync);

    public async Task<ControlResponse> SendAsync(string method, string path, object? body, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (State != "connected")
            throw new IOException("Control channel is not connected.");
        string id = Guid.NewGuid().ToString("D");
        JsonElement? bodyElement = body == null
            ? null
            : ControlPipeCodec.SerializeBody(body);
        var request = new ControlRequest(id, method.ToUpperInvariant(), path, bodyElement);
        var completion = new TaskCompletionSource<ControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            throw new InvalidOperationException("Could not allocate control request ID.");
        try
        {
            await WriteAsync(ControlPipeCodec.SerializeRequest(request), cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public void Dispose()
    {
        State = "closed";
        FailPending(new IOException("Control channel closed."));
        _shutdown.Cancel();
        try { _readStream.Dispose(); } catch { }
        try { _writeStream.Dispose(); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _writer.Dispose();
        _reader.Dispose();
        _writeLock.Dispose();
        _shutdown.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                string? line = await _reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
                if (line == null)
                    break;

                if (ControlPipeCodec.TryParseResponse(line, out ControlResponse? incomingResponse))
                {
                    if (incomingResponse?.Id is { } responseId && _pending.TryGetValue(responseId, out TaskCompletionSource<ControlResponse>? completion))
                        completion.TrySetResult(incomingResponse);
                    continue;
                }

                if (ControlPipeCodec.TryParseEvent(line, out ControlEvent? controlEvent))
                    continue;

                ControlResponse? response = null;
                if (!ControlPipeCodec.TryParseRequest(line, out ControlRequest? request, out ControlError? parseError))
                {
                    response = new ControlResponse(null, 400, Error: parseError);
                }
                else
                {
                    Task<ControlResponse> responseTask = GetRequestTask(request!);
                    _ = WriteRequestResponseAsync(request!.Id, responseTask);
                }

                if (response != null)
                    await WriteAsync(ControlPipeCodec.Serialize(response), _shutdown.Token).ConfigureAwait(false);
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

    public Task SendEventAsync(string name, object? body = null, CancellationToken cancellationToken = default) =>
        WriteAsync(ControlPipeCodec.SerializeEvent(name, body), cancellationToken);

    public void TrySendEvent(string name, object? body = null)
    {
        _ = SendEventAsync(name, body).ContinueWith(
            _ => { },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private Task<ControlResponse> GetRequestTask(ControlRequest request)
    {
        string fingerprint = ControlPipeCodec.SerializeRequest(request);
        return _inflightRequests.GetOrAdd(request.Id, _ => Task.Run(() => ExecuteRequest(request, fingerprint), _shutdown.Token));
    }

    private ControlResponse ExecuteRequest(ControlRequest request, string fingerprint)
    {
        if (TryGetCached(request.Id, fingerprint, out ControlResponse? cached, out bool reused))
        {
            return reused
                ? new ControlResponse(request.Id, 409, Error: new ControlError("request_id_reused", "Request ID was reused with different content."))
                : cached!;
        }

        ControlResponse response;
        try { response = _handler(request); }
        catch (Exception ex)
        {
            response = new ControlResponse(request.Id, 500,
                Error: new ControlError("internal_error", ex.Message));
        }
        Cache(request.Id, fingerprint, response);
        return response;
    }

    private async Task WriteRequestResponseAsync(string requestId, Task<ControlResponse> responseTask)
    {
        try
        {
            ControlResponse response = await responseTask.ConfigureAwait(false);
            await WriteAsync(ControlPipeCodec.Serialize(response), _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            _inflightRequests.TryRemove(requestId, out _);
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
        lock (_idempotencySync)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (string expired in _idempotency.Where(item => item.Value.Expires <= now).Select(item => item.Key).ToArray())
                _idempotency.Remove(expired);
            if (!_idempotency.TryGetValue(id, out var entry))
            {
                response = null;
                reused = false;
                return false;
            }
            response = ControlPipeCodec.TryParseResponse(entry.Response, out ControlResponse? parsed) ? parsed : null;
            reused = !string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal);
            return true;
        }
    }

    private void Cache(string id, string fingerprint, ControlResponse response)
    {
        lock (_idempotencySync)
        {
            if (_idempotency.Count >= 256)
                _idempotency.Remove(_idempotency.Keys.First());
            _idempotency[id] = (fingerprint, ControlPipeCodec.Serialize(response), DateTimeOffset.UtcNow.AddMinutes(5));
        }
    }
}
