using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Thanslator.Core;
using Thanslator.Storage;

namespace Thanslator.Tests;

public sealed class ModelMigrationTests
{
    [Fact]
    public async Task DownloadedModelRecordAndActiveSelectionShareOneTransaction()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            var settings = new SettingsRepository(path);
            var store = new MoreSettingsRepository(path);
            await settings.InitializeAsync();
            await settings.SaveAsync(new AppSettings { LlmLocal = new("http://localhost:11434", "old"), Theme = "saved" });
            var selected = new AppSettings { TranslationMode = SentenceTranslationMode.LlmLocal,
                LlmLocal = new("http://localhost:11434", "new") };
            JsonObject record = new() { ["id"] = "new", ["kind"] = "llm", ["model"] = "new" };
            await using (var database = new SqliteConnection("Data Source=" + path))
            {
                await database.OpenAsync();
                await using var command = database.CreateCommand();
                command.CommandText = """
                    CREATE TRIGGER fail_activation BEFORE INSERT ON settings
                    WHEN NEW.key='llmLocal' BEGIN SELECT RAISE(ABORT,'fixture write failure'); END;
                    """;
                await command.ExecuteNonQueryAsync();
                await Assert.ThrowsAsync<SqliteException>(() => store.CommitDownloadedModelAsync("new", record, selected));
                command.CommandText = "DROP TRIGGER fail_activation;";
                await command.ExecuteNonQueryAsync();
            }
            Assert.Empty(await store.ModelsAsync());
            Assert.Equal("old", (await settings.LoadAsync()).LlmLocal.Model);
            var result = await store.CommitDownloadedModelAsync("new", record, selected);
            Assert.Equal("saved", result.Theme);
            Assert.Equal("new", (await settings.LoadAsync()).LlmLocal.Model);
            Assert.Single(await store.ModelsAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public async Task MigrationCommitsReferencesTogetherAndRejectsConcurrentChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            var settings = new SettingsRepository(path);
            var old = new AppSettings { ModelDownloadDirectory = "D:/original", LocalMtModelPath = "D:/original/mt" };
            var draft = new AppSettings { ModelDownloadDirectory = "D:/copied", LocalMtModelPath = "D:/copied/mt" };
            await settings.InitializeAsync();
            await settings.SaveAsync(old);
            var store = new MoreSettingsRepository(path);
            await store.SaveModelAsync("first", new() { ["id"] = "first", ["path"] = "D:/original/mt" });
            await store.SaveModelAsync("second", new() { ["id"] = "second", ["path"] = "D:/original/ocr" });
            var replacement = new Dictionary<string, JsonObject>
            {
                ["first"] = new() { ["id"] = "first", ["path"] = "D:/copied/mt" },
                ["second"] = new() { ["id"] = "second", ["path"] = "D:/copied/ocr" }
            };
            var before = await store.PreviewModelMigrationAsync();

            await ExecuteSqlAsync("""
                CREATE TRIGGER stop_second_model BEFORE UPDATE ON configured_models
                WHEN OLD.id='second' BEGIN SELECT RAISE(ABORT,'fixture write failure'); END;
                """);
            await Assert.ThrowsAsync<SqliteException>(() => store.CommitModelMigrationAsync(before, replacement, draft));
            var failed = await store.PreviewModelMigrationAsync();
            Assert.Equal(before.Models.OrderBy(row => row.Key), failed.Models.OrderBy(row => row.Key));
            Assert.Equal(before.PathSettings.OrderBy(row => row.Key), failed.PathSettings.OrderBy(row => row.Key));
            await ExecuteSqlAsync("DROP TRIGGER stop_second_model;");

            var current = await settings.LoadAsync();
            current.ModelDownloadDirectory = "D:/changed";
            await settings.SaveAsync(current);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitModelMigrationAsync(before, replacement, draft));

            var fresh = await store.PreviewModelMigrationAsync();
            current.Theme = "custom";
            await settings.SaveAsync(current);
            await store.CommitModelMigrationAsync(fresh, replacement, draft);
            var persisted = await settings.LoadAsync();
            Assert.Equal("D:/copied", persisted.ModelDownloadDirectory);
            Assert.Equal("D:/copied/mt", persisted.LocalMtModelPath);
            Assert.Equal("custom", persisted.Theme);
            Assert.All(await store.ModelsAsync(), model => Assert.StartsWith("D:/copied/", MoreSettingsKnowledge.Text(model, "path")));
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }

        async Task ExecuteSqlAsync(string sql)
        {
            await using var connection = new SqliteConnection("Data Source=" + path);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }
}
