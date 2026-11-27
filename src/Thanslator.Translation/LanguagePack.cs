using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Thanslator.Translation;

public sealed record LanguageDirection(string SourceLanguage, string TargetLanguage, string Path,
    string? SourcePrefix = null);
public sealed record LanguagePackFile(string Path, long Size, string Sha256);
public sealed record LanguagePackManifest(int FormatVersion, string Engine, string Architecture,
    string Name, string Kind, string Source, string License, LanguageDirection[] Directions,
    LanguagePackFile[] Files, string Version = "1");

/// <summary>A package declares only directions backed by an intact local model.</summary>
public static class LanguagePack
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true };
    public static readonly string[] RecommendedLanguages = ["zh-Hans", "en", "ja", "ko", "fr", "de", "es"];
    public static string[] RequiredFiles(string architecture) => architecture switch {
        "m2m100" => ["model.bin", "config.json", "shared_vocabulary.json", "sentencepiece.bpe.model"],
        "marian" => ["model.bin", "config.json", "shared_vocabulary.json", "source.spm", "target.spm"],
        _ => throw new InvalidDataException("语言包模型架构不受支持。")
    };

    public static string ResolvePath(string root, string relative, bool allowRoot = false)
    {
        if (allowRoot && relative == ".") return System.IO.Path.GetFullPath(root);
        if (string.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Replace('\\', '/').Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("语言包包含无效路径。");
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!full.StartsWith(System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("语言包路径超出安装目录。");
        return full;
    }

    public static LanguagePackManifest Parse(string text)
    {
        try
        {
            if (text.Length > 1024 * 1024) throw new InvalidDataException("语言包清单过大。");
            var pack = JsonSerializer.Deserialize<LanguagePackManifest>(text, Json)
                ?? throw new InvalidDataException("语言包清单为空。");
            if (pack.FormatVersion != 3 || pack.Engine != "ctranslate2" || pack.Kind is not ("bidirectional" or "multidirectional") ||
                string.IsNullOrWhiteSpace(pack.Name) || string.IsNullOrWhiteSpace(pack.Version) ||
                string.IsNullOrWhiteSpace(pack.Source) || string.IsNullOrWhiteSpace(pack.License) ||
                pack.Directions is not { Length: >= 2 and <= 10000 } || pack.Files is not { Length: >= 4 and <= 2000 })
                throw new InvalidDataException("语言包清单缺少版本、来源、许可或双向语言信息。");
            _ = RequiredFiles(pack.Architecture);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var file in pack.Files)
            {
                _ = ResolvePath(System.IO.Path.GetTempPath(), file.Path);
                if (!paths.Add(file.Path.Replace('\\', '/')) || file.Size <= 0 || file.Size > 4L * 1024 * 1024 * 1024 ||
                    file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit))
                    throw new InvalidDataException("语言包文件大小、哈希或路径无效。");
                total = checked(total + file.Size);
            }
            if (total > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("语言包超过 4 GB。");
            var pairs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var direction in pack.Directions)
            {
                if (!Thanslator.Core.MoreSettingsKnowledge.ValidLanguage(direction.SourceLanguage) ||
                    !Thanslator.Core.MoreSettingsKnowledge.ValidLanguage(direction.TargetLanguage) ||
                    direction.SourceLanguage == direction.TargetLanguage ||
                    !pairs.Add(direction.SourceLanguage + ">" + direction.TargetLanguage))
                    throw new InvalidDataException("语言包方向重复或无效。");
                _ = ResolvePath(System.IO.Path.GetTempPath(), direction.Path, true);
                foreach (var required in RequiredFiles(pack.Architecture))
                {
                    var path = direction.Path == "." ? required : direction.Path.Replace('\\', '/') + "/" + required;
                    if (!paths.Contains(path)) throw new InvalidDataException("语言包方向缺少配套模型文件。");
                }
            }
            if (pairs.Any(pair => !pairs.Contains(string.Join('>', pair.Split('>').Reverse()))) ||
                pack.Kind == "bidirectional" && pairs.Count != 2)
                throw new InvalidDataException("语言包必须包含每组语言的往返方向。");
            return pack;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NullReferenceException or OverflowException)
        { throw new InvalidDataException("语言包清单格式无效。", ex); }
    }

    public static string Serialize(LanguagePackManifest manifest) => JsonSerializer.Serialize(manifest, Json);

    public static LanguagePackManifest? Read(string root, bool verifyHashes = false)
    {
        var file = System.IO.Path.Combine(root, "manifest.json");
        if (!File.Exists(file)) return null;
        if(new FileInfo(file).Length>1024*1024)throw new InvalidDataException("语言包清单过大。");
        var text = File.ReadAllText(file);
        using (var json = JsonDocument.Parse(text))
        {
            var version=json.RootElement.EnumerateObject().FirstOrDefault(property=>property.Name.Equals("formatVersion",StringComparison.OrdinalIgnoreCase)).Value;
            if(version.ValueKind==JsonValueKind.Undefined)return null;
            if(version.ValueKind!=JsonValueKind.Number || !version.TryGetInt32(out var number))throw new InvalidDataException("语言包格式版本无效。");
            if(number!=3)return null;
        }
        var pack = Parse(text);
        foreach (var expected in pack.Files)
        {
            var path = ResolvePath(root, expected.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != expected.Size)
                throw new InvalidDataException("语言包文件缺失或大小不符：" + expected.Path);
            if (verifyHashes)
            {
                using var input = File.OpenRead(path);
                if (!Convert.ToHexString(SHA256.HashData(input)).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("语言包文件校验失败：" + expected.Path);
            }
        }
        return pack;
    }

    public static LocalMtOptions CreateOptions(string host, string root, string source, string target,
        string pipe = "Thanslator.LocalMt", int threads = 4)
    {
        var pack = Read(root);
        if (pack is null)
        {
            if (source is not ("auto" or "en") || target != "zh-Hans")
                throw new InvalidDataException("当前离线模型不包含所选翻译方向。");
            return new(host, root, pipe, threads);
        }
        var direction = pack.Directions.FirstOrDefault(pair => pair.SourceLanguage == source && pair.TargetLanguage == target)
            ?? throw new InvalidDataException("当前语言包未启用所选翻译方向。");
        var model = ResolvePath(root, direction.Path, true);
        using var config = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(model, "config.json")));
        var appendEos = config.RootElement.TryGetProperty("add_source_eos", out var eos) && eos.ValueKind == JsonValueKind.False;
        return new(host, model, pipe, threads, Architecture: pack.Architecture,
            SourceLanguage: source, TargetLanguage: target, SourcePrefix: direction.SourcePrefix ?? "", AppendSourceEos: appendEos);
    }

    public static async Task InstallZipAsync(string zipFile, string destination, CancellationToken token = default)
    {
        using var zip = ZipFile.OpenRead(zipFile);
        var manifests = zip.Entries.Where(entry => entry.FullName.Replace('\\', '/').Split('/') is var parts &&
            parts.Length <= 2 && parts[^1] == "manifest.json").ToArray();
        if (manifests.Length != 1 || zip.Entries.Count > 2500) throw new InvalidDataException("请选择包含一个清单的简译语言包。");
        if (manifests[0].Length > 1024 * 1024) throw new InvalidDataException("语言包清单过大。");
        using var reader = new StreamReader(manifests[0].Open());
        var text = await reader.ReadToEndAsync(token);
        var pack = Parse(text);
        var prefix = manifests[0].FullName[..^"manifest.json".Length].Replace('\\', '/');
        var parent = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination))!;
        var staging = System.IO.Path.Combine(parent, ".language-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var expected in pack.Files)
            {
                token.ThrowIfCancellationRequested();
                var entries = zip.Entries.Where(entry => entry.FullName.Replace('\\', '/') == prefix + expected.Path.Replace('\\', '/')).ToArray();
                if (entries.Length != 1 || entries[0].Length != expected.Size || (entries[0].ExternalAttributes >> 16 & 0xF000) == 0xA000)
                    throw new InvalidDataException("语言包文件缺失、重复或不符合清单。");
                var path = ResolvePath(staging, expected.Path);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                await ModelArchive.CopyEntryAsync(entries[0],path,expected.Size,token);
            }
            await File.WriteAllTextAsync(System.IO.Path.Combine(staging, "manifest.json"), text, token);
            _ = Read(staging, verifyHashes: true);
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(destination))
            {
                if (Directory.EnumerateFileSystemEntries(destination).Any()) throw new IOException("语言包目标目录已存在，原模型保持不变。");
                Directory.Delete(destination);
            }
            Directory.Move(staging, destination);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }
}
