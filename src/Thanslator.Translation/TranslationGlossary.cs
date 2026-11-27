using System.Text.Json;
using System.Text.RegularExpressions;

namespace Thanslator.Translation;

/// <summary>
/// 术语表，两类条目：
/// 1. <c>targetTerm</c>：专名/术语，翻译前用占位符保护，翻完还原成指定写法；
/// 2. <c>replacements</c>：源文本包含某术语时，纠正 MT 的已知错误译法。
/// 内置表在 assets\overrides；用户表在 data\overrides，同名文件会合并。
/// </summary>
public sealed partial class TranslationGlossary
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IReadOnlyList<Entry> _entries;
    private readonly IReadOnlyList<TermMapping> _termMappings;

    private TranslationGlossary(IReadOnlyList<Entry> entries)
    {
        _entries = entries;
        _termMappings = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.TargetTerm) && e.SourceTerms is { Length: > 0 })
            .SelectMany(e => e.SourceTerms!.Select(term =>
                new TermMapping(term, e.TargetTerm!, e.CaseSensitive, e.WholeWord)))
            .Where(m => m.Source.Length > 0)
            .DistinctBy(m => m.Source, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(m => m.Source.Length)
            .ToArray();
    }

    public static TranslationGlossary Empty { get; } = new(Array.Empty<Entry>());

    public static TranslationGlossary Load(string? path = null)
    {
        path ??= Core.AppPaths.TranslationGlossary;
        return new TranslationGlossary(ReadEntries(path));
    }

    /// <summary>用户术语表优先，然后是内置术语表。</summary>
    public static TranslationGlossary LoadDefault()
    {
        var entries = new List<Entry>();
        entries.AddRange(ReadEntries(Core.AppPaths.UserTranslationGlossary));
        entries.AddRange(ReadEntries(Core.AppPaths.TranslationGlossary));
        return new TranslationGlossary(entries);
    }

    private static Entry[] ReadEntries(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize<GlossaryDocument>(json, JsonOptions);
            return document?.Entries?
                .Where(e => e.SourceTerms is { Length: > 0 } &&
                            (e.Replacements is { Count: > 0 } || !string.IsNullOrWhiteSpace(e.TargetTerm)))
                .ToArray() ?? [];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load translation glossary '{path}': {ex.Message}");
            return [];
        }
    }

    /// <summary>把专名/术语替换成占位符，返回掩码文本和还原表。</summary>
    public TermProtection ProtectTerms(string text)
    {
        if (_termMappings.Count == 0 || string.IsNullOrEmpty(text))
        {
            return new TermProtection(text, Array.Empty<TermSpan>());
        }

        var spans = new List<TermSpan>();
        var result = text;
        foreach (var mapping in _termMappings)
        {
            // 已能原样保留的专名不掩码，避免模型把 GitHub 变成损坏的 T0。
            if (mapping.Source.Equals(mapping.Target, StringComparison.Ordinal))
            {
                continue;
            }
            var pattern = mapping.WholeWord
                ? $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(mapping.Source)}(?![\p{{L}}\p{{N}}_])"
                : Regex.Escape(mapping.Source);
            var options = mapping.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            result = Regex.Replace(result, pattern, match =>
            {
                var index = spans.Count;
                spans.Add(new TermSpan(index, match.Value, mapping.Target));
                return $"⟦T{index}⟧";
            }, options);
        }

        return new TermProtection(result, spans);
    }

    public string RestoreTerms(string translated, TermProtection protection)
    {
        if (protection.Spans.Count == 0 || string.IsNullOrEmpty(translated))
        {
            return translated;
        }

        var result = translated;
        foreach (var span in protection.Spans)
        {
            result = result.Replace($"⟦T{span.Index}⟧", span.Target, StringComparison.Ordinal);
        }

        return TermPlaceholderRegex().Replace(result, match =>
        {
            return int.TryParse(match.Groups[1].Value, out var index) && index < protection.Spans.Count
                ? protection.Spans[index].Target
                : match.Value;
        });
    }

    /// <summary>专名还原要求身份与次数均完整；否则由调用方重试原句。</summary>
    public bool TryRestoreTerms(string translated, TermProtection protection, out string restored)
    {
        restored = translated;
        var matches = TermPlaceholderRegex().Matches(translated);
        if (matches.Count != protection.Spans.Count ||
            protection.Spans.Any(span => matches.Count(m =>
                int.TryParse(m.Groups[1].Value, out var index) && index == span.Index) != 1))
        {
            return false;
        }
        restored = RestoreTerms(translated, protection);
        return true;
    }

    /// <summary>检查本句命中的强制术语及重复次数，不把缺失术语补到句末。</summary>
    public bool AreTermsComplete(string source, string translated)
    {
        foreach (var mapping in _termMappings)
        {
            var pattern = mapping.WholeWord
                ? $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(mapping.Source)}(?![\p{{L}}\p{{N}}_])"
                : Regex.Escape(mapping.Source);
            var count = Regex.Matches(source, pattern,
                mapping.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase).Count;
            if (TextProtector.CountOccurrences(translated, mapping.Target) < count)
            {
                return false;
            }
        }
        return true;
    }

    public string Apply(string source, string translation)
    {
        if (_entries.Count == 0 || string.IsNullOrWhiteSpace(translation))
        {
            return translation;
        }

        var result = translation;
        foreach (var entry in _entries)
        {
            if (entry.SourceTerms is null || entry.Replacements is null)
            {
                continue;
            }

            if (!entry.SourceTerms.Any(term =>
                    ContainsTerm(source, term, entry.CaseSensitive, entry.WholeWord)))
            {
                continue;
            }

            if (entry.ContextTerms is { Length: > 0 } && !entry.ContextTerms.Any(term =>
                    ContainsTerm(source, term, caseSensitive: false, wholeWord: true)))
            {
                continue;
            }

            foreach (var (from, to) in entry.Replacements)
            {
                if (from.Length == 0 || to.Length == 0)
                {
                    continue;
                }

                // If the model already produced the preferred term, do not
                // turn e.g. "客户端" into "客户端端".
                if (!result.Contains(to, StringComparison.Ordinal))
                {
                    result = result.Replace(from, to, StringComparison.Ordinal);
                }
            }
        }

        return result;
    }

    private static bool ContainsTerm(string source, string term, bool caseSensitive, bool wholeWord)
    {
        if (!wholeWord)
        {
            return source.Contains(
                term,
                caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        var pattern = $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}_])";
        return Regex.IsMatch(
            source,
            pattern,
            caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
    }

    [GeneratedRegex(@"[⟦【]\s*T\s*(\d+)\s*[⟧】]", RegexOptions.Compiled)]
    private static partial Regex TermPlaceholderRegex();

    private sealed class GlossaryDocument
    {
        public Entry[]? Entries { get; set; }
    }

    private sealed class Entry
    {
        public string[]? SourceTerms { get; set; }
        public Dictionary<string, string>? Replacements { get; set; }
        public string? TargetTerm { get; set; }
        public string[]? ContextTerms { get; set; }
        public bool CaseSensitive { get; set; }
        public bool WholeWord { get; set; }
    }
}

public sealed record TermMapping(string Source, string Target, bool CaseSensitive, bool WholeWord);

public sealed record TermSpan(int Index, string Source, string Target);

public sealed record TermProtection(string Text, IReadOnlyList<TermSpan> Spans);
