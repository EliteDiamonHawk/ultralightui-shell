namespace KhaosShell.MinimalBackend;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine(BackendOptions.HelpText);
            return 0;
        }

        try
        {
            BackendOptions options = BackendOptions.Parse(args);
            using var shutdown = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            Console.CancelKeyPress += cancel;
            try
            {
                await using var server = new BackendServer(options);
                await server.RunAsync(shutdown.Token).ConfigureAwait(false);
            }
            finally
            {
                Console.CancelKeyPress -= cancel;
            }
            return 0;
        }
        catch (BackendOptionException ex)
        {
            Console.Error.WriteLine($"[Options] {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MinimalBackend] {ex.Message}");
            return 1;
        }
    }
}
