using System.IO;
using System.Text.Json.Nodes;
using Thanslator.App.Interop;
using Thanslator.Core;
using Thanslator.Storage;
using Thanslator.Translation;

namespace Thanslator.App;

internal static class TranslationSelection
{
    internal static readonly string[] AiLanguages = ["en", "zh-Hans", "ja", "ko", "fr", "de", "es"];

    internal static async Task<List<JsonObject>> LoadModelsAsync(
        MoreSettingsRepository store, AppSettings settings, CancellationToken token)
    {
        var models = (await store.ModelsAsync(token)).ToList();
        async Task Register(string id, JsonObject value)
        {
            var kind = MoreSettingsKnowledge.Text(value, "kind");
            if (models.Any(item => MoreSettingsKnowledge.Text(item, "id") == id ||
                MoreSettingsKnowledge.Text(item, "kind") == kind && (
                    kind == "llm" && !string.IsNullOrEmpty(settings.ManagedLlmStorageRoot) &&
                    MoreSettingsKnowledge.Text(item, "storageRoot") == settings.ManagedLlmStorageRoot &&
                    MoreSettingsKnowledge.Text(item, "model") == MoreSettingsKnowledge.Text(value, "model") ||
                    MoreSettingsKnowledge.Text(item, "path") == MoreSettingsKnowledge.Text(value, "path") &&
                    MoreSettingsKnowledge.Text(item, "address") == MoreSettingsKnowledge.Text(value, "address") &&
                    MoreSettingsKnowledge.Text(item, "model") == MoreSettingsKnowledge.Text(value, "model")))) return;
            value["id"] = id;
            await store.SaveModelAsync(id, value, token);
            models.Add(value);
        }

        var mtPath = settings.LocalMtModelPath ?? AppPaths.DefaultModelDirectory;
        if (settings.LocalMtEnabled && !models.Any(row => MoreSettingsKnowledge.Text(row, "kind") == "mt" &&
            SamePath(MoreSettingsKnowledge.Text(row, "path"), mtPath)) && LocalMtModelInstaller.VerifyDirectory(mtPath))
        {
            var keys = LanguagePack.Read(mtPath)?.Directions.Select(pair => pair.SourceLanguage + ":" + pair.TargetLanguage)
                .ToArray() ?? ["en:zh-Hans"];
            await Register("legacy-mt", new() { ["kind"] = "mt", ["name"] = "离线翻译", ["path"] = mtPath,
                ["keys"] = new JsonArray(keys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()) });
        }
        if (!string.IsNullOrWhiteSpace(settings.LlmApi.Model))
            await Register("legacy-api", new() { ["kind"] = "api", ["name"] = settings.LlmApi.Model,
                ["address"] = settings.LlmApi.Address, ["model"] = settings.LlmApi.Model });
        if (!string.IsNullOrWhiteSpace(settings.LlmLocal.Model))
            await Register("legacy-llm", new() { ["kind"] = "llm", ["name"] = settings.LlmLocal.Model,
                ["address"] = settings.LlmLocal.Address, ["model"] = settings.LlmLocal.Model,
                ["storageRoot"] = settings.ManagedLlmStorageRoot ?? "" });
        if (!string.IsNullOrWhiteSpace(settings.OcrComponentPath))
            await Register("legacy-ocr", new() { ["kind"] = "ocr", ["name"] = Path.GetFileName(settings.OcrComponentPath),
                ["path"] = settings.OcrComponentPath });
        return models;
    }

    internal static bool IsCurrent(JsonObject model, AppSettings settings)
    {
        var kind = MoreSettingsKnowledge.Text(model, "kind");
        if (kind == "mt")
            return settings.TranslationMode == SentenceTranslationMode.LocalMt && settings.LocalMtEnabled &&
                SamePath(MoreSettingsKnowledge.Text(model, "path"), settings.LocalMtModelPath ?? AppPaths.DefaultModelDirectory);

        var connection = kind switch
        {
            "llm" when settings.TranslationMode == SentenceTranslationMode.LlmLocal => settings.LlmLocal,
            "api" when settings.TranslationMode == SentenceTranslationMode.LlmApi => settings.LlmApi,
            _ => null
        };
        if (connection is null || MoreSettingsKnowledge.Text(model, "model") != connection.Model) return false;
        var root = MoreSettingsKnowledge.Text(model, "storageRoot");
        return kind == "llm" && (root.Length > 0 || !string.IsNullOrEmpty(settings.ManagedLlmStorageRoot))
            ? SamePath(root, settings.ManagedLlmStorageRoot)
            : MoreSettingsKnowledge.Text(model, "address") == connection.Address;
    }

    internal static (string Source, string Target)[] OfflineLanguages(JsonObject model) =>
        (model["keys"] is JsonArray keys ? keys.Select(key => key?.GetValue<string>() ?? "") : ["en:zh-Hans"])
        .Select(key => key.Split(':'))
        .Where(pair => pair.Length == 2 && pair[0] != pair[1] &&
            pair.All(code => MoreSettingsKnowledge.ValidLanguage(code) && code != "auto"))
        .Select(pair => (pair[0], pair[1])).Distinct().ToArray();

