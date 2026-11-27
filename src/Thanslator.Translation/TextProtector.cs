using System.Text;
using System.Text.RegularExpressions;

namespace Thanslator.Translation;

public enum ProtectedSpanKind
{
    Url,
    Email,
    Path,
    Code,
    FileName,
    Guid,
    Number,
    Emoji,
    Expression
}

public sealed record ProtectedSpan(int Index, string Text, ProtectedSpanKind Kind, int Start = 0);

public sealed record ProtectedText(string MaskedText, IReadOnlyList<ProtectedSpan> Spans);

/// <summary>
/// 翻译前把 URL、邮箱、路径、代码、文件名、GUID、数字、emoji 替换成占位符，
/// 翻完再还原。避免本地 MT 改写或丢掉这些不该翻译的片段。
/// </summary>
public static partial class TextProtector
{
    private const char Open = '⟦';
    private const char Close = '⟧';

    public static ProtectedText Protect(string text, bool protectNumbers = true)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new ProtectedText(text, []);
        }

        var spans = new List<ProtectedSpan>();
        var builder = new StringBuilder(text.Length);
        var last = 0;

        foreach (Match match in SpanRegex().Matches(text))
        {
            if (!protectNumbers && match.Groups["num"].Success)
            {
                continue;
            }
            if (match.Index < last)
            {
                continue;
            }

            if (match.Index > last)
            {
                builder.Append(text, last, match.Index - last);
            }

            var index = spans.Count;
            spans.Add(new ProtectedSpan(index, match.Value, KindOf(match), match.Index));
            builder.Append(Open).Append(index).Append(Close);
            last = match.Index + match.Length;
        }

        if (last < text.Length)
        {
            builder.Append(text, last, text.Length - last);
        }

        return new ProtectedText(builder.ToString(), spans);
    }

    /// <summary>每个占位符必须恰好出现一次；损坏的响应交给有界回退，不猜测补全。</summary>
    public static bool TryRestore(string translated, ProtectedText protection, out string restored)
    {
        restored = translated;
        var matches = PlaceholderRegex().Matches(translated);
        if (matches.Count != protection.Spans.Count ||
            protection.Spans.Any(span => matches.Count(m =>
                int.TryParse(m.Groups[1].Value, out var index) && index == span.Index) != 1))
        {
            return false;
        }

        restored = Restore(translated, protection);
        return !restored.Contains(Open) && !restored.Contains(Close) && IsComplete(restored, protection);
    }

    public static string Restore(string translated, ProtectedText protectedText)
    {
        if (protectedText.Spans.Count == 0 || string.IsNullOrEmpty(translated))
        {
            return translated;
        }

        var result = translated.Trim();
        foreach (var span in protectedText.Spans)
        {
            result = result.Replace($"{Open}{span.Index}{Close}", span.Text, StringComparison.Ordinal);
        }

        // 模型可能在占位符周围插入空格，或把 ⟦⟧ 换成普通方括号
        return PlaceholderRegex().Replace(result, match =>
        {
            return int.TryParse(match.Groups[1].Value, out var index) && index < protectedText.Spans.Count
                ? protectedText.Spans[index].Text
                : match.Value;
        });
    }

    /// <summary>还原后是否所有受保护片段都还在（用于判断 MT 是否丢失内容）。</summary>
    public static bool IsComplete(string restored, ProtectedText protectedText) =>
        protectedText.Spans.GroupBy(span => (span.Text, span.Kind))
            .All(group => (group.Key.Kind == ProtectedSpanKind.Number
                ? SpanRegex().Matches(restored).Count(match => match.Groups["num"].Success && match.Value == group.Key.Text)
                : CountOccurrences(restored, group.Key.Text)) >= group.Count());

    internal static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
        {
            count++;
        }
        return count;
    }

    private static ProtectedSpanKind KindOf(Match match)
    {
        if (match.Groups["url"].Success) return ProtectedSpanKind.Url;
        if (match.Groups["email"].Success) return ProtectedSpanKind.Email;
        if (match.Groups["path"].Success) return ProtectedSpanKind.Path;
        if (match.Groups["code"].Success) return ProtectedSpanKind.Code;
        if (match.Groups["file"].Success) return ProtectedSpanKind.FileName;
        if (match.Groups["guid"].Success) return ProtectedSpanKind.Guid;
        if (match.Groups["num"].Success) return ProtectedSpanKind.Number;
        if (match.Groups["expr"].Success) return ProtectedSpanKind.Expression;
        return ProtectedSpanKind.Emoji;
    }

    [GeneratedRegex(
        @"(?<url>https?://[^\s]+)" +
        @"|(?<email>[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,})" +
        @"|(?<path>[A-Za-z]:\\[^\s]+|\\\\[^\s]+)" +
        @"|(?<code>```[\s\S]*?```|`[^`\r\n]+`)" +
        @"|(?<expr>\b[A-Za-z][A-Za-z0-9_]*\s*(?:<=|>=|==|!=|[≤≥≠≈±×÷])\s*[-+]?\d+(?:[.,]\d+)*)" +
        @"|(?<file>\b[\w.\-]+\.(?:exe|dll|json|txt|md|log|db|zip|png|jpg|jpeg|gif|pdf|cs|xaml|py|js|ts|html|css|xml|yml|yaml|ini|cfg|sqlite)\b)" +
        @"|(?<guid>\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\b)" +
        @"|(?<num>(?<![A-Za-z0-9_])\d+(?:[.,:]\d+)*%?(?![\p{N}_]))" +
        @"|(?<emoji>[\p{So}\p{Sk}️‍]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex SpanRegex();

    [GeneratedRegex(@"[⟦【]\s*(\d+)\s*[⟧】]", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();
}
