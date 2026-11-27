using System.Text.Json;
using System.Security.Cryptography;
using Thanslator.Core;

namespace Thanslator.Translation;

public static class RecommendedLanguagePack
{
    public const string Id = "common-languages-m2m100-7c1b2620";
    public const string Source = "https://huggingface.co/jncraton/m2m100_418M-ct2-int8/tree/7c1b2620a4e58dacecbd8bf89cfd6da7eb9eb7b0";
    private const string Download = "https://huggingface.co/jncraton/m2m100_418M-ct2-int8/resolve/7c1b2620a4e58dacecbd8bf89cfd6da7eb9eb7b0/";
    public static IReadOnlyList<ModelArtifact> Files { get; } = new[] {
        new ModelArtifact("config.json", Download + "config.json", 189, "4244772990e30069563e3ddfb4ad6dc95bdfd2ac3de667ea8858c9b0a8433fa8"),
        new ModelArtifact("model.bin", Download + "model.bin", 490667713, "d6703dd9f920ff896e45c3d97b490761bed5944937b90bbe6a7245f5652542d4"),
        new ModelArtifact("sentencepiece.bpe.model", Download + "sentencepiece.bpe.model", 2423393, "d8f7c76ed2a5e0822be39f0a4f95a55eb19c78f4593ce609e2edbc2aea4d380a"),
        new ModelArtifact("shared_vocabulary.json", Download + "shared_vocabulary.json", 2796509, "7eb5d0ff184c6095c7c10f9911c0aea492250abd12854f9c3d787c64b1c6397e"),
        new ModelArtifact("README.md", Download + "README.md", 4603, "1fd660f130aedc5ecf1796b47ab47d43d3c4b36541f5387b7adb3df75cb5dfdb")
    };

    public static async Task<string> EnsureAsync(HttpClient http, string downloadRoot,
        IProgress<string>? progress, CancellationToken token)
    {
        var root = Path.Combine(downloadRoot, "language-packs", Id);
        foreach (var file in Files) await ModelFileDownload.EnsureAsync(http, root, file, progress, token);
        var directions = LanguagePack.RecommendedLanguages.SelectMany(from => LanguagePack.RecommendedLanguages
            .Where(to => to != from).Select(to => new LanguageDirection(from, to, "."))).ToArray();
        var license=await File.ReadAllBytesAsync(Path.Combine(AppPaths.AssetsDirectory,"licenses","M2M100-fairseq-LICENSE.txt"),token);
        await File.WriteAllBytesAsync(Path.Combine(root,"LICENSE.txt"),license,token);
        var declarations=Files.Select(file=>new LanguagePackFile(file.Path,file.Size,file.Sha256))
            .Append(new("LICENSE.txt",license.Length,Convert.ToHexString(SHA256.HashData(license)))).ToArray();
        var manifest = new LanguagePackManifest(3, "ctranslate2", "m2m100", "常用语言离线包", "multidirectional",
            Source, "MIT", directions, declarations);
        var temporary = Path.Combine(root, "manifest.json.tmp");
        await File.WriteAllTextAsync(temporary, LanguagePack.Serialize(manifest), token);
        File.Move(temporary, Path.Combine(root, "manifest.json"), overwrite: true);
        _ = LanguagePack.Read(root, verifyHashes: true);
        return root;
    }
}
