using Thanslator.Core;
using Thanslator.Dictionary;

namespace Thanslator.Tests;

public sealed class DictionaryServiceTests
{
    [Fact]
    public async Task LookupAsync_ParsesPosAndGlosses()
    {
        using var fixture = await TestDictionaryFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.LookupAsync("run");

        Assert.True(result.Found);
        Assert.Equal("run", result.Headword);
        Assert.Contains(result.Senses, s => s.PartOfSpeech == "v." && s.Glosses.Contains("跑"));
        Assert.Contains(result.Senses, s => s.PartOfSpeech == "n." && s.Glosses.Contains("奔跑"));
    }

    [Fact]
    public async Task LookupAsync_UsesFormsIndexForInflections()
    {
        using var fixture = await TestDictionaryFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.LookupAsync("running");

        Assert.True(result.Found);
        Assert.Equal("run", result.Headword);
        Assert.Contains(result.Senses, s => s.Glosses.Contains("跑"));
    }

    [Fact]
    public async Task LookupAsync_InflectedFormFallsBackToLemmaWhenExactRowOnlyHasGrammarNote()
    {
        using var fixture = await TestDictionaryFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.LookupAsync("went");

        Assert.True(result.Found);
        Assert.Equal("go", result.Headword);
        Assert.Contains(result.Senses, s => s.Glosses.Contains("去"));
        Assert.Equal("ECDICT（词形还原）", result.Source);
    }

    [Fact]
    public async Task LookupAsync_InflectedFormReceivesLemmaCuratedOverride()
    {
        using var fixture = await TestDictionaryFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.LookupAsync("runs");

        Assert.True(result.Found);
        Assert.Equal("run", result.Headword);
        Assert.Contains(result.Senses, s => s.PartOfSpeech == "v." && s.Glosses.Contains("运行"));
        Assert.Equal("ECDICT + 校订", result.Source);
    }

    [Fact]
    public async Task LookupAsync_PossessiveStripsApostropheS()
    {
        using var fixture = await TestDictionaryFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.LookupAsync("dog's");

        Assert.True(result.Found);
        Assert.Equal("dog", result.Headword);
        Assert.Contains(result.Senses, s => s.Glosses.Contains("狗"));
    }

    [Fact]
    public async Task LookupAsync_UsesCuratedOverrideWithoutEntry()
    {
        using var fixture = await TestDictionaryFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.LookupAsync("the");

        Assert.True(result.Found);
        Assert.Equal("the", result.Headword);
        Assert.Contains(result.Senses, s => s.PartOfSpeech == "art." && s.Glosses.Contains("这"));
        Assert.Equal("人工校订", result.Source);
    }

    [Fact]
    public async Task LookupAsync_PhraseEntry_IsFound()
    {
        using var fixture = await TestDictionaryFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.LookupAsync("break the ice");

        Assert.True(result.Found);
        Assert.Contains(result.Senses, s => s.Glosses.Contains("打破僵局"));
    }

    [Fact]
    public async Task LookupAsync_Sentence_ReturnsGuidanceInsteadOfCallingMt()
    {
        using var fixture = await TestDictionaryFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.LookupAsync("the quick brown fox jumps over the lazy dog");

        Assert.False(result.Found);
        Assert.Contains("只处理单词/短语", result.Message);
    }

    [Fact]
    public async Task LookupAsync_UsesExtraOverridesForSymbolWords()
    {
        using var fixture = await TestDictionaryFixture.CreateAsync();
        var extraPath = Path.Combine(
            Path.GetTempPath(),
            "thanslator-extra-overrides",
            Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(extraPath)!);
        await File.WriteAllTextAsync(
            extraPath,
            """
            {
              "c++": [{ "pos": "n.", "glosses": ["C++（编程语言）"] }],
              "u.s": [{ "pos": "n.", "glosses": ["美国"] }]
            }
            """);

        try
        {
            var service = new DictionaryService(
                fixture.DatabasePath, fixture.OverridesPath, extraPath);

            var cpp = await service.LookupAsync("C++");
            Assert.True(cpp.Found);
            Assert.Contains("C++", cpp.Senses[0].Glosses[0]);

            var us = await service.LookupAsync("U.S.");
            Assert.True(us.Found);
            Assert.Equal("美国", us.Senses[0].Glosses[0]);
        }
        finally
        {
            File.Delete(extraPath);
        }
    }
}
