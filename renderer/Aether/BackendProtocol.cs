using System.Text.Json;

namespace Aether;

internal sealed record BackendStreamDescriptor(string StreamId, string Type, JsonSchemaDescriptor Schema);

internal sealed record BackendEvent(string Name, string StreamId, string Type, JsonElement? Data);

internal sealed record JsonSchemaDescriptor(
    string Kind,
    JsonSchemaDescriptor? Items = null,
    IReadOnlyDictionary<string, JsonSchemaDescriptor>? Fields = null)
{
    public static JsonSchemaDescriptor Parse(JsonElement value, int depth = 0)
    {
        if (depth > 32 || value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("kind", out JsonElement kindValue) || kindValue.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Backend schema is invalid.");

        string kind = kindValue.GetString() ?? "";
        return kind switch
        {
            "null" or "boolean" or "number" or "string" => RejectExtraKeys(value, kind, null, null),
            "array" => ParseArray(value, depth),
            "object" => ParseObject(value, depth),
            _ => throw new InvalidOperationException("Backend schema kind is unsupported.")
        };
    }

    public bool Matches(JsonElement value) => Kind switch
    {
        "null" => value.ValueKind == JsonValueKind.Null,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number" => value.ValueKind == JsonValueKind.Number,
        "string" => value.ValueKind == JsonValueKind.String,
        "array" => value.ValueKind == JsonValueKind.Array && Items != null && value.EnumerateArray().All(Items.Matches),
        "object" => value.ValueKind == JsonValueKind.Object && Fields != null &&
                    Fields.All(field => value.TryGetProperty(field.Key, out JsonElement fieldValue) && field.Value.Matches(fieldValue)) &&
                    value.EnumerateObject().All(property => Fields.ContainsKey(property.Name)),
        _ => false
    };

    private static JsonSchemaDescriptor ParseArray(JsonElement value, int depth)
    {
        if (!value.TryGetProperty("items", out JsonElement items))
            throw new InvalidOperationException("Backend array schema requires items.");
        return RejectExtraKeys(value, "array", Parse(items, depth + 1), null);
    }

    private static JsonSchemaDescriptor ParseObject(JsonElement value, int depth)
    {
        if (!value.TryGetProperty("fields", out JsonElement fields) || fields.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Backend object schema requires fields.");
        var parsed = new Dictionary<string, JsonSchemaDescriptor>(StringComparer.Ordinal);
        foreach (JsonProperty field in fields.EnumerateObject())
            parsed.Add(field.Name, Parse(field.Value, depth + 1));
        return RejectExtraKeys(value, "object", null, parsed);
    }

    private static JsonSchemaDescriptor RejectExtraKeys(JsonElement value, string kind, JsonSchemaDescriptor? items, IReadOnlyDictionary<string, JsonSchemaDescriptor>? fields)
    {
        foreach (JsonProperty property in value.EnumerateObject())
            if (property.Name is not "kind" and not "items" and not "fields")
                throw new InvalidOperationException("Backend schema contains unknown fields.");
        if (kind != "array" && value.TryGetProperty("items", out _))
            throw new InvalidOperationException("Backend schema items is only valid for arrays.");
        if (kind != "object" && value.TryGetProperty("fields", out _))
            throw new InvalidOperationException("Backend schema fields is only valid for objects.");
        return new JsonSchemaDescriptor(kind, items, fields);
    }
}

internal sealed class BackendEventQueue
{
    private const int Capacity = 256;
    private readonly Queue<BackendEvent> _events = new();
    private bool _overflowReported;

    public int Count { get { lock (_events) return _events.Count; } }

    public void Enqueue(BackendEvent value, Action<BackendEvent>? overflow)
    {
        BackendEvent? overflowEvent = null;
        lock (_events)
        {
            if (_events.Count >= Capacity)
            {
                _events.Dequeue();
                if (!_overflowReported)
                {
                    _overflowReported = true;
                    overflowEvent = new BackendEvent("stream.overflow", value.StreamId, value.Type, null);
                }
            }
            _events.Enqueue(value);
        }
        if (overflowEvent != null)
            overflow?.Invoke(overflowEvent);
    }

    public BackendEvent[] Drain()
    {
        lock (_events)
        {
            BackendEvent[] result = _events.ToArray();
            _events.Clear();
            _overflowReported = false;
            return result;
        }
    }
}
