using System.Diagnostics;

namespace KhaosShell.Nyx;

internal sealed class SystemActions
{
    private static readonly IReadOnlyDictionary<string, (string FileName, string Arguments)> Commands = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["sleep"] = ("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"),
        ["shutdown"] = ("shutdown.exe", "/s /t 0"),
        ["lock"] = ("rundll32.exe", "user32.dll,LockWorkStation"),
        ["logout"] = ("shutdown.exe", "/l"),
        ["restart"] = ("shutdown.exe", "/r /t 0"),
    };

    public void Execute(string name)
    {
        if (!Commands.TryGetValue(name, out var command))
            throw new KeyNotFoundException(name);
        if (IsDevelopment())
        {
            Console.Error.WriteLine($"[Nyx] development system control requested: {name}");
            return;
        }
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows system control is unavailable.");
        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = command.FileName,
            Arguments = command.Arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null) throw new InvalidOperationException("System control process could not start.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"System control failed with code {process.ExitCode}.");
    }

    private static bool IsDevelopment() => Environment.GetEnvironmentVariable("NYX_DEV")?.ToLowerInvariant() is "1" or "true" or "yes";
}
