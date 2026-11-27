using System.Security.Cryptography;

namespace Thanslator.Translation;

public static class ModelStorage
{
    public static bool Within(string path, string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return !full.Equals(parent, StringComparison.OrdinalIgnoreCase) && full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string ManagedDirectory(string root,string path)
    {
        var full=Path.GetFullPath(path);
        if(!Within(full,root))throw new ArgumentException("只能操作简译模型目录内的子目录，外部文件保留。");
        for(var current=new DirectoryInfo(full);current is not null;current=current.Parent)
            if(current.Exists&&(current.Attributes&FileAttributes.ReparsePoint)!=0)throw new ArgumentException("模型路径包含目录链接，已停止文件操作。");
        return full;
    }

    public static IReadOnlyList<FileInfo> Files(string directory,CancellationToken token)
    {
        if(!Directory.Exists(directory))return [];
        var files=new List<FileInfo>();var pending=new Stack<string>();pending.Push(directory);
        while(pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            var info=new DirectoryInfo(current);
            if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new ArgumentException("模型目录包含链接，未执行操作。");
            foreach(var item in info.EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if((item.Attributes&FileAttributes.ReparsePoint)!=0)throw new ArgumentException("模型目录包含链接，未执行操作。");
                if(item is DirectoryInfo folder)pending.Push(folder.FullName);else if(item is FileInfo file)files.Add(file);
                if(files.Count+pending.Count>100000)throw new ArgumentException("模型目录项目过多，请选择专用目录。");
            }
        }
        return files;
    }

    public static async Task CopyVerifiedAsync(string root, string destination, IEnumerable<string> directories,
        IProgress<string>? progress, CancellationToken token)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (root.Equals(destination, StringComparison.OrdinalIgnoreCase) || Within(root, destination) || Within(destination, root))
            throw new ArgumentException("新位置不能与原位置相同或互相包含。");
        var parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException("请选择专用的模型目录。");
        _ = ManagedDirectory(parent, destination);

        var files = directories.Select(directory => ManagedDirectory(root, directory)).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(directory => Files(directory, token)).DistinctBy(file => file.FullName, StringComparer.OrdinalIgnoreCase).ToArray();
        var relative = files.ToDictionary(file => Path.GetRelativePath(root, file.FullName), StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            // A verified copy can remain if the process stops before committing the references.
            var copies = Files(destination, token);
            if (copies.Count != relative.Count || copies.Any(file => !relative.ContainsKey(Path.GetRelativePath(destination, file.FullName))))
                throw new ArgumentException("目标文件夹已有其他内容，请选择空文件夹。");
            foreach (var copy in copies)
                if (!await SameContentAsync(relative[Path.GetRelativePath(destination, copy.FullName)], copy.FullName, token))
                    throw new ArgumentException("目标文件夹中的文件和原件不一致，请选择空文件夹。");
            return;
        }

        if (new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace < files.Sum(file => file.Length))
            throw new IOException("目标磁盘空间不足。");
        var staging = ManagedDirectory(parent, Path.Combine(parent, ".jianyi-migration-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(staging);
        var copiedPaths = new List<string>();
        try
        {
            foreach (var (entry, index) in relative.Select((entry, index) => (entry, index)))
            {
                token.ThrowIfCancellationRequested();
                var (name, original) = entry;
                var target = ManagedDirectory(staging, Path.Combine(staging, name));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                progress?.Report($"复制并校验 {index + 1}/{files.Length} · {original.Name}");
                await using (var input = original.OpenRead())
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                {
                    copiedPaths.Add(target);
                    await input.CopyToAsync(output, token);
                    await output.FlushAsync(token);
                    output.Flush(flushToDisk: true);
                }
                if (!await SameContentAsync(original, target, token))
                    throw new IOException("复制校验失败，原文件与配置保持不变。");
            }

            token.ThrowIfCancellationRequested();
            _ = ManagedDirectory(parent, destination);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: false);
            Directory.Move(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging)) DiscardStagedFiles(parent, staging, copiedPaths);
        }
    }

    private static void DiscardStagedFiles(string parent, string staging, IEnumerable<string> files)
    {
        try
        {
            _ = ManagedDirectory(parent, staging);
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { staging };
            foreach (var file in files)
            {
                var directory = ManagedDirectory(parent, Path.GetDirectoryName(file)!);
                if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.Delete(file);
                while (Within(directory, staging))
                {
                    folders.Add(directory);
                    directory = Path.GetDirectoryName(directory)!;
                }
            }
            foreach (var directory in folders.OrderByDescending(path => path.Length))
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory, recursive: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static async Task<bool> SameContentAsync(FileInfo original, string copy, CancellationToken token)
    {
        await using var input = original.OpenRead();
        await using var output = File.OpenRead(copy);
        if (input.Length != output.Length) return false;
        var originalHash = await SHA256.HashDataAsync(input, token);
        var copiedHash = await SHA256.HashDataAsync(output, token);
        return CryptographicOperations.FixedTimeEquals(originalHash, copiedHash);
    }
}
