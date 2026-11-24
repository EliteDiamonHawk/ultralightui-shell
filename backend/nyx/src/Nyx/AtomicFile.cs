using System.Text;

namespace KhaosShell.Nyx;

internal static class AtomicFile
{
    public static void WriteJson(string path, string content)
    {
        string directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("File directory is missing.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(true);
            }
            if (File.Exists(path))
            {
                try { File.Replace(temporary, path, null, true); }
                catch (PlatformNotSupportedException) { File.Move(temporary, path, true); }
            }
            else File.Move(temporary, path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
