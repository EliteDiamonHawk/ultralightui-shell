using System.Security.Principal;
using KhaosShell.Contracts;

namespace KhaosShell.Nyx;

internal sealed record BackendOptions(
    string PipeName,
    string BackendId,
    int Protocol,
    string Sid,
    string DataRoot)
{
    public static BackendOptions Parse(string[] args)
    {
        string? pipe = null;
        string? backendId = null;
        string? sid = null;
        string? dataRoot = null;
        var protocol = 0;

        for (var index = 0; index < args.Length; index++)
        {
            string value = args[index];
            if (value is "--chronos-pipe" or "--backend-id" or "--protocol" or "--sid" or "--data-root")
            {
                if (++index >= args.Length)
                    throw new ArgumentException($"Missing value for {value}.");
                switch (value)
                {
                    case "--chronos-pipe": pipe = args[index]; break;
                    case "--backend-id": backendId = args[index]; break;
                    case "--protocol":
                        if (!int.TryParse(args[index], out protocol))
                            throw new ArgumentException("Protocol must be an integer.");
                        break;
                    case "--sid": sid = args[index]; break;
                    case "--data-root": dataRoot = args[index]; break;
                }
            }
            else
            {
                throw new ArgumentException($"Unknown argument: {value}.");
            }
        }

        if (string.IsNullOrWhiteSpace(pipe) || string.IsNullOrWhiteSpace(backendId) || string.IsNullOrWhiteSpace(sid) || protocol != BackendCodec.ProtocolVersion)
            throw new ArgumentException("Required launch contract is invalid.");

        string pipeName = pipe.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase) ? pipe[9..] : pipe;
        if (pipeName.Length == 0 || pipeName.Contains('\\') || pipeName.Contains('/') || pipeName.Contains('\0'))
            throw new ArgumentException("Pipe name is invalid.");

        string currentSid;
        try { currentSid = WindowsIdentity.GetCurrent().User?.Value ?? ""; }
        catch { currentSid = ""; }
        if (!string.Equals(currentSid, sid, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Supplied SID does not match backend process identity.");

        string configuredRoot = string.IsNullOrWhiteSpace(dataRoot) ? Environment.GetEnvironmentVariable("NYX_DATA_ROOT") ?? "" : dataRoot;
        string root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(LocalAppData(), "KhaosShell", "Nyx")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredRoot));
        return new BackendOptions(pipeName, backendId, protocol, sid, root);
    }

    private static string LocalAppData()
    {
        string value = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(value)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local")
            : value;
    }
}
