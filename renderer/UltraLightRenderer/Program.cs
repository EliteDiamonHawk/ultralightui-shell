namespace UltraLightRenderer;

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

            if (options.Command.Kind != RendererCommandKind.None)
                return CommandPipeClient.Send(options.Command);

            ApplicationConfiguration.Initialize();
            using SingleInstanceLease lease = SingleInstanceLease.Acquire();
            Application.Run(new RendererHostContext(options, lease));
            return 0;
        }
        catch (OptionException ex)
        {
            Console.Error.WriteLine($"[Error] {ex.Message}");
            Console.Error.WriteLine("Use --help for usage.");
            return 2;
        }
        catch (RendererConfigurationException ex)
        {
            Console.Error.WriteLine($"[Config] {ex.Message}");
            return 2;
        }
        catch (RendererInstanceAlreadyRunningException ex)
        {
            Console.Error.WriteLine($"[Instance] {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Error] {ex.Message}");
            return 1;
        }
    }
}
