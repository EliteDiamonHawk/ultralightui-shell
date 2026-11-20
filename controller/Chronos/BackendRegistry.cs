using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

namespace Chronos;

internal sealed record BackendDefinition(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

internal sealed class BackendRegistry
{
    private readonly Dictionary<string, BackendDefinition> _definitions;

    private BackendRegistry(Dictionary<string, BackendDefinition> definitions) => _definitions = definitions;

    public IReadOnlyDictionary<string, BackendDefinition> Definitions => _definitions;

    public static BackendRegistry Load(string path, Action<string>? diagnostic = null)
    {
        diagnostic ??= _ => { };
        if (!File.Exists(path))
        {
            PersistenceStore.EnsureFile(
                path,
                new { schemaVersion = 1, backends = new Dictionary<string, BackendDefinition>(StringComparer.Ordinal) },
                diagnostic);
            return new BackendRegistry(new Dictionary<string, BackendDefinition>(StringComparer.Ordinal));
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out int schemaVersion) || schemaVersion != 1 ||
                !root.TryGetProperty("backends", out JsonElement backends) || backends.ValueKind != JsonValueKind.Object)
            {
                diagnostic($"Ignoring backend registry with unsupported schema: {path}");
                return Empty();
            }

            var definitions = new Dictionary<string, BackendDefinition>(StringComparer.Ordinal);
            foreach (JsonProperty property in backends.EnumerateObject())
            {
                if (!IsBackendId(property.Name) || property.Value.ValueKind != JsonValueKind.Object)
                    continue;
                BackendDefinition? definition = ParseDefinition(property.Value);
                if (definition != null)
                    definitions[property.Name] = definition;
            }
            return new BackendRegistry(definitions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            diagnostic($"Ignoring malformed backend registry '{path}': {ex.Message}");
            return Empty();
        }
    }

    public static BackendRegistry Empty() =>
        new(new Dictionary<string, BackendDefinition>(StringComparer.Ordinal));

    private static BackendDefinition? ParseDefinition(JsonElement value)
    {
        string? executable = ControlCodec.GetString(value, "executable");
        if (string.IsNullOrWhiteSpace(executable))
            return null;

        var arguments = new List<string>();
        if (value.TryGetProperty("arguments", out JsonElement argumentValues) && argumentValues.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement argument in argumentValues.EnumerateArray())
            {
                if (argument.ValueKind != JsonValueKind.String)
                    return null;
                arguments.Add(argument.GetString() ?? "");
            }
        }

        string? workingDirectory = ControlCodec.GetString(value, "workingDirectory");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (value.TryGetProperty("environment", out JsonElement environmentValue) && environmentValue.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty entry in environmentValue.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String)
                    return null;
                environment[entry.Name] = entry.Value.GetString() ?? "";
            }
        }

        return new BackendDefinition(executable, arguments, workingDirectory, environment);
    }

    private static bool IsBackendId(string value) =>
        value.Length > 0 && value.Length <= 128 && value.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or ':' or '-');
}

internal sealed record BackendGrant(
    string BackendId,
    string PipeName,
    DateTimeOffset ExpiresAt,
    int ProcessId,
    int ActiveGrants);

internal sealed record BackendSnapshot(string BackendId, string State, int QueueDepth, int GrantCount);

internal sealed class BackendManager : IDisposable
{
    private readonly BackendRegistry _registry;
    private readonly string _sid;
    private readonly object _sync = new();
    private readonly Dictionary<string, BackendRuntime> _runtimes = new(StringComparer.Ordinal);
    private readonly System.Threading.Timer _expiryTimer;
    private bool _disposed;

