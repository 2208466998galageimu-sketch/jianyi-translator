namespace Thanslator.Core;

public readonly record struct PersonalTermKey(string SourceLang, string TargetLang, string Term)
{
    public const int MaxTermLength = 120;
    public const int MaxGlossLength = 160;

    public static PersonalTermKey? FromSelection(string text, AppSettings settings)
    {
        var input = text.Trim();
        if (input.Length is 0 or > MaxTermLength) return null;
        var kind = TextClassifier.Classify(input);
        if (kind is not (TextKind.Word or TextKind.Phrase)) return null;
        var term = string.Join(' ', input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return new(settings.SourceLang.ToLowerInvariant(), settings.TargetLang.ToLowerInvariant(), term);
    }

    public DictionaryResult Apply(DictionaryResult result, string gloss)
    {
        var senses = new List<DictionarySense> { new("个人首选", [gloss]) };
        foreach (var sense in result.Senses)
        {
            var alternatives = sense.Glosses.Where(value => !string.Equals(value, gloss, StringComparison.Ordinal)).ToArray();
            if (alternatives.Length > 0) senses.Add(sense with { Glosses = alternatives });
        }

        return result with
        {
            Query = Term,
            Headword = Term,
            Found = true,
            Source = result.Found ? $"个人首选 + {result.Source}" : "个人首选",
            Senses = senses,
            Message = null
        };
    }
}
