using System.Globalization;
using KhaosShell.Contracts;
using RendererCommandKind = KhaosShell.Contracts.RendererCommandKind;
using SystemTrayAction = KhaosShell.Contracts.SystemTrayAction;

namespace Aether;

public sealed class HostOptions
{
    private HostOptions() { }

    public IReadOnlyList<string> ConfigPaths { get; private init; } = Array.Empty<string>();
    public string? UltralightRoot { get; private init; }
    public IntPtr? ControlReadHandle { get; private init; }
    public IntPtr? ControlWriteHandle { get; private init; }
    internal bool HasInheritedControlChannel => ControlReadHandle.HasValue && ControlWriteHandle.HasValue;
    public int ReconcileIntervalMs { get; private init; } = 250;
    public TimeSpan StartupTimeout { get; private init; } = TimeSpan.FromSeconds(30);
    public bool Verbose { get; private init; }
    public bool NoTray { get; private init; }
    public bool ShowHelp { get; private init; }
    public SystemTrayAction? InitialSystemTrayAction { get; private init; }
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
        bool noTray = false;
        bool showHelp = false;
        SystemTrayAction? systemTrayAction = null;
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
                case "--no-tray":
                    EnsureNoCommand(command, option);
                    if (inlineValue != null)
                        throw new OptionException($"{option} does not accept a value.");
                    noTray = true;
                    break;
                case "--system-tray":
                    EnsureNoCommand(command, option);
                    if (systemTrayAction != null)
                        throw new OptionException($"{option} cannot be specified more than once.");
                    systemTrayAction = NextSystemTrayAction(args, ref index, inlineValue, option);
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
                case "--list":
                    SetCommand(ref command, new(RendererCommandKind.Show, Topic: NextRequiredTopic(args, ref index, inlineValue, option)), option);
                    break;
                case "--toggle-menu":
                    SetCommand(ref command, ParseMenuCommand(args, ref index, inlineValue, option), option);
                    break;
                case "--hide":
                    SetCommand(ref command, new(RendererCommandKind.HideWindow, TargetId: NextRequiredId(args, ref index, inlineValue, option)), option);
                    break;
                case "--show":
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
            if (configPaths.Count > 0 || ultralightRoot != null || verbose || noTray || reconcileMs != 250 || startupTimeoutMs != 30_000 || controlReadHandle != null || controlWriteHandle != null || systemTrayAction != null)
                throw new OptionException("Runtime commands cannot be combined with startup options.");
            return new HostOptions { Command = command };
        }

        if (controlReadHandle.HasValue != controlWriteHandle.HasValue)
            throw new OptionException("Control read and write handles must be supplied together.");

        bool managed = controlReadHandle.HasValue && controlWriteHandle.HasValue;
        if (managed && (configPaths.Count > 0 || ultralightRoot != null || reconcileMs != 250 ||
                        startupTimeoutMs != 30_000 || verbose || noTray || systemTrayAction != null))
            throw new OptionException("Managed Aether startup accepts only inherited control handles; send startup options through the control channel.");

        return new HostOptions
        {
            ConfigPaths = configPaths,
            UltralightRoot = ultralightRoot,
            ControlReadHandle = controlReadHandle,
            ControlWriteHandle = controlWriteHandle,
            ReconcileIntervalMs = reconcileMs,
            StartupTimeout = TimeSpan.FromMilliseconds(startupTimeoutMs),
            Verbose = verbose,
            NoTray = noTray,
            InitialSystemTrayAction = systemTrayAction
        };
    }

    internal HostOptions WithManagedRuntime(string? ultralightRoot, bool noTray) => new()
    {
        ConfigPaths = Array.Empty<string>(),
        UltralightRoot = ultralightRoot,
        ControlReadHandle = ControlReadHandle,
        ControlWriteHandle = ControlWriteHandle,
        ReconcileIntervalMs = ReconcileIntervalMs,
        StartupTimeout = StartupTimeout,
        Verbose = Verbose,
        NoTray = noTray,
        InitialSystemTrayAction = null
    };

    public const string HelpText = """
        Aether - unified Ultralight wallpaper, widget, and menu host

        Usage:
          Aether.exe [--config <path> ...] [options]
          Aether.exe --toggle-menu [show|hide]
          Aether.exe --hide <id> | --show <id>
          Aether.exe --list pipes|config|windows|menu|paths
          Aether.exe --system-tray [show|hide]
          Aether.exe --pause [group] | --pause-all
          Aether.exe --resume [group] | --resume-all
          Aether.exe --status [group] | --status-all
          Aether.exe --reload [group] | --reload-all
          Aether.exe --exit

        Startup options:
          --config <path>                Configuration JSON; repeatable. Optional.
          --ultralight-root <path>       External Ultralight SDK/install root.
          --reconcile-ms <n>             Geometry reconciliation interval. Default: 250.
          --startup-timeout-ms <n>       Window startup timeout. Default: 30000.
          --control-read-handle <n>     Inherited control-to-renderer pipe handle.
          --control-write-handle <n>    Inherited renderer-to-control pipe handle.
          --verbose                      Log embedding diagnostics.
          --no-tray                      Do not construct an Aether tray icon.
          --system-tray [show|hide]      Set tray visibility; bare flag toggles it.

        Runtime commands:
          --toggle-menu [show|hide]      Toggle, show, or hide configured menu.
          --hide <id>, --show <id>       Hide or show one window.
          --list <topic>                 List pipes, config, windows, menu, or paths as JSON.
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
        if (value is not ("pipes" or "config" or "windows" or "menu" or "paths"))
            throw new OptionException($"{option} topic must be pipes, config, windows, menu, or paths.");
        return value;
    }

    private static RendererCommand ParseMenuCommand(string[] args, ref int index, string? inlineValue, string option)
    {
        string? value = inlineValue;
        if (value == null && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            value = args[++index];

        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" => new(RendererCommandKind.ToggleMenu),
            "show" => new(RendererCommandKind.ShowMenu),
            "hide" => new(RendererCommandKind.HideMenu),
            _ => throw new OptionException($"{option} value must be show or hide.")
        };
    }

    private static SystemTrayAction NextSystemTrayAction(string[] args, ref int index, string? inlineValue, string option)
    {
        string? value = inlineValue;
        if (value == null && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            value = args[++index];
        if (string.IsNullOrWhiteSpace(value))
            return SystemTrayAction.Toggle;
        return value.Trim().ToLowerInvariant() switch
        {
            "show" => SystemTrayAction.Show,
            "hide" => SystemTrayAction.Hide,
            _ => throw new OptionException($"{option} value must be show or hide.")
        };
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
    public static string Serialize(RendererCommand command) => RendererCommandCodec.Serialize(command);

    public static bool TryParse(string text, out RendererCommand? command) => RendererCommandCodec.TryParse(text, out command);
}
