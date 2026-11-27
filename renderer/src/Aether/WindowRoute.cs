namespace Aether;

internal readonly record struct RendererWindowRoute(string? Id, bool IsAction);

internal static class WindowIdValidation
{
    public static bool IsValid(string id) =>
        id.Length is > 0 and <= 128 && id.All(character =>
            character is >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            >= '0' and <= '9' or '.' or '_' or ':' or '-');

    public static void Validate(string id, string prefix)
    {
        if (!IsValid(id))
            throw new RendererConfigurationException($"{prefix} must match [A-Za-z0-9._:-] and be at most 128 characters.");
    }
}

internal static class RendererWindowRouteParser
{
    private const string Prefix = "/v1/renderer/windows";

    public static bool TryParse(string path, out RendererWindowRoute route, out string error)
    {
        route = default;
        error = "Window route is invalid.";
        if (!path.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        string remainder = path[Prefix.Length..];
        if (remainder.Length == 0)
        {
            error = "";
            route = new(null, false);
            return true;
        }

        if (remainder[0] != '/' || remainder.Length > 1 && remainder[1] == '/')
            return false;
        if (remainder.EndsWith("/", StringComparison.Ordinal))
            remainder = remainder[..^1];
        if (remainder.Length == 1)
        {
            error = "";
            route = new(null, false);
            return true;
        }

        string[] segments = remainder[1..].Split('/');
        bool isAction = segments.Length == 2 &&
            string.Equals(segments[1], "actions", StringComparison.OrdinalIgnoreCase);
        if (segments.Length != 1 && !isAction || segments.Any(string.IsNullOrEmpty))
        {
            error = "Window route must be /windows/{id} or /windows/{id}/actions.";
            return false;
        }

        if (!TryDecodeSegment(segments[0], out string? id) || !WindowIdValidation.IsValid(id!))
        {
            error = "Window ID must be a valid encoded [A-Za-z0-9._:-] identifier.";
            return false;
        }

        error = "";
        route = new(id, isAction);
        return true;
    }

    private static bool TryDecodeSegment(string value, out string? decoded)
    {
        decoded = null;
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
                continue;
            if (index + 2 >= value.Length || !IsHex(value[index + 1]) || !IsHex(value[index + 2]))
                return false;
            index += 2;
        }

        try
        {
            decoded = Uri.UnescapeDataString(value);
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
