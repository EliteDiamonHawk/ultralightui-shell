using System.Security.Principal;

namespace UltraLightStartMenu;

internal static class StartMenuIpcNames
{
    internal static string MutexName => $"Local\\UltraLightStartMenu-{GetUserScope()}";
    internal const string PipeName = "UltraLightStartMenu.Commands";

    private static string GetUserScope() =>
        WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
}
