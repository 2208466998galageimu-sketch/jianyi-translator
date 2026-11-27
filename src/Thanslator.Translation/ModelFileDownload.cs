using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Thanslator.Translation;

public sealed record ModelArtifact(string Path, string Url, long Size, string Sha256);
public sealed record DownloadFootprint(long TotalBytes, long RemainingBytes, bool CanResume)
{
    public bool Cached => RemainingBytes == 0 && !CanResume;
}

public static class ModelFileDownload
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    public static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        // Some artifact CDNs reject requests without an application identifier.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Jianyi/0.8.1");
        return client;
    }

    public static DownloadFootprint Inspect(string root, IEnumerable<ModelArtifact> files)
    {
        long total = 0, remaining = 0;
        var canResume = false;
        foreach (var file in files)
        {
            var path = LanguagePack.ResolvePath(root, file.Path);
            var available = ExistingBytes(path, file.Size);
            var partial = available == 0 ? ExistingBytes(path + ".download", file.Size, partial: true) : 0;
            total += file.Size;
            remaining += file.Size - available - partial;
            canResume |= partial > 0;
        }
        return new(total, remaining, canResume);
    }

    public static void RequireSpace(string root, long bytes)
    {
        if (bytes > 0 && new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace < bytes)
            throw new IOException("下载位置空间不足，请释放空间或更换下载位置。");
    }

    public static async Task EnsureAsync(HttpClient http, string root, ModelArtifact file,
        IProgress<string>? progress, CancellationToken token)
    {
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var url) || url.Scheme != "https" ||
            file.Size <= 0 || file.Size > 4L * 1024 * 1024 * 1024 || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("模型下载清单无效。");

        var target = LanguagePack.ResolvePath(root, file.Path);
        _ = ModelStorage.ManagedDirectory(root, target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".download";
        var furthestOffset = ExistingBytes(partial, file.Size, partial: true);
        var consecutiveStalls = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(target))
            {
                _ = ExistingBytes(target, file.Size);
                if (await MatchesAsync(target, file, token)) return;
                throw new InvalidDataException("已有模型文件校验失败，请更换下载位置或移除损坏文件后重试。");
            }

            var offset = ExistingBytes(partial, file.Size, partial: true);
            try
            {
                RequireSpace(root, file.Size - offset);
                if (offset < file.Size)
                    await ReceiveAsync(http, url, file, partial, offset, progress, token);
            }
            catch (Exception ex) when (IsRetryable(ex, token))
            {
                var received = ExistingBytes(partial, file.Size, partial: true);
                var advanced = received > furthestOffset;
                furthestOffset = Math.Max(furthestOffset, received);
                consecutiveStalls = advanced ? 0 : consecutiveStalls + 1;
                if (consecutiveStalls >= 3)
                    throw new IOException("下载中断，请检查网络后重试。已下载的部分会保留。", ex);
                progress?.Report(advanced ? "连接中断，正在续传…" : "连接中断，正在重试…");
                await Task.Delay(TimeSpan.FromMilliseconds(350 * Math.Max(1, consecutiveStalls)), token);
                continue;
            }

            progress?.Report($"正在校验 · {file.Path}");
            if (!await MatchesAsync(partial, file, token))
            {
                File.Delete(partial);
                throw new InvalidDataException("文件校验失败，请重新下载。当前模型仍可使用。");
            }
            token.ThrowIfCancellationRequested();
            File.Move(partial, target);
            return;
        }
    }

    private static long ExistingBytes(string path, long size, bool partial = false)
    {
        var info = new FileInfo(path);
        var attributes = info.Attributes;
        if (attributes != (FileAttributes)(-1) && (attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("下载文件位置包含链接，请选择其他目录。");
        if (!info.Exists) return 0;
        var bytes = info.Length;
        return partial && bytes <= size || bytes == size ? bytes : 0;
    }

    private static bool IsRetryable(Exception error, CancellationToken token) => !token.IsCancellationRequested &&
        (error is OperationCanceledException or EndOfStreamException ||
         error is HttpRequestException http && (http.StatusCode is null or HttpStatusCode.RequestTimeout
             or HttpStatusCode.TooManyRequests || http.StatusCode is { } status && (int)status >= 500));

    private static async Task ReceiveAsync(HttpClient http, Uri url, ModelArtifact file, string partial, long offset,
        IProgress<string>? progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(IdleTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
            throw new EndOfStreamException("服务器暂时拒绝续传，已保留下载进度。");
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { Scheme: not "https" })
            throw new InvalidDataException("下载地址跳转到了不安全的连接。");

        var responseStart = response.StatusCode == HttpStatusCode.OK ? 0 : offset;
        if (response.StatusCode != HttpStatusCode.OK && (response.StatusCode != HttpStatusCode.PartialContent ||
            response.Content.Headers.ContentRange is not { } range || range.From != offset ||
            range.Length != file.Size || range.To != file.Size - 1))
            throw new InvalidDataException("下载服务器返回了无效的续传范围。");
        if (response.Content.Headers.ContentLength is { } declared && declared != file.Size - responseStart)
            throw new InvalidDataException("服务器返回的文件大小与下载清单不符。");

        RequireSpace(Path.GetDirectoryName(partial)!, file.Size - offset);
        if (responseStart < offset)
            progress?.Report("服务器从头发送文件，正在跳过已保存的部分…");
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        await using var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Append,
            FileAccess.Write, FileShare.None, 131072, true);
        var buffer = new byte[131072];
        var total = responseStart;
        long reported = 0;
        while (true)
        {
            deadline.CancelAfter(IdleTimeout);
            int count;
            try { count = await input.ReadAsync(buffer, deadline.Token); }
            catch (IOException ex) { throw new EndOfStreamException("下载连接中断。", ex); }
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            if (count == 0) break;
            // A full response can reach the saved offset without overwriting the checkpoint.
            var alreadySaved = (int)Math.Clamp(offset - total, 0, count);
            total += count;
            if (total > file.Size) throw new InvalidDataException("下载文件大于清单声明的大小。");
            if (alreadySaved == count) continue;
            await output.WriteAsync(buffer.AsMemory(alreadySaved, count - alreadySaved), token);
            if (Environment.TickCount64 - reported > 400)
            {
                reported = Environment.TickCount64;
                progress?.Report($"{file.Path} · {total * 100 / file.Size}% · {total / 1048576.0:F1}/{file.Size / 1048576.0:F1} MiB");
            }
        }
        if (total != file.Size) throw new EndOfStreamException("下载中断，将从已下载位置继续。");
    }

    private static async Task<bool> MatchesAsync(string path, ModelArtifact file, CancellationToken token)
    {
        if (new FileInfo(path).Length != file.Size) return false;
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
