using System.Diagnostics;
using System.IO.Pipes;

namespace Chronos;

internal static class RendererLocator
{
    public static string Resolve(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string requested = Path.GetFullPath(explicitPath);
            if (!File.Exists(requested))
                throw new FileNotFoundException($"Aether executable was not found: {requested}", requested);
            return requested;
        }

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Aether.exe")
        };
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            candidates.Add(Path.Combine(localAppData, "KhaosShell", "Aether", "installation", "Aether.exe"));

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        throw new FileNotFoundException(
            "Aether.exe was not found. Use --renderer-path or install it under %LOCALAPPDATA%\\KhaosShell\\Aether\\installation.",
            candidates.FirstOrDefault());
    }
}

internal sealed class RendererSession : IDisposable
{
    private readonly AnonymousPipeServerStream _toRenderer;
    private readonly AnonymousPipeServerStream _fromRenderer;
    private readonly Process _process;
    private readonly ControlChannel _control;
    private int _disposed;

    private RendererSession(
        AnonymousPipeServerStream toRenderer,
        AnonymousPipeServerStream fromRenderer,
        Process process,
        ControlChannel control)
    {
        _toRenderer = toRenderer;
        _fromRenderer = fromRenderer;
        _process = process;
        _control = control;
    }

    public Process Process => _process;
    public ControlChannel Control => _control;
    public event Action? ProcessExited;

    public bool WaitForExit(TimeSpan timeout)
    {
        try
        {
            if (_process.HasExited)
                return true;
            return _process.WaitForExit(timeout);
        }
        catch (InvalidOperationException) { return true; }
    }

    public static RendererSession Start(
        string rendererPath,
        IReadOnlyList<string> configPaths,
        Func<ControlRequest, Task<ControlResponse>> controlHandler,
        Action<string>? output,
        Action<string>? error)
    {
        (AnonymousPipeServerStream toRenderer,
            AnonymousPipeServerStream fromRenderer,
            string childReadHandle,
            string childWriteHandle) = AnonymousPipeFactory.Create();

        var startInfo = new ProcessStartInfo
        {
            FileName = rendererPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string configPath in configPaths)
        {
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add(configPath);
        }
        startInfo.ArgumentList.Add("--no-tray");
        startInfo.ArgumentList.Add("--control-read-handle");
        startInfo.ArgumentList.Add(childReadHandle);
        startInfo.ArgumentList.Add("--control-write-handle");
        startInfo.ArgumentList.Add(childWriteHandle);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
                output?.Invoke(args.Data);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
                error?.Invoke(args.Data);
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Aether process did not start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            toRenderer.DisposeLocalCopyOfClientHandle();
            fromRenderer.DisposeLocalCopyOfClientHandle();
            var control = new ControlChannel(fromRenderer, toRenderer, controlHandler);
            var session = new RendererSession(toRenderer, fromRenderer, process, control);
            process.Exited += (_, _) => session.ProcessExited?.Invoke();
            control.Start();
            return session;
        }
        catch
        {
            try { process.Dispose(); } catch { }
            try { toRenderer.Dispose(); } catch { }
            try { fromRenderer.Dispose(); } catch { }
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _control.Dispose();
        try { _toRenderer.Dispose(); } catch { }
        try { _fromRenderer.Dispose(); } catch { }
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(TimeSpan.FromSeconds(5));
            }
        }
        catch { }
        _process.Dispose();
    }
}
