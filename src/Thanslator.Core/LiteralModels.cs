namespace Thanslator.Core;

public enum TextTokenKind
{
    Word,
    Number,
    Punctuation,
    Whitespace,
    Opaque
}

public sealed record TextToken(string Text, TextTokenKind Kind);

public sealed record LiteralUnit(
    string Source,
    string Gloss,
    string? PartOfSpeech,
    bool IsUnknown,
    TextTokenKind Kind);

public sealed record LiteralGlossResult(
    string OriginalText,
    IReadOnlyList<LiteralUnit> Units,
    string GlossedText,
    int WordCount,
    int UnknownCount,
    string Source);
