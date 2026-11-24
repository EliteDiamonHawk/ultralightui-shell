using KhaosShell.Contracts;

namespace KhaosShell.Nyx;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            BackendOptions options = BackendOptions.Parse(args);
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
            await using var server = new BackendServer(options);
            await server.RunAsync(cancellation.Token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Nyx] fatal: {ex.Message}");
            return 1;
        }
    }
}
