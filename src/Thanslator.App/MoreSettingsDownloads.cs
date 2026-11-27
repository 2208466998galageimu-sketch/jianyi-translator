using System.IO;
using System.Text.Json.Nodes;
using Thanslator.Capture;
using Thanslator.Core;
using Thanslator.Translation;

namespace Thanslator.App;

public sealed partial class MoreSettingsWindow
{
    private const long MinimumFreeSpace = 32L * 1024 * 1024;
    private ModelArtifact? _preparedRuntime;
    private string? _preparedRuntimeRoot;
    private readonly HashSet<(string Root, string Tag)> _localDownloads = [];

    private static string FullDownloadPath(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private static bool SameDownloadPath(string first, string second) =>
        first.Length > 0 && FullDownloadPath(first).Equals(FullDownloadPath(second), StringComparison.OrdinalIgnoreCase);

    private static ModelArtifact[] OcrFiles(JsonNode profile) =>
        profile["files"]!.AsArray().OfType<JsonObject>().Select(file =>
            new ModelArtifact(MoreSettingsKnowledge.Text(file, "path"), MoreSettingsKnowledge.Text(file, "url"),
                file["size"]!.GetValue<long>(), MoreSettingsKnowledge.Text(file, "sha256"))).ToArray();

    private static JsonObject DescribeFiles(string root, IReadOnlyList<ModelArtifact> files)
    {
        try
        {
            var footprint = ModelFileDownload.Inspect(root, files);
            return new() { ["totalBytes"] = footprint.TotalBytes, ["remainingBytes"] = footprint.RemainingBytes,
                ["cached"] = footprint.Cached, ["canResume"] = footprint.CanResume };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new() { ["totalBytes"] = files.Sum(file => file.Size), ["unavailable"] = true };
        }
    }

    private JsonArray OcrRecommendations(string root)
    {
        var profiles = OcrCatalog();
        foreach (var profile in profiles.OfType<JsonObject>())
            profile["download"] = DescribeFiles(Path.Combine(root, "ocr", MoreSettingsKnowledge.Text(profile, "id")), OcrFiles(profile));
        return profiles;
    }

    private JsonArray LocalRecommendations(string root) => new(LocalModelDownloads.Recommended.Select(model =>
        (JsonNode)new JsonObject { ["tag"] = model.Tag, ["name"] = model.Name, ["use"] = model.Use,
            ["downloadBytes"] = model.DownloadBytes, ["cached"] = _localDownloads.Contains((root, model.Tag)) }).ToArray());

    private static long? FreeBytes(string root)
    {
        try { return new DriveInfo(Path.GetPathRoot(FullDownloadPath(root))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private static RecommendedLocalModel RecommendedModel(string tag) =>
        LocalModelDownloads.Recommended.FirstOrDefault(item => item.Tag == tag)
        ?? throw new ArgumentException("请选择列表中的本机模型。");

    private async Task<JsonNode> PrepareRuntimeDownloadAsync(JsonObject data, CancellationToken token)
    {
        if (_settings.OfflineMode) throw new ArgumentException("离线模式下无法获取运行组件。");
        var tag = MoreSettingsKnowledge.Text(data, "tag");
        var model = tag.Length == 0 ? null : RecommendedModel(tag);
        var root = FullDownloadPath(DownloadDirectory());
        var artifact = await ManagedOllama.GetRuntimeArtifactAsync(root, token, reuseExtracted: model is not null,
            checkForUpdate: model is null && DownloadRuntimePath(root) is not null);
        var runtime = ManagedOllama.RuntimeDestination(root, artifact);
        var footprint = ModelFileDownload.Inspect(Path.Combine(root, "downloads"), [artifact]);
        var runtimeInstalled = File.Exists(runtime);
        var remaining = runtimeInstalled ? 0 : footprint.RemainingBytes;
        var modelBytes = model is null || _localDownloads.Contains((root, model.Tag)) ? 0 : model.DownloadBytes;
        _preparedRuntime = artifact;
        _preparedRuntimeRoot = root;
        return new JsonObject { ["runtimeBytes"] = remaining, ["modelBytes"] = modelBytes,
            ["directory"] = root, ["freeBytes"] = FreeBytes(root), ["requiresExpansion"] = !runtimeInstalled };
    }

    private string? DownloadRuntimePath(string root)
    {
        var runtime = ManagedOllama.FindExecutable(_settings.OllamaRuntimePath);
        if (runtime is not null) return runtime;
        if (_preparedRuntime is not { } artifact || _preparedRuntimeRoot != root) return null;
        var cached = ManagedOllama.RuntimeDestination(root, artifact);
        return File.Exists(cached) ? cached : null;
    }

    private ModelArtifact PreparedRuntime(string root) =>
        _preparedRuntime is { } artifact && _preparedRuntimeRoot == root
            ? artifact : throw new ArgumentException("请先查看运行组件的下载量后再确认。");

    private async Task<JsonNode> InstallRuntimeAsync(CancellationToken token)
    {
        if (_settings.OfflineMode) throw new ArgumentException("离线模式下无法下载运行组件。");
        var root = FullDownloadPath(DownloadDirectory());
        var runtime = await ManagedOllama.DownloadRuntimeAsync(root, DownloadProgress(), token, PreparedRuntime(root));
        token.ThrowIfCancellationRequested();
        var draft = _settings.Clone();
        draft.OllamaRuntimePath = runtime;
        await _saveModels(draft, null);
        return await ModelStateAsync(CancellationToken.None);
    }

    private void CheckDownloadSpace(string root, DownloadFootprint footprint)
    {
        if (_settings.OfflineMode && footprint.RemainingBytes > 0)
            throw new ArgumentException("离线模式下无法下载文件。");
        if (footprint.RemainingBytes > 0)
            ModelFileDownload.RequireSpace(root, footprint.RemainingBytes + MinimumFreeSpace);
    }

    private async Task<JsonNode> DownloadModelAsync(JsonObject data, CancellationToken token)
    {
        var model = RecommendedModel(MoreSettingsKnowledge.Text(data, "tag"));
        var root = FullDownloadPath(DownloadDirectory());
        var progress = DownloadProgress();
        var runtime = DownloadRuntimePath(root);
        var runtimePath = ManagedOllama.FindExecutable(_settings.OllamaRuntimePath) is null ? runtime : _settings.OllamaRuntimePath;
        if (runtime is null)
        {
            if (_settings.OfflineMode) throw new ArgumentException("离线模式下无法下载运行组件。");
            var artifact = PreparedRuntime(root);
            if (data["withRuntime"]?.GetValue<bool>() != true)
                throw new ArgumentException("请确认同时下载本机运行组件。");
            var remaining = File.Exists(ManagedOllama.RuntimeDestination(root, artifact)) ? 0 :
                ModelFileDownload.Inspect(Path.Combine(root, "downloads"), [artifact]).RemainingBytes;
            ModelFileDownload.RequireSpace(root, remaining + model.DownloadBytes + MinimumFreeSpace);
            runtimePath = runtime = await ManagedOllama.DownloadRuntimeAsync(root, progress, token, artifact);
        }

        progress.Report("正在检查本机模型…");
        var address = await ManagedOllama.Shared.EnsureAsync(root, runtime, token);
        using var http = LlmTranslationClient.CreateHttpClient(true);
        http.Timeout = Timeout.InfiniteTimeSpan;
        var downloader = new LocalModelDownloads(http);
        using (var listDeadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            listDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            IReadOnlyList<string> installed;
            try { installed = await downloader.ListAsync(address, listDeadline.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new IOException("检查本机已下载模型超时。"); }
            if (!installed.Contains(model.Tag, StringComparer.Ordinal))
            {
                _localDownloads.Remove((root, model.Tag));
                if (_settings.OfflineMode) throw new ArgumentException("此模型需要联网下载，离线模式已开启。");
                ModelFileDownload.RequireSpace(root, model.DownloadBytes + MinimumFreeSpace);
                await downloader.DownloadAsync(address, model.Tag, progress, token);
            }
        }
        _localDownloads.Add((root, model.Tag));

        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("llm" + root + model.Tag)))[..24];
        var record = new JsonObject { ["id"] = id, ["kind"] = "llm", ["name"] = model.Tag, ["model"] = model.Tag,
            ["address"] = address, ["storageRoot"] = root };
        var draft = _settings.Clone();
        draft.OllamaRuntimePath = runtimePath;
        progress.Report("文件已就绪，正在试译…");
        try { draft.LlmLocal = await TranslationSelection.CheckConnectionAsync(record, draft, token); }
        catch (LlmTranslationException ex)
        { throw new InvalidDataException("本机模型已下载，试译未通过。" + ex.Message, ex); }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException)
        { throw new InvalidDataException("本机模型已下载，试译未通过。可重试检查。", ex); }
        draft.TranslationMode = SentenceTranslationMode.LlmLocal;
        draft.ManagedLlmStorageRoot = root;
        return await CommitDownloadAsync(id, record, draft, token);
    }

    private async Task<JsonNode> CommitDownloadAsync(string id, JsonObject record, AppSettings draft, CancellationToken token)
    {
        record["lastCheck"] = "passed";
        record["lastCheckedAt"] = DateTimeOffset.UtcNow.ToString("O");
        DownloadProgress().Report("试运行通过，正在启用…");
        await _commitDataChange(() => _store.CommitDownloadedModelAsync(id, record, draft, token));
        return await ModelStateAsync(CancellationToken.None);
    }
}
