using System.Collections.Concurrent;
using System.Text.Json;

namespace Chronos;

internal sealed class LogStore
{
    private const int MaxRememberedBatches = 256;
    private static readonly TimeSpan BatchRetention = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> AllowedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "group", "exceptionType", "command", "hasGroup", "hasTarget", "configuredWindows",
        "controllerConnected", "windowCount", "error"
    };

    private readonly string _directory;
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _batches = new(StringComparer.Ordinal);

    public LogStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public LogBatchResult Accept(JsonElement? body)
    {
        if (!ControlCodec.TryGetObject(body, out JsonElement root))
            throw new LogStoreException(400, "logging_batch_invalid", "Logging batch must be a JSON object.");
        if (!root.TryGetProperty("protocolVersion", out JsonElement protocol) ||
            !protocol.TryGetInt32(out int protocolVersion) || protocolVersion != 1)
            throw new LogStoreException(400, "logging_protocol_unsupported", "Logging protocol version must be 1.");

        string batchId = ControlCodec.GetString(root, "batchId") ?? "";
        if (!Guid.TryParse(batchId, out _))
            throw new LogStoreException(400, "logging_batch_invalid", "batchId must be a UUID.");
        if (!root.TryGetProperty("processId", out JsonElement processId) ||
            !processId.TryGetInt32(out int pid) || pid <= 0)
            throw new LogStoreException(400, "logging_batch_invalid", "processId must be positive.");
        if (!Guid.TryParse(ControlCodec.GetString(root, "sessionId"), out _))
            throw new LogStoreException(400, "logging_batch_invalid", "sessionId must be a UUID.");
        if (!root.TryGetProperty("events", out JsonElement events) || events.ValueKind != JsonValueKind.Array || events.GetArrayLength() > 256)
            throw new LogStoreException(400, "logging_batch_invalid", "events must be an array with at most 256 items.");

        List<object> sanitizedEvents = new();
        foreach (JsonElement item in events.EnumerateArray())
            sanitizedEvents.Add(SanitizeEvent(item));

        lock (_sync)
        {
            PruneLocked();
            if (_batches.ContainsKey(batchId))
                return new LogBatchResult(batchId, sanitizedEvents.Count, true);

            object payload = new
            {
                protocolVersion = 1,
                batchId,
                processId = pid,
                sessionId = ControlCodec.GetString(root, "sessionId"),
                events = sanitizedEvents
            };
            AppendDurably(payload);
            _batches[batchId] = DateTimeOffset.UtcNow.Add(BatchRetention);
            while (_batches.Count > MaxRememberedBatches)
                _batches.Remove(_batches.OrderBy(pair => pair.Value).First().Key);
            return new LogBatchResult(batchId, sanitizedEvents.Count, false);
        }
    }

    private void AppendDurably(object payload)
    {
        string date = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string path = Path.Combine(_directory, $"chronos-{date}.ndjson");
        if (File.Exists(path) && new FileInfo(path).Length >= 10 * 1024 * 1024)
            path = Path.Combine(_directory, $"chronos-{date}-{DateTime.UtcNow:HHmmssfff}.ndjson");

        using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true);
        writer.WriteLine(JsonSerializer.Serialize(payload, ControlCodec.JsonOptions));
        writer.Flush();
        stream.Flush(true);
    }

    private void PruneLocked()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (string expired in _batches.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
            _batches.Remove(expired);
    }

    private static object SanitizeEvent(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            throw new LogStoreException(400, "logging_event_invalid", "Each logging event must be an object.");

        string eventId = ControlCodec.GetString(item, "eventId") ?? "";
        string timestamp = ControlCodec.GetString(item, "timestampUtc") ?? "";
        string level = ControlCodec.GetString(item, "level") ?? "";
        string eventName = ControlCodec.GetString(item, "eventName") ?? "";
        if (!Guid.TryParse(eventId, out _) || string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(level) || string.IsNullOrWhiteSpace(eventName))
            throw new LogStoreException(400, "logging_event_invalid", "Logging event identity fields are invalid.");

        var properties = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (item.TryGetProperty("properties", out JsonElement propertyElement) && propertyElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in propertyElement.EnumerateObject())
            {
                if (!AllowedProperties.Contains(property.Name))
                    continue;
                properties[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
            }
        }

        return new { eventId, timestampUtc = timestamp, level, eventName, properties };
    }
}

internal sealed record LogBatchResult(string BatchId, int Accepted, bool Duplicate);

internal sealed class LogStoreException : Exception
{
    public LogStoreException(int status, string code, string message) : base(message)
    {
        Status = status;
        Code = code;
    }

    public int Status { get; }
    public string Code { get; }
}
