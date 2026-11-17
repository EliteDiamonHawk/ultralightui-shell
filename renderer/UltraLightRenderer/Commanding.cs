using System.Globalization;
using System.Text.Json;

namespace UltraLightRenderer;

public enum RendererCommandKind
{
    None,
    Show,
    ShowMenu,
    HideMenu,
    HideWindow,
    RevealWindow,
    ApplyConfig,
    Pause,
    Resume,
    Status,
    Reload,
    Exit
}

public sealed record RendererCommand(
    RendererCommandKind Kind,
    string? Group = null,
    bool All = false,
    string? TargetId = null,
    string? Topic = null,
    IReadOnlyList<string>? ConfigPaths = null);

public sealed class HostOptions
{
    private HostOptions() { }

    public IReadOnlyList<string> ConfigPaths { get; private init; } = Array.Empty<string>();
    public string? UltralightRoot { get; private init; }
    public IntPtr? ControlReadHandle { get; private init; }
    public IntPtr? ControlWriteHandle { get; private init; }
    public int ReconcileIntervalMs { get; private init; } = 250;
    public TimeSpan StartupTimeout { get; private init; } = TimeSpan.FromSeconds(30);
    public bool Verbose { get; private init; }
    public bool ShowHelp { get; private init; }
    public RendererCommand Command { get; private init; } = new(RendererCommandKind.None);

    public static HostOptions Parse(string[] args)
    {
        var configPaths = new List<string>();
        string? ultralightRoot = null;
        int reconcileMs = 250;
        int startupTimeoutMs = 30_000;
        IntPtr? controlReadHandle = null;
        IntPtr? controlWriteHandle = null;
        bool verbose = false;
        bool showHelp = false;
        RendererCommand? command = null;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument is "--help" or "-h")
            {
                showHelp = true;
                continue;
            }

            string option = argument;
            string? inlineValue = null;
            int equalsIndex = argument.IndexOf('=');
            if (equalsIndex > 0)
            {
                option = argument[..equalsIndex];
                inlineValue = argument[(equalsIndex + 1)..];
            }

