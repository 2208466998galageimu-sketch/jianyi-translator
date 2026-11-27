using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Thanslator.Core;
using Thanslator.Storage;

namespace Thanslator.Tests;

public sealed class UserDataBackupTests
{
    [Fact]
    public async Task BackupExcludesModelConnectionsAndSamples_RestoreIsAtomicAndRejectsStalePreview()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
        try
        {
            var settings=new SettingsRepository(path);await settings.InitializeAsync();await new KnowledgeRepository(path).InitializeAsync();
            await settings.SaveAsync(new AppSettings{LlmApi=new("https://private.example/v1","private-model"),OcrShortWords=12});
            var knowledge = new MoreSettingsRepository(path);
            var store=new UserDataBackup(path);var backup=await store.CreateAsync();
            Assert.DoesNotContain("private.example",backup.ToJsonString());Assert.DoesNotContain("local_key",backup.ToJsonString());
            Assert.Empty(backup["preferences"]!["candidates"]!.AsArray());
            backup["settings"]!["ocrShortWords"]=15;
            var preview = await store.PreviewAsync(backup);
            await store.RestoreAsync(preview);
            Assert.Equal(15,(await settings.LoadAsync()).OcrShortWords);
            Assert.Equal("private-model",(await settings.LoadAsync()).LlmApi.Model);
            var committed=await knowledge.LoadAsync([]);
            var current = await store.PreviewAsync(backup);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.RestoreAsync(current with { PreferencesRevision = preview.PreferencesRevision }));
            Assert.Equal(committed.Revision,(await knowledge.LoadAsync([])).Revision);
            Assert.Equal(15,(await settings.LoadAsync()).OcrShortWords);
        }
        finally{SqliteConnection.ClearAllPools();File.Delete(path);}
    }

    [Fact]
    public async Task RestoreRejectsOnlyChangesToSettingsThatWouldBeOverwritten()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            var settings = new SettingsRepository(path);
            await settings.InitializeAsync();
            await new KnowledgeRepository(path).InitializeAsync();
            await settings.SaveAsync(new AppSettings { DwellMs = 400, Theme = "dark" });
            var store = new UserDataBackup(path);
            var backup = await store.CreateAsync();
            backup["settings"] = new JsonObject { ["dwellMs"] = 500 };
            var preview = await store.PreviewAsync(backup);

            await settings.SaveAsync(new AppSettings { DwellMs = 750, Theme = "light" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.RestoreAsync(preview));
            var current = await store.PreviewAsync(backup);
            Assert.Equal(preview.KnowledgeRevision, current.KnowledgeRevision);
            Assert.Equal(preview.PreferencesRevision, current.PreferencesRevision);
            Assert.Equal(750, (await settings.LoadAsync()).DwellMs);

            await settings.SaveAsync(new AppSettings { DwellMs = 750, Theme = "system" });
            await store.RestoreAsync(current);
            var loaded = await settings.LoadAsync();
            Assert.Equal(500, loaded.DwellMs);
            Assert.Equal("system", loaded.Theme);
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public async Task ExportSkipsDamagedSettingsAndKeepsThePreviousFileOnCancellation()
    {
        var folder = Directory.CreateTempSubdirectory("jianyi-backup-");
        var path = Path.Combine(folder.FullName, "settings.db");
        var output = Path.Combine(folder.FullName, "backup.json");
        try
        {
            var settings = new SettingsRepository(path);
            await settings.InitializeAsync();
            await new KnowledgeRepository(path).InitializeAsync();
            await settings.SaveAsync(new AppSettings { DwellMs = 400, OfflineMode = true });
            await using (var db = new SqliteConnection("Data Source=" + path))
            {
                await db.OpenAsync();
                await using var command = db.CreateCommand();
                command.CommandText = "UPDATE settings SET value_json='broken' WHERE key='dwellMs';";
                await command.ExecuteNonQueryAsync();
            }
            var skipped = new List<string>();
            var backup = await new UserDataBackup(path).CreateAsync(skippedSettings: skipped);
            Assert.Equal(new[] { "dwellMs" }, skipped);
            Assert.False(backup["settings"]!.AsObject().ContainsKey("dwellMs"));
            Assert.True(backup["settings"]!["offlineMode"]!.GetValue<bool>());
            UserDataBackup.Validate(backup);

            await File.WriteAllTextAsync(output, "existing backup");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AtomicExport.WriteTextAsync(output, backup.ToJsonString(), System.Text.Encoding.UTF8, new CancellationToken(true)));
            Assert.Equal("existing backup", await File.ReadAllTextAsync(output));
            await AtomicExport.WriteTextAsync(output, backup.ToJsonString(), System.Text.Encoding.UTF8, CancellationToken.None);
            UserDataBackup.Validate(JsonNode.Parse(await File.ReadAllTextAsync(output))!.AsObject());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(output);
            File.Delete(path);
            folder.Delete();
        }
    }

    [Fact]
    public void InvalidTermStateIsRejectedInCurrentAndHistoricalTerms()
    {
        var term = new JsonObject { ["id"] = "term", ["source"] = "sensor", ["target"] = "传感器", ["direction"] = "en>zh-Hans" };
        var terms = new JsonArray(term);
        var historyTerm = (JsonObject)term.DeepClone();
        var document = MoreSettingsKnowledge.Migrate([], []);
        document["libraries"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "library", ["name"] = "测试库", ["kind"] = "mixed", ["terms"] = terms,
            ["versions"] = new JsonArray(new JsonObject { ["number"] = 1, ["terms"] = new JsonArray(historyTerm) })
        });
        MoreSettingsKnowledge.Validate(document);
        term["enabled"] = false;
        historyTerm["enabled"] = true;
        MoreSettingsKnowledge.Validate(document);
        term["enabled"] = "false";
        Assert.Throws<ArgumentException>(() => MoreSettingsKnowledge.Validate(document));
        term["enabled"] = true;
        historyTerm["enabled"] = 1;
        Assert.Throws<ArgumentException>(() => MoreSettingsKnowledge.Validate(document));
    }

    [Fact]
    public void UntrustedBackupCannotInjectCredentialsPathsOrInvalidRules()
    {
        var value=new JsonObject { ["format"]="jianyi-user-data",["version"]=1,["knowledge"]=MoreSettingsKnowledge.Migrate([],[]),
            ["preferences"]=new JsonObject{["rules"]=new JsonArray(),["candidates"]=new JsonArray(),["scope"]="",["collect"]=false},
            ["settings"]=new JsonObject{["modelDownloadDirectory"]="C:/"} };
        Assert.Throws<ArgumentException>(()=>UserDataBackup.Validate(value));
        value["settings"]=new JsonObject{["ocrShortSeconds"]=-1};Assert.Throws<ArgumentException>(()=>UserDataBackup.Validate(value));
    }
}
