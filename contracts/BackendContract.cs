using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaosShell.Contracts;

public sealed record BackendRequest(
    string Id,
    string Method,
    string Path,
    JsonElement? Body);

public sealed record BackendError(
    string Code,
    string Message,
    JsonElement? Details = null);

public sealed record BackendResponse(
    string? Id,
    int Status,
    JsonElement? Body = null,
    BackendError? Error = null)
{
    public static BackendResponse FromObject(string? id, int status, object? body = null, BackendError? error = null) =>
        new(id, status, body == null ? null : BackendCodec.SerializeBody(body), error);
}

public sealed record BackendEventFrame(
    string Name,
    string StreamId,
    string Type,
    JsonElement? Data)
{
    public static BackendEventFrame FromObject(string name, string streamId, string type, object? data = null) =>
        new(name, streamId, type, data == null ? null : BackendCodec.SerializeBody(data));
}

public sealed record BackendStreamDescriptor(
    string StreamId,
    string Type,
    JsonSchemaDescriptor Schema);

public sealed record JsonSchemaDescriptor(
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

public static class BackendCodec
{
    public const int ProtocolVersion = 1;
    public const int MaxFrameBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    public static bool TryParseRequest(string line, out BackendRequest? request, out BackendError? error)
    {
        request = null;
        error = null;
        if (Encoding.UTF8.GetByteCount(line) > MaxFrameBytes)
        {
            error = new BackendError("frame_too_large", "Backend frame exceeds 1 MiB.");
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new BackendProtocolException("invalid_frame", "Backend frame must be a JSON object.");

            string id = RequiredString(root, "id");
            if (!Guid.TryParse(id, out _))
                throw new BackendProtocolException("request_id_invalid", "Request id must be a UUID.");
            string method = RequiredString(root, "method").ToUpperInvariant();
            if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE"))
                throw new BackendProtocolException("method_invalid", "Unsupported HTTP method.");
            string path = RequiredString(root, "path");
            if (!path.StartsWith("/v1/", StringComparison.Ordinal))
                throw new BackendProtocolException("path_invalid", "Path must begin with /v1/.");

            JsonElement? body = root.TryGetProperty("body", out JsonElement bodyElement) && bodyElement.ValueKind != JsonValueKind.Null
                ? bodyElement.Clone()
                : null;
            request = new BackendRequest(id, method, path, body);
            return true;
        }
        catch (JsonException)
        {
            error = new BackendError("invalid_json", "Backend frame is not valid JSON.");
            return false;
        }
        catch (BackendProtocolException ex)
        {
            error = new BackendError(ex.Code, ex.Message);
            return false;
        }
    }

    public static string SerializeRequest(BackendRequest request) => JsonSerializer.Serialize(new
    {
        id = request.Id,
        method = request.Method,
        path = request.Path,
        body = request.Body
    }, JsonOptions);

    public static bool TryParseResponse(string line, out BackendResponse? response)
    {
        response = null;
        if (Encoding.UTF8.GetByteCount(line) > MaxFrameBytes)
            return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("status", out JsonElement statusElement) ||
                statusElement.ValueKind != JsonValueKind.Number ||
                !statusElement.TryGetInt32(out int status))
                return false;

            string? id = null;
            if (!root.TryGetProperty("id", out JsonElement idElement))
                return false;
            if (idElement.ValueKind == JsonValueKind.String)
            {
                id = idElement.GetString();
                if (!Guid.TryParse(id, out _))
                    return false;
            }
            else if (idElement.ValueKind != JsonValueKind.Null)
            {
                return false;
            }

            JsonElement? body = root.TryGetProperty("body", out JsonElement bodyElement) && bodyElement.ValueKind != JsonValueKind.Null
                ? bodyElement.Clone()
                : null;
            BackendError? error = null;
            if (root.TryGetProperty("error", out JsonElement errorElement) && errorElement.ValueKind != JsonValueKind.Null)
            {
                if (errorElement.ValueKind != JsonValueKind.Object ||
                    !errorElement.TryGetProperty("code", out JsonElement codeElement) || codeElement.ValueKind != JsonValueKind.String ||
                    !errorElement.TryGetProperty("message", out JsonElement messageElement) || messageElement.ValueKind != JsonValueKind.String)
                    return false;
                JsonElement? details = errorElement.TryGetProperty("details", out JsonElement detailsElement) && detailsElement.ValueKind != JsonValueKind.Null
                    ? detailsElement.Clone()
                    : null;
                error = new BackendError(codeElement.GetString() ?? "", messageElement.GetString() ?? "", details);
            }

            if (body != null && error != null)
                return false;
            if (id == null && error == null)
                return false;

            response = new BackendResponse(id, status, body, error);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string SerializeResponse(BackendResponse response) => JsonSerializer.Serialize(new
    {
        id = response.Id,
        status = response.Status,
        body = response.Error == null ? response.Body : null,
        error = response.Error
    }, JsonOptions);

    public static bool TryParseEvent(string line, out BackendEventFrame? value)
    {
        value = null;
        if (Encoding.UTF8.GetByteCount(line) > MaxFrameBytes)
            return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.Null ||
                !root.TryGetProperty("event", out JsonElement name) || name.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("streamId", out JsonElement stream) || stream.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String)
                return false;

            JsonElement? data = root.TryGetProperty("data", out JsonElement dataElement) && dataElement.ValueKind != JsonValueKind.Null
                ? dataElement.Clone()
                : null;
            value = new BackendEventFrame(name.GetString()!, stream.GetString()!, type.GetString()!, data);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string SerializeEvent(BackendEventFrame value) => JsonSerializer.Serialize(new
    {
        id = (string?)null,
        @event = value.Name,
        streamId = value.StreamId,
        type = value.Type,
        data = value.Data
    }, JsonOptions);

    public static JsonElement SerializeBody(object value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    public static bool TryGetObject(JsonElement? value, out JsonElement result)
    {
        result = default;
        if (value is not JsonElement element || element.ValueKind != JsonValueKind.Object)
            return false;
        result = element;
        return true;
    }

    public static string? GetString(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    public static string SerializeJson(object value) => JsonSerializer.Serialize(value, JsonOptions);

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new BackendProtocolException("field_invalid", $"Field '{name}' must be a string.");
        string result = value.GetString()?.Trim() ?? "";
        if (result.Length == 0)
            throw new BackendProtocolException("field_empty", $"Field '{name}' must not be empty.");
        return result;
    }

    private sealed class BackendProtocolException : Exception
    {
        public BackendProtocolException(string code, string message) : base(message) => Code = code;

        public string Code { get; }
    }
}
