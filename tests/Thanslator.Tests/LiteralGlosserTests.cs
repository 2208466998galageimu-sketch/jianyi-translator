using Thanslator.Core;
using Thanslator.Translation;

namespace Thanslator.Tests;

public sealed class LiteralGlosserTests
{
    [Fact]
    public async Task GlossAsync_PreservesOriginalOrderAndPunctuation()
    {
        using var fixture = await TestDictionaryFixture.CreateAsync();
        var glosser = fixture.CreateGlosser();

        var result = await glosser.GlossAsync("The quick brown fox jumps.");

        Assert.Equal("这 快的 棕色的 狐狸 跳.", result.GlossedText);
        Assert.Equal(5, result.WordCount);
        Assert.Equal(0, result.UnknownCount);
        Assert.Equal(
            ["The", " ", "quick", " ", "brown", " ", "fox", " ", "jumps", "."],
            result.Units.Select(u => u.Source));
        Assert.All(
            result.Units.Where(u => u.Kind == TextTokenKind.Word),
            u => Assert.False(u.IsUnknown));
    }

    [Fact]
    public async Task GlossAsync_UnknownWordIsMarkedWithoutBeingDropped()
    {
        using var fixture = await TestDictionaryFixture.CreateAsync();
        var glosser = fixture.CreateGlosser();

        var result = await glosser.GlossAsync("The zzz.");

        var unknownUnit = Assert.Single(result.Units, u => u.Source == "zzz");
        Assert.True(unknownUnit.IsUnknown);
        Assert.Equal("zzz", unknownUnit.Gloss);
        Assert.Equal(1, result.UnknownCount);
        Assert.Contains("zzz", result.GlossedText);
        Assert.DoesNotContain("zzz?", result.GlossedText);
    }

    [Fact]
    public async Task GlossAsync_UsesPrimaryMeaningOverride()
    {
        using var fixture = await TestDictionaryFixture.CreateAsync();
        var table = PrimaryMeaningTable.FromEntries(
            new Dictionary<string, string> { ["run"] = "本义：跑" });
        var glosser = new LiteralGlosser(fixture.CreateService(), table);

        var result = await glosser.GlossAsync("run");

        var unit = Assert.Single(result.Units, u => u.Source == "run");
        Assert.Equal("本义：跑", unit.Gloss);
        Assert.False(unit.IsUnknown);
    }

    [Fact]
    public async Task GlossAsync_PhraseWithIncludePhrases_GetsWordByWordTreatment()
    {
        using var fixture = await TestDictionaryFixture.CreateAsync();
        var glosser = fixture.CreateGlosser();

        var result = await glosser.GlossAsync("break the ice");

        Assert.Equal(3, result.WordCount);
        Assert.Contains(result.Units, u => u.Source == "break" && u.IsUnknown);
        Assert.Contains(result.Units, u => u.Source == "the" && u.Gloss == "这");
        Assert.Contains(result.Units, u => u.Source == "ice" && u.IsUnknown);
        Assert.DoesNotContain("打破僵局", result.GlossedText);
    }
}
