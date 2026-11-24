using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaosShell.MinimalBackend;

internal sealed record BackendRequest(
    string Id,
    string Method,
    string Path,
    JsonElement? Body);

internal sealed record BackendError(
    string Code,
    string Message,
    object? Details = null);

internal sealed record BackendResponse(
    string? Id,
    int Status,
    object? Body = null,
    BackendError? Error = null);

internal sealed record BackendEvent(
    string StreamId,
    string Type,
    object Data,
    string Name = "stream.data");

internal sealed class BackendProtocolException : Exception
{
    public BackendProtocolException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}

internal static class BackendCodec
{
    public const int MaxFrameBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static bool TryParseRequest(string line, out BackendRequest? request, out BackendError? error)
    {
        request = null;
        error = null;
        if (System.Text.Encoding.UTF8.GetByteCount(line) > MaxFrameBytes)
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

    public static string SerializeResponse(BackendResponse response) => JsonSerializer.Serialize(new
    {
        id = response.Id,
        status = response.Status,
        body = response.Error == null ? response.Body : null,
        error = response.Error
    }, JsonOptions);

    public static string SerializeEvent(BackendEvent value) => JsonSerializer.Serialize(new
    {
        id = (string?)null,
        @event = value.Name,
        streamId = value.StreamId,
        type = value.Type,
        data = value.Data
    }, JsonOptions);

    public static JsonElement? CloneBody(JsonElement? body) => body?.Clone();

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
}
