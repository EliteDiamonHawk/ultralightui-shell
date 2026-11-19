using System.Globalization;

namespace Chronos;

internal sealed class ControllerOptions
{
    private ControllerOptions() { }

    public IReadOnlyList<string> ConfigPaths { get; private init; } = Array.Empty<string>();
    public string? RendererPath { get; private init; }
    public bool? Tray { get; private init; }
    public bool? RestoreState { get; private init; }
    public bool? StatusPolling { get; private init; }
    public bool? WindowsKeyOverride { get; private init; }
    public bool ShowHelp { get; private init; }

    public static ControllerOptions Parse(string[] args)
    {
        var configs = new List<string>();
        string? rendererPath = null;
        bool? tray = null;
        bool? restoreState = null;
        bool? statusPolling = null;
        bool? windowsKeyOverride = null;

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
                case "--tray":
                    SetBooleanOption(ref tray, NextBoolean(args, ref index, option, inlineValue), option);
                    break;
                case "--restore-state":
                    SetBooleanOption(ref restoreState, NextBoolean(args, ref index, option, inlineValue), option);
                    break;
                case "--status-poll":
                    SetBooleanOption(ref statusPolling, NextBoolean(args, ref index, option, inlineValue), option);
                    break;
                case "--override-windows-key":
                    SetBooleanOption(ref windowsKeyOverride, NextBoolean(args, ref index, option, inlineValue), option);
                    break;
                default:
                    throw new OptionException($"Unknown argument: {argument}");
            }
        }

        return new ControllerOptions
        {
            ConfigPaths = configs,
            RendererPath = rendererPath,
            Tray = tray,
            RestoreState = restoreState,
            StatusPolling = statusPolling,
            WindowsKeyOverride = windowsKeyOverride
        };
    }

    public const string HelpText = """
        Chronos - KhaosShell controller for Aether

        Usage:
          Chronos.exe [options]

        Options:
          --config <absolute-path>       Forward theme file; repeatable.
          --restore-state <true|false>   Restore persisted theme state; default true.
          --tray <true|false>            Create Chronos tray icon; default true.
          --status-poll <true|false>     Enable WinForms status polling; events remain active.
          --override-windows-key <true|false>
                                        Enable Windows Key Override; default false.
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

    private static bool NextBoolean(string[] args, ref int index, string option, string? inlineValue)
    {
        string value = NextValue(args, ref index, option, inlineValue);
        if (!bool.TryParse(value, out bool parsed))
            throw new OptionException($"{option} requires true or false.");
        return parsed;
    }

    private static void SetBooleanOption(ref bool? current, bool value, string option)
    {
        if (current.HasValue)
            throw new OptionException($"{option} cannot be specified more than once.");
        current = value;
    }
}

internal sealed class OptionException : Exception
{
    public OptionException(string message) : base(message) { }
}
