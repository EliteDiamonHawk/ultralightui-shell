using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chronos;

internal sealed record PersistentState(
    int SchemaVersion = 3,
    IReadOnlyList<string> LastAcceptedConfigFiles = null!,
    Guid? ActiveThemeId = null,
    IReadOnlyList<string> FallbackConfigFiles = null!,
    Guid? FallbackThemeId = null,
    string RuntimeState = "idle")
{
    public IReadOnlyList<string> Files => LastAcceptedConfigFiles ?? Array.Empty<string>();
    public IReadOnlyList<string> FallbackFiles => FallbackConfigFiles ?? Array.Empty<string>();
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

internal enum WindowsKeyMenuHideMode
{
    Direct = 0,
    Cue = 1
}

internal sealed record PersistentSettings(
    int SchemaVersion = 4,
    bool TrayEnabled = true,
    bool RestoreState = true,
    bool WindowsKeyOverride = false,
    bool PanicEscapeEnabled = true,
    bool StatusPollingEnabled = true,
    string? RendererPath = null,
    string? UltralightRoot = null,
    IReadOnlyList<KeybindDefinition>? Keybinds = null,
    WindowsKeyMenuHideMode WindowsKeyMenuHideMode = WindowsKeyMenuHideMode.Direct,
    string WindowsKeyMenuHideCue = "hide-menu",
    bool WindowsKeyMenuShowCueEnabled = false,
    string WindowsKeyMenuShowCue = "show-menu");

internal sealed class PersistenceStore
{
    private const int CurrentSchemaVersion = 3;
    private const int CurrentSettingsSchemaVersion = 4;
    private readonly ChronosPaths _paths;
    private readonly Action<string> _diagnostic;

    public PersistenceStore(ChronosPaths paths, Action<string>? diagnostic = null)
    {
        _paths = paths;
        _diagnostic = diagnostic ?? (_ => { });
        _paths.EnsureDirectories();
        EnsureFile(_paths.StateFile, new PersistentState(CurrentSchemaVersion, Array.Empty<string>()), _diagnostic);
        EnsureFile(_paths.ThemeRegistryFile, new ThemeRegistryState(CurrentSchemaVersion, Array.Empty<ThemeDefinition>()), _diagnostic);
        EnsureFile(_paths.SettingsFile, new PersistentSettings(CurrentSettingsSchemaVersion), _diagnostic);
    }

    public PersistentState ReadState()
    {
        PersistentState? state = Read<PersistentState>(_paths.StateFile);
        return state?.SchemaVersion switch
        {
            CurrentSchemaVersion => state,
            2 => new PersistentState(CurrentSchemaVersion, state.Files, state.ActiveThemeId,
                Array.Empty<string>(), null, state.Files.Count == 0 ? "idle" : "active"),
            1 => new PersistentState(CurrentSchemaVersion, state.Files, state.ActiveThemeId,
                Array.Empty<string>(), null, state.Files.Count == 0 ? "idle" : "active"),
            _ => new PersistentState(CurrentSchemaVersion, Array.Empty<string>())
        };
    }

    public void WriteState(
        IReadOnlyList<string> files,
        Guid? activeThemeId = null,
        IReadOnlyList<string>? fallbackFiles = null,
        Guid? fallbackThemeId = null,
        string? runtimeState = null)
    {
        string[] normalized = files.Select(Path.GetFullPath).ToArray();
        string[] normalizedFallback = (fallbackFiles ?? Array.Empty<string>()).Select(Path.GetFullPath).ToArray();
        string state = runtimeState ?? (normalized.Length == 0 ? "idle" : "active");
        WriteAtomic(_paths.StateFile, new PersistentState(CurrentSchemaVersion, normalized, activeThemeId,
            normalizedFallback, fallbackThemeId, state));
    }

    public PersistentSettings ReadSettings()
    {
        PersistentSettings? settings = Read<PersistentSettings>(_paths.SettingsFile);
        return settings?.SchemaVersion switch
        {
            CurrentSettingsSchemaVersion => NormalizeSettings(settings),
            3 => NormalizeSettings(settings with { SchemaVersion = CurrentSettingsSchemaVersion }),
            2 => NormalizeSettings(settings with { SchemaVersion = CurrentSettingsSchemaVersion, Keybinds = null }),
            1 => NormalizeSettings(settings with { SchemaVersion = CurrentSettingsSchemaVersion, PanicEscapeEnabled = true, Keybinds = null }),
            _ => new PersistentSettings(CurrentSettingsSchemaVersion, Keybinds: KeybindCatalog.EnsureWindowsBinding(null, false))
        };
    }

    public void WriteSettings(PersistentSettings settings)
    {
        WriteAtomic(_paths.SettingsFile, settings with { SchemaVersion = CurrentSettingsSchemaVersion });
    }

    private PersistentSettings NormalizeSettings(PersistentSettings settings)
    {
        try
        {
            IReadOnlyList<KeybindDefinition> bindings = KeybindCatalog.EnsureWindowsBinding(settings.Keybinds, settings.WindowsKeyOverride);
            bool windowsOverride = bindings.First(binding => binding.SystemBinding).Enabled;
            WindowsKeyMenuHideMode hideMode = settings.WindowsKeyMenuHideMode is WindowsKeyMenuHideMode.Cue
                ? WindowsKeyMenuHideMode.Cue
                : WindowsKeyMenuHideMode.Direct;
            return settings with
            {
                Keybinds = bindings,
                WindowsKeyOverride = windowsOverride,
                WindowsKeyMenuHideMode = hideMode,
                WindowsKeyMenuHideCue = NormalizeCue(settings.WindowsKeyMenuHideCue, "hide-menu"),
                WindowsKeyMenuShowCue = NormalizeCue(settings.WindowsKeyMenuShowCue, "show-menu")
            };
        }
        catch (InvalidOperationException ex)
        {
            _diagnostic($"Ignoring invalid persisted keybinds: {ex.Message}");
            return settings with
            {
                Keybinds = KeybindCatalog.EnsureWindowsBinding(null, settings.WindowsKeyOverride),
                WindowsKeyOverride = settings.WindowsKeyOverride,
                WindowsKeyMenuHideMode = WindowsKeyMenuHideMode.Direct,
                WindowsKeyMenuHideCue = NormalizeCue(settings.WindowsKeyMenuHideCue, "hide-menu"),
                WindowsKeyMenuShowCue = NormalizeCue(settings.WindowsKeyMenuShowCue, "show-menu")
            };
        }
    }

    private static string NormalizeCue(string? cue, string fallback) =>
        string.IsNullOrWhiteSpace(cue) ? fallback : cue.Trim();

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

    internal static void EnsureFile(string path, object value, Action<string> diagnostic)
    {
        if (File.Exists(path))
            return;

        try
        {
            WriteAtomic(path, value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostic($"Could not create default persistence file '{path}': {ex.Message}");
        }
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
                catch (UnauthorizedAccessException) { File.Move(temporary, path, true); }
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
