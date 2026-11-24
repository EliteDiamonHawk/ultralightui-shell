using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aether;

internal enum RendererLogLevel
{
    Trace,
    Info,
    Warn,
    Error
}

internal sealed record RendererLogEvent(
    string EventId,
    DateTimeOffset TimestampUtc,
    string Level,
    string EventName,
    IReadOnlyDictionary<string, string?> Properties);

internal sealed record RendererLogBatch(
    int ProtocolVersion,
    string BatchId,
    int ProcessId,
    string SessionId,
    IReadOnlyList<RendererLogEvent> Events);

internal sealed record RendererLogPolicy(
    bool Enabled = true,
    int MaxEvents = RendererLogPolicy.DefaultMaxEvents,
    int MaxBytes = RendererLogPolicy.DefaultMaxBytes,
    int FlushIntervalMs = RendererLogPolicy.DefaultFlushIntervalMs)
{
    public const int DefaultMaxEvents = 32;
    public const int DefaultMaxBytes = 64 * 1024;
    public const int DefaultFlushIntervalMs = 250;
    public const int QueueCapacity = 256;

    public bool IsValid(out string error)
    {
        if (MaxEvents is < 1 or > QueueCapacity)
        {
            error = $"maxEvents must be between 1 and {QueueCapacity}.";
            return false;
        }
        if (MaxBytes is < 4096 or > DefaultMaxBytes)
        {
            error = $"maxBytes must be between 4096 and {DefaultMaxBytes}.";
            return false;
        }
        if (FlushIntervalMs is < 10 or > 5000)
        {
            error = "flushIntervalMs must be between 10 and 5000.";
            return false;
        }

        error = "";
        return true;
    }

    public object ToPayload() => new
    {
        enabled = Enabled,
        maxEvents = MaxEvents,
        maxBytes = MaxBytes,
        flushIntervalMs = FlushIntervalMs
    };

    public static bool TryParse(JsonElement? body, RendererLogPolicy current, out RendererLogPolicy policy, out string error)
    {
        policy = current;
        error = "";
        if (body is not { } value || value.ValueKind != JsonValueKind.Object)
        {
            error = "Logging policy body must be a JSON object.";
            return false;
        }

        if (value.TryGetProperty("enabled", out JsonElement enabled))
        {
            if (enabled.ValueKind != JsonValueKind.True && enabled.ValueKind != JsonValueKind.False)
            {
                error = "enabled must be a boolean.";
                return false;
            }
            policy = policy with { Enabled = enabled.GetBoolean() };
        }
        if (!TryReadInt(value, "maxEvents", policy.MaxEvents, out int maxEvents, out error) ||
            !TryReadInt(value, "maxBytes", policy.MaxBytes, out int maxBytes, out error) ||
            !TryReadInt(value, "flushIntervalMs", policy.FlushIntervalMs, out int flushIntervalMs, out error))
            return false;

        policy = policy with
        {
            MaxEvents = maxEvents,
            MaxBytes = maxBytes,
            FlushIntervalMs = flushIntervalMs
        };
        return policy.IsValid(out error);
    }

    private static bool TryReadInt(JsonElement body, string name, int fallback, out int value, out string error)
    {
        value = fallback;
        error = "";
        if (!body.TryGetProperty(name, out JsonElement element))
            return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value))
        {
            error = $"{name} must be an integer.";
            return false;
        }
        return true;
    }
}

internal interface IRendererLogTransport
{
    string State { get; }
    event Action? Disconnected;
    Task<ControlResponse> SendAsync(string method, string path, object? body, TimeSpan timeout, CancellationToken cancellationToken = default);
}

internal sealed class RendererLogger : IDisposable
{
    private sealed record QueuedEvent(RendererLogEvent Event, int Bytes);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly object _sync = new();
    private readonly LinkedList<QueuedEvent> _queue = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("D");
    private readonly Action<RendererLogEvent> _consoleSink;
    private readonly Task _worker;
    private IRendererLogTransport? _transport;
    private RendererLogPolicy _policy = new();
    private int _queueBytes;
    private bool _disablePending;
    private bool _disposed;

    public RendererLogger(IRendererLogTransport? transport = null, Action<RendererLogEvent>? consoleSink = null)
    {
        _consoleSink = consoleSink ?? WriteConsole;
        _worker = Task.Run(WorkerAsync);
        if (transport != null)
            AttachControlChannel(transport);
    }

    public RendererLogPolicy Policy
    {
        get { lock (_sync) return _policy; }
    }

