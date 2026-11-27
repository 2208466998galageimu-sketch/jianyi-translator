using System.IO;

namespace Thanslator.App;

internal static class StartupLog
{
    private static readonly object Sync = new();
    private static readonly string LogPath = Path.Combine(
        Thanslator.Core.AppPaths.LogsDirectory,
        "startup.log");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Startup diagnostics must never take the app down.
        }
    }
}
