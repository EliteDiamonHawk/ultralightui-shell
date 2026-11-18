using System.Globalization;

namespace Chronos;

internal sealed class ControllerOptions
{
    private ControllerOptions() { }

    public IReadOnlyList<string> ConfigPaths { get; private init; } = Array.Empty<string>();
    public string? RendererPath { get; private init; }
    public bool NoLastConfig { get; private init; }
    public bool NoTray { get; private init; }
    public bool? WinMenuToggle { get; private init; }
    public bool ShowHelp { get; private init; }

    public static ControllerOptions Parse(string[] args)
    {
        var configs = new List<string>();
        string? rendererPath = null;
        bool noLastConfig = false;
        bool noTray = false;
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
                    noLastConfig = true;
                    break;
                case "--no-tray":
                    if (inlineValue != null)
                        throw new OptionException("--no-tray does not accept a value.");
                    noTray = true;
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
            NoTray = noTray,
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
          --no-tray                      Do not create a system-tray icon.
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
}

internal sealed class OptionException : Exception
{
    public OptionException(string message) : base(message) { }
}
