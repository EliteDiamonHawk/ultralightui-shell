using System.IO.Pipes;
using System.Text;

namespace UltraLightRenderer;

public sealed class CommandPipeServer : IDisposable
{
    private readonly Func<RendererCommand, string> _handler;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;

    public CommandPipeServer(Func<RendererCommand, string> handler) => _handler = handler;

    public void Start() => _loop = Task.Run(RunAsync);

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _shutdown.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    RendererIpcNames.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                string? line = await reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
                if (!RendererCommandProtocol.TryParse(line ?? "", out RendererCommand? command) || command == null)
                {
                    await writer.WriteLineAsync("ERR Invalid runtime command.").ConfigureAwait(false);
                    continue;
                }

                string response;
                try { response = _handler(command); }
                catch (Exception ex) { response = $"ERR {ex.Message}"; }
                await writer.WriteLineAsync(response).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (!_shutdown.IsCancellationRequested)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex) when (!_shutdown.IsCancellationRequested)
            {
                Console.Error.WriteLine($"[IPC] Command server failed: {ex.Message}");
                Thread.Sleep(100);
            }
        }
    }
}

public static class CommandPipeClient
{
    public static int Send(RendererCommand command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", RendererIpcNames.PipeName, PipeDirection.InOut);
            pipe.Connect(1500);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(RendererCommandProtocol.Serialize(command));
            string response = reader.ReadLine() ?? "ERR No response from UltraLightRenderer.";
            if (response.StartsWith("ERR ", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"[Command] {response[4..]}");
                return 3;
            }
            Console.WriteLine(response);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            Console.Error.WriteLine($"[Command] No running UltraLightRenderer instance accepted the command: {ex.Message}");
            return 3;
        }
    }
}
