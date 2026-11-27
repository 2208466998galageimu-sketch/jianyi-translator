using System.Text;
using Thanslator.Translation;

namespace Thanslator.Tests;

public sealed class TranslationDocumentTests
{
    [Fact]
    public void Protect_KeepsMathematicalRelationOpaqueWithoutDroppingSurroundingText()
    {
        const string text = "If x ≤ 5, the system returns a result within 3.5 ms.";
        var parts = TranslationSegmenter.Split(text);
        Assert.Equal(text, string.Concat(parts));
        Assert.Contains(TextProtector.Protect(text).Spans, span => span.Kind == ProtectedSpanKind.Expression && span.Text == "x ≤ 5");
        Assert.Contains(parts, part => part.Contains("3.5 ms"));
    }
    [Fact]
    public void Split_PreservesAbbreviationsNumbersUrlsCodeAndLineBreaks()
    {
        const string text = "Dr. Smith uses U.S. data and 3.5 ms. Read https://example.com/a.b now.\r\nRun `x = 1.2;` safely.";
        var parts = TranslationSegmenter.Split(text);
        Assert.Equal(text, string.Concat(parts));
        Assert.Contains(parts, p => p.Contains("Dr. Smith uses U.S. data and 3.5 ms."));
        Assert.Contains(parts, p => p.Contains("https://example.com/a.b"));
        Assert.Contains("`x = 1.2;`", parts);
    }

    [Fact]
    public void Split_LongSentenceFitsBudgetWithoutDroppingText()
    {
        var text = string.Concat(Enumerable.Repeat("The system handles incoming requests ☃ ", 250));
        var parts = TranslationSegmenter.Split(text);
        Assert.Equal(text, string.Concat(parts));
        Assert.All(parts, part => Assert.InRange(Encoding.UTF8.GetByteCount(part), 1, TranslationSegmenter.MaxInputBytes));
    }

    [Fact]
    public void Split_DoesNotBreakSurrogatePairsAtByteBudget()
    {
        var text = string.Concat(Enumerable.Repeat("𠀀", 250));
        var parts = TranslationSegmenter.Split(text);
        Assert.Equal(text, string.Concat(parts));
        Assert.All(parts, part =>
        {
            Assert.False(char.IsLowSurrogate(part[0]));
            Assert.False(char.IsHighSurrogate(part[^1]));
            Assert.InRange(Encoding.UTF8.GetByteCount(part), 1, TranslationSegmenter.MaxInputBytes);
        });
    }

    [Fact]
    public void Document_PreservesFailedSentenceAndSuccessfulNeighbours()
    {
        var document = new TranslationDocument([
            new("Hello. ", "你好。 "), new("Unknown. ", null, "localmt.invalid_output"), new("Goodbye.", "再见。")]);
        Assert.Equal("你好。 〔未译〕Unknown. 再见。", document.Text);
        Assert.Equal(1, document.FailedCount);
    }
}
