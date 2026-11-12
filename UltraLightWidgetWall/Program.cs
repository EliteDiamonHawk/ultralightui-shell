using System.Globalization;

namespace UltraLightWidgetWall;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            HostOptions options = HostOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(HostOptions.HelpText);
                return 0;
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new WallpaperHostContext(options));
            return 0;
        }
        catch (OptionException ex)
        {
            Console.Error.WriteLine($"[Error] {ex.Message}");
            Console.Error.WriteLine("Use --help for usage.");
            return 2;
        }
        catch (WidgetConfigurationException ex)
        {
            Console.Error.WriteLine($"[Config] {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Error] {ex.Message}");
            return 1;
        }
    }
}

public sealed record WallpaperWindowSpec(
    string LogicalId,
    string Url,
    Rectangle Bounds,
    Rectangle VirtualBounds,
    bool Transparent = true,
    int MonitorNumber = 0);

public sealed class HostOptions
{
    private HostOptions() { }

    public string ConfigPath { get; private init; } = "";
    public TimeSpan StartupTimeout { get; private init; } = TimeSpan.FromSeconds(30);
    public string? UltralightRoot { get; private init; }
    public int ReconcileIntervalMs { get; private init; } = 250;
    public bool Verbose { get; private init; }
    public bool ShowHelp { get; private init; }

    public static HostOptions Parse(string[] args)
    {
        string? configPath = null;
        int reconcileMs = 250;
        int startupTimeoutMs = 30_000;
        string? ultralightRoot = null;
        bool verbose = false;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument is "--help" or "-h")
                return new HostOptions { ShowHelp = true };

            if (argument == "--verbose")
            {
                verbose = true;
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
                    configPath = NextValue(args, ref index, option, inlineValue);
                    break;
                case "--reconcile-ms":
                    reconcileMs = ParsePositiveInt(NextValue(args, ref index, option, inlineValue), option);
                    break;
                case "--startup-timeout-ms":
                    startupTimeoutMs = ParsePositiveInt(NextValue(args, ref index, option, inlineValue), option);
                    break;
                case "--ultralight-root":
                    ultralightRoot = Path.GetFullPath(NextValue(args, ref index, option, inlineValue));
                    break;
                default:
                    throw new OptionException($"Unknown argument: {argument}");
            }
        }

        if (string.IsNullOrWhiteSpace(configPath))
            throw new OptionException("--config <path> is required.");

        return new HostOptions
        {
            ConfigPath = Path.GetFullPath(configPath),
            UltralightRoot = ultralightRoot,
            ReconcileIntervalMs = reconcileMs,
            StartupTimeout = TimeSpan.FromMilliseconds(startupTimeoutMs),
            Verbose = verbose
        };
    }

    public WidgetConfiguration LoadConfiguration() => WidgetConfiguration.Load(ConfigPath);

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

    public const string HelpText = """
        UltraLightWidgetWall - JSON-configured Ultralight desktop widget wall

        Usage:
          UltraLightWidgetWall.exe --config <path> [options]

        Options:
          --config <path>                Required JSON widget configuration.
          --reconcile-ms <n>             Desktop reconciliation interval. Default: 250.
          --startup-timeout-ms <n>       Window startup timeout. Default: 30000.
          --ultralight-root <path>      External Ultralight SDK/install root.
          --verbose                      Log embedding diagnostics.
          --help, -h                     Show this help.
        """;
}

public sealed class OptionException : Exception
{
    public OptionException(string message) : base(message) { }
}
