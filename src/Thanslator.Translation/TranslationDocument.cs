using System.Text;
using System.Text.RegularExpressions;

namespace Thanslator.Translation;

/// <summary>逐句结果；失败单元保留原文，不冒充已翻译。</summary>
public sealed record TranslationUnit(string Source, string? Translation, string? ErrorCode = null)
{
    public string DisplayText => Translation ?? $"〔未译〕{Source}";
}

/// <summary>本地模型结果与来源；单位列表保留顺序及段落边界。</summary>
public sealed record TranslationDocument(IReadOnlyList<TranslationUnit> Units, string SourceLang = "en",
    string TargetLang = "zh-Hans", string Provider = "localmt")
{
    public int FailedCount => Units.Count(unit => unit.ErrorCode is not null);
    public string Text => string.Concat(Units.Select(unit => unit.DisplayText));
}

/// <summary>按句及保守 UTF-8 预算切分，原文拼回后逐字符相同。</summary>
public static class TranslationSegmenter
{
    // shortcut: 以 UTF-8 字节保守约束输入；更换 tokenizer/模型时重新验证预算。
    public const int MaxInputBytes = 384;

    public static IReadOnlyList<string> Split(string text)
    {
        var parts = new List<string>();
        var spans = TextProtector.Protect(text).Spans;
        var spanIndex = 0;
        var start = 0;
        var lastSpace = -1;

        void Add(int end)
        {
            if (end > start) parts.Add(text[start..end]);
            start = end;
            lastSpace = -1;
        }

        for (var index = 0; index < text.Length; index++)
        {
            if (spanIndex < spans.Count && index == spans[spanIndex].Start)
            {
                var span = spans[spanIndex++];
                if (span.Kind == ProtectedSpanKind.Code ||
                    Encoding.UTF8.GetByteCount(span.Text) > MaxInputBytes)
                {
                    Add(index);
                    Add(index + span.Text.Length);
                }
                else if (Encoding.UTF8.GetByteCount(text.AsSpan(start, index + span.Text.Length - start)) > MaxInputBytes)
                {
                    Add(index);
                }
                index += span.Text.Length - 1;
                continue;
            }

            var characterStart = index;
            if (char.IsWhiteSpace(text[index])) lastSpace = index;
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) index++;

            if (Encoding.UTF8.GetByteCount(text.AsSpan(start, index - start + 1)) > MaxInputBytes)
            {
                var end = lastSpace > start ? lastSpace + 1 : characterStart;
                if (end > start)
                {
                    Add(end);
                    index = end - 1;
                    continue;
                }
            }

            if (text[index] is '\r' or '\n' or '\u2028' or '\u2029')
            {
                if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                Add(index + 1);
            }
            else if (IsSentenceEnd(text, index))
            {
                var end = index + 1;
                while (end < text.Length && text[end] is '"' or '\'' or '”' or '’' or ')' or '）') end++;
                if (end == text.Length || char.IsWhiteSpace(text[end]))
                {
                    while (end < text.Length && char.IsWhiteSpace(text[end]) && text[end] is not '\r' and not '\n') end++;
                    Add(end);
                    index = end - 1;
                }
            }
        }
        Add(text.Length);
        return parts;
    }

    private static bool IsSentenceEnd(string text, int index)
    {
        if (text[index] is '!' or '?' or '。' or '！' or '？') return true;
        if (text[index] != '.') return false;
        var begin = index;
        while (begin > 0 && !char.IsWhiteSpace(text[begin - 1])) begin--;
        var word = text[begin..(index + 1)];
        return !Regex.IsMatch(word, @"^(?:[A-Za-z]\.)+$|^(?:Mr|Mrs|Ms|Dr|Prof|Sr|Jr|vs|etc|e\.g|i\.e)\.$", RegexOptions.IgnoreCase);
    }
}
