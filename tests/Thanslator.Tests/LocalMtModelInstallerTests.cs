using System.IO.Compression;
using Thanslator.Translation;

namespace Thanslator.Tests;

public sealed class LocalMtModelInstallerTests
{
    [Fact]
    public async Task InstallFromZipAsync_InstallsRequiredFilesAtomically()
    {
        var root = NewTempDirectory();
        try
        {
            var zip = Path.Combine(root, "model.zip");
            CreatePackage(zip, includeTopFolder: false);
            var target = Path.Combine(root, "installed");

            await LocalMtModelInstaller.InstallFromZipAsync(zip, target);

            Assert.True(LocalMtModelInstaller.VerifyDirectory(target));
            Assert.Equal("model", File.ReadAllText(Path.Combine(target, "model.bin")));
            Assert.Equal("source", File.ReadAllText(Path.Combine(target, "source.spm")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task InstallFromZipAsync_AcceptsOneTopLevelFolder()
    {
        var root = NewTempDirectory();
        try
        {
            var zip = Path.Combine(root, "model.zip");
            CreatePackage(zip, includeTopFolder: true);
            var target = Path.Combine(root, "installed");

            await LocalMtModelInstaller.InstallFromZipAsync(zip, target);

            Assert.True(LocalMtModelInstaller.VerifyDirectory(target));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task InstallFromZipAsync_RejectsPackageWithoutRequiredFiles()
    {
        var root = NewTempDirectory();
        try
        {
            var zip = Path.Combine(root, "bad.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                Add(archive, "README.md", "hello");
            }

            var target = Path.Combine(root, "installed");
            await Assert.ThrowsAsync<InvalidDataException>(
                () => LocalMtModelInstaller.InstallFromZipAsync(zip, target));
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task InstallFromZipAsync_DoesNotEscapeTargetViaZipSlip()
    {
        var root = NewTempDirectory();
        try
        {
            var zip = Path.Combine(root, "model.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                foreach (var file in LocalMtModelInstaller.RequiredFiles)
                {
                    Add(archive, file, file);
                }

                Add(archive, "../evil.txt", "bad");
            }

            var target = Path.Combine(root, "installed");
            await LocalMtModelInstaller.InstallFromZipAsync(zip, target);

            Assert.False(File.Exists(Path.Combine(root, "evil.txt")));
            Assert.True(LocalMtModelInstaller.VerifyDirectory(target));
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static void CreatePackage(string path, bool includeTopFolder)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in LocalMtModelInstaller.RequiredFiles)
        {
            Add(archive, includeTopFolder ? $"opus-mt-en-zh/{file}" : file, Path.GetFileNameWithoutExtension(file));
        }
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static string NewTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "thanslator-model-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
