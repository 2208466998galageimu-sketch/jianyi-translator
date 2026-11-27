using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Thanslator.Core;

namespace Thanslator.Storage;

public sealed record TranslationPreferenceSnapshot(JsonObject Document,long Revision);

public sealed class TranslationPreferenceRepository(string databasePath,TimeProvider? clock=null)
{
    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=databasePath}.ToString());
        try
        {
            await connection.OpenAsync(token);
            await using var command=connection.CreateCommand();
            command.CommandText="CREATE TABLE IF NOT EXISTS translation_preferences (id INTEGER PRIMARY KEY CHECK(id=1),revision INTEGER NOT NULL,payload TEXT NOT NULL,local_key BLOB NOT NULL);";
            await command.ExecuteNonQueryAsync(token);
            command.CommandText="INSERT OR IGNORE INTO translation_preferences VALUES(1,0,$state,$key);";
            command.Parameters.AddWithValue("$state",new JsonObject{["rules"]=new JsonArray(),["candidates"]=new JsonArray(),["scope"]="",["collect"]=false}.ToJsonString());
            command.Parameters.AddWithValue("$key",RandomNumberGenerator.GetBytes(32));
            await command.ExecuteNonQueryAsync(token);return connection;
        }
        catch{await connection.DisposeAsync();throw;}
    }

    public async Task<TranslationPreferenceSnapshot> LoadAsync(CancellationToken token=default)
    {
        await using var connection=await OpenAsync(token);await using var command=connection.CreateCommand();
        command.CommandText="SELECT payload,revision FROM translation_preferences WHERE id=1;";
        await using var reader=await command.ExecuteReaderAsync(token);await reader.ReadAsync(token);
        return new(JsonNode.Parse(reader.GetString(0))!.AsObject(),reader.GetInt64(1));
    }

    public async Task<long> SaveAsync(JsonObject document,long revision,CancellationToken token=default)
    {
        TranslationPreferences.Validate(document);
        if(document.ToJsonString().Length>1024*1024)throw new ArgumentException("偏好记录过多，请先整理。");
        await using var connection=await OpenAsync(token);await using var command=connection.CreateCommand();
        command.CommandText="UPDATE translation_preferences SET payload=$state,revision=revision+1 WHERE id=1 AND revision=$revision;";
        command.Parameters.AddWithValue("$state",document.ToJsonString());command.Parameters.AddWithValue("$revision",revision);
        if(await command.ExecuteNonQueryAsync(token)!=1)throw new InvalidOperationException("偏好已更新，请重新打开该页面后保存。");
        return revision+1;
    }

    public async Task RecordEditAsync(string source,string target,string original,string before,string after,CancellationToken token)
    {
        var patterns=TranslationPreferences.DetectEdits(original,before,after);if(patterns.Count==0)return;
        var snapshot=await LoadAsync(token);if(snapshot.Document["collect"]!.GetValue<bool>()!=true||snapshot.Document["enabled"]?.GetValue<bool>()==false)return;
        await using var connection=await OpenAsync(token);await using var command=connection.CreateCommand();
        command.CommandText="SELECT local_key FROM translation_preferences WHERE id=1;";
        var key=(byte[])(await command.ExecuteScalarAsync(token))!;
        var digest=Convert.ToHexString(HMACSHA256.HashData(key,Encoding.UTF8.GetBytes(source+">"+target+"|"+KnowledgeRules.Normalize(original))));
        var day=(clock??TimeProvider.System).GetLocalNow().ToString("yyyy-MM-dd");
        var candidates=snapshot.Document["candidates"]!.AsArray();
        foreach(var pattern in patterns)
        {
            var scope=MoreSettingsKnowledge.Text(snapshot.Document,"scope");
            var candidate=candidates.FirstOrDefault(row=>MoreSettingsKnowledge.Text(row,"rule")==pattern &&
                MoreSettingsKnowledge.Text(row,"source")==source && MoreSettingsKnowledge.Text(row,"target")==target && MoreSettingsKnowledge.Text(row,"scope")==scope);
            if(candidate is null)
            {
                if(candidates.Count>=300)continue;
                candidate=new JsonObject{["id"]=Guid.NewGuid().ToString("N"),["source"]=source,["target"]=target,["scope"]=scope,["rule"]=pattern,["samples"]=new JsonArray(),["status"]="pending"};
                candidates.Add(candidate);
            }
            if(MoreSettingsKnowledge.Text(candidate,"status")!="pending")continue;
            var samples=candidate["samples"]!.AsArray();
            if(samples.Count<100 && !samples.Any(row=>MoreSettingsKnowledge.Text(row,"hash")==digest))samples.Add(new JsonObject{["hash"]=digest,["day"]=day});
            candidate["ready"]=samples.Count>=3 && samples.Select(row=>MoreSettingsKnowledge.Text(row,"day")).Distinct().Count()>=2;
        }
        await SaveAsync(snapshot.Document,snapshot.Revision,token);
    }
}
