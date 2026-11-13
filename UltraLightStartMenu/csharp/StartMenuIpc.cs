using System.IO.Pipes;

namespace UltraLightStartMenu;

public sealed class StartMenuInstance : IDisposable
{
    internal const string MutexName = "Local\\UltraLightStartMenu";
    internal const string PipeName = "UltraLightStartMenu.Commands";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _serverTask;
    private int _disposed;

    private StartMenuInstance(Mutex mutex) => _mutex = mutex;

    public static StartMenuInstance Acquire()
    {
        Mutex mutex = new(false, MutexName, out bool created);
        if (!created)
        {
            mutex.Dispose();
            throw new StartMenuInstanceAlreadyRunningException(
                "Another UltraLightStartMenu instance is already running.");
        }

        try
        {
            if (!mutex.WaitOne(TimeSpan.Zero))
            {
                mutex.Dispose();
                throw new StartMenuInstanceAlreadyRunningException(
                    "Another UltraLightStartMenu instance is already running.");
            }

            return new StartMenuInstance(mutex);
        }
        catch (AbandonedMutexException)
        {
            return new StartMenuInstance(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public static int SendCommand(StartMenuCommand command)
    {
        if (command is StartMenuCommand.Run)
            throw new StartMenuCommandException("The run command cannot be sent to another instance.");

        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect(2000);

            using var reader = new StreamReader(client);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(CommandName(command));
            string? response = reader.ReadLine();
            if (!string.Equals(response, "ok", StringComparison.OrdinalIgnoreCase))
                throw new StartMenuCommandException(response ?? "The running instance closed the command connection.");

            return 0;
        }
        catch (StartMenuCommandException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw new StartMenuCommandException(
                "No running UltraLightStartMenu instance accepted the command.");
        }
        catch (IOException ex)
        {
            throw new StartMenuCommandException(
                "No running UltraLightStartMenu instance accepted the command.", ex);
        }
    }

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
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { }
        _mutex.Dispose();
        _shutdown.Dispose();
    }

    internal static bool TryParseCommand(string? value, out StartMenuCommand command)
    {
        command = value?.Trim().ToLowerInvariant() switch
        {
            "show" => StartMenuCommand.Show,
            "hide" => StartMenuCommand.Hide,
            _ => StartMenuCommand.Run
        };
        return command is StartMenuCommand.Show or StartMenuCommand.Hide;
    }

    private async Task ServeAsync(Control dispatcher, Action<StartMenuCommand> execute)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(_shutdown.Token);
                using var reader = new StreamReader(server);
                using var writer = new StreamWriter(server) { AutoFlush = true };
                string? request = await reader.ReadLineAsync(_shutdown.Token);
                if (!TryParseCommand(request, out StartMenuCommand command))
                {
                    await writer.WriteLineAsync("error: expected show or hide");
                    continue;
                }

                try
                {
                    await DispatchToUiAsync(dispatcher, () => execute(command));
                    await writer.WriteLineAsync("ok");
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
            catch (IOException ex) when (!_shutdown.IsCancellationRequested)
            {
                Console.Error.WriteLine($"[IPC] named-pipe connection failed: {ex.Message}");
            }
        }
    }

    private static Task DispatchToUiAsync(Control dispatcher, Action action)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            dispatcher.BeginInvoke((Action)(() =>
            {
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

    private static string CommandName(StartMenuCommand command) => command switch
    {
        StartMenuCommand.Show => "show",
        StartMenuCommand.Hide => "hide",
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };
}

public sealed class StartMenuInstanceAlreadyRunningException : Exception
{
    public StartMenuInstanceAlreadyRunningException(string message) : base(message) { }
}

public sealed class StartMenuCommandException : Exception
{
    public StartMenuCommandException(string message) : base(message) { }
    public StartMenuCommandException(string message, Exception innerException) : base(message, innerException) { }
}
