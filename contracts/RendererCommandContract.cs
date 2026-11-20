using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaosShell.Contracts;

public enum RendererCommandKind
{
    None = 0,
    Show = 1,
    SystemTray = 2,
    ShowMenu = 3,
    HideMenu = 4,
    HideWindow = 5,
    RevealWindow = 6,
    ApplyConfig = 7,
    Pause = 8,
    Resume = 9,
    Status = 10,
    Reload = 11,
    Exit = 12,
    ToggleMenu = 13
}

public enum SystemTrayAction
{
    Toggle = 0,
    Show = 1,
    Hide = 2
}

public sealed record RendererCommand(
    RendererCommandKind Kind,
    string? Group = null,
    bool All = false,
    string? TargetId = null,
    string? Topic = null,
    IReadOnlyList<string>? ConfigPaths = null,
    string? ConfigJson = null,
    IReadOnlyList<string>? ConfigSourcePaths = null,
    SystemTrayAction TrayAction = SystemTrayAction.Toggle,
    int Version = RendererCommandCodec.Version,
    Guid? ThemeId = null,
    Guid? ThemeActivationId = null,
    long? ThemeGeneration = null);

public static class RendererCommandCodec
{
    public const int Version = 1;
    public const int MaxFrameBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    public static string Serialize(RendererCommand command) => JsonSerializer.Serialize(command, Options);

    public static bool TryParse(string text, out RendererCommand? command)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxFrameBytes)
        {
            command = null;
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("version", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out int wireVersion) || wireVersion != Version)
            {
                command = null;
                return false;
            }
            command = JsonSerializer.Deserialize<RendererCommand>(text, Options);
            return command is { Kind: not RendererCommandKind.None, Version: Version };
        }
        catch (JsonException)
        {
            command = null;
            return false;
        }
    }
}
