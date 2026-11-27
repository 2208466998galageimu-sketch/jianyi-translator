using System.IO.Compression;
using System.Security.Cryptography;

namespace Thanslator.Translation;

/// <summary>
/// Installs an OPUS-MT/CTranslate2 model from a ZIP package. Packages may
/// contain the required files at the root or inside one top-level folder.
/// </summary>
public static class LocalMtModelInstaller
{
    public static readonly string[] RequiredFiles =
    [
        "model.bin",
        "config.json",
        "shared_vocabulary.json",
        "source.spm",
        "target.spm"
    ];

    private static readonly HashSet<string> AllowedFiles = new(RequiredFiles, StringComparer.OrdinalIgnoreCase)
    {
        "LICENSE",
        "LICENSE.txt",
        "README.md",
        "THIRD_PARTY_NOTICES.md",
        "manifest.json"
    };

    public static bool VerifyDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        try { if (LanguagePack.Read(directory) is not null) return true; }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or IOException) { return false; }
        foreach (var file in RequiredFiles)
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                return false;
            }
        }

        return true;
    }

    public static async Task InstallFromZipAsync(
        string zipPath,
        string targetDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("模型包不存在", zipPath);
        }

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            var manifests = archive.Entries.Where(entry => entry.FullName.Split('/') is var parts && parts.Length <= 2 && parts[^1] == "manifest.json").ToArray();
            if (manifests.Length == 1 && manifests[0].Length <= 1024 * 1024)
            {
                using var reader = new StreamReader(manifests[0].Open());
                using var json = System.Text.Json.JsonDocument.Parse(await reader.ReadToEndAsync(cancellationToken));
                if (json.RootElement.TryGetProperty("formatVersion", out var version) && version.GetInt32() == 3)
                {
                    await LanguagePack.InstallZipAsync(zipPath, targetDirectory, cancellationToken);
                    return;
                }
            }
        }
        var parent = Path.GetDirectoryName(targetDirectory)
                     ?? throw new ArgumentException("模型目录无效", nameof(targetDirectory));
        Directory.CreateDirectory(parent);

        var staging = Path.Combine(parent, $".installing-{Guid.NewGuid():N}");
        var backup = Path.Combine(parent, $".backup-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(staging);
            await ExtractPackageAsync(zipPath, staging, progress, cancellationToken);

            if (!VerifyDirectory(staging))
            {
                throw new InvalidDataException("模型包缺少 model.bin/config.json/source.spm/target.spm 等必需文件");
            }

            if (Directory.Exists(targetDirectory))
            {
                Directory.Move(targetDirectory, backup);
            }

            Directory.Move(staging, targetDirectory);
            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }

            progress?.Report(1.0);
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                TryDelete(staging);
            }

            if (!Directory.Exists(targetDirectory) && Directory.Exists(backup))
            {
                Directory.Move(backup, targetDirectory);
            }

            throw;
        }
    }

    public static async Task<string> DownloadAndInstallAsync(
        HttpClient httpClient,
        string url,
        string targetDirectory,
        IProgress<double>? progress = null,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), $"thanslator-model-{Guid.NewGuid():N}.zip");
        try
        {
            using var response = await httpClient.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(
                             tempZip,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 1024,
                             useAsync: true))
            {
                var buffer = new byte[1024 * 1024];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;
                    if (total is > 0)
                    {
                        progress?.Report((double)copied / total.Value * 0.8);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var actual = ComputeSha256(tempZip);
                if (!actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"模型包 SHA-256 不匹配：期望 {expectedSha256}，实际 {actual}");
                }
            }

            await InstallFromZipAsync(tempZip, targetDirectory, progress, cancellationToken);
            return targetDirectory;
        }
        finally
        {
            TryDelete(tempZip);
        }
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static async Task ExtractPackageAsync(
        string zipPath,
        string staging,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(zipPath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var entries = archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name))
            .ToList();
        var allowedEntries = entries
            .Select(e => (Entry: e, Name: ResolveAllowedName(e.FullName)))
            .Where(x => x.Name is not null)
            .ToList();
        var total = allowedEntries.Sum(x => Math.Max(1L, x.Entry.Length));
        if(entries.Count>2500 || total>4L*1024*1024*1024)throw new InvalidDataException("模型包过大。");
        long copied = 0;

        foreach (var (entry, name) in allowedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(staging, name!);
            if (!destination.StartsWith(
                    Path.GetFullPath(staging) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"模型包包含非法路径: {entry.FullName}");
            }

            await ModelArchive.CopyEntryAsync(entry,destination,4L*1024*1024*1024,cancellationToken);
            copied += Math.Max(1L, entry.Length);
            progress?.Report((double)copied / total * 0.8);
        }

        if (allowedEntries.Count == 0)
        {
            throw new InvalidDataException("模型包中没有可识别的模型文件");
        }
    }

    private static string? ResolveAllowedName(string entryName)
    {
        var normalized = entryName.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains("..", StringComparison.Ordinal) ||
            normalized.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var file = parts.Length switch
        {
            1 => parts[0],
            2 => parts[1], // allow one top-level package folder
            _ => null
        };

        return file is not null && AllowedFiles.Contains(file) ? file : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
