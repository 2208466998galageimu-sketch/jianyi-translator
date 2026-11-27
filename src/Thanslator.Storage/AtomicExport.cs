using System.Text;

namespace Thanslator.Storage;

public static class AtomicExport
{
    public static async Task WriteTextAsync(string destination, string text, Encoding encoding, CancellationToken token)
    {
        destination = Path.GetFullPath(destination);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".jianyi-export-{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768, true))
            {
                created = true;
                await using (var writer = new StreamWriter(stream, encoding, 32768, leaveOpen: true))
                    await writer.WriteAsync(text.AsMemory(), token);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (created)
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