    public void AttachControlChannel(IRendererLogTransport? transport)
    {
        IRendererLogTransport? previous;
        lock (_sync)
        {
            if (_disposed)
                return;
            previous = _transport;
            _transport = transport;
            _queue.Clear();
            _queueBytes = 0;
        }

        if (previous != null)
            previous.Disconnected -= OnTransportDisconnected;
        if (transport != null)
        {
            transport.Disconnected += OnTransportDisconnected;
            Info("control.connected");
        }
        SignalWorker();
    }

    public void Trace(string eventName, IReadOnlyDictionary<string, string?>? properties = null) =>
        Log(RendererLogLevel.Trace, eventName, properties);

    public void Info(string eventName, IReadOnlyDictionary<string, string?>? properties = null) =>
        Log(RendererLogLevel.Info, eventName, properties);

    public void Warn(string eventName, IReadOnlyDictionary<string, string?>? properties = null) =>
        Log(RendererLogLevel.Warn, eventName, properties);

    public void Error(string eventName, IReadOnlyDictionary<string, string?>? properties = null) =>
        Log(RendererLogLevel.Error, eventName, properties);

    public bool TryApplyPolicy(JsonElement? body, out RendererLogPolicy policy, out int status, out string error)
    {
        RendererLogPolicy current;
        lock (_sync) current = _policy;
        if (!RendererLogPolicy.TryParse(body, current, out policy, out error))
        {
            status = 400;
            return false;
        }

        if (!policy.Enabled && current.Enabled)
        {
            lock (_sync) _disablePending = true;
            bool flushed;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                flushed = FlushAllAsync(requireDurable: true, timeout.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                flushed = false;
            }

            if (!flushed)
            {
                lock (_sync) _disablePending = false;
                status = 503;
                error = "Could not durably flush pending logs before disabling logging.";
                return false;
            }
        }

        lock (_sync)
        {
            _policy = policy;
            _disablePending = false;
        }
        SignalWorker();
        status = 200;
        return true;
    }

