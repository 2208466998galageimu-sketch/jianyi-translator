using System.IO.Compression;

namespace Thanslator.Translation;

public static class ModelArchive
{
    public static async Task<string> ExtractGgufAsync(string source, string destination, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(source);
        var models = zip.Entries.Where(entry => entry.Name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (zip.Entries.Count > 10000 || models.Length != 1)
            throw new InvalidDataException("模型 ZIP 应包含一个 GGUF 文件；分片模型暂不支持。");
        var model = models[0];
        if (model.Length < 24 || model.Length > 32L * 1024 * 1024 * 1024 || (model.ExternalAttributes >> 16 & 0xF000) == 0xA000)
            throw new InvalidDataException("GGUF 文件大小或类型无效。");
        Directory.CreateDirectory(destination);
        var outputPath = Path.Combine(destination, "model.gguf");
        await CopyEntryAsync(model,outputPath,32L*1024*1024*1024,token);
        await using var check = File.OpenRead(outputPath);
        var header = new byte[24];
        await check.ReadExactlyAsync(header, token);
        var version = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4,4));
        if (!header.AsSpan(0,4).SequenceEqual("GGUF"u8) || version is not (2 or 3))
            throw new InvalidDataException("GGUF 文件头或版本无效。");
        return outputPath;
    }

    public static async Task ExtractAsync(string source, string destination, CancellationToken token,
        long sizeLimit = 4L * 1024 * 1024 * 1024)
    {
        using var zip = ZipFile.OpenRead(source);
        if (zip.Entries.Count > 10000) throw new InvalidDataException("组件文件数量过多。");
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;
            size = checked(size + entry.Length);
            if (size > sizeLimit || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("组件包过大或包含符号链接。");
            var target = LanguagePack.ResolvePath(destination, entry.FullName);
            if (!targets.Add(target)) throw new InvalidDataException("组件包包含重复文件。");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await CopyEntryAsync(entry, target, sizeLimit, token);
        }
    }
    public static async Task CopyEntryAsync(ZipArchiveEntry entry,string target,long limit,CancellationToken token)
    {
        if(entry.Length<0 || entry.Length>limit || (entry.ExternalAttributes>>16 & 0xF000)==0xA000)throw new InvalidDataException("模型文件大小或类型无效。");
        await using var input=entry.Open();
        await using var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,true);
        var buffer=new byte[131072];long copied=0;int count;
        while((count=await input.ReadAsync(buffer,token))>0)
        {
            copied+=count;
            if(copied>entry.Length || copied>limit)throw new InvalidDataException("模型解压大小与声明不符。");
            await output.WriteAsync(buffer.AsMemory(0,count),token);
        }
        if(copied!=entry.Length)throw new InvalidDataException("模型未完整解压。");
    }

}
