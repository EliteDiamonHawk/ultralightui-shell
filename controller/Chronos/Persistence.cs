using System.Text.Json;

namespace Chronos;

internal sealed record PersistentState(
    int SchemaVersion = 1,
    IReadOnlyList<string> LastAcceptedConfigFiles = null!)
{
    public IReadOnlyList<string> Files => LastAcceptedConfigFiles ?? Array.Empty<string>();
}

internal sealed record WrapperSettings(
    int SchemaVersion = 1,
    bool WinMenuToggleEnabled = false);

internal sealed class PersistenceStore
{
    private const int CurrentSchemaVersion = 1;
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
        return state?.SchemaVersion == CurrentSchemaVersion
            ? state
            : new PersistentState(1, Array.Empty<string>());
    }

    public WrapperSettings ReadSettings()
    {
        WrapperSettings? settings = Read<WrapperSettings>(_paths.SettingsFile);
        return settings?.SchemaVersion == CurrentSchemaVersion
            ? settings
            : new WrapperSettings();
    }

    public void WriteState(IReadOnlyList<string> files)
    {
        string[] normalized = files.Select(Path.GetFullPath).ToArray();
        WriteAtomic(_paths.StateFile, new PersistentState(CurrentSchemaVersion, normalized));
    }

    public void WriteSettings(bool winMenuToggleEnabled)
    {
        WriteAtomic(_paths.SettingsFile, new WrapperSettings(CurrentSchemaVersion, winMenuToggleEnabled));
    }

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
