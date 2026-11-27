namespace Thanslator.Core;

public enum TextKind
{
    Word,
    Phrase,
    Sentence,
    Paragraph
}

public static class TextClassifier
{
    private static readonly char[] SentenceTerminators = ['.', '!', '?', '。', '！', '？'];

    public static TextKind Classify(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return TextKind.Word;
        }

        if (trimmed.Any(c => c is '\n' or '\r' or '\u2028' or '\u2029'))
        {
            return TextKind.Paragraph;
        }

        if (trimmed.Any(SentenceTerminators.Contains) && trimmed.Any(c => c is >= '\u3400' and <= '\u9fff' or >= '\u3040' and <= '\u30ff' or >= '\uac00' and <= '\ud7af'))
            return TextKind.Sentence;

        var tokens = CountWords(trimmed);

        if (tokens <= 1)
        {
            return TextKind.Word;
        }

        if (tokens <= 8 && !trimmed.Any(SentenceTerminators.Contains))
        {
            return TextKind.Phrase;
        }

        return TextKind.Sentence;
    }

    /// <summary>按 Unicode 空白计词，避免 PDF 中的不换行空格使整句被当成单词。</summary>
    public static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// 短语词典未命中且至少 3 个词时按句子处理：锁序逐词直译，通序走本地 MT。
    /// 命中的短语仍由「短语拆分」开关决定，不受这里影响。
    /// </summary>
    public static bool ShouldFallbackToSentence(TextKind kind, string text, bool dictionaryFound, bool literalMode = true) =>
        kind == TextKind.Phrase && !dictionaryFound && CountWords(text) >= (literalMode ? 3 : 2);
}
