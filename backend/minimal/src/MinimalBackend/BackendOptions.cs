using System.Globalization;
using System.Security.Principal;
using KhaosShell.Contracts;

namespace KhaosShell.MinimalBackend;

internal sealed record BackendOptions(string PipeName, string BackendId, int Protocol, string Sid)
{
    public static BackendOptions Parse(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
            throw new BackendOptionException(HelpText);

        string? pipeName = null;
        string? backendId = null;
        string? sid = null;
        int? protocol = null;

        for (int index = 0; index < args.Length; index++)
        {
            string option = args[index];
            if (option.Contains('='))
                throw new BackendOptionException($"Options must use separate values: {option}");

            string value = ReadValue(args, ref index, option);
            switch (option)
            {
                case "--chronos-pipe":
                    SetOnce(ref pipeName, value, option);
                    break;
                case "--backend-id":
                    SetOnce(ref backendId, value, option);
                    break;
                case "--protocol":
                    if (protocol != null || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedProtocol))
                        throw new BackendOptionException($"{option} requires one integer value.");
                    protocol = parsedProtocol;
                    break;
                case "--sid":
                    SetOnce(ref sid, value, option);
                    break;
                default:
                    throw new BackendOptionException($"Unknown option: {option}");
            }
        }

        if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(backendId) || protocol == null || string.IsNullOrWhiteSpace(sid))
            throw new BackendOptionException("Required options: --chronos-pipe, --backend-id, --protocol, and --sid.");
        if (protocol != BackendCodec.ProtocolVersion)
            throw new BackendOptionException($"Unsupported backend protocol: {protocol}.");

        string normalizedPipeName = NormalizePipeName(pipeName);
        if (!IsBackendId(backendId))
            throw new BackendOptionException("Backend ID must contain 1-128 ASCII letters, digits, '.', '_', ':', or '-'.");

        try
        {
            _ = new SecurityIdentifier(sid);
        }
        catch (ArgumentException ex)
        {
            throw new BackendOptionException($"SID is invalid: {ex.Message}");
        }

        return new BackendOptions(normalizedPipeName, backendId, protocol.Value, sid);
    }

    public static string NormalizePipeName(string value)
    {
        const string prefix = "\\\\.\\pipe\\";
        string name = value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;
        if (name.Length == 0 || name.Contains('\\') || name.Contains('/') || name.Any(char.IsWhiteSpace))
            throw new BackendOptionException("Pipe name must be one non-empty local named-pipe name.");
        return name;
    }

    private static bool IsBackendId(string value) =>
        value.Length is > 0 and <= 128 && value.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or ':' or '-');

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            throw new BackendOptionException($"{option} requires a value.");
        return args[index];
    }

    private static void SetOnce(ref string? target, string value, string option)
    {
        if (target != null)
            throw new BackendOptionException($"{option} cannot be specified more than once.");
        target = value;
    }

    public const string HelpText = """
        MinimalBackend - generic KhaosShell backend protocol fixture

        Usage:
          MinimalBackend.exe --chronos-pipe <name> --backend-id <id> --protocol 1 --sid <sid>

        Routes:
          POST /v1/request
          POST /v1/streams
          DELETE /v1/streams/{stream-id}
        """;
}

internal sealed class BackendOptionException : Exception
{
    public BackendOptionException(string message) : base(message) { }
}
