namespace Chronos;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            ControllerOptions options = ControllerOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(ControllerOptions.HelpText);
                return 0;
            }

            ApplicationConfiguration.Initialize();
            using SingleInstanceLease lease = SingleInstanceLease.Acquire();
            using var context = new ChronosHostContext(options);
            Application.Run(context);
            return 0;
        }
        catch (OptionException ex)
        {
            Console.Error.WriteLine($"[Error] {ex.Message}");
            Console.Error.WriteLine("Use --help for usage.");
            return 2;
        }
        catch (InstanceAlreadyRunningException ex)
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
