using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chronos;

internal sealed record PersistentState(
    int SchemaVersion = 2,
    IReadOnlyList<string> LastAcceptedConfigFiles = null!,
    Guid? ActiveThemeId = null)
{
    public IReadOnlyList<string> Files => LastAcceptedConfigFiles ?? Array.Empty<string>();
}

internal sealed record ThemeDefinition(
    string Name,
    IReadOnlyList<string> Files,
    Guid Id = default);

internal sealed record ThemeRegistryState(
    int SchemaVersion = 2,
    IReadOnlyList<ThemeDefinition> Entries = null!)
{
    [JsonIgnore]
    public IReadOnlyList<ThemeDefinition> Themes => Entries ?? Array.Empty<ThemeDefinition>();
}

internal sealed class PersistenceStore
{
    private const int CurrentSchemaVersion = 2;
    private readonly ChronosPaths _paths;
    private readonly Action<string> _diagnostic;

    public PersistenceStore(ChronosPaths paths, Action<string>? diagnostic = null)
    {
        _paths = paths;
        _diagnostic = diagnostic ?? (_ => { });
        _paths.EnsureDirectories();
    }

    public PersistentState ReadState()
    {
        PersistentState? state = Read<PersistentState>(_paths.StateFile);
        return state?.SchemaVersion switch
        {
            CurrentSchemaVersion => state,
            1 => new PersistentState(CurrentSchemaVersion, state.Files),
            _ => new PersistentState(CurrentSchemaVersion, Array.Empty<string>())
        };
    }

    public void WriteState(IReadOnlyList<string> files, Guid? activeThemeId = null)
    {
        string[] normalized = files.Select(Path.GetFullPath).ToArray();
        WriteAtomic(_paths.StateFile, new PersistentState(CurrentSchemaVersion, normalized, activeThemeId));
    }

    public ThemeRegistryState ReadThemes()
    {
        ThemeRegistryState? themes = Read<ThemeRegistryState>(_paths.ThemeRegistryFile);
        if (themes == null || themes.SchemaVersion is not (1 or CurrentSchemaVersion))
            return new ThemeRegistryState(CurrentSchemaVersion, Array.Empty<ThemeDefinition>());

        ThemeDefinition[] normalized = themes.Themes.Select(NormalizeTheme).ToArray();
        var result = new ThemeRegistryState(CurrentSchemaVersion, normalized);
        bool idsChanged = themes.Themes.Zip(normalized).Any(pair => pair.First.Id != pair.Second.Id);
        if (themes.SchemaVersion != CurrentSchemaVersion || idsChanged)
        {
            try { WriteAtomic(_paths.ThemeRegistryFile, result); }
            catch (Exception ex) { _diagnostic($"Could not migrate theme registry: {ex.Message}"); }
        }
        return result;
    }

    public void WriteThemes(IReadOnlyList<ThemeDefinition> themes)
    {
        ThemeDefinition[] normalized = themes.Select(NormalizeTheme).ToArray();
        WriteAtomic(_paths.ThemeRegistryFile, new ThemeRegistryState(CurrentSchemaVersion, normalized));
    }

    private static ThemeDefinition NormalizeTheme(ThemeDefinition theme) => new(
        theme.Name.Trim(),
        theme.Files.Select(Path.GetFullPath).ToArray(),
        theme.Id == Guid.Empty ? Guid.NewGuid() : theme.Id);

    private T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, ControlCodec.JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _diagnostic($"Ignoring malformed persistence file '{path}': {ex.Message}");
            return null;
        }
    }

    private static void WriteAtomic(string path, object value)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new IOException($"Persistence path has no directory: {path}");
        Directory.CreateDirectory(directory);

        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, ControlCodec.JsonOptions);
                stream.Flush(true);
            }

            if (File.Exists(path))
            {
                try { File.Replace(temporary, path, null); }
                catch (PlatformNotSupportedException) { File.Move(temporary, path, true); }
                catch (IOException) { File.Move(temporary, path, true); }
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
