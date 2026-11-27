using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Thanslator.Core;

namespace Thanslator.Storage;

public sealed record BackupPreview(JsonObject Document, long KnowledgeRevision, long PreferencesRevision,
    IReadOnlyDictionary<string, string?> Settings);

public sealed class UserDataBackup(string databasePath)
{
    private static readonly string[] BooleanKeys = ["enabled", "literalMode", "includePhrases", "offlineMode", "clipboardFallback", "ocrEnabled"];
    private static readonly Dictionary<string, (int Min, int Max)> NumberKeys = new()
    {
        ["dwellMs"] = (200, 1000), ["panelAutoCloseMs"] = (0, 86400000), ["ocrShortWords"] = (1, 10000), ["ocrMediumWords"] = (2, 10000),
        ["ocrShortSeconds"] = (1, 86400), ["ocrMediumSeconds"] = (1, 86400), ["ocrLongSeconds"] = (1, 86400)
    };
    private static bool IsBackupSetting(string key) => BooleanKeys.Contains(key) || NumberKeys.ContainsKey(key) ||
        key is "sourceLang" or "targetLang" or "surfaceMode";

    public async Task<JsonObject> CreateAsync(CancellationToken token = default, ICollection<string>? skippedSettings = null)
    {
        var knowledge = await new MoreSettingsRepository(databasePath).LoadAsync([], token);
        var preferences = await new TranslationPreferenceRepository(databasePath).LoadAsync(token);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        var settings = new JsonObject();
        foreach (var (key, raw) in await SettingsRepository.ReadRowsAsync(connection, null, token))
        {
            if (!IsBackupSetting(key)) continue;
            try
            {
                var node = JsonNode.Parse(raw);
                ValidateSetting(key, node);
                settings[key] = node;
            }
            catch (Exception error) when (error is ArgumentException or JsonException)
            {
                skippedSettings?.Add(key);
            }
        }
        if (settings["ocrShortWords"] is { } shortWords && settings["ocrMediumWords"] is { } mediumWords &&
            shortWords.GetValue<int>() >= mediumWords.GetValue<int>())
        {
            settings.Remove("ocrShortWords");
            settings.Remove("ocrMediumWords");
            skippedSettings?.Add("ocrShortWords");
            skippedSettings?.Add("ocrMediumWords");
        }

        // Sample hashes depend on the database's local key.
        var prefs = new JsonObject
        {
            ["rules"] = preferences.Document["rules"]!.DeepClone(), ["scope"] = MoreSettingsKnowledge.Text(preferences.Document, "scope"),
            ["enabled"] = preferences.Document["enabled"]?.DeepClone() ?? JsonValue.Create(true),
            ["collect"] = preferences.Document["collect"]!.DeepClone(), ["candidates"] = new JsonArray()
        };
        return new()
        {
            ["format"] = "jianyi-user-data", ["version"] = 1, ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["knowledge"] = knowledge.Document.DeepClone(), ["preferences"] = prefs, ["settings"] = settings
        };
    }

    public static void Validate(JsonObject value)
    {
        try
        {
            if (MoreSettingsKnowledge.Text(value, "format") != "jianyi-user-data" || value["version"]?.GetValue<int>() != 1 ||
                value.ToJsonString().Length > 36 * 1024 * 1024) throw new ArgumentException("备份版本或文件大小无效。");
            MoreSettingsKnowledge.Validate(value["knowledge"]!.AsObject());
            TranslationPreferences.Validate(value["preferences"]!.AsObject());
            if (value["preferences"]!["candidates"]!.AsArray().Count != 0) throw new ArgumentException("备份不能包含采集样本。");
            var settings = value["settings"]!.AsObject();
            foreach (var (key, node) in settings) ValidateSetting(key, node);
            if (settings["ocrShortWords"] is { } shortWords && settings["ocrMediumWords"] is { } mediumWords &&
                shortWords.GetValue<int>() >= mediumWords.GetValue<int>()) throw new ArgumentException("备份长度分界无效。");
        }
        catch (Exception error) when (error is InvalidOperationException or NullReferenceException or FormatException)
        {
            throw new ArgumentException("备份结构无效，未修改任何数据。");
        }
    }

