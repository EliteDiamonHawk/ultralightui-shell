using System.Globalization;

namespace Chronos;

internal sealed class ControllerOptions
{
    private ControllerOptions() { }

    public IReadOnlyList<string> ConfigPaths { get; private init; } = Array.Empty<string>();
    public string? RendererPath { get; private init; }
    public bool NoLastConfig { get; private init; }
    public bool NoLastConfigSpecified { get; private init; }
    public bool NoTray { get; private init; }
    public bool NoTraySpecified { get; private init; }
    public bool NoStatusPoll { get; private init; }
    public bool? WinMenuToggle { get; private init; }
    public bool ShowHelp { get; private init; }

    public static ControllerOptions Parse(string[] args)
    {
        var configs = new List<string>();
        string? rendererPath = null;
        bool noLastConfig = false;
        bool noLastConfigSpecified = false;
        bool noTray = false;
        bool noTraySpecified = false;
        bool noStatusPoll = false;
        bool? winMenuToggle = null;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            string option = argument;
            string? inlineValue = null;
            int equals = argument.IndexOf('=');
            if (equals > 0)
            {
                option = argument[..equals];
                inlineValue = argument[(equals + 1)..];
            }

            switch (option)
            {
                case "--help":
                case "-h":
                    return new ControllerOptions { ShowHelp = true };
                case "--config":
                    configs.Add(NormalizePath(NextValue(args, ref index, option, inlineValue), option));
                    break;
                case "--renderer-path":
                    if (rendererPath != null)
                        throw new OptionException("--renderer-path cannot be specified more than once.");
                    rendererPath = NormalizePath(NextValue(args, ref index, option, inlineValue), option);
                    break;
                case "--no-last-config":
                    if (inlineValue != null)
                        throw new OptionException("--no-last-config does not accept a value.");
                    SetBooleanOption(ref noLastConfig, ref noLastConfigSpecified, true, "--last-config and --no-last-config cannot be combined.");
                    break;
                case "--last-config":
                    if (inlineValue != null)
                        throw new OptionException("--last-config does not accept a value.");
                    SetBooleanOption(ref noLastConfig, ref noLastConfigSpecified, false, "--last-config and --no-last-config cannot be combined.");
                    break;
                case "--no-tray":
                    if (inlineValue != null)
                        throw new OptionException("--no-tray does not accept a value.");
                    SetBooleanOption(ref noTray, ref noTraySpecified, true, "--tray and --no-tray cannot be combined.");
                    break;
                case "--tray":
                    if (inlineValue != null)
                        throw new OptionException("--tray does not accept a value.");
                    SetBooleanOption(ref noTray, ref noTraySpecified, false, "--tray and --no-tray cannot be combined.");
                    break;
                case "--no-status-poll":
                    if (inlineValue != null)
                        throw new OptionException("--no-status-poll does not accept a value.");
                    noStatusPoll = true;
                    break;
                case "--win-menu-toggle":
                    if (inlineValue != null)
                        throw new OptionException("--win-menu-toggle does not accept a value.");
                    SetToggle(ref winMenuToggle, true);
                    break;
                case "--no-win-menu-toggle":
                    if (inlineValue != null)
                        throw new OptionException("--no-win-menu-toggle does not accept a value.");
                    SetToggle(ref winMenuToggle, false);
                    break;
                default:
                    throw new OptionException($"Unknown argument: {argument}");
            }
        }

        return new ControllerOptions
        {
            ConfigPaths = configs,
            RendererPath = rendererPath,
            NoLastConfig = noLastConfig,
            NoLastConfigSpecified = noLastConfigSpecified,
            NoTray = noTray,
            NoTraySpecified = noTraySpecified,
            NoStatusPoll = noStatusPoll,
            WinMenuToggle = winMenuToggle
        };
    }

    public const string HelpText = """
        Chronos - KhaosShell controller for Aether

        Usage:
          Chronos.exe [options]

        Options:
          --config <absolute-path>       Forward configuration file; repeatable.
          --no-last-config               Skip persisted configuration restoration.
          --last-config                  Restore persisted configuration despite saved preference.
          --no-tray                      Do not create a system-tray icon.
          --tray                         Create a system-tray icon despite saved preference.
          --no-status-poll               Disable WinForms status polling; events remain enabled.
          --win-menu-toggle              Enable bare-Win menu interception.
          --no-win-menu-toggle           Disable bare-Win menu interception.
          --renderer-path <absolute-path> Override Aether executable discovery.
          --help, -h                     Print this help.
        """;

    private static string NextValue(string[] args, ref int index, string option, string? inlineValue)
    {
        if (inlineValue != null)
        {
            if (inlineValue.Length == 0)
                throw new OptionException($"{option} requires a value.");
            return inlineValue;
        }
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            throw new OptionException($"{option} requires a value.");
        return args[index];
    }

    private static string NormalizePath(string value, string option)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new OptionException($"{option} requires a non-empty path.");
        try { return Path.GetFullPath(value); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new OptionException($"{option} path is invalid: {ex.Message}");
        }
    }

    private static void SetToggle(ref bool? current, bool value)
    {
        if (current.HasValue)
            throw new OptionException("Win-menu toggle options cannot be combined.");
        current = value;
    }

    private static void SetBooleanOption(ref bool current, ref bool specified, bool value, string conflict)
    {
        if (specified && current != value)
            throw new OptionException(conflict);
        current = value;
        specified = true;
    }
}

internal sealed class OptionException : Exception
{
    public OptionException(string message) : base(message) { }
}
