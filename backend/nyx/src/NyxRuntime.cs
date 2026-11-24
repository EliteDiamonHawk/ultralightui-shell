using System.Text.Json;
using System.Text.Json.Nodes;
using KhaosShell.Contracts;

namespace KhaosShell.Nyx;

internal sealed class NyxRuntime : IDisposable
{
    private static readonly IReadOnlySet<string> RuntimeSettingKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "hardware_monitor_enabled",
    };
    private readonly BackendOptions _options;
    private readonly IClock _clock = new SystemClock();
    private readonly Lazy<SettingsStore> _settings;
    private readonly Lazy<SystemMetricsService> _metrics;
    private readonly SystemActions _actions = new();
    private int _disposed;

    public NyxRuntime(BackendOptions options)
    {
        _options = options;
        _settings = new Lazy<SettingsStore>(() => new SettingsStore(_options.DataRoot), LazyThreadSafetyMode.ExecutionAndPublication);
        _metrics = new Lazy<SystemMetricsService>(() =>
        {
            bool enabled = true;
            try
            {
                JsonNode? configured = _settings.Value.GetAppValue("hardware_monitor_enabled", out bool found);
                if (found && configured is JsonValue value && value.TryGetValue<bool>(out bool parsed)) enabled = parsed;
            }
            catch { }
            return new SystemMetricsService(new HardwareMonitor(enabled), _clock);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<BackendResponse> HandleRequestAsync(BackendRequest request, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return Failure(request.Id, 503, "backend_stopping", "Nyx backend is stopping.");
        if (!BackendCodec.TryGetObject(request.Body, out JsonElement envelope))
            return Failure(request.Id, 400, "request_body_invalid", "Request body must be an object.");

        string method = (BackendCodec.GetString(envelope, "method") ?? "").ToUpperInvariant();
        string path = BackendCodec.GetString(envelope, "path") ?? "";
        if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE"))
            return Failure(request.Id, 400, "method_invalid", "Nested method is unsupported.");
        if (!path.StartsWith("/v1/", StringComparison.Ordinal))
            return Failure(request.Id, 400, "path_invalid", "Nested path must begin with /v1/.");
        JsonElement? body = envelope.TryGetProperty("body", out JsonElement bodyElement) && bodyElement.ValueKind != JsonValueKind.Null ? bodyElement : null;

        try
        {
            if (method == "GET" && path == "/v1/nyx/health")
                return Success(request.Id, new { ok = true, backendId = _options.BackendId, protocol = _options.Protocol, capabilities = new[] { "settings", "metrics", "actions" } });
            if (method == "GET" && path == "/v1/nyx/status")
                return Success(request.Id, new { ok = true, service = "nyx", state = "ready" });
            if (method == "GET" && path == "/v1/nyx/system/time")
                return Success(request.Id, new { epochMilliseconds = _clock.UtcNow.ToUnixTimeMilliseconds() });
            if (path.StartsWith("/v1/nyx/settings/", StringComparison.Ordinal))
                return await HandleSettingsAsync(request.Id, method, path, body).ConfigureAwait(false);
            if (method == "GET" && path == "/v1/nyx/system/metrics")
                return await HandleMetricsAsync(request.Id, body, cancellationToken).ConfigureAwait(false);
            if (method == "POST" && path.StartsWith("/v1/nyx/system/controls/", StringComparison.Ordinal))
                return HandleControl(request.Id, path);

            if (path == "/v1/nyx/system/controls")
                return Failure(request.Id, 405, "method_not_allowed", "Control name is required.");
            return Failure(request.Id, 404, "route_not_found", "Nyx route was not found.");
        }
        catch (NyxValidationException ex) { return Failure(request.Id, 422, "validation_failed", ex.Message); }
        catch (KeyNotFoundException ex) { return Failure(request.Id, 404, "setting_not_found", $"Setting was not found: {ex.Message}"); }
        catch (ArgumentException ex) { return Failure(request.Id, 400, "argument_invalid", ex.Message); }
        catch (FileNotFoundException) { return Failure(request.Id, 404, "setting_not_found", "Setting was not found."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine($"[Nyx] data operation failed: {ex.Message}");
            return Failure(request.Id, 503, "storage_unavailable", "Nyx data storage is unavailable.");
        }
    }

    public async Task<(BackendStreamDescriptor? Descriptor, NyxStreamProducer? Producer, BackendResponse? Error)> OpenStreamAsync(string requestId, string dataId, string type)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        if (!string.Equals(type, "json", StringComparison.Ordinal))
            return (null, null, Failure(requestId, 400, "stream_type_unsupported", "Only json streams are supported."));
        string[] queryParts = dataId.Split('?', 2);
        if (!string.Equals(queryParts[0], "nyx.system.metrics", StringComparison.Ordinal))
            return (null, null, Failure(requestId, 404, "data_not_found", "Nyx stream data ID was not found."));

        try
        {
            JsonObject persisted = _settings.Value.GetData("nyx.system.metrics", "json");
            int interval = ReadPersistedInterval(persisted);
            HashSet<string>? fields = ReadPersistedFields(persisted);
            if (queryParts.Length == 2)
                ApplyStreamQuery(queryParts[1], ref interval, ref fields);
            string streamId = Guid.NewGuid().ToString("D");
            var descriptor = new BackendStreamDescriptor(streamId, "json", new JsonSchemaDescriptor("object", Fields: new Dictionary<string, JsonSchemaDescriptor>(StringComparer.Ordinal) { ["json"] = new("string") }));
            return (descriptor, new NyxMetricStreamProducer(_metrics.Value, fields, interval), null);
        }
        catch (NyxValidationException ex) { return (null, null, Failure(requestId, 422, "validation_failed", ex.Message)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return (null, null, Failure(requestId, 503, "storage_unavailable", "Nyx data storage is unavailable."));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_metrics.IsValueCreated) _metrics.Value.Dispose();
    }

    private async Task<BackendResponse> HandleSettingsAsync(string id, string method, string path, JsonElement? body)
    {
        if (path == "/v1/nyx/settings/app")
        {
            if (method != "GET") return Failure(id, 405, "method_not_allowed", "App settings namespace is read-only at this path.");
            return SuccessNode(id, _settings.Value.GetApp());
        }
        if (path.StartsWith("/v1/nyx/settings/app/", StringComparison.Ordinal))
        {
            string key = DecodeSegment(path["/v1/nyx/settings/app/".Length..]);
            if (!RuntimeSettingKeys.Contains(key)) return Failure(id, 404, "setting_not_found", "Runtime setting was not found.");
            return await HandleSettingValueAsync(id, method, "app", null, null, key, body).ConfigureAwait(false);
        }

        const string prefix = "/v1/nyx/settings/data/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return Failure(id, 404, "route_not_found", "Nyx settings route was not found.");
        string[] parts = path[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3) return Failure(id, 400, "settings_path_invalid", "Data settings path must contain data ID and datatype.");
        string dataId = DecodeSegment(parts[0]);
        string datatype = DecodeSegment(parts[1]);
        string? keyName = parts.Length == 3 ? DecodeSegment(parts[2]) : null;
        if (keyName is null)
        {
            if (method != "GET") return Failure(id, 405, "method_not_allowed", "Data settings namespace is read-only at this path.");
            return SuccessNode(id, _settings.Value.GetData(dataId, datatype));
        }
        return await HandleSettingValueAsync(id, method, "data", dataId, datatype, keyName, body).ConfigureAwait(false);
    }

    private Task<BackendResponse> HandleSettingValueAsync(string id, string method, string scope, string? dataId, string? datatype, string key, JsonElement? body)
    {
        JsonNode? value;
        if (body is JsonElement bodyElement) value = JsonNode.Parse(bodyElement.GetRawText());
        else value = null;

        if (scope == "app")
        {
            if (method == "GET")
            {
                JsonNode? current = _settings.Value.GetAppValue(key, out bool found);
                return Task.FromResult(found ? SuccessNode(id, current) : Failure(id, 404, "setting_not_found", "Setting was not found."));
            }
            if (method == "PUT") { _settings.Value.SetAppValue(key, value); return Task.FromResult(SuccessNode(id, value)); }
            if (method == "DELETE") { return Task.FromResult(_settings.Value.DeleteAppValue(key) ? Success(id, null) : Failure(id, 404, "setting_not_found", "Setting was not found.")); }
        }
        else if (dataId is not null && datatype is not null)
        {
            if (method == "GET")
            {
                JsonNode? current = _settings.Value.GetDataValue(dataId, datatype, key, out bool found);
                return Task.FromResult(found ? SuccessNode(id, current) : Failure(id, 404, "setting_not_found", "Setting was not found."));
            }
            if (method == "PUT") { _settings.Value.SetDataValue(dataId, datatype, key, value); return Task.FromResult(SuccessNode(id, value)); }
            if (method == "DELETE") { return Task.FromResult(_settings.Value.DeleteDataValue(dataId, datatype, key) ? Success(id, null) : Failure(id, 404, "setting_not_found", "Setting was not found.")); }
        }
        return Task.FromResult(Failure(id, 405, "method_not_allowed", "Settings method is unsupported."));
    }

    private async Task<BackendResponse> HandleMetricsAsync(string id, JsonElement? body, CancellationToken cancellationToken)
    {
        JsonObject persisted = _settings.Value.GetData("nyx.system.metrics", "json");
        HashSet<string>? fields = ReadPersistedFields(persisted);
        if (body is JsonElement bodyElement && bodyElement.ValueKind == JsonValueKind.Object && bodyElement.TryGetProperty("fields", out JsonElement requestedFields))
            fields = MetricFields.Parse(requestedFields);
        return Success(id, await _metrics.Value.CollectAsync(fields, cancellationToken).ConfigureAwait(false));
    }

    private BackendResponse HandleControl(string id, string path)
    {
        string name = DecodeSegment(path["/v1/nyx/system/controls/".Length..]);
        try { _actions.Execute(name); return Success(id, null); }
        catch (KeyNotFoundException) { return Failure(id, 404, "control_not_found", "System control was not found."); }
        catch (Exception ex) { Console.Error.WriteLine($"[Nyx] control failed: {ex.Message}"); return Failure(id, 503, "control_unavailable", "System control is unavailable."); }
    }

    private static int ReadPersistedInterval(JsonObject settings)
    {
        try { return MetricFields.ValidateInterval(settings["interval"] is JsonNode node ? JsonSerializer.SerializeToElement(node) : null); }
        catch (NyxValidationException) { return MetricFields.DefaultInterval; }
    }

    private static HashSet<string>? ReadPersistedFields(JsonObject settings)
    {
        try { return MetricFields.Parse(settings["fields"] is JsonNode node ? JsonSerializer.SerializeToElement(node) : null); }
        catch (NyxValidationException) { return null; }
    }

    private static void ApplyStreamQuery(string query, ref int interval, ref HashSet<string>? fields)
    {
        foreach (string item in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = item.Split('=', 2);
            string key = Uri.UnescapeDataString(pair[0]);
            string value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "";
            using JsonDocument document = JsonDocument.Parse(key == "interval" ? value : JsonSerializer.Serialize(value));
            if (key == "interval") interval = MetricFields.ValidateInterval(document.RootElement);
            else if (key == "fields") fields = MetricFields.Parse(document.RootElement);
            else throw new NyxValidationException($"Unknown metric stream option: {key}");
        }
    }

    private static string DecodeSegment(string value)
    {
        string result = Uri.UnescapeDataString(value);
        if (result.Length == 0 || result is "." or ".." || result.Contains('/') || result.Contains('\\') || result.Contains('\0'))
            throw new ArgumentException("Settings identifier is invalid.");
        return result;
    }

    private static BackendResponse Success(string id, object? body) => BackendResponse.FromObject(id, 200, body);
    private static BackendResponse SuccessNode(string id, JsonNode? node) => new(id, 200, node is null ? null : JsonSerializer.SerializeToElement(node));
    private static BackendResponse Failure(string id, int status, string code, string message) => new(id, status, Error: new BackendError(code, message));
}

internal abstract class NyxStreamProducer : IAsyncDisposable
{
    public abstract Task StartAsync(Action<object?> publish, CancellationToken cancellationToken);
    public abstract ValueTask DisposeAsync();
}

internal sealed class NyxMetricStreamProducer : NyxStreamProducer
{
    private readonly SystemMetricsService _metrics;
    private readonly IReadOnlySet<string>? _fields;
    private readonly int _interval;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _worker;

    public NyxMetricStreamProducer(SystemMetricsService metrics, IReadOnlySet<string>? fields, int interval)
    {
        _metrics = metrics;
        _fields = fields;
        _interval = interval;
    }

    public override Task StartAsync(Action<object?> publish, CancellationToken cancellationToken)
    {
        _worker = Task.Run(async () =>
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
            while (!linked.IsCancellationRequested)
            {
                try
                {
                    Dictionary<string, object?> snapshot = await _metrics.CollectAsync(_fields, linked.Token).ConfigureAwait(false);
                    publish(new { json = BackendCodec.SerializeJson(new { type = "snapshot", data = snapshot }) });
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    publish(new { json = BackendCodec.SerializeJson(new { type = "error", code = "collection_failed", message = ex.Message }) });
                }
                try { await Task.Delay(TimeSpan.FromSeconds(_interval), linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { break; }
            }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_worker is not null)
        {
            try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _shutdown.Dispose();
    }
}