    public async Task<BackupPreview> PreviewAsync(JsonObject document, CancellationToken token = default)
    {
        Validate(document);
        // Load once to initialize or migrate old databases before opening the read snapshot.
        await new MoreSettingsRepository(databasePath).LoadAsync([], token);
        await new TranslationPreferenceRepository(databasePath).LoadAsync(token);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var revisions = connection.CreateCommand();
        revisions.Transaction = transaction;
        revisions.CommandText = "SELECT (SELECT revision FROM more_settings WHERE id=1), (SELECT revision FROM translation_preferences WHERE id=1);";
        long knowledgeRevision, preferencesRevision;
        await using (var reader = await revisions.ExecuteReaderAsync(token))
        {
            await reader.ReadAsync(token);
            knowledgeRevision = reader.GetInt64(0);
            preferencesRevision = reader.GetInt64(1);
        }
        var values = await SettingsRepository.ReadRowsAsync(connection, transaction, token);
        return new((JsonObject)document.DeepClone(), knowledgeRevision, preferencesRevision,
            document["settings"]!.AsObject().ToDictionary(row => row.Key, row => values.GetValueOrDefault(row.Key)));
    }

    public async Task<AppSettings> RestoreAsync(BackupPreview preview, CancellationToken token = default)
    {
        var document = preview.Document;
        Validate(document);
        var settings = document["settings"]!.AsObject();
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = connection.BeginTransaction();
        var current = await SettingsRepository.ReadRowsAsync(connection, transaction, token);
        if (preview.Settings.Count != settings.Count || settings.Any(row =>
            !preview.Settings.TryGetValue(row.Key, out var expected) || current.GetValueOrDefault(row.Key) != expected))
            throw new InvalidOperationException("基础设置已有更新，请重新预览备份后恢复。");

        if (settings.ContainsKey("ocrShortWords") || settings.ContainsKey("ocrMediumWords"))
        {
            int EffectiveBound(string key, int fallback) => settings[key]?.GetValue<int>() ??
                (current.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) &&
                    value >= NumberKeys[key].Min && value <= NumberKeys[key].Max ? value : fallback);
            if (EffectiveBound("ocrShortWords", 10) >= EffectiveBound("ocrMediumWords", 50))
                throw new ArgumentException("备份的长度分界与当前设置冲突，请调整后重试。");
        }

        foreach (var (table, field, revision) in new[] { ("more_settings", "knowledge", preview.KnowledgeRevision),
            ("translation_preferences", "preferences", preview.PreferencesRevision) })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"UPDATE {table} SET payload=$data,revision=revision+1 WHERE id=1 AND revision=$revision;";
            command.Parameters.AddWithValue("$data", document[field]!.ToJsonString());
            command.Parameters.AddWithValue("$revision", revision);
            if (await command.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidOperationException("数据已有更新，请重新预览备份后恢复。");
        }
        var restored = settings.ToDictionary(row => row.Key, row => row.Value!.ToJsonString());
        await SettingsRepository.WriteRowsAsync(connection, transaction, restored, token);
        foreach (var (key, raw) in restored) current[key] = raw;
        var loaded = SettingsRepository.FromMap(current);
        await transaction.CommitAsync(token);
        return loaded;
    }

    private static void ValidateSetting(string key, JsonNode? node)
    {
        if (BooleanKeys.Contains(key) && node?.GetValueKind() is JsonValueKind.True or JsonValueKind.False) return;
        if (NumberKeys.TryGetValue(key, out var bounds) && node is JsonValue value &&
            value.TryGetValue<int>(out var number) && number >= bounds.Min && number <= bounds.Max) return;
        if (node is JsonValue textNode && textNode.TryGetValue<string>(out var text))
        {
            if (key == "sourceLang" && (text == "auto" || MoreSettingsKnowledge.ValidLanguage(text))) return;
            if (key == "targetLang" && text != "auto" && MoreSettingsKnowledge.ValidLanguage(text)) return;
            if (key == "surfaceMode" && text is "tooltip" or "panel") return;
        }
        throw new ArgumentException("备份包含无效或不允许恢复的设置。");
    }

    private SqliteConnection CreateConnection() =>
        new(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
}