    public BackendManager(BackendRegistry registry)
    {
        _registry = registry;
        _sid = GetCurrentSid();
        _expiryTimer = new System.Threading.Timer(_ => ExpireGrants(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public BackendGrant Connect(string backendId, string rendererSid, int rendererPid, string rendererSessionId)
    {
        if (!string.Equals(rendererSid, _sid, StringComparison.OrdinalIgnoreCase))
            throw new BackendException(403, "backend_renderer_unauthorized", "Renderer SID does not match current user.");
        if (rendererPid <= 0 || string.IsNullOrWhiteSpace(rendererSessionId))
            throw new BackendException(400, "backend_request_invalid", "Renderer identity is invalid.");

        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_registry.Definitions.TryGetValue(backendId, out BackendDefinition? definition))
                throw new BackendException(404, "backend_not_configured", "Backend is not configured.");

            if (!_runtimes.TryGetValue(backendId, out BackendRuntime? runtime))
            {
                runtime = StartRuntime(backendId, definition);
                if (runtime.Process.HasExited)
                {
                    int exitCode = runtime.Process.ExitCode;
                    StopRuntime(runtime);
                    throw new BackendException(500, "backend_start_failed", $"Backend exited after readiness with code {exitCode}.");
                }
                _runtimes[backendId] = runtime;
            }
            else if (runtime.Process.HasExited)
            {
                StopRuntime(runtime);
                _runtimes.Remove(backendId);
                runtime = StartRuntime(backendId, definition);
                if (runtime.Process.HasExited)
                {
                    int exitCode = runtime.Process.ExitCode;
                    StopRuntime(runtime);
                    throw new BackendException(500, "backend_start_failed", $"Backend exited after readiness with code {exitCode}.");
                }
                _runtimes[backendId] = runtime;
            }

            DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            runtime.Grants[rendererSessionId] = expiresAt;
            return new BackendGrant(backendId, runtime.PipeName, expiresAt, runtime.Process.Id, runtime.Grants.Count);
        }
    }

    public void Disconnect(string backendId, string rendererSessionId)
    {
        lock (_sync)
        {
            if (!_runtimes.TryGetValue(backendId, out BackendRuntime? runtime))
                return;
            runtime.Grants.Remove(rendererSessionId);
            if (runtime.Grants.Count == 0)
            {
                StopRuntime(runtime);
                _runtimes.Remove(backendId);
            }
        }
    }

    public IReadOnlyList<BackendSnapshot> Snapshot()
    {
        lock (_sync)
        {
            PruneExpiredLocked();
            return _runtimes.Values.Select(runtime => new BackendSnapshot(
                runtime.BackendId,
                runtime.Process.HasExited ? "stopped" : "connected",
                0,
                runtime.Grants.Count)).ToArray();
        }
    }

    public void RevokeSession(string rendererSessionId)
    {
        lock (_sync)
        {
            foreach (string backendId in _runtimes.Keys.ToArray())
                Disconnect(backendId, rendererSessionId);
        }
    }

    public void RevokeAll()
    {
        lock (_sync)
        {
            foreach (BackendRuntime runtime in _runtimes.Values)
                StopRuntime(runtime);
            _runtimes.Clear();
        }
    }

    public void Dispose()
    {
        _expiryTimer.Dispose();
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (BackendRuntime runtime in _runtimes.Values)
                StopRuntime(runtime);
            _runtimes.Clear();
        }
    }

    private BackendRuntime StartRuntime(string backendId, BackendDefinition definition)
    {
        string pipeName = $"KhaosShell-Backend-{Guid.NewGuid():N}";
        var startInfo = new ProcessStartInfo
        {
            FileName = definition.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(definition.WorkingDirectory)
                ? Path.GetDirectoryName(Path.GetFullPath(definition.Executable)) ?? AppContext.BaseDirectory
                : definition.WorkingDirectory
        };
        foreach (string argument in definition.Arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("--chronos-pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--backend-id");
        startInfo.ArgumentList.Add(backendId);
        startInfo.ArgumentList.Add("--protocol");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--sid");
        startInfo.ArgumentList.Add(_sid);
        foreach ((string key, string value) in definition.Environment)
            startInfo.Environment[key] = value;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new BackendException(500, "backend_start_failed", "Backend process did not start.");
        }
        catch (BackendException)
        {
            process.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            process.Dispose();
            throw new BackendException(500, "backend_start_failed", $"Backend process could not start: {ex.Message}");
        }
        if (process.HasExited)
        {
            int exitCode = process.ExitCode;
            process.Dispose();
            throw new BackendException(500, "backend_start_failed", $"Backend process exited during startup with code {exitCode}.");
        }

        BackendReadySignal ready;
        try
        {
            string? line = process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
            ready = ParseReadySignal(line, backendId, pipeName);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            StopRuntime(new BackendRuntime(backendId, pipeName, process));
            throw new BackendException(500, "backend_ready_timeout", $"Backend did not become ready: {ex.Message}");
        }

        if (!string.Equals(ready.Sid, _sid, StringComparison.OrdinalIgnoreCase))
        {
            StopRuntime(new BackendRuntime(backendId, pipeName, process));
            throw new BackendException(500, "backend_acl_invalid", "Backend readiness SID does not match current user.");
        }
        var runtime = new BackendRuntime(backendId, pipeName, process);
        process.Exited += (_, _) => OnRuntimeExited(runtime);
        if (process.HasExited)
            OnRuntimeExited(runtime);
        return runtime;
    }

