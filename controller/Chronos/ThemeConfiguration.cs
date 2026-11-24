using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Chronos;

internal sealed record ThemeHook(
    string? CommandLine,
    string? Executable,
    IReadOnlyList<string> Arguments,
    bool WaitForExit,
    bool Stall,
    bool ExitAfter)
{
    public bool IsShellCommand => CommandLine != null;
}

internal sealed record PreparedTheme(
    IReadOnlyList<string> SourcePaths,
    string AetherJson,
    IReadOnlyList<ThemeHook> Before,
    IReadOnlyList<ThemeHook> After);

internal static class ThemeConfigurationLoader
{
    private static readonly HashSet<string> AetherSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "wallpaper", "widgets", "menu", "variables", "cues"
    };

    public static PreparedTheme Load(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            throw new InvalidOperationException("Theme requires at least one theme file.");

        var sanitizedSections = new List<(string Name, JsonElement Value)>();
        var seenSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var before = new List<ThemeHook>();
        var after = new List<ThemeHook>();
        bool hasAetherSection = false;

        foreach (string path in paths)
        {
            JsonElement root = ReadRoot(path);
            var seenFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!seenFields.Add(property.Name))
                    throw new InvalidOperationException($"Configuration file '{path}' contains duplicate field '{property.Name}'.");

                if (string.Equals(property.Name, "before", StringComparison.OrdinalIgnoreCase))
                {
                    ParseHooks(property.Value, path, "before", before);
                    continue;
                }

                if (string.Equals(property.Name, "after", StringComparison.OrdinalIgnoreCase))
                {
                    ParseHooks(property.Value, path, "after", after);
                    continue;
                }

                string section = property.Name.ToLowerInvariant();
                if (!seenSections.Add(section))
                    throw new InvalidOperationException($"Top-level section '{property.Name}' may only appear in one theme file.");

                hasAetherSection |= AetherSections.Contains(property.Name);
                sanitizedSections.Add((property.Name, property.Value.Clone()));
            }
        }

        if (!hasAetherSection)
            throw new InvalidOperationException("Theme requires at least one Aether configuration section.");

        return new PreparedTheme(
            paths.ToArray(),
            SerializeSections(sanitizedSections),
            before,
            after);
    }

    private static JsonElement ReadRoot(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Configuration root must be an object: {path}");
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON in '{path}': {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Could not read '{path}': {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Could not read '{path}': {ex.Message}", ex);
        }
    }

    private static void ParseHooks(JsonElement value, string path, string phase, ICollection<ThemeHook> destination)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"'{phase}' in '{path}' must be an array.");

        foreach (JsonElement item in value.EnumerateArray())
        {
            ThemeHook hook = item.ValueKind switch
            {
                JsonValueKind.String => ParseCommandLine(item, path, phase),
                JsonValueKind.Object => ParseStructured(item, path, phase),
                _ => throw new InvalidOperationException($"Each '{phase}' entry in '{path}' must be a string or object.")
            };

            if (phase == "after" && hook.ExitAfter)
                throw new InvalidOperationException("'exitAfter' is only valid for before hooks.");
            destination.Add(hook);
        }
    }

    private static ThemeHook ParseCommandLine(JsonElement value, string path, string phase)
    {
        string command = value.GetString()?.Trim() ?? "";
        if (command.Length == 0)
            throw new InvalidOperationException($"Each '{phase}' entry in '{path}' must not be empty.");
        return new(command, null, Array.Empty<string>(), false, false, false);
    }

    private static ThemeHook ParseStructured(JsonElement value, string path, string phase)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new InvalidOperationException($"A '{phase}' entry in '{path}' contains duplicate field '{property.Name}'.");
            if (property.Name is not ("executable" or "arguments" or "waitForExit" or "stall" or "exitAfter"))
                throw new InvalidOperationException($"A '{phase}' entry in '{path}' contains unknown field '{property.Name}'.");
        }

        string executable = GetRequiredString(value, "executable", path, phase);
        var arguments = new List<string>();
        if (TryGet(value, "arguments", out JsonElement argumentValue))
        {
            if (argumentValue.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"'{phase}' arguments in '{path}' must be an array.");
            foreach (JsonElement argument in argumentValue.EnumerateArray())
            {
                if (argument.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException($"'{phase}' arguments in '{path}' must contain only strings.");
                arguments.Add(argument.GetString() ?? "");
            }
        }

        return new(
            null,
            executable,
            arguments,
            GetBool(value, "waitForExit", path, phase),
            GetBool(value, "stall", path, phase),
            GetBool(value, "exitAfter", path, phase));
    }

    private static string GetRequiredString(JsonElement value, string name, string path, string phase)
    {
        if (!TryGet(value, name, out JsonElement result) || result.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(result.GetString()))
            throw new InvalidOperationException($"'{phase}' executable in '{path}' must be a non-empty string.");
        return result.GetString()!.Trim();
    }

    private static bool GetBool(JsonElement value, string name, string path, string phase)
    {
        if (!TryGet(value, name, out JsonElement result))
            return false;
        if (result.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return result.GetBoolean();
        if (result.ValueKind == JsonValueKind.String && bool.TryParse(result.GetString(), out bool parsed))
            return parsed;
        throw new InvalidOperationException($"'{phase}' {name} in '{path}' must be boolean.");
    }

    private static bool TryGet(JsonElement value, string name, out JsonElement result)
    {
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                result = property.Value;
                return true;
            }
        }
        result = default;
        return false;
    }

    private static string SerializeSections(IReadOnlyList<(string Name, JsonElement Value)> sections)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach ((string name, JsonElement value) in sections)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

