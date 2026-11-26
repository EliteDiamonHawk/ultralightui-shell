namespace Chronos;

internal sealed record ChronosPaths(
    string Root,
    string ChronosRoot,
    string StateFile,
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
            Path.Combine(chronosRoot, "state.json"),
            Path.Combine(chronosRoot, "backend-registry.json"),
            Path.Combine(root, "logs"));
    }

    public string ThemeRegistryFile => Path.Combine(ChronosRoot, "themes.json");
    public string SettingsFile => Path.Combine(ChronosRoot, "settings.json");
    public string ThemesDirectory => Path.Combine(Root, "themes");
    public string NyxInstallationDirectory => Path.Combine(Root, "Nyx", "installation");
    public string NyxExecutableFile => Path.Combine(NyxInstallationDirectory, "Nyx.exe");
    public string BackendsDirectory => Path.Combine(Root, "backends");

    public string BackendChooserDirectory(string backendId)
    {
        string preferred = string.Equals(backendId.Trim(), "nyx", StringComparison.OrdinalIgnoreCase)
            ? NyxInstallationDirectory
            : BackendsDirectory;
        return Directory.Exists(preferred) ? preferred : Root;
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ChronosRoot);
        Directory.CreateDirectory(ThemesDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
