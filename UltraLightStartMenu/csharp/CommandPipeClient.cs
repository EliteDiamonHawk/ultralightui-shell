using System.IO.Pipes;

namespace UltraLightStartMenu;

public static class CommandPipeClient
{
    public static int Send(StartMenuCommand command)
    {
        if (command is StartMenuCommand.Run)
            throw new StartMenuCommandException("The run command cannot be sent to another instance.");

        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                StartMenuIpcNames.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect(2000);

            using var reader = new StreamReader(client);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(StartMenuCommandProtocol.Serialize(command));
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
}

public sealed class StartMenuCommandException : Exception
{
    public StartMenuCommandException(string message) : base(message) { }
    public StartMenuCommandException(string message, Exception innerException) : base(message, innerException) { }
}
