using Thanslator.Core;

namespace Thanslator.Tests;

public sealed class TextClassifierTests
{
    [Fact]
    public void MissedTwoWordPhrase_UsesMtOnlyInFluentMode()
    {
        Assert.True(TextClassifier.ShouldFallbackToSentence(TextKind.Phrase, "attention mechanism", false, literalMode: false));
        Assert.False(TextClassifier.ShouldFallbackToSentence(TextKind.Phrase, "attention mechanism", false, literalMode: true));
    }

    [Theory]
    [InlineData("(kernel)", "kernel")]
    [InlineData("“kernel”", "kernel")]
    [InlineData("C++", "c++")]
    [InlineData("C#", "c#")]
    [InlineData(".NET", ".net")]
    public void NormalizeLookup_PreservesTechnicalNames(string source, string expected)
    {
        Assert.Equal(expected, WordNormalizer.NormalizeLookup(source));
    }
    [Theory]
    [InlineData("The\u00a0system\u00a0handles\u00a0incoming\u00a0requests.")]
    [InlineData("The\u202fsystem\u2003handles incoming requests.")]
    public void Classify_UnicodeWhitespaceSentence(string text)
    {
        Assert.Equal(TextKind.Sentence, TextClassifier.Classify(text));
        Assert.Equal(5, TextClassifier.CountWords(text));
    }
    [Theory]
    [InlineData("run", TextKind.Word)]
    [InlineData("break the ice", TextKind.Phrase)]
    [InlineData("the quick brown fox jumps over the lazy dog", TextKind.Sentence)]
    [InlineData("hello\nworld", TextKind.Paragraph)]
    [InlineData("a well-designed API", TextKind.Phrase)]
    public void Classify_ReturnsExpectedKind(string text, TextKind expected)
    {
        Assert.Equal(expected, TextClassifier.Classify(text));
    }

    [Theory]
    [InlineData("Every week brings new forecasts", false, true)]
    [InlineData("break the ice", false, true)]
    [InlineData("break the ice", true, false)]
    [InlineData("hello world", false, false)]
    [InlineData("run", false, false)]
    public void ShouldFallbackToSentence_OnlyForMissedThreeWordPhrases(
        string text,
        bool dictionaryFound,
        bool expected)
    {
        var kind = TextClassifier.Classify(text);

        Assert.Equal(expected, TextClassifier.ShouldFallbackToSentence(kind, text, dictionaryFound));
    }
}