    public void Dispose()
    {
        IRendererLogTransport? transport;
        lock (_sync)
        {
            if (_disposed)
                return;
            transport = _transport;
        }

        if (transport?.State == "connected")
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                FlushAllAsync(requireDurable: false, timeout.Token).GetAwaiter().GetResult();
            }
            catch (Exception) { }
        }

        lock (_sync) _disposed = true;
        _shutdown.Cancel();
        SignalWorker();
        try { _worker.Wait(TimeSpan.FromSeconds(1)); } catch { }
        if (transport != null)
            transport.Disconnected -= OnTransportDisconnected;
        _sendLock.Dispose();
        _wake.Dispose();
        _shutdown.Dispose();
    }

    internal void Log(RendererLogLevel level, string eventName, IReadOnlyDictionary<string, string?>? properties)
    {
        RendererLogEvent item = new(
            Guid.NewGuid().ToString("D"),
            DateTimeOffset.UtcNow,
            level.ToString().ToLowerInvariant(),
            eventName,
            SanitizeProperties(properties));

        bool writeConsole = false;
        bool signal = false;
        lock (_sync)
        {
            if (_disposed || _disablePending)
                return;

            if (_policy.Enabled && _transport?.State == "connected")
            {
                int bytes = JsonSerializer.SerializeToUtf8Bytes(item, JsonOptions).Length;
                if (!TryQueueLocked(item, bytes))
                    return;
                signal = _queue.Count >= _policy.MaxEvents || _queueBytes >= _policy.MaxBytes;
            }
            else
            {
                writeConsole = true;
            }
        }

        if (writeConsole)
            _consoleSink(item);
        if (signal)
            SignalWorker();
    }

    private bool TryQueueLocked(RendererLogEvent item, int bytes)
    {
        if (_queue.Count >= RendererLogPolicy.QueueCapacity)
        {
            LinkedListNode<QueuedEvent>? candidate = _queue.First;
            while (candidate != null && Severity(candidate.Value.Event.Level) > 1)
                candidate = candidate.Next;
            if (candidate == null)
                return false;
            _queueBytes -= candidate.Value.Bytes;
            _queue.Remove(candidate);
        }

        _queue.AddLast(new QueuedEvent(item, bytes));
        _queueBytes += bytes;
        return true;
    }

    private async Task WorkerAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                int interval;
                lock (_sync) interval = _policy.FlushIntervalMs;
                Task delay = Task.Delay(interval, _shutdown.Token);
                Task signal = _wake.WaitAsync(_shutdown.Token);
                await Task.WhenAny(delay, signal).ConfigureAwait(false);
                if (_shutdown.IsCancellationRequested)
                    return;
                await FlushOneAsync(requireDurable: false, _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task<bool> FlushAllAsync(bool requireDurable, CancellationToken cancellationToken)
    {
        while (true)
        {
            bool hasItems;
            lock (_sync) hasItems = _queue.Count > 0;
            if (!hasItems)
                return true;
            if (!await FlushOneAsync(requireDurable, cancellationToken).ConfigureAwait(false))
                return false;
        }
    }

    private async Task<bool> FlushOneAsync(bool requireDurable, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IRendererLogTransport? transport;
            List<QueuedEvent> selected;
            RendererLogPolicy policy;
            lock (_sync)
            {
                transport = _transport;
                policy = _policy;
                selected = SelectBatchLocked(policy);
            }

            if (selected.Count == 0)
                return true;
            if (transport?.State != "connected" || !policy.Enabled)
            {
                RequeueFront(selected);
                return false;
            }

            var batch = new RendererLogBatch(
                1,
                Guid.NewGuid().ToString("D"),
                Environment.ProcessId,
                _sessionId,
                selected.Select(item => item.Event).ToArray());

            ControlResponse response;
            try
            {
                response = await transport.SendAsync(
                    "POST",
                    "/v1/logs",
                    batch,
                    TimeSpan.FromSeconds(2),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!requireDurable)
            {
                RequeueFront(selected);
                return false;
            }
            catch (Exception)
            {
                RequeueFront(selected);
                return false;
            }

            if (response.Status is >= 200 and < 300)
                return true;

            RequeueFront(selected);
            return !requireDurable && response.Status is >= 400 and < 500;
        }
        finally { _sendLock.Release(); }
    }

    private List<QueuedEvent> SelectBatchLocked(RendererLogPolicy policy)
    {
        var selected = new List<QueuedEvent>();
        LinkedListNode<QueuedEvent>? node = _queue.First;
        while (node != null && selected.Count < policy.MaxEvents)
        {
            QueuedEvent item = node.Value;
            int candidateBytes = JsonSerializer.SerializeToUtf8Bytes(
                new RendererLogBatch(1, "batch", Environment.ProcessId, _sessionId,
                    selected.Select(entry => entry.Event).Append(item.Event).ToArray()),
                JsonOptions).Length;
            if (selected.Count > 0 && candidateBytes > policy.MaxBytes)
                break;
            selected.Add(item);
            node = node.Next;
        }

        foreach (QueuedEvent item in selected)
        {
            _queue.RemoveFirst();
            _queueBytes -= item.Bytes;
        }
        return selected;
    }

    private void RequeueFront(IReadOnlyList<QueuedEvent> items)
    {
        lock (_sync)
        {
            if (_transport?.State != "connected")
                return;
            for (int index = items.Count - 1; index >= 0; index--)
            {
                _queue.AddFirst(items[index]);
                _queueBytes += items[index].Bytes;
            }
        }
    }

    private void OnTransportDisconnected()
    {
        lock (_sync)
        {
            _transport = null;
            _policy = _policy with { Enabled = true };
            _disablePending = false;
            _queue.Clear();
            _queueBytes = 0;
        }
        Info("control.disconnected");
    }

    private void SignalWorker()
    {
        try { _wake.Release(); } catch (ObjectDisposedException) { }
    }

    private static int Severity(string level) => level switch
    {
        "trace" => 0,
        "info" => 1,
        "warn" => 2,
        "error" => 3,
        _ => 1
    };

    private static IReadOnlyDictionary<string, string?> SanitizeProperties(IReadOnlyDictionary<string, string?>? properties)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (properties == null)
            return result;

        foreach ((string key, string? value) in properties)
        {
            if (value == null)
            {
                result[key] = null;
                continue;
            }

            if (key.Equals("group", StringComparison.OrdinalIgnoreCase))
            {
                byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
                result[key] = $"sha256:{Convert.ToHexString(digest)[..12].ToLowerInvariant()}";
            }
            else if (key.Equals("command", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("exceptionType", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("hasGroup", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("hasTarget", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("configuredWindows", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("controllerConnected", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("windowCount", StringComparison.OrdinalIgnoreCase))
            {
                result[key] = value.Length <= 128 ? value : value[..128];
            }
            else
            {
                result[key] = "[redacted]";
            }
        }
        return result;
    }

    private static void WriteConsole(RendererLogEvent item)
    {
        string properties = item.Properties.Count == 0
            ? ""
            : " " + JsonSerializer.Serialize(item.Properties, JsonOptions);
        string line = $"[Log] {item.Level} {item.EventName}{properties}";
        if (item.Level is "warn" or "error")
            Console.Error.WriteLine(line);
        else
            Console.WriteLine(line);
    }
}
