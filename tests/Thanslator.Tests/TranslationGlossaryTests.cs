using Thanslator.Translation;

namespace Thanslator.Tests;

public sealed class TranslationGlossaryTests
{
    [Fact]
    public void Apply_ReplacesKnownWrongTermWhenSourceContainsIt()
    {
        var glossary = Load(
            """
            {
              "entries": [
                {
                  "sourceTerms": ["race condition", "race conditions"],
                  "replacements": { "种族状况": "竞态条件" }
                }
              ]
            }
            """);

        Assert.Equal(
            "竞态条件需要小心处理。",
            glossary.Apply("Race conditions need care.", "种族状况需要小心处理。"));
    }

    [Fact]
    public void Apply_DoesNotRunWhenSourceDoesNotContainTerm()
    {
        var glossary = Load(
            """
            {
              "entries": [
                {
                  "sourceTerms": ["legacy"],
                  "replacements": { "遗产": "遗留" }
                }
              ]
            }
            """);

        Assert.Equal("这是一座建筑。", glossary.Apply("This is a building.", "这是一座建筑。"));
    }

    [Fact]
    public void Apply_DoesNotDoubleSuffixPreferredTerm()
    {
        var glossary = Load(
            """
            {
              "entries": [
                {
                  "sourceTerms": ["client"],
                  "replacements": { "客户": "客户端" }
                }
              ]
            }
            """);

        Assert.Equal("客户端已连接。", glossary.Apply("The client is connected.", "客户端已连接。"));
    }

    [Fact]
    public void Apply_AppliesMultipleEntries()
    {
        var glossary = Load(
            """
            {
              "entries": [
                { "sourceTerms": ["legacy"], "replacements": { "遗产": "遗留" } },
                { "sourceTerms": ["architecture"], "replacements": { "建筑": "架构" } }
              ]
            }
            """);

        Assert.Equal(
            "遗留服务迁移到新架构。",
            glossary.Apply("legacy service to a new architecture", "遗产服务迁移到新建筑。"));
    }

    [Fact]
    public void ProtectTerms_MapsProperNounAndRestoresTarget()
    {
        var glossary = Load(
            """
            {
              "entries": [
                { "sourceTerms": ["GitHub"], "targetTerm": "GitHub", "caseSensitive": true, "wholeWord": true }
              ]
            }
            """);

        var protection = glossary.ProtectTerms("I use GitHub daily.");

        Assert.Equal("I use GitHub daily.", protection.Text);
        Assert.Empty(protection.Spans);
        Assert.True(glossary.AreTermsComplete("I use GitHub daily.", "我每天使用 GitHub。"));
        Assert.False(glossary.AreTermsComplete("I use GitHub daily.", "我每天使用 T0。"));
    }

    [Fact]
    public void ProtectTerms_RespectsCaseSensitivity()
    {
        var glossary = Load(
            """
            {
              "entries": [
                { "sourceTerms": ["Windows"], "targetTerm": "Windows", "caseSensitive": true, "wholeWord": true }
              ]
            }
            """);

        Assert.Equal("Open the windows.", glossary.ProtectTerms("Open the windows.").Text);
    }

    [Fact]
    public void ProtectTerms_RespectsWholeWord()
    {
        var glossary = Load(
            """
            {
              "entries": [
                { "sourceTerms": ["Microsoft"], "targetTerm": "微软", "wholeWord": true }
              ]
            }
            """);

        Assert.Equal(
            "Microsofter is not a company.",
            glossary.ProtectTerms("Microsofter is not a company.").Text);
        Assert.Equal(
            "I use 微软.",
            glossary.RestoreTerms("I use ⟦T0⟧.", glossary.ProtectTerms("I use Microsoft.")));
    }

    [Fact]
    public void Apply_RespectsCaseSensitiveSourceTerm()
    {
        var glossary = Load(
            """
            {
              "entries": [
                {
                  "sourceTerms": ["Windows"],
                  "replacements": { "窗户": "Windows" },
                  "caseSensitive": true,
                  "wholeWord": true
                }
              ]
            }
            """);

        Assert.Equal("打开窗户。", glossary.Apply("Open the windows.", "打开窗户。"));
        Assert.Equal("启动Windows。", glossary.Apply("Start Windows.", "启动窗户。"));
    }

    [Fact]
    public void BuiltInGlossary_LoadsProperNouns()
    {
        var glossary = TranslationGlossary.Load();
        var protection = glossary.ProtectTerms("I use GitHub and Microsoft.");

        Assert.Contains("⟦T", protection.Text);

        var restored = glossary.RestoreTerms(protection.Text, protection);
        Assert.Contains("GitHub", restored);
        Assert.Contains("微软", restored);
    }

    [Fact]
    public void BuiltInGlossary_DoesNotForceTechnicalMeaningWithoutContext()
    {
        var glossary = TranslationGlossary.Load();
        Assert.Equal("客户向工人付款。", glossary.Apply("The client paid the worker.", "客户向工人付款。"));
        Assert.Equal("客户端连接到服务器。", glossary.Apply("The client connects to the server.", "客户连接到服务器。"));
    }

    private static TranslationGlossary Load(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "thanslator-glossary-tests", Guid.NewGuid() + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        try
        {
            return TranslationGlossary.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
