using Thanslator.Core;
using Thanslator.Translation;

namespace Thanslator.Tests;

public sealed class TextTokenizerTests
{
    [Fact]
    public void Tokenize_PreservesWordPunctuationAndWhitespaceOrder()
    {
        var tokens = TextTokenizer.Tokenize("The quick, brown fox.");

        Assert.Equal(
            ["The", " ", "quick", ",", " ", "brown", " ", "fox", "."],
            tokens.Select(t => t.Text));
        Assert.Equal(TextTokenKind.Punctuation, tokens[^1].Kind);
    }

    [Fact]
    public void Tokenize_KeepsHyphenatedWordTogether()
    {
        var tokens = TextTokenizer.Tokenize("a well-designed API");

        Assert.Contains(tokens, t => t.Text == "well-designed" && t.Kind == TextTokenKind.Word);
    }

    [Theory]
    [InlineData("AT&T")]
    [InlineData("node.js")]
    [InlineData("C++")]
    [InlineData("C#")]
    [InlineData("snake_case")]
    public void Tokenize_KeepsSymbolWordsTogether(string word)
    {
        var tokens = TextTokenizer.Tokenize(word);

        // node.js 会被识别为 Opaque（文件类），其余是 Word；关键是不要拆成字母。
        Assert.Contains(tokens, t => t.Text == word &&
                                    (t.Kind == TextTokenKind.Word || t.Kind == TextTokenKind.Opaque));
    }

    [Fact]
    public void Tokenize_KeepsDottedAbbreviationCoreTogether()
    {
        var tokens = TextTokenizer.Tokenize("U.S.");

        Assert.Contains(tokens, t => t.Kind == TextTokenKind.Word && t.Text == "U.S");
    }

    [Fact]
    public void Tokenize_TreatsUrlAsOpaque()
    {
        var tokens = TextTokenizer.Tokenize("see https://example.com/a-b now");

        Assert.Contains(tokens, t => t.Text == "https://example.com/a-b" && t.Kind == TextTokenKind.Opaque);
    }

    [Theory]
    [InlineData("dog's", "dog")]
    [InlineData("DON'T", "don't")]
    [InlineData("running", "running")]
    public void NormalizeForLookup_StripsPossessiveAndLowercases(string input, string expected)
    {
        Assert.Equal(expected, TextTokenizer.NormalizeForLookup(input));
    }
}
