namespace UltraLightStartMenu;

internal static class StartMenuCommandProtocol
{
    internal static bool TryParse(string? value, out StartMenuCommand command)
    {
        command = value?.Trim().ToLowerInvariant() switch
        {
            "show" => StartMenuCommand.Show,
            "hide" => StartMenuCommand.Hide,
            _ => StartMenuCommand.Run
        };
        return command is StartMenuCommand.Show or StartMenuCommand.Hide;
    }

    internal static string Serialize(StartMenuCommand command) => command switch
    {
        StartMenuCommand.Show => "show",
        StartMenuCommand.Hide => "hide",
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };
}
