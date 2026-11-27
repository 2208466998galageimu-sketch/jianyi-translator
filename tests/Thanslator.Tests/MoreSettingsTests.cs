using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Thanslator.Core;
using Thanslator.Storage;

namespace Thanslator.Tests;

public sealed class MoreSettingsTests
{
    private static JsonObject Document() => MoreSettingsKnowledge.Migrate([
        new("first", "领域库", "说明", "en", "zh-Hans", [new("rule", "sensor", "传感器", "来源")]),
        new("second", "备选库", "", "en", "zh-Hans", [new("other", "sensor", "感应器")])
    ], ["first", "second"]);
    private static async Task<string> Database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jianyi-more-{Guid.NewGuid():N}.db");
        await new SettingsRepository(path).InitializeAsync();
        await new KnowledgeRepository(path).InitializeAsync();
        return path;
    }

    [Fact]
    public async Task Upgrade_PreservesOldTermsAndLibrarySelection_ThenPersistsIndependentState()
    {
        var path = await Database();
        try
        {
            var legacy = new KnowledgeRepository(path);
            await legacy.SaveAsync(new("first", "已有库", "", "en", "zh-Hans", [new("old", "sensor", "传感器")]));
            var settings = new SettingsRepository(path);
            var key = new PersonalTermKey("en", "zh-Hans", "runtime");
            await settings.SavePersonalTermAsync(key, "运行时");
            var repository = new MoreSettingsRepository(path);
            var snapshot = await repository.LoadAsync(["first"]);
            Assert.Equal("first", snapshot.Document["presets"]![0]!["ids"]![0]!.GetValue<string>());
            snapshot.Document["libraries"]![0]!["terms"]![0]!["target"] = "感应器";
            var revision = await repository.SaveAsync(snapshot.Document, snapshot.Revision);
            var reopened = await new MoreSettingsRepository(path).LoadAsync([]);
            Assert.Equal(revision, reopened.Revision);
            Assert.Equal("感应器", reopened.Document["libraries"]![0]!["terms"]![0]!["target"]!.GetValue<string>());
            Assert.Equal("传感器", (await legacy.LoadAsync())[0].Terms[0].Translation);
            Assert.Equal("运行时", (await settings.LoadPersonalTermsAsync())[key]);
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public async Task StaleOrInvalidSave_CannotOverwriteCommittedLibrary()
    {
        var path = await Database();
        try
        {
            var repository = new MoreSettingsRepository(path);
            var first = await repository.LoadAsync([]);
            var stale = await repository.LoadAsync([]);
            first.Document["libraries"] = Document()["libraries"]!.DeepClone();
            var revision = await repository.SaveAsync(first.Document, first.Revision);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(stale.Document, stale.Revision));
            first.Document["presets"]![0]!["ids"] = new JsonArray("missing");
            await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveAsync(first.Document, revision));
            var loaded = await repository.LoadAsync([]);
            Assert.Equal(2, loaded.Document["libraries"]!.AsArray().Count);
            Assert.Equal(revision, loaded.Revision);
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public void DeletionAndCombinationPriority_UseOnlyLiveCommittedLibraries()
    {
        var document = Document();
        var first = document["libraries"]![0]!;
        first["deletedAt"] = DateTime.UtcNow.ToString("O");
        var result = MoreSettingsKnowledge.SelectedLibraries(document, "en", "zh-Hans");
        Assert.Equal("second", Assert.Single(result).Id);
        first.AsObject().Remove("deletedAt");
        document["presets"]![0]!["mode"] = "frequency";
        first["hits"] = 3;
        document["libraries"]![1]!["hits"] = 10;
        Assert.Equal("second", MoreSettingsKnowledge.SelectedLibraries(document, "en", "zh-Hans")[0].Id);
        document["libraries"]![1]!["personal"] = true;
        Assert.Equal("first", MoreSettingsKnowledge.SelectedLibraries(document, "en", "zh-Hans")[0].Id);
        document["presets"]![0]!["deletedAt"] = "deleted";
        document["active"] = null;
        Assert.Empty(MoreSettingsKnowledge.SelectedLibraries(document, "en", "zh-Hans"));
        MoreSettingsKnowledge.Validate(document);
    }

    [Fact]
    public void MixedLanguagesAndReverseDirection_DoNotLeakAcrossLanguages()
    {
        var document = Document();
        var library = document["libraries"]![0]!;
        library["kind"] = "mixed";
        library["terms"]!.AsArray().Add(new JsonObject { ["source"] = "sensor", ["target"] = "センサー", ["direction"] = "en>ja" });
        library["terms"]!.AsArray().Add(new JsonObject { ["source"] = "传感器", ["target"] = "sensor", ["direction"] = "zh-Hans>en" });
        MoreSettingsKnowledge.Validate(document);
        var ja = MoreSettingsKnowledge.SelectedLibraries(document, "en", "ja");
        Assert.Equal("センサー", Assert.Single(Assert.Single(ja).Terms).Translation);
        var reverse = MoreSettingsKnowledge.SelectedLibraries(document, "zh-Hans", "en");
        Assert.Equal("sensor", Assert.Single(Assert.Single(reverse).Terms).Translation);
        Assert.Empty(MoreSettingsKnowledge.SelectedLibraries(document, "auto", "en"));
        library["terms"]!.AsArray().Add(library["terms"]![0]!.DeepClone());
        Assert.Throws<ArgumentException>(() => MoreSettingsKnowledge.Validate(document));
    }

    [Fact]
    public async Task UsageCountsLibrariesOncePerRequest_AndModelRecordsStaySeparate()
    {
        var path = await Database();
        try
        {
            var repository = new MoreSettingsRepository(path);
            var snapshot = await repository.LoadAsync([]);
            await repository.SaveAsync(Document(), snapshot.Revision);
            var matches = await repository.MatchAsync("sensor sensor", "en", "zh-Hans", [], CancellationToken.None);
            Assert.Single(matches.Hits);
            Assert.Single(matches.Conflicts);
            var loaded = await repository.LoadAsync([]);
            await repository.FillUsageAsync(loaded.Document, "en", "zh-Hans");
            Assert.All(loaded.Document["libraries"]!.AsArray(), library => Assert.Equal(1, library!["hits"]!.GetValue<int>()));
            await repository.FillUsageAsync(loaded.Document, "en", "ja");
            Assert.All(loaded.Document["libraries"]!.AsArray(), library => Assert.Equal(0, library!["hits"]!.GetValue<int>()));
            await repository.SaveModelAsync("translation", new() { ["id"] = "translation", ["kind"] = "llm", ["model"] = "one" });
            await repository.SaveModelAsync("assistant", new() { ["id"] = "assistant", ["kind"] = "assistant", ["model"] = "two" });
            await repository.DeleteModelAsync("assistant");
            Assert.Equal("translation", Assert.Single(await repository.ModelsAsync())["id"]!.GetValue<string>());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public async Task DownloadLocations_KeepPortableDefault_AndPersistExplicitChoice()
    {
        var path = await Database();
        try
        {
            var repository = new SettingsRepository(path);
            Assert.Null((await repository.LoadAsync()).ModelDownloadDirectory);
            var settings = await repository.LoadAsync();
            settings.ModelDownloadDirectory = @"D:自选模型 目录";
            settings.ManagedLlmStorageRoot = settings.ModelDownloadDirectory;
            settings.OllamaRuntimePath = @"D:简译untimesollama.exe";
            await repository.SaveAsync(settings);
            var loaded = await new SettingsRepository(path).LoadAsync();
            Assert.Equal(settings.ModelDownloadDirectory, loaded.ModelDownloadDirectory);
            Assert.Equal(settings.ManagedLlmStorageRoot, loaded.ManagedLlmStorageRoot);
            Assert.Equal(settings.OllamaRuntimePath, loaded.OllamaRuntimePath);
            loaded.ModelDownloadDirectory = null;
            await repository.SaveAsync(loaded);
            Assert.Null((await repository.LoadAsync()).ModelDownloadDirectory);
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public void ChineseAndJapaneseTerminology_MatchInsideSentencesWithoutSpaces()
    {
        var zh = new KnowledgeLibrary("zh", "中文词库", "", "zh-Hans", "en", [new("one", "传感器", "sensor")]);
        var ja = new KnowledgeLibrary("ja", "日本語", "", "ja", "en", [new("two", "センサー", "sensor")]);
        Assert.Single(KnowledgeRules.Match("这个传感器需要校准。", "zh-Hans", "en", [zh]).Hits);
        Assert.Single(KnowledgeRules.Match("センサーを確認します。", "ja", "en", [ja]).Hits);
    }

    [Fact]
    public void HistoricalSnapshots_ValidateBeforeTheyCanBeSavedOrRestored()
    {
        var document = Document();
        var library = document["libraries"]![0]!;
        library["versions"] = new JsonArray(new JsonObject {
            ["number"] = 1, ["action"] = "初始版本", ["terms"] = library["terms"]!.DeepClone()
        });
        MoreSettingsKnowledge.Validate(document);
        library["versions"]![0]!["terms"]![0]!["target"] = "";
        Assert.Throws<ArgumentException>(() => MoreSettingsKnowledge.Validate(document));
    }
}

