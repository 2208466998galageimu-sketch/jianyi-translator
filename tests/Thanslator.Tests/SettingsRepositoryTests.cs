using Microsoft.Data.Sqlite;
using Thanslator.Core;
using Thanslator.Storage;

namespace Thanslator.Tests;

public sealed class SettingsRepositoryTests
{
    [Fact]
    public async Task PersonalTerms_PersistIndependentlyByCaseAndLanguage()
    {
        var path = NewDatabasePath();
        try
        {
            var repository = new SettingsRepository(path);
            await repository.InitializeAsync();
            var key = PersonalTermKey.FromSelection("  Runtime   environment ", new())!.Value;
            await repository.SavePersonalTermAsync(key, "运行环境");
            await repository.SavePersonalTermAsync(key, "运行时环境");
            await repository.SavePersonalTermAsync(key with { TargetLang = "ja" }, "実行環境");
            await repository.SavePersonalTermAsync(key with { Term = "runtime environment" }, "运行环境");

            var reopened = new SettingsRepository(path);
            await reopened.InitializeAsync();
            var terms = await reopened.LoadPersonalTermsAsync();
            Assert.Equal("运行时环境", terms[key]);
            Assert.Equal("実行環境", terms[key with { TargetLang = "ja" }]);
            Assert.Equal("运行环境", terms[key with { Term = "runtime environment" }]);

            await reopened.SavePersonalTermAsync(key, null);
            terms = await repository.LoadPersonalTermsAsync();
            Assert.False(terms.ContainsKey(key));
            Assert.Equal(2, terms.Count);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void PersonalTerm_PrependsPreferenceAndKeepsOtherSenses()
    {
        var key = PersonalTermKey.FromSelection("runtime", new())!.Value;
        var entry = new DictionaryResult("runtime", "runtime", null,
            [new("n.", ["运行时间", "运行时"])], true, "ECDICT");
        var preferred = key.Apply(entry, "运行时");
        Assert.Equal("运行时", Assert.Single(preferred.Senses[0].Glosses));
        Assert.Equal("运行时间", Assert.Single(preferred.Senses[1].Glosses));
        Assert.True(key.Apply(DictionaryResult.NotFound("runtime", "未收录"), "运行时").Found);
        Assert.Null(PersonalTermKey.FromSelection("This is a sentence.", new()));
    }

    [Fact]
    public async Task LegacyTermReminderSettingDoesNotAffectSavedPersonalTerms()
    {
        var path = NewDatabasePath();
        try
        {
            var repository = new SettingsRepository(path);
            await repository.InitializeAsync();
            var key = new PersonalTermKey("en", "zh-hans", "race condition");
            await repository.SavePersonalTermAsync(key, "竞态条件");
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var seed = connection.CreateCommand();
                seed.CommandText = """
                    INSERT INTO settings VALUES ('termPromptEnabled', 'true', 1);
                    CREATE TABLE term_prompt_shown (term TEXT);
                    INSERT INTO term_prompt_shown VALUES ('race condition');
                    """;
                await seed.ExecuteNonQueryAsync();
            }
            var reopened = new SettingsRepository(path);
            await reopened.InitializeAsync();
            await reopened.SaveAsync(await reopened.LoadAsync());
            Assert.Equal("竞态条件", (await reopened.LoadPersonalTermsAsync())[key]);
            await using var verify = new SqliteConnection($"Data Source={path}");
            await verify.OpenAsync();
            await using var query = verify.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM term_prompt_shown;";
            Assert.Equal(1L, (long)(await query.ExecuteScalarAsync())!);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task LoadAsync_EmptyDatabase_ReturnsDefaults()
    {
        var path = NewDatabasePath();
        try
        {
            var repository = new SettingsRepository(path);
            await repository.InitializeAsync();

            var settings = await repository.LoadAsync();

            Assert.True(settings.Enabled);
            Assert.False(settings.LiteralMode);
            Assert.False(settings.IncludePhrases);
            Assert.Equal(SurfaceMode.Tooltip, settings.SurfaceMode);
            Assert.Equal(400, settings.DwellMs);
            Assert.Equal(0, settings.PanelAutoCloseMs);
            Assert.Equal("zh-Hans", settings.TargetLang);
            Assert.Equal(SentenceTranslationMode.LocalMt, settings.TranslationMode);
            Assert.False(settings.LocalMtEnabled);
            Assert.False(settings.OcrEnabled);
            Assert.Null(settings.OcrComponentPath);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task SaveAsync_RoundTripsAllTogglesAndPosition()
    {
        var path = NewDatabasePath();
        try
        {
            var repository = new SettingsRepository(path);
            await repository.InitializeAsync();

            var settings = new AppSettings
            {
                Enabled = false,
                LiteralMode = true,
                IncludePhrases = true,
                SurfaceMode = SurfaceMode.Panel,
                DwellMs = 650,
                PanelAutoCloseMs = 30_000,
                TargetLang = "ja",
                OfflineMode = true,
                TranslationMode = SentenceTranslationMode.LlmLocal,
                LlmApi = new("https://example.com/v1", "remote-model"),
                LlmLocal = new("http://127.0.0.1:11434/v1", "local-model"),
                LocalMtEnabled = true,
                OcrEnabled = true,
                OcrComponentPath = "E:\\portable-ocr",
                LocalMtModelId = "opus-mt-en-zh-int8",
                LocalMtModelStatus = LocalMtModelStatus.Ready,
                PopperLeft = 100.5,
                PopperTop = 200.25,
                PopperDockEdge = PopperDockEdge.Right,
                PopperDockOffset = 321.5,
                PopperDrawerOpen = true
            };

            await repository.SaveAsync(settings);
            var loaded = await repository.LoadAsync();

            Assert.False(loaded.Enabled);
            Assert.True(loaded.LiteralMode);
            Assert.True(loaded.IncludePhrases);
            Assert.Equal(SurfaceMode.Panel, loaded.SurfaceMode);
            Assert.Equal(650, loaded.DwellMs);
            Assert.Equal(30_000, loaded.PanelAutoCloseMs);
            Assert.Equal("ja", loaded.TargetLang);
            Assert.True(loaded.OfflineMode);
            Assert.Equal(SentenceTranslationMode.LlmLocal, loaded.TranslationMode);
            Assert.Equal(settings.LlmApi, loaded.LlmApi);
            Assert.Equal(settings.LlmLocal, loaded.LlmLocal);
            Assert.True(loaded.LocalMtEnabled);
            Assert.True(loaded.OcrEnabled);
            Assert.Equal("E:\\portable-ocr", loaded.OcrComponentPath);
            Assert.Equal("opus-mt-en-zh-int8", loaded.LocalMtModelId);
            Assert.Equal(LocalMtModelStatus.Ready, loaded.LocalMtModelStatus);
            Assert.Equal(100.5, loaded.PopperLeft);
            Assert.Equal(200.25, loaded.PopperTop);
            Assert.Equal(PopperDockEdge.Right, loaded.PopperDockEdge);
            Assert.Equal(321.5, loaded.PopperDockOffset);
            Assert.True(loaded.PopperDrawerOpen);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task SaveAsync_SecondWrite_UpdatesExistingKeys()
    {
        var path = NewDatabasePath();
        try
        {
            var repository = new SettingsRepository(path);
            await repository.InitializeAsync();
            var settings = new AppSettings { Enabled = true, SurfaceMode = SurfaceMode.Tooltip };
            await repository.SaveAsync(settings);

            settings.Enabled = false;
            settings.SurfaceMode = SurfaceMode.Panel;
            await repository.SaveAsync(settings);

            var loaded = await repository.LoadAsync();
            Assert.False(loaded.Enabled);
            Assert.Equal(SurfaceMode.Panel, loaded.SurfaceMode);
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static string NewDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "thanslator-tests", Guid.NewGuid().ToString("N"));
        return Path.Combine(directory, "thanslator.db");
    }

    private static void Cleanup(string path)
    {
        SqliteConnection.ClearAllPools();
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; the temp directory is disposable.
            }
        }
    }
}
