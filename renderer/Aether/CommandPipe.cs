using System.IO.Pipes;
using System.Text;

namespace Aether;

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
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                BoundedLine frame = await new BoundedLineReader(server).ReadAsync(
                    RendererCommandCodec.MaxFrameBytes,
                    _shutdown.Token).ConfigureAwait(false);
                if (frame.EndOfStream || frame.TooLarge ||
                    !RendererCommandProtocol.TryParse(frame.Text ?? "", out RendererCommand? command) || command == null)
                {
                    string message = frame.TooLarge ? "ERR Command frame exceeds 1 MiB." : "ERR Invalid runtime command.";
                    await writer.WriteLineAsync(message).ConfigureAwait(false);
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
                await Task.Delay(50, _shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!_shutdown.IsCancellationRequested)
            {
                Console.Error.WriteLine($"[IPC] Command server failed: {ex.Message}");
                await Task.Delay(100, _shutdown.Token).ConfigureAwait(false);
            }
        }
    }
}

public static class CommandPipeClient
{
    public static int Send(RendererCommand command)
    {
        if (TrySend(command, out int exitCode))
            return exitCode;

        Console.Error.WriteLine("[Command] No running Aether instance accepted the command.");
        return 3;
    }

    public static bool TrySend(RendererCommand command, out int exitCode)
    {
        exitCode = 3;
        try
        {
            using var pipe = new NamedPipeClientStream(".", RendererIpcNames.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(250);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(RendererCommandProtocol.Serialize(command));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            BoundedLine frame = new BoundedLineReader(pipe).ReadAsync(
                RendererCommandCodec.MaxFrameBytes,
                timeout.Token).GetAwaiter().GetResult();
            string response = frame.EndOfStream
                ? "ERR No response from Aether."
                : frame.TooLarge
                    ? "ERR Aether response exceeded 1 MiB."
                    : frame.Text ?? "ERR No response from Aether.";
            if (response.StartsWith("ERR ", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"[Command] {response[4..]}");
                exitCode = 3;
                return true;
            }
            if (!response.StartsWith("OK ", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"[Command] Invalid response from Aether: {response}");
                exitCode = 3;
                return true;
            }

            Console.WriteLine(response);
            exitCode = 0;
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            return false;
        }
    }
}
