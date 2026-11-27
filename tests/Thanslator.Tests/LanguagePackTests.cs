using System.IO.Compression;
using System.Security.Cryptography;
using Thanslator.Translation;

namespace Thanslator.Tests;

public sealed class LanguagePackTests
{
    private static LanguagePackManifest Manifest() => new(3, "ctranslate2", "m2m100", "测试双向包", "bidirectional",
        "https://example.org/model", "MIT", [new("en", "zh-Hans", "."), new("zh-Hans", "en", ".")],
        LanguagePack.RequiredFiles("m2m100").Select(path => new LanguagePackFile(path, 1,
            Convert.ToHexString(SHA256.HashData(new byte[] { 1 })))).ToArray());

    [Fact]
    public void BidirectionalLabelCannotClaimAMissingReverseDirection()
    {
        var oneWay = Manifest() with { Directions = [new("en", "zh-Hans", ".")] };
        Assert.Throws<InvalidDataException>(() => LanguagePack.Parse(LanguagePack.Serialize(oneWay)));
    }

    [Theory]
    [InlineData("../outside/model.bin")]
    [InlineData("C:/outside/model.bin")]
    [InlineData("models/a:payload")]
    [InlineData("models/../model.bin")]
    public void RejectsManifestPathsOutsidePackage(string path)
    {
        var manifest = Manifest();
        manifest.Files[0] = manifest.Files[0] with { Path = path };
        Assert.Throws<InvalidDataException>(() => LanguagePack.Parse(LanguagePack.Serialize(manifest)));
    }

    [Fact]
    public async Task CorruptPackageDoesNotReplaceAnExistingModel()
    {
        var root = Path.Combine(Path.GetTempPath(), "jianyi-pack-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var zipFile = Path.Combine(root, "bad.zip");
            var destination = Path.Combine(root, "installed");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "old-model"), "preserve");
            using (var zip = ZipFile.Open(zipFile, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) writer.Write(LanguagePack.Serialize(Manifest()));
                foreach (var path in LanguagePack.RequiredFiles("m2m100"))
                    using (var stream = zip.CreateEntry(path).Open()) stream.WriteByte(2);
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => LanguagePack.InstallZipAsync(zipFile, destination));
            Assert.Equal("preserve", File.ReadAllText(Path.Combine(destination, "old-model")));
            Assert.Empty(Directory.GetDirectories(root, ".language-pack-*"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SharedCoreCanDeclareMultipleCompleteLanguagePairs()
    {
        var manifest = Manifest() with { Kind = "multidirectional", Directions = [new("en", "zh-Hans", "."),
            new("zh-Hans", "en", "."), new("ja", "en", "."), new("en", "ja", ".")] };
        Assert.Equal(4, LanguagePack.Parse(LanguagePack.Serialize(manifest)).Directions.Length);
    }
}
