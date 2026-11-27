using Microsoft.Data.Sqlite;
using Thanslator.Core;
using Thanslator.Storage;

namespace Thanslator.Tests;

public sealed class KnowledgeTests
{
    private static KnowledgeTerm Rule(string term, string translation, bool enabled = true) =>
        new(Guid.NewGuid().ToString("N"), term, translation, "", enabled);
    private static KnowledgeLibrary Library(string name, params KnowledgeTerm[] terms) =>
        new(Guid.NewGuid().ToString("N"), name, "智能感知工程", "en", "zh-Hans", terms);

    [Fact]
    public void Matching_PropagatesCancellationBeforeSearchingRules()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => KnowledgeRules.Match("point cloud", "en", "zh-Hans",
            [Library("感知", Rule("point cloud", "点云"))], canceled.Token));
    }

    [Fact]
    public async Task SelectedLibraries_PreserveExplicitOrderAcrossRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jianyi-selected-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SettingsRepository(path);
            await repository.InitializeAsync();
            await repository.SaveAsync(new AppSettings { KnowledgeLibraryIds = ["second", "first"] });
            Assert.Equal(new[] { "second", "first" }, (await new SettingsRepository(path).LoadAsync()).KnowledgeLibraryIds);
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public void Import_HandlesBomQuotesMultilineAndReorderedColumns()
    {
        var preview = KnowledgeTermImport.Parse("\uFEFFtranslation,term,note\r\n点云,point cloud,\"first, second\nline\"\r\n");
        Assert.True(preview.CanImport);
        var term = Assert.Single(preview.Terms);
        Assert.Equal("point cloud", term.Term);
        Assert.Contains("first, second", term.Note);
        Assert.Contains("line", term.Note);
        Assert.Equal("点云", term.Translation);
        Assert.True(KnowledgeTermImport.Parse("term\ttranslation\nsensor fusion\t传感器融合", '\t').CanImport);
    }

    [Fact]
    public void Import_ReportsPhysicalLineAndRejectsDuplicatesAgainstExistingRules()
    {
        var preview = KnowledgeTermImport.Parse("term,translation\n\"point cloud\",点云\nPOINT   CLOUD,云\ncat,\n", existing: [Rule("point cloud", "点云")]);
        Assert.False(preview.CanImport);
        Assert.Equal(new long[] { 2, 3, 4 }, preview.Errors.Select(e => e.Line));
    }

    [Theory]
    [InlineData("term,note\ncat,note")]
    [InlineData("term,translation\ncat")]
    [InlineData("term,translation\n\"cat,猫")]
    [InlineData("term,translation,term\na,b,c")]
    public void Import_InvalidFilesCannotBeConfirmed(string content) =>
        Assert.False(KnowledgeTermImport.Parse(content).CanImport);

    [Fact]
    public void Matching_UsesFullWordsLongPhrasesAndIndependentOccurrences()
    {
        var lib = Library("感知", Rule("cat", "猫"), Rule("point", "点"), Rule("point cloud", "点云"),
            Rule("sensor fusion", "传感器融合", false));
        var result = KnowledgeRules.Match("CATEGORY: POINT\nCLOUD and point; cat. sensor fusion", "auto", "zh-Hans", [lib]);
        Assert.Equal(new[] { "point cloud", "point", "cat" }, result.Hits.Select(h => h.Rule.Term));
        Assert.Empty(KnowledgeRules.Match("category", "en", "zh-Hans", [lib]).Hits);
    }

    [Fact]
    public void Matching_PriorityConflictsLanguageIsolationAndDeduplication()
    {
        var first = Library("首选", Rule("point cloud", "点云"));
        var second = Library("备选", Rule("POINT CLOUD", "点集合"));
        var duplicate = Library("同译法", Rule("point cloud", "点云"));
        var result = KnowledgeRules.Match("point cloud and point cloud", "en", "zh-Hans", [first, second, duplicate]);
        Assert.Equal(first.Id, Assert.Single(result.Hits).LibraryId);
        Assert.Equal(second.Id, Assert.Single(result.Conflicts).Alternative.LibraryId);
        Assert.Empty(KnowledgeRules.Match("point cloud", "ja", "zh-Hans", [first]).Hits);
        Assert.Empty(KnowledgeRules.Match("point cloud", "en", "ja", [first]).Hits);
        Assert.Empty(KnowledgeRules.Match("point cloud", "en", "zh-Hans", []).Hits);
    }

    [Fact]
    public async Task Storage_ReopensEditsAndKeepsLegacyData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jianyi-knowledge-{Guid.NewGuid():N}.db");
        try
        {
            var settings = new SettingsRepository(path);
            await settings.InitializeAsync();
            var legacy = new PersonalTermKey("en", "zh-Hans", "runtime");
            await settings.SavePersonalTermAsync(legacy, "运行时");
            var repository = new KnowledgeRepository(path);
            await repository.InitializeAsync();
            var term = Rule("sensor fusion", "传感器融合");
            var library = Library("智能感知", term);
            await repository.SaveAsync(library);
            var reopened = new KnowledgeRepository(path);
            await reopened.InitializeAsync();
            var loaded = Assert.Single(await reopened.LoadAsync());
            Assert.Equal(library.Id, loaded.Id);
            Assert.Equal(term, Assert.Single(loaded.Terms));
            await reopened.SaveAsync(loaded with { Terms = [term with { Translation = "多传感器融合", Enabled = false }] });
            var changed = Assert.Single((await repository.LoadAsync()).Single().Terms);
            Assert.False(changed.Enabled);
            Assert.Equal("多传感器融合", changed.Translation);
            Assert.Equal("运行时", (await settings.LoadPersonalTermsAsync())[legacy]);
            await repository.DeleteAsync(library.Id);
            Assert.Empty(await repository.LoadAsync());
            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM knowledge_terms;";
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public async Task Import_StalePreviewRollsBackWholeBatchWithoutOverwritingNewerRules()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jianyi-knowledge-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new KnowledgeRepository(path);
            await repository.InitializeAsync();
            var library = Library("感知");
            await repository.SaveAsync(library);
            var preview = KnowledgeTermImport.Parse("term,translation\npoint cloud,点云\nsensor fusion,融合\n");
            await repository.ImportAsync(library.Id, KnowledgeTermImport.Parse("term,translation\nsensor fusion,传感器融合\n"));
            await Assert.ThrowsAsync<SqliteException>(() => repository.ImportAsync(library.Id, preview));
            var saved = Assert.Single(Assert.Single(await repository.LoadAsync()).Terms);
            Assert.Equal("sensor fusion", saved.Term);
            Assert.Equal("传感器融合", saved.Translation);
            var invalid = KnowledgeTermImport.Parse("term,translation\ncat,猫\ncat,猫\n");
            await Assert.ThrowsAsync<ArgumentException>(() => repository.ImportAsync(library.Id, invalid));
            Assert.Single(Assert.Single(await repository.LoadAsync()).Terms);
            await Assert.ThrowsAsync<SqliteException>(() => repository.ImportAsync("missing", preview));
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }
}
