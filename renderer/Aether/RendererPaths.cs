namespace Aether;

public sealed record RendererPaths(
    string Root,
    string RendererRoot,
    string UltralightFileSystemPath,
    string ExecutableBasePath,
    string SdkRoot,
    string LibraryDirectory,
    string ResourceSource,
    string LegacyWritablePath)
{
    public static RendererPaths Resolve(string? ultralightRoot)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = AppContext.BaseDirectory;

        string root = Path.Combine(localAppData, "KhaosShell");
        string executableBase = Path.GetFullPath(AppContext.BaseDirectory);
        string sdkRoot = string.IsNullOrWhiteSpace(ultralightRoot)
            ? executableBase
            : Path.GetFullPath(ultralightRoot);
        bool external = !string.IsNullOrWhiteSpace(ultralightRoot);

        return new RendererPaths(
            root,
            Path.Combine(root, "renderer"),
            Path.Combine(root, "renderer", "ultralight"),
            executableBase,
            sdkRoot,
            external ? Path.Combine(sdkRoot, "bin") : executableBase,
            external ? Path.Combine(sdkRoot, "resources") : Path.Combine(executableBase, "assets", "resources"),
            Path.Combine(executableBase, "assets"));
    }

    public void EnsureWritableLayout()
    {
        EnsureWritableRoot();
        string targetResources = Path.Combine(UltralightFileSystemPath, "resources");
        if (!Directory.Exists(ResourceSource))
            throw new DirectoryNotFoundException($"Ultralight resources source is missing: {ResourceSource}");

        try
        {
            CopyMissing(ResourceSource, targetResources);
            MigrateLegacyData(targetResources);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Could not provision Ultralight data from '{ResourceSource}' to '{UltralightFileSystemPath}': {ex.Message}", ex);
        }
    }

    public void EnsureWritableRoot()
    {
        Directory.CreateDirectory(UltralightFileSystemPath);
    }

    private void MigrateLegacyData(string targetResources)
    {
        if (!Directory.Exists(LegacyWritablePath) ||
            string.Equals(Path.GetFullPath(LegacyWritablePath), Path.GetFullPath(UltralightFileSystemPath), StringComparison.OrdinalIgnoreCase))
            return;

        string[] legacyEntries = Directory.EnumerateFileSystemEntries(LegacyWritablePath)
            .Where(entry => !string.Equals(Path.GetFullPath(entry), Path.GetFullPath(ResourceSource), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (legacyEntries.Length == 0)
            return;

        if (Directory.EnumerateFileSystemEntries(UltralightFileSystemPath)
            .Any(entry => !string.Equals(Path.GetFullPath(entry), Path.GetFullPath(Path.Combine(UltralightFileSystemPath, "resources")), StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"[Paths] Skipping legacy assets migration because '{UltralightFileSystemPath}' already contains user data.");
            return;
        }

        foreach (string entry in legacyEntries)
        {
            string target = Path.Combine(UltralightFileSystemPath, Path.GetFileName(entry));
            if (Directory.Exists(entry))
                CopyMissing(entry, target);
            else if (!File.Exists(target))
                File.Copy(entry, target);
        }
        Console.Error.WriteLine($"[Paths] Migrated legacy assets from '{LegacyWritablePath}' to '{UltralightFileSystemPath}'.");
    }

    private static void CopyMissing(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            string target = Path.Combine(destination, Path.GetFileName(file));
            if (!File.Exists(target))
                File.Copy(file, target);
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
            CopyMissing(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
