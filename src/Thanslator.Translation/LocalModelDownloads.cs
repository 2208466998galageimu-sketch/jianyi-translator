using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Thanslator.Core;

namespace Thanslator.Translation;

public sealed record RecommendedLocalModel(string Tag, string Name, string Use, long DownloadBytes);

public sealed class LocalModelDownloads(HttpClient http)
{
    public static IReadOnlyList<RecommendedLocalModel> Recommended { get; } = Array.AsReadOnly(new[] {
        new RecommendedLocalModel("qwen2.5:1.5b", "Qwen2.5 · 1.5B", "下载体积较小", 1_000_000_000),
        new RecommendedLocalModel("qwen2.5:3b", "Qwen2.5 · 3B", "较小规格", 1_900_000_000),
        new RecommendedLocalModel("qwen2.5:7b", "Qwen2.5 · 7B", "中等规格", 4_700_000_000),
        new RecommendedLocalModel("qwen2.5:14b", "Qwen2.5 · 14B", "较高资源需求", 9_000_000_000) });

    public static Uri ResolveRoot(string address)
    {
        var endpoint = LlmTranslationClient.ResolveEndpoint(SentenceTranslationMode.LlmLocal, address);
        return new UriBuilder(endpoint) { Path = "/", Query = "", Fragment = "" }.Uri;
    }
    public async Task<IReadOnlyList<string>> ListAsync(string address, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await http.GetAsync(new Uri(ResolveRoot(address), "api/tags"), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(1024 * 1024, deadline.Token);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(deadline.Token));
        if (body.RootElement.ValueKind != JsonValueKind.Object ||
            !body.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new IOException("本机运行组件返回的模型列表格式不正确。");
        return models.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Object)
            .Where(m => !m.TryGetProperty("remote_host", out var host) || host.ValueKind == JsonValueKind.Null ||
                host.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(host.GetString()))
            .Where(m => !m.TryGetProperty("remote_model", out var model) || model.ValueKind == JsonValueKind.Null ||
                model.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(model.GetString()))
            .Where(m => m.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number &&
                size.TryGetInt64(out var bytes) && bytes > 0)
            .Select(m => m.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "")
            .Where(n => n.Length is > 0 and <= 160)
            .Distinct(StringComparer.Ordinal).ToArray();
    }
    public async Task DownloadAsync(string address, string tag, IProgress<string>? progress, CancellationToken token)
    {
        if (!Recommended.Any(m => m.Tag == tag)) throw new ArgumentException("请选择列表中的本地模型。");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ResolveRoot(address), "api/pull"))
            { Content = JsonContent.Create(new { model = tag, stream = true }) };
        using var connectionDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        connectionDeadline.CancelAfter(TimeSpan.FromSeconds(20));
        HttpResponseMessage opened;
        try { opened = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectionDeadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("连接本机运行组件超时，请重试。"); }
        using var response = opened;
        connectionDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);
        var success = false;
        long lastReport = 0;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task<string?> ReadStatusAsync()
        {
            idle.CancelAfter(TimeSpan.FromSeconds(60));
            try { return await reader.ReadLineAsync(idle.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new IOException("本机模型下载长时间没有进度，请重试。"); }
            finally { idle.CancelAfter(Timeout.InfiniteTimeSpan); }
        }
        while (await ReadStatusAsync() is { } line)
        {
            if (line.Length > 65536) throw new IOException("下载状态格式异常。");
            using var body = JsonDocument.Parse(line);
            var data = body.RootElement;
            if (data.ValueKind != JsonValueKind.Object) throw new IOException("本机模型下载状态格式异常。");
            if (data.TryGetProperty("error", out var failure))
            {
                var detail = failure.ValueKind == JsonValueKind.String ? failure.GetString() : null;
                if (detail?.Contains("redirect target not allowed", StringComparison.OrdinalIgnoreCase) == true &&
                    detail.Contains("resolves to non-public", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("模型文件地址被解析为非公网地址，Ollama 已拒绝下载。请检查 DNS 或代理设置后重试。");
                throw new IOException("模型服务报告下载失败，请检查网络和磁盘空间。");
            }
            if (!data.TryGetProperty("status", out var state) || state.ValueKind != JsonValueKind.String)
                throw new IOException("本机模型下载状态格式异常。");
            var status = state.GetString();
            if (status == "success") { success = true; break; }
            if (Environment.TickCount64 - lastReport < 400) continue;
            lastReport = Environment.TickCount64;
            var message = "正在下载或校验模型…";
            if (data.TryGetProperty("completed", out var completed) && completed.ValueKind == JsonValueKind.Number && completed.TryGetInt64(out var done) &&
                data.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number && total.TryGetInt64(out var bytes) && bytes > 0)
                message = $"当前文件：{Math.Clamp(done * 100.0 / bytes, 0, 100):F0}% · {done / 1048576.0:F0}/{bytes / 1048576.0:F0} MB";
            progress?.Report(message);
        }
        if (!success) throw new IOException("下载未完成，请重试。");
        try
        {
            if (!(await ListAsync(address, token)).Contains(tag, StringComparer.Ordinal))
                throw new IOException("模型尚未出现在本地列表，请重试。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("下载后检查本地模型列表超时，请重试。"); }
    }
}
