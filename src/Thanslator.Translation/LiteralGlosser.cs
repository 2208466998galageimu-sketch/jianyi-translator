using System.Text;
using Thanslator.Core;
using Thanslator.Dictionary;

namespace Thanslator.Translation;

/// <summary>
/// Produces a word-by-word gloss in original token order. It never invokes a
/// machine translator; each word is resolved through the local dictionary.
/// </summary>
public sealed class LiteralGlosser
{
    private readonly DictionaryService _dictionary;
    private readonly PrimaryMeaningTable _primaryMeanings;

    public LiteralGlosser(DictionaryService dictionary, PrimaryMeaningTable? primaryMeanings = null)
    {
        _dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        _primaryMeanings = primaryMeanings ?? PrimaryMeaningTable.LoadDefault();
    }

    public async Task<LiteralGlossResult> GlossAsync(
        string text,
        string targetLang = "zh-Hans",
        CancellationToken cancellationToken = default)
    {
        var tokens = TextTokenizer.Tokenize(text);
        var lookupKeys = tokens
            .Where(t => t.Kind == TextTokenKind.Word)
            .Select(t => TextTokenizer.NormalizeForLookup(t.Text))
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var lookups = new Dictionary<string, DictionaryResult>(StringComparer.Ordinal);
        foreach (var key in lookupKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lookups[key] = await _dictionary.LookupAsync(key, targetLang, cancellationToken);
        }

        var units = new List<LiteralUnit>(tokens.Count);
        var glossed = new StringBuilder(text.Length);
        var wordCount = 0;
        var unknownCount = 0;

        foreach (var token in tokens)
        {
            if (token.Kind != TextTokenKind.Word)
            {
                units.Add(new LiteralUnit(
                    token.Text,
                    token.Text,
                    null,
                    false,
                    token.Kind));
                glossed.Append(token.Text);
                continue;
            }

            wordCount++;
            var key = TextTokenizer.NormalizeForLookup(token.Text);
            lookups.TryGetValue(key, out var result);
            var firstSense = ChoosePrimarySense(result);
            var gloss = ResolvePrimaryGloss(key, result, firstSense);
            var unknown = string.IsNullOrWhiteSpace(gloss);
            if (unknown)
            {
                unknownCount++;
                gloss = token.Text;
            }

            units.Add(new LiteralUnit(
                token.Text,
                gloss!,
                firstSense?.PartOfSpeech,
                unknown,
                TextTokenKind.Word));

            // 未收录词原样保留；UI 用下划线标注，不再追加 '?' 字符
            glossed.Append(gloss);
        }

        return new LiteralGlossResult(
            text,
            units,
            glossed.ToString(),
            wordCount,
            unknownCount,
            "本地词典");
    }

    /// <summary>优先取核心词性义项；"其他" 多为 DOS 命令/专业注释等补充信息，不作为本义。</summary>
    private static DictionarySense? ChoosePrimarySense(DictionaryResult? result)
    {
        var senses = result?.Senses;
        if (senses is null || senses.Count == 0)
        {
            return null;
        }

        return senses.FirstOrDefault(s =>
                   !string.Equals(s.PartOfSpeech, "其他", StringComparison.Ordinal))
               ?? senses[0];
    }

    /// <summary>
    /// 优先使用 primary-meanings.json 指定的本义（按原词或词形还原后的 headword），
    /// 否则取第一义项的第一条释义。
    /// </summary>
    private string? ResolvePrimaryGloss(string key, DictionaryResult? result, DictionarySense? sense)
    {
        if (_primaryMeanings.TryGet(key, out var primary))
        {
            return primary;
        }

        if (result is { Headword: { Length: > 0 } headword } &&
            _primaryMeanings.TryGet(TextTokenizer.NormalizeForLookup(headword), out var lemmaPrimary))
        {
            return lemmaPrimary;
        }

        return sense?.Glosses.FirstOrDefault();
    }
}
