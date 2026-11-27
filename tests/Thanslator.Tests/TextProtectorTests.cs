using Thanslator.Core;
using Thanslator.Translation;

namespace Thanslator.Tests;

public sealed class TextProtectorTests
{
    [Fact]
    public void IsComplete_NumbersMayTouchTargetLanguageLettersButNotOtherNumbers()
    {
        var protection = TextProtector.Protect("Return 3.5 ms for 42 items");
        Assert.True(TextProtector.IsComplete("在3.5毫秒内返回42个项目", protection));
        Assert.False(TextProtector.IsComplete("在13.5毫秒内返回142个项目", protection));
    }
    [Fact]
    public void IsComplete_RequiresEveryOccurrence()
    {
        var protection = TextProtector.Protect("42 items and another 42 items");
        Assert.False(TextProtector.IsComplete("42 个项目", protection));
        Assert.True(TextProtector.IsComplete("42 个项目和另外 42 个项目", protection));
    }

    [Theory]
    [InlineData("访问 ⟦0")]
    [InlineData("访问 ⟦0⟧ 和 ⟦0⟧")]
    [InlineData("访问 ⟦1⟧")]
    public void TryRestore_RejectsMissingDuplicateOrForeignPlaceholder(string translated)
    {
        var protection = TextProtector.Protect("Visit https://example.com now");
        Assert.False(TextProtector.TryRestore(translated, protection, out _));
    }

    [Fact]
    public void Protect_CanLeaveNumbersVisibleToModel()
    {
        var protection = TextProtector.Protect("Return 3.5 ms for 42 items", protectNumbers: false);
        Assert.Empty(protection.Spans);
    }
    [Fact]
    public void ProtectAndRestore_RoundTripsProtectedSpans()
    {
        const string text =
            "Open https://example.com/a?b=1 or mail a.b@example.com, run C:\\tools\\x.exe, " +
            "`dotnet build`, see config.json, id 12345678-1234-1234-1234-1234567890ab, 42 items 🚀";

        var protectedText = TextProtector.Protect(text);

        Assert.NotEmpty(protectedText.Spans);
        Assert.Contains("⟦0⟧", protectedText.MaskedText);

        var restored = TextProtector.Restore(protectedText.MaskedText, protectedText);
        Assert.Equal(text, restored);
        Assert.True(TextProtector.IsComplete(restored, protectedText));
    }

    [Fact]
    public void Protect_LeavesNormalTextUnchanged()
    {
        const string text = "The quick brown fox jumps over the lazy dog.";

        var protectedText = TextProtector.Protect(text);

        Assert.Empty(protectedText.Spans);
        Assert.Equal(text, protectedText.MaskedText);
    }

    [Fact]
    public void Restore_ToleratesSpacesInsidePlaceholders()
    {
        var protectedText = TextProtector.Protect("Visit https://example.com now");
        var translated = protectedText.MaskedText.Replace("⟦0⟧", "⟦ 0 ⟧");

        Assert.Equal("Visit https://example.com now", TextProtector.Restore(translated, protectedText));
    }

    [Fact]
    public void Tokenize_TreatsEmailPathAndCodeAsOpaque()
    {
        var tokens = TextTokenizer.Tokenize(
            "Mail a.b@example.com, open C:\\tools\\x.exe or `dotnet build`.");

        Assert.Contains(tokens, t => t.Kind == TextTokenKind.Opaque && t.Text.Contains("a.b@example.com"));
        Assert.Contains(tokens, t => t.Kind == TextTokenKind.Opaque && t.Text.Contains("C:\\tools\\x.exe"));
        Assert.Contains(tokens, t => t.Kind == TextTokenKind.Opaque && t.Text.Contains("`dotnet build`"));
    }
}