            switch (option)
            {
                case "--config":
                    EnsureNoCommand(command, option);
                    configPaths.Add(Path.GetFullPath(NextValue(args, ref index, option, inlineValue)));
                    break;
                case "--verbose":
                    EnsureNoCommand(command, option);
                    verbose = true;
                    break;
                case "--ultralight-root":
                    EnsureNoCommand(command, option);
                    ultralightRoot = Path.GetFullPath(NextValue(args, ref index, option, inlineValue));
                    break;
                case "--reconcile-ms":
                    EnsureNoCommand(command, option);
                    reconcileMs = ParsePositiveInt(NextValue(args, ref index, option, inlineValue), option);
                    break;
                case "--startup-timeout-ms":
                    EnsureNoCommand(command, option);
                    startupTimeoutMs = ParsePositiveInt(NextValue(args, ref index, option, inlineValue), option);
                    break;
                case "--control-read-handle":
                    EnsureNoCommand(command, option);
                    controlReadHandle = ParseHandle(NextValue(args, ref index, option, inlineValue), option);
                    break;
                case "--control-write-handle":
                    EnsureNoCommand(command, option);
                    controlWriteHandle = ParseHandle(NextValue(args, ref index, option, inlineValue), option);
                    break;
                case "--show":
                    SetCommand(ref command, new(RendererCommandKind.Show, Topic: NextRequiredTopic(args, ref index, inlineValue, option)), option);
                    break;
                case "--show-menu":
                    SetCommand(ref command, new(RendererCommandKind.ShowMenu), option);
                    break;
                case "--hide-menu":
                    SetCommand(ref command, new(RendererCommandKind.HideMenu), option);
                    break;
                case "--hide":
                    SetCommand(ref command, new(RendererCommandKind.HideWindow, TargetId: NextRequiredId(args, ref index, inlineValue, option)), option);
                    break;
                case "--reveal":
                    SetCommand(ref command, new(RendererCommandKind.RevealWindow, TargetId: NextRequiredId(args, ref index, inlineValue, option)), option);
                    break;
                case "--pause": SetCommand(ref command, new(RendererCommandKind.Pause, NextOptionalGroup(args, ref index, inlineValue)), option); break;
                case "--resume": SetCommand(ref command, new(RendererCommandKind.Resume, NextOptionalGroup(args, ref index, inlineValue)), option); break;
                case "--status": SetCommand(ref command, new(RendererCommandKind.Status, NextOptionalGroup(args, ref index, inlineValue)), option); break;
                case "--reload": SetCommand(ref command, new(RendererCommandKind.Reload, NextOptionalGroup(args, ref index, inlineValue)), option); break;
                case "--pause-all": SetCommand(ref command, new(RendererCommandKind.Pause, All: true), option); break;
                case "--resume-all": SetCommand(ref command, new(RendererCommandKind.Resume, All: true), option); break;
                case "--status-all": SetCommand(ref command, new(RendererCommandKind.Status, All: true), option); break;
                case "--reload-all": SetCommand(ref command, new(RendererCommandKind.Reload, All: true), option); break;
                case "--exit": SetCommand(ref command, new(RendererCommandKind.Exit), option); break;
                default:
                    throw new OptionException($"Unknown argument: {argument}");
            }
        }

        if (showHelp)
            return new HostOptions { ShowHelp = true };

        if (command != null)
        {
            if (configPaths.Count > 0 || ultralightRoot != null || verbose || reconcileMs != 250 || startupTimeoutMs != 30_000 || controlReadHandle != null || controlWriteHandle != null)
                throw new OptionException("Runtime commands cannot be combined with startup options.");
            return new HostOptions { Command = command };
        }

        if (controlReadHandle.HasValue != controlWriteHandle.HasValue)
            throw new OptionException("Control read and write handles must be supplied together.");

        return new HostOptions
        {
            ConfigPaths = configPaths,
            UltralightRoot = ultralightRoot,
            ControlReadHandle = controlReadHandle,
            ControlWriteHandle = controlWriteHandle,
            ReconcileIntervalMs = reconcileMs,
            StartupTimeout = TimeSpan.FromMilliseconds(startupTimeoutMs),
            Verbose = verbose
        };
    }

    public const string HelpText = """
        UltraLightRenderer - unified Ultralight wallpaper, widget, and menu host

        Usage:
          UltraLightRenderer.exe [--config <path> ...] [options]
          UltraLightRenderer.exe --show-menu | --hide-menu
          UltraLightRenderer.exe --hide <id> | --reveal <id>
          UltraLightRenderer.exe --show pipes|config|windows|paths
          UltraLightRenderer.exe --pause [group] | --pause-all
          UltraLightRenderer.exe --resume [group] | --resume-all
          UltraLightRenderer.exe --status [group] | --status-all
          UltraLightRenderer.exe --reload [group] | --reload-all
          UltraLightRenderer.exe --exit

        Startup options:
          --config <path>                Configuration JSON; repeatable. Optional.
          --ultralight-root <path>       External Ultralight SDK/install root.
          --reconcile-ms <n>             Geometry reconciliation interval. Default: 250.
          --startup-timeout-ms <n>       Window startup timeout. Default: 30000.
          --control-read-handle <n>     Inherited control-to-renderer pipe handle.
          --control-write-handle <n>    Inherited renderer-to-control pipe handle.
          --verbose                      Log embedding diagnostics.

        Runtime commands:
          --show-menu, --hide-menu       Show or hide configured menu.
          --hide <id>, --reveal <id>     Hide or reveal one window.
          --show <topic>                 Show pipes, config, windows, or paths as JSON.
          --pause [group], --pause-all   Pause one group or all groups.
          --resume [group], --resume-all Resume one group or all groups.
          --status [group], --status-all Print one group or all groups.
          --reload [group], --reload-all Reload one group or all groups.
          --exit                         Shut down the running instance.
        """;

    private static void SetCommand(ref RendererCommand? command, RendererCommand requested, string option)
    {
        if (command != null)
            throw new OptionException($"{option} cannot be combined with another runtime command.");
        command = requested;
    }

    private static void EnsureNoCommand(RendererCommand? command, string option)
    {
        if (command != null)
            throw new OptionException($"{option} cannot be combined with a runtime command.");
    }

    private static string? NextOptionalGroup(string[] args, ref int index, string? inlineValue)
    {
        if (inlineValue != null)
            return inlineValue.Trim();
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            return null;
        return args[++index].Trim();
    }

    private static string NextRequiredTopic(string[] args, ref int index, string? inlineValue, string option)
    {
        string value = NextValue(args, ref index, option, inlineValue).Trim().ToLowerInvariant();
        if (value is not ("pipes" or "config" or "windows" or "paths"))
            throw new OptionException($"{option} topic must be pipes, config, windows, or paths.");
        return value;
    }

    private static string NextRequiredId(string[] args, ref int index, string? inlineValue, string option)
    {
        string value = NextValue(args, ref index, option, inlineValue).Trim();
        if (value.Length == 0)
            throw new OptionException($"{option} requires a non-empty window ID.");
        return value;
    }

    private static string NextValue(string[] args, ref int index, string option, string? inlineValue)
    {
        if (inlineValue != null)
            return inlineValue;
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            throw new OptionException($"{option} requires a value.");
        return args[index];
    }

    private static int ParsePositiveInt(string value, string option)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
            throw new OptionException($"{option} requires a positive integer.");
        return parsed;
    }

    private static IntPtr ParseHandle(string value, string option)
    {
        if (!ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong raw) || raw == 0 || raw > long.MaxValue)
            throw new OptionException($"{option} requires a positive native handle value.");
        return new IntPtr(unchecked((long)raw));
    }
}

public sealed class OptionException : Exception
{
    public OptionException(string message) : base(message) { }
}

internal static class RendererCommandProtocol
{
    public static string Serialize(RendererCommand command) => JsonSerializer.Serialize(command);

    public static bool TryParse(string text, out RendererCommand? command)
    {
        try
        {
            command = JsonSerializer.Deserialize<RendererCommand>(text);
            return command != null && command.Kind != RendererCommandKind.None;
        }
        catch (JsonException)
        {
            command = null;
            return false;
        }
    }
}
