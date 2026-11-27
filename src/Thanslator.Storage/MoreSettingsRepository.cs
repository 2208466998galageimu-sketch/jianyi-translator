using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Thanslator.Core;

namespace Thanslator.Storage;

public sealed record MoreSettingsSnapshot(JsonObject Document, long Revision);
public sealed record ModelMigrationSnapshot(IReadOnlyDictionary<string, string> Models,
    IReadOnlyDictionary<string, string?> PathSettings);

public sealed class MoreSettingsRepository(string databasePath)
{
    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        try
        {
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS more_settings (id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL, payload TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS knowledge_usage (library_id TEXT NOT NULL, day TEXT NOT NULL, source TEXT NOT NULL, target TEXT NOT NULL, hits INTEGER NOT NULL,
                    PRIMARY KEY(library_id,day,source,target));
                CREATE TABLE IF NOT EXISTS knowledge_requests (request TEXT NOT NULL, library_id TEXT NOT NULL, day TEXT NOT NULL, PRIMARY KEY(request,library_id));
                CREATE TABLE IF NOT EXISTS configured_models (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
                """;
            await command.ExecuteNonQueryAsync(token);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<MoreSettingsSnapshot> LoadAsync(string[] selected, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT payload,revision FROM more_settings WHERE id=1;";
            await using var reader = await command.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
                return new(JsonNode.Parse(reader.GetString(0))!.AsObject(), reader.GetInt64(1));
        }
        var legacy = await new KnowledgeRepository(databasePath).LoadAsync(token);
        var document = MoreSettingsKnowledge.Migrate(legacy, selected);
        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT OR IGNORE INTO more_settings VALUES(1,0,$data);";
        insert.Parameters.AddWithValue("$data", document.ToJsonString());
        await insert.ExecuteNonQueryAsync(token);
        await using var reload = connection.CreateCommand();
        reload.CommandText = "SELECT payload,revision FROM more_settings WHERE id=1;";
        await using var row = await reload.ExecuteReaderAsync(token);
        await row.ReadAsync(token);
        return new(JsonNode.Parse(row.GetString(0))!.AsObject(), row.GetInt64(1));
    }

    public async Task<long> SaveAsync(JsonObject document, long revision, CancellationToken token = default)
    {
        MoreSettingsKnowledge.Validate(document);
        var text = document.ToJsonString();
        if (text.Length > 32 * 1024 * 1024) throw new ArgumentException("知识库历史记录过大，请先导出并整理。");
        await using var connection = await OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE more_settings SET payload=$data,revision=revision+1 WHERE id=1 AND revision=$revision;";
        command.Parameters.AddWithValue("$data", text);
        command.Parameters.AddWithValue("$revision", revision);
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new InvalidOperationException("知识库已在其他窗口更新，请重新打开更多设置后重试。");
        return revision + 1;
    }

    private static readonly string[] ModelPathKeys =
        ["modelDownloadDirectory", "localMtModelPath", "ocrComponentPath", "ollamaRuntimePath", "managedLlmStorageRoot"];

    public async Task<IReadOnlyList<JsonObject>> ModelsAsync(CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        return (await ReadModelRowsAsync(connection, null, token)).Values.Select(value => JsonNode.Parse(value)!.AsObject()).ToArray();
    }

    public async Task<ModelMigrationSnapshot> PreviewModelMigrationAsync(CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var models = await ReadModelRowsAsync(connection, transaction, token);
        var settings = await SettingsRepository.ReadRowsAsync(connection, transaction, token);
        return new(models, ModelPathKeys.ToDictionary(key => key, key => settings.GetValueOrDefault(key)));
    }

    public async Task<AppSettings> CommitModelMigrationAsync(ModelMigrationSnapshot preview, IReadOnlyDictionary<string, JsonObject> replacements,
        AppSettings draft, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = connection.BeginTransaction();
        var models = await ReadModelRowsAsync(connection, transaction, token);
        var settings = await SettingsRepository.ReadRowsAsync(connection, transaction, token);
        if (models.Count != preview.Models.Count || models.Any(row => preview.Models.GetValueOrDefault(row.Key) != row.Value) ||
            ModelPathKeys.Any(key => preview.PathSettings.GetValueOrDefault(key) != settings.GetValueOrDefault(key)))
            throw new InvalidOperationException("模型配置在复制期间已有更新，请重新开始迁移。");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE configured_models SET payload=$payload WHERE id=$id;";
        var payload = command.Parameters.Add("$payload", SqliteType.Text);
        var id = command.Parameters.Add("$id", SqliteType.Text);
        foreach (var (key, value) in replacements)
        {
            if (!preview.Models.ContainsKey(key)) throw new ArgumentException("迁移不能新增模型档案。");
            payload.Value = value.ToJsonString();
            id.Value = key;
            if (await command.ExecuteNonQueryAsync(token) != 1) throw new InvalidOperationException("模型档案已变更。");
        }

        var replacementSettings = SettingsRepository.ToMap(draft);
        var changedPaths = ModelPathKeys.Where(key => replacementSettings[key] != settings.GetValueOrDefault(key))
            .ToDictionary(key => key, key => replacementSettings[key]);
        await SettingsRepository.WriteRowsAsync(connection, transaction, changedPaths, token);
        foreach (var (key, raw) in changedPaths) settings[key] = raw;
        var loaded = SettingsRepository.FromMap(settings);
        await transaction.CommitAsync(token);
        return loaded;
    }

    private static async Task<Dictionary<string, string>> ReadModelRowsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,payload FROM configured_models;";
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(token)) rows.Add(reader.GetString(0), reader.GetString(1));
        return rows;
    }
    public async Task<AppSettings> CommitDownloadedModelAsync(string id, JsonObject record, AppSettings draft,
        CancellationToken token = default)
    {
        string[] keys = MoreSettingsKnowledge.Text(record, "kind") switch
        {
            "mt" => ["translationMode", "localMtModelPath", "localMtEnabled", "sourceLang", "targetLang"],
            "llm" => ["translationMode", "llmLocal", "ollamaRuntimePath", "managedLlmStorageRoot"],
            "ocr" => ["ocrComponentPath"],
            _ => throw new ArgumentException("未知的下载模型类型。")
        };
        var values = SettingsRepository.ToMap(draft);
        await using var connection = await OpenAsync(token);
        await using var transaction = connection.BeginTransaction();
        var current = await SettingsRepository.ReadRowsAsync(connection, transaction, token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO configured_models VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET payload=$data;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$data", record.ToJsonString());
        await command.ExecuteNonQueryAsync(token);
        await SettingsRepository.WriteRowsAsync(connection, transaction,
            keys.Select(key => KeyValuePair.Create(key, values[key])), token);
        foreach (var key in keys) current[key] = values[key];
        var loaded = SettingsRepository.FromMap(current);
        await transaction.CommitAsync(token);
        return loaded;
    }

    public async Task SaveModelAsync(string id, JsonObject value, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO configured_models VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET payload=$data;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$data", value.ToJsonString());
        await command.ExecuteNonQueryAsync(token);
    }
    public async Task DeleteModelAsync(string id, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM configured_models WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(token);
    }
    public async Task<KnowledgeMatches> MatchAsync(string text, string source, string target, string[] selected,
        CancellationToken token, string? requestId = null)
    {
        requestId ??= Guid.NewGuid().ToString("N");
        if(requestId.Length is < 1 or > 128) throw new ArgumentException("无效请求编号。");
        var snapshot = await LoadAsync(selected, token);
        await FillUsageAsync(snapshot.Document, source, target, token);
        var libraries = MoreSettingsKnowledge.SelectedLibraries(snapshot.Document, source, target);
        var matches = KnowledgeRules.Match(text, source, target, libraries, token);
        var hits = libraries.GroupBy(library => library.Id)
            .Where(group => KnowledgeRules.Match(text, source, target, group.ToArray(), token).Hits.Count > 0)
            .Select(group => group.Key).ToArray();
        if (hits.Length > 0)
        {
            await using var connection = await OpenAsync(token);
            await using var transaction = connection.BeginTransaction();
            await using (var prune = connection.CreateCommand())
            {
                prune.Transaction=transaction;
                prune.CommandText="DELETE FROM knowledge_requests WHERE day < $day;";
                prune.Parameters.AddWithValue("$day",DateTime.UtcNow.AddDays(-29).ToString("yyyy-MM-dd"));
                await prune.ExecuteNonQueryAsync(token);
            }
            foreach (var id in hits)
            {
                await using var dedupe=connection.CreateCommand();
                dedupe.Transaction=transaction;
                dedupe.CommandText="INSERT OR IGNORE INTO knowledge_requests VALUES($request,$id,$day);";
                dedupe.Parameters.AddWithValue("$request",requestId+":"+source+":"+target);
                dedupe.Parameters.AddWithValue("$id",id);
                dedupe.Parameters.AddWithValue("$day",DateTime.UtcNow.ToString("yyyy-MM-dd"));
                if(await dedupe.ExecuteNonQueryAsync(token)==0)continue;
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO knowledge_usage VALUES($id,$day,$source,$target,1)
                    ON CONFLICT(library_id,day,source,target) DO UPDATE SET hits=hits+1;
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$day", DateTime.UtcNow.ToString("yyyy-MM-dd"));
                command.Parameters.AddWithValue("$source", source);
                command.Parameters.AddWithValue("$target", target);
                await command.ExecuteNonQueryAsync(token);
            }
            await transaction.CommitAsync(token);
        }
        var combo=snapshot.Document["presets"]!.AsArray().FirstOrDefault(item=>MoreSettingsKnowledge.Text(item,"id")==MoreSettingsKnowledge.Text(snapshot.Document,"active"));
        return KnowledgeChoices.ApplyPreferred(matches,combo,source,target) with { PresetId=MoreSettingsKnowledge.Text(snapshot.Document,"active") };
    }
    public async Task SavePreferredAsync(string presetId,string source,string target,KnowledgeHit choice,CancellationToken token)
    {
        var snapshot=await LoadAsync([],token);
        KnowledgeChoices.SavePreferred(snapshot.Document,presetId,source,target,choice);
        await SaveAsync(snapshot.Document,snapshot.Revision,token);
    }
    public async Task FillUsageAsync(JsonObject document, string source, string target, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT library_id,SUM(hits) FROM knowledge_usage WHERE day >= $day AND target=$target AND (source=$source OR source='auto') GROUP BY library_id;";
        command.Parameters.AddWithValue("$day", DateTime.UtcNow.AddDays(-29).ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$source", source == "auto" ? "en" : source);
        command.Parameters.AddWithValue("$target", target);
        var counts = new Dictionary<string,int>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) counts[reader.GetString(0)] = reader.GetInt32(1);
        foreach (var library in document["libraries"]!.AsArray())
            library!["hits"] = counts.GetValueOrDefault(MoreSettingsKnowledge.Text(library, "id"));
    }
}