    private static BackendReadySignal ParseReadySignal(string? line, string backendId, string expectedPipeName)
    {
        if (string.IsNullOrWhiteSpace(line))
            throw new InvalidOperationException("Backend readiness signal was empty.");

        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("ready", out JsonElement ready) || ready.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("protocol", out JsonElement protocol) || protocol.ValueKind != JsonValueKind.Number || !protocol.TryGetInt32(out int version) || version != 1 ||
            !root.TryGetProperty("backendId", out JsonElement id) || id.GetString() != backendId ||
            !root.TryGetProperty("sid", out JsonElement sid) || sid.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("pipeName", out JsonElement pipe) || pipe.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Backend readiness signal is invalid.");

        string returnedPipe = pipe.GetString() ?? "";
        string normalizedExpected = expectedPipeName.StartsWith("\\\\.\\pipe\\", StringComparison.OrdinalIgnoreCase)
            ? expectedPipeName[9..]
            : expectedPipeName;
        string normalizedReturned = returnedPipe.StartsWith("\\\\.\\pipe\\", StringComparison.OrdinalIgnoreCase)
            ? returnedPipe[9..]
            : returnedPipe;
        if (!string.Equals(normalizedExpected, normalizedReturned, StringComparison.Ordinal))
            throw new InvalidOperationException("Backend readiness pipe name does not match launch contract.");

        return new BackendReadySignal(sid.GetString() ?? "");
    }

    private void ExpireGrants()
    {
        try
        {
            lock (_sync)
            {
                if (!_disposed)
                    PruneExpiredLocked();
            }
        }
        catch { }
    }

    private void OnRuntimeExited(BackendRuntime runtime)
    {
        lock (_sync)
        {
            if (_runtimes.TryGetValue(runtime.BackendId, out BackendRuntime? current) && ReferenceEquals(current, runtime))
            {
                _runtimes.Remove(runtime.BackendId);
                try { runtime.Process.Dispose(); } catch { }
            }
        }
    }

    private void PruneExpiredLocked()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (BackendRuntime runtime in _runtimes.Values.ToArray())
        {
            foreach (string sessionId in runtime.Grants.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
                runtime.Grants.Remove(sessionId);
            if (runtime.Grants.Count == 0)
            {
                StopRuntime(runtime);
                _runtimes.Remove(runtime.BackendId);
            }
        }
    }

    private static void StopRuntime(BackendRuntime runtime)
    {
        try
        {
            if (!runtime.Process.HasExited)
                runtime.Process.Kill(entireProcessTree: true);
        }
        catch { }
        runtime.Process.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BackendManager));
    }

    private static string GetCurrentSid()
    {
        try { return WindowsIdentity.GetCurrent().User?.Value ?? "unknown"; }
        catch { return "unknown"; }
    }

    private sealed class BackendRuntime
    {
        public BackendRuntime(string backendId, string pipeName, Process process)
        {
            BackendId = backendId;
            PipeName = pipeName;
            Process = process;
        }

        public string BackendId { get; }
        public string PipeName { get; }
        public Process Process { get; }
        public Dictionary<string, DateTimeOffset> Grants { get; } = new(StringComparer.Ordinal);
    }

    private sealed record BackendReadySignal(string Sid);
}

internal sealed class BackendException : Exception
{
    public BackendException(int status, string code, string message) : base(message)
    {
        Status = status;
        Code = code;
    }

    public int Status { get; }
    public string Code { get; }
}
