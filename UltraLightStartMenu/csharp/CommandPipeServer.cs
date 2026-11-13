using System.IO.Pipes;

namespace UltraLightStartMenu;

public sealed class CommandPipeServer : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private NamedPipeServerStream? _activeServer;
    private Task? _serverTask;
    private int _disposed;

    public void Start(Control dispatcher, Action<StartMenuCommand> execute)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _serverTask = Task.Run(() => ServeAsync(dispatcher, execute));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdown.Cancel();
        _activeServer?.Dispose();
        if (_serverTask is { IsCompleted: false })
        {
            try
            {
                _serverTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(error => error is OperationCanceledException)) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[IPC] named-pipe shutdown failed: {ex.Message}");
            }
        }
    }

    private async Task ServeAsync(Control dispatcher, Action<StartMenuCommand> execute)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                StartMenuIpcNames.PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _activeServer = server;
            try
            {
                await server.WaitForConnectionAsync(_shutdown.Token);
                using var reader = new StreamReader(server);
                using var writer = new StreamWriter(server) { AutoFlush = true };
                string? request = await reader.ReadLineAsync(_shutdown.Token);
                if (!StartMenuCommandProtocol.TryParse(request, out StartMenuCommand command))
                {
                    await writer.WriteLineAsync("error: expected show or hide");
                    continue;
                }

                try
                {
                    await DispatchToUiAsync(dispatcher, () => execute(command), _shutdown.Token);
                    await writer.WriteLineAsync("ok");
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    await writer.WriteLineAsync($"error: {ex.Message}");
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (IOException ex) when (!_shutdown.IsCancellationRequested)
            {
                Console.Error.WriteLine($"[IPC] named-pipe connection failed: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_activeServer, server))
                    _activeServer = null;
            }
        }
    }

    private static Task DispatchToUiAsync(Control dispatcher, Action action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration cancellation = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        _ = completion.Task.ContinueWith(
            _ => cancellation.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            dispatcher.BeginInvoke((Action)(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                try
                {
                    action();
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }));
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
        return completion.Task;
    }
}