internal interface IThemeHookProcess : IDisposable
{
    bool HasExited { get; }
    Task WaitForExitAsync();
    void ForceExit();
}

internal interface IThemeHookLauncher
{
    IThemeHookProcess Launch(ThemeHook hook);
}

internal sealed class ThemeHookLauncher : IThemeHookLauncher
{
    public IThemeHookProcess Launch(ThemeHook hook)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = hook.IsShellCommand
                ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                : hook.Executable!,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (hook.IsShellCommand)
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(hook.CommandLine!);
        }
        else
        {
            foreach (string argument in hook.Arguments)
                startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Hook process did not start.");
        }
        return new ThemeHookProcess(process);
    }

    private sealed class ThemeHookProcess : IThemeHookProcess
    {
        private readonly Process _process;

        public ThemeHookProcess(Process process) => _process = process;

        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        public Task WaitForExitAsync() => _process.WaitForExitAsync();

        public void ForceExit()
        {
            try
            {
                if (!HasExited)
                    _process.Kill(entireProcessTree: false);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        public void Dispose() => _process.Dispose();
    }
}

internal sealed class ThemeHookRunner
{
    private readonly IThemeHookLauncher _launcher;
    private readonly Action<string> _diagnostic;
    private readonly object _sync = new();
    private readonly List<IThemeHookProcess> _trackedBefore = new();

    public ThemeHookRunner(Action<string> diagnostic, IThemeHookLauncher? launcher = null)
    {
        _diagnostic = diagnostic;
        _launcher = launcher ?? new ThemeHookLauncher();
    }

    public async Task RunBeforeAsync(IReadOnlyList<ThemeHook> hooks)
    {
        await RunAsync(hooks, trackExitAfter: true).ConfigureAwait(false);
    }

    public async Task RunAfterAsync(IReadOnlyList<ThemeHook> hooks)
    {
        await RunAsync(hooks, trackExitAfter: false).ConfigureAwait(false);
    }

    public void TerminateTrackedBefore()
    {
        IThemeHookProcess[] processes;
        lock (_sync)
        {
            processes = _trackedBefore.ToArray();
            _trackedBefore.Clear();
        }

        foreach (IThemeHookProcess process in processes)
        {
            try { process.ForceExit(); }
            catch (Exception ex) { _diagnostic($"Could not terminate theme before process: {ex.Message}"); }
            process.Dispose();
        }
    }

    private async Task RunAsync(IReadOnlyList<ThemeHook> hooks, bool trackExitAfter)
    {
        var waits = new List<Task>();
        foreach (ThemeHook hook in hooks)
        {
            IThemeHookProcess? process = null;
            try
            {
                process = _launcher.Launch(hook);
                if (trackExitAfter && hook.ExitAfter)
                {
                    lock (_sync) _trackedBefore.Add(process);
                }
            }
            catch (Exception ex)
            {
                _diagnostic($"Theme {(trackExitAfter ? "before" : "after")} hook could not start: {ex.Message}");
                process?.Dispose();
                continue;
            }

            bool keepTracked = trackExitAfter && hook.ExitAfter;
            if (hook.Stall || hook.WaitForExit)
            {
                Task wait = WaitAndReleaseAsync(process, keepTracked);
                if (hook.Stall)
                    await wait.ConfigureAwait(false);
                else
                    waits.Add(wait);
            }
            else if (!keepTracked)
                process.Dispose();
        }

        if (waits.Count > 0)
            await Task.WhenAll(waits).ConfigureAwait(false);
    }

    private async Task WaitAndReleaseAsync(IThemeHookProcess process, bool tracked)
    {
        try { await process.WaitForExitAsync().ConfigureAwait(false); }
        catch (Exception ex) { _diagnostic($"Theme hook wait failed: {ex.Message}"); }
        finally
        {
            if (tracked)
            {
                lock (_sync) _trackedBefore.Remove(process);
            }
            process.Dispose();
        }
    }
}
