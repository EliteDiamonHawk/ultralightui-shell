using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chronos;

internal sealed record ControlRequest(string Id, string Method, string Path, JsonElement? Body);

internal sealed record ControlError(string Code, string Message, object? Details = null);

internal sealed record ControlResponse(string? Id, int Status, JsonElement? Body = null, ControlError? Error = null)
{
    public static ControlResponse Success(string id, int status, object? body = null) =>
        new(id, status, body == null ? null : ControlCodec.SerializeBody(body));

    public static ControlResponse Failure(string? id, int status, string code, string message, object? details = null) =>
        new(id, status, Error: new ControlError(code, message, details));
}

internal sealed record ControlEvent(string Name, JsonElement? Body);

internal static class ControlCodec
{
    public const int MaxFrameBytes = 1024 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    public static JsonElement SerializeBody(object value) =>
        JsonSerializer.SerializeToElement(value, JsonOptions);

    public static string SerializeRequest(ControlRequest request) =>
        JsonSerializer.Serialize(new
        {
            id = request.Id,
            method = request.Method,
            path = request.Path,
            body = request.Body
        }, JsonOptions);

    public static string SerializeResponse(ControlResponse response) =>
        JsonSerializer.Serialize(new
        {
            id = response.Id,
            status = response.Status,
            body = response.Error == null ? response.Body : null,
            error = response.Error
        }, JsonOptions);

    public static bool TryParseResponse(string line, out ControlResponse? response)
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
            if (root.TryGetProperty("id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.String)
                id = idElement.GetString();
            if (id == null || !Guid.TryParse(id, out _))
                return false;

            JsonElement? body = root.TryGetProperty("body", out JsonElement bodyElement) &&
                                bodyElement.ValueKind != JsonValueKind.Null
                ? bodyElement.Clone()
                : null;
            ControlError? error = ParseError(root);
            response = new ControlResponse(id, status, body, error);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsUnsolicitedEvent(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("event", out JsonElement eventValue) && eventValue.ValueKind == JsonValueKind.String &&
                   root.TryGetProperty("id", out JsonElement idValue) && idValue.ValueKind == JsonValueKind.Null;
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

    public static bool TryParseRequest(
        string line,
        out ControlRequest? request,
        out ControlError? error,
        out string? requestId)
    {
        request = null;
        error = null;
        requestId = null;
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

            if (root.TryGetProperty("id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.String)
                requestId = idElement.GetString();
            string id = RequiredString(root, "id");
            if (!Guid.TryParse(id, out _))
                throw new ProtocolException("request_id_invalid", "Request id must be a UUID.");

            string method = RequiredString(root, "method").ToUpperInvariant();
            if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE"))
                throw new ProtocolException("method_invalid", "Unsupported HTTP method.");
            string path = RequiredString(root, "path");
            if (!path.StartsWith("/v1/", StringComparison.Ordinal))
                throw new ProtocolException("path_invalid", "Path must begin with /v1/.");

            JsonElement? body = root.TryGetProperty("body", out JsonElement bodyElement) &&
                                bodyElement.ValueKind != JsonValueKind.Null
                ? bodyElement.Clone()
                : null;
            request = new ControlRequest(id, method, path, body);
            requestId = id;
            return true;
        }
        catch (JsonException)
        {
            error = new ControlError("invalid_json", "Control frame is not valid JSON.");
            return false;
        }
        catch (ProtocolException ex)
        {
            error = new ControlError(ex.Code, ex.Message);
            return false;
        }
    }

    public static bool TryGetObject(JsonElement? body, out JsonElement value)
    {
        value = default;
        return body is { } candidate && candidate.ValueKind == JsonValueKind.Object && (value = candidate).ValueKind == JsonValueKind.Object;
    }

    public static string? GetString(JsonElement objectValue, string name)
    {
        return objectValue.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new ProtocolException("field_invalid", $"Field '{name}' must be a string.");
        string result = value.GetString()?.Trim() ?? "";
        if (result.Length == 0)
            throw new ProtocolException("field_empty", $"Field '{name}' must not be empty.");
        return result;
    }

    private static ControlError? ParseError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out JsonElement errorElement) || errorElement.ValueKind != JsonValueKind.Object)
            return null;
        string code = GetString(errorElement, "code") ?? "error";
        string message = GetString(errorElement, "message") ?? "";
        object? details = errorElement.TryGetProperty("details", out JsonElement detailsElement)
            ? detailsElement.Clone()
            : null;
        return new ControlError(code, message, details);
    }

    private sealed class ProtocolException : Exception
    {
        public ProtocolException(string code, string message) : base(message) => Code = code;
        public string Code { get; }
    }
}
