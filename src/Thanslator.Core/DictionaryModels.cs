namespace Thanslator.Core;

public sealed record DictionarySense(
    string PartOfSpeech,
    IReadOnlyList<string> Glosses);

public sealed record DictionaryResult(
    string Query,
    string? Headword,
    string? Phonetic,
    IReadOnlyList<DictionarySense> Senses,
    bool Found,
    string Source,
    string? Message = null)
{
    public static DictionaryResult NotFound(string query, string message) =>
        new(query, null, null, Array.Empty<DictionarySense>(), false, "local", message);

    public static DictionaryResult Unsupported(string query, string message) =>
        new(query, null, null, Array.Empty<DictionarySense>(), false, "local", message);
}