    internal static (string Source, string Target) OfflineDirection(JsonObject model, AppSettings settings)
    {
        var pairs = OfflineLanguages(model);
        if (pairs.Length == 0) throw new ArgumentException("此语言包没有可用的翻译方向。");
        var current = (settings.SourceLang, settings.TargetLang);
        var preferred = ("en", "zh-Hans");
        return pairs.Contains(current) ? current : pairs.Contains(preferred) ? preferred : pairs[0];
    }

    internal static AppSettings ChangeLanguages(
        AppSettings settings, IReadOnlyList<JsonObject> models, string source, string target)
    {
        if ((source != "auto" && !MoreSettingsKnowledge.ValidLanguage(source)) ||
            target == "auto" || !MoreSettingsKnowledge.ValidLanguage(target) || source == target)
            throw new ArgumentException("请选择不同的原文语言和译文语言。");
        if (settings.TranslationMode == SentenceTranslationMode.LocalMt)
        {
            var installed = models.FirstOrDefault(model => IsCurrent(model, settings));
            var path = settings.LocalMtModelPath ?? AppPaths.DefaultModelDirectory;
            if (installed is not null && !OfflineLanguages(installed).Contains((source, target)))
                throw new ArgumentException("当前语言包未启用所选方向。");
            _ = LanguagePack.CreateOptions(LocalMtOptions.DefaultHostPath(), path, source, target);
        }
        var draft = settings.Clone();
        draft.SourceLang = source;
        draft.TargetLang = target;
        return draft;
    }

    internal static async Task<AppSettings> ActivateAsync(JsonObject config, AppSettings settings, CancellationToken token)
    {
        var kind = MoreSettingsKnowledge.Text(config, "kind");
        if (kind == "mt")
        {
            var path = MoreSettingsKnowledge.Text(config, "path");
            if (!LocalMtModelInstaller.VerifyDirectory(path)) throw new ArgumentException("离线语言包文件不完整。");
            var (source, target) = OfflineDirection(config, settings);
            _ = LanguagePack.CreateOptions(LocalMtOptions.DefaultHostPath(), path, source, target);
            var draft = settings.Clone();
            draft.LocalMtModelPath = path;
            draft.LocalMtEnabled = true;
            draft.TranslationMode = SentenceTranslationMode.LocalMt;
            draft.SourceLang = source;
            draft.TargetLang = target;
            return draft;
        }

        if (kind is not ("api" or "llm")) throw new ArgumentException("请选择已配置的翻译模型。");
        var options = await CheckConnectionAsync(config, settings, token);
        var next = settings.Clone();
        if (kind == "api")
        {
            if (next.OfflineMode) throw new LlmTranslationException("离线模式已开启，无法使用云端 AI。");
            next.LlmApi = options;
            next.TranslationMode = SentenceTranslationMode.LlmApi;
        }
        else
        {
            next.LlmLocal = options;
            next.ManagedLlmStorageRoot = MoreSettingsKnowledge.Text(config, "storageRoot") is { Length: > 0 } root ? root : null;
            next.TranslationMode = SentenceTranslationMode.LlmLocal;
        }
        return next;
    }

    internal static async Task<LlmConnectionSettings> ResolveConnectionAsync(
        JsonObject config, AppSettings settings, CancellationToken token)
    {
        var root = MoreSettingsKnowledge.Text(config, "storageRoot");
        var address = root.Length == 0 ? MoreSettingsKnowledge.Text(config, "address")
            : await ManagedOllama.Shared.EnsureAsync(root, settings.OllamaRuntimePath, token);
        return new(address, MoreSettingsKnowledge.Text(config, "model"));
    }

    internal static async Task<LlmConnectionSettings> CheckConnectionAsync(
        JsonObject config, AppSettings settings, CancellationToken token)
    {
        var mode = MoreSettingsKnowledge.Text(config, "kind") == "api" ? SentenceTranslationMode.LlmApi : SentenceTranslationMode.LlmLocal;
        if (mode == SentenceTranslationMode.LlmApi && settings.OfflineMode)
            throw new LlmTranslationException("离线模式已开启，无法使用云端 AI。");
        var options = await ResolveConnectionAsync(config, settings, token);
        var endpoint = LlmTranslationClient.ResolveEndpoint(mode, options.Address);
        var key = MoreSettingsKnowledge.Text(config, "key");
        if (key.Length == 0) key = LlmCredentialStore.Read(endpoint) ?? "";
        using var http = LlmTranslationClient.CreateHttpClient(mode == SentenceTranslationMode.LlmLocal);
        _ = await new LlmTranslationClient(http).TranslateDocumentAsync(mode, options, key, "The connection is ready.", "auto", "zh-Hans", token);
        return options;
    }

    private static bool SamePath(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
