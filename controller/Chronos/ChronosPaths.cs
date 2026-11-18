namespace Chronos;

internal sealed record ChronosPaths(
    string Root,
    string ChronosRoot,
    string StateFile,
    string SettingsFile,
    string BackendRegistryFile,
    string LogsDirectory)
{
    public static ChronosPaths Resolve()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = AppContext.BaseDirectory;

        string root = Path.Combine(localAppData, "KhaosShell");
        string chronosRoot = Path.Combine(root, "Chronos");
        return new ChronosPaths(
            root,
            chronosRoot,
            Path.Combine(root, "state.json"),
            Path.Combine(root, "settings.json"),
            Path.Combine(chronosRoot, "backend-registry.json"),
            Path.Combine(root, "logs"));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ChronosRoot);
        Directory.CreateDirectory(LogsDirectory);
    }
}
