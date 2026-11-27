using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace Thanslator.Core;

public sealed record KnowledgeTerm(string Id, string Term, string Translation, string Note = "", bool Enabled = true);
public sealed record KnowledgeLibrary(string Id, string Name, string Domain, string SourceLang,
    string TargetLang, IReadOnlyList<KnowledgeTerm> Terms);
public sealed record KnowledgeHit(string LibraryId, string LibraryName, KnowledgeTerm Rule);
public sealed record KnowledgeConflict(string Term, KnowledgeHit Preferred, KnowledgeHit Alternative);
public sealed record KnowledgeMatches(IReadOnlyList<KnowledgeHit> Hits, IReadOnlyList<KnowledgeConflict> Conflicts,
    IReadOnlyList<KnowledgeHit>? Candidates = null, string PresetId = "");
public sealed record TermImportError(long Line, string Message);
public sealed record TermImportPreview(IReadOnlyList<KnowledgeTerm> Terms, IReadOnlyList<TermImportError> Errors)
{
    public bool CanImport => Terms.Count > 0 && Errors.Count == 0;
}

public static class KnowledgeRules
{
    public const int MaxTerms = 5000;
    public static string Normalize(string value) => string.Join(' ', value.Trim().Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    public static void ValidateTerm(KnowledgeTerm term)
    {
        if (string.IsNullOrWhiteSpace(term.Id) || term.Id.Length > 80 ||
            string.IsNullOrWhiteSpace(term.Term) || term.Term.Length > 200 ||
            string.IsNullOrWhiteSpace(term.Translation) || term.Translation.Length > 500 || term.Note.Length > 1000)
            throw new ArgumentException("术语需包含原词和译法，长度上限分别为 200、500 字符，备注上限为 1000 字符。");
    }

    public static void ValidateLibrary(KnowledgeLibrary library)
    {
        if (string.IsNullOrWhiteSpace(library.Id) || library.Id.Length > 80 ||
            string.IsNullOrWhiteSpace(library.Name) || library.Name.Length > 80 || library.Domain.Length > 500 ||
            string.IsNullOrWhiteSpace(library.SourceLang) || library.SourceLang.Length > 32 ||
            string.IsNullOrWhiteSpace(library.TargetLang) || library.TargetLang.Length > 32 || library.Terms.Count > MaxTerms)
            throw new ArgumentException("知识库名称、语言对或术语数量无效。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in library.Terms)
        {
            ValidateTerm(term);
            if (!names.Add(Normalize(term.Term)) || !ids.Add(term.Id))
                throw new ArgumentException("同库存在重复术语或条目编号，请先处理冲突。");
        }
    }

    // Library order is explicitly the caller's selected priority order.
    public static KnowledgeMatches Match(string text, string sourceLang, string targetLang,
        IReadOnlyList<KnowledgeLibrary> libraries, CancellationToken cancellationToken = default)
    {
        var preferred = new Dictionary<string, KnowledgeHit>(StringComparer.Ordinal);
        var conflicts = new List<KnowledgeConflict>();
        var candidates = new List<KnowledgeHit>();
        foreach (var library in libraries.Where(l =>
            l.TargetLang.Equals(targetLang, StringComparison.OrdinalIgnoreCase) &&
            (l.SourceLang.Equals(sourceLang, StringComparison.OrdinalIgnoreCase) ||
             sourceLang.Equals("auto", StringComparison.OrdinalIgnoreCase) && l.SourceLang.Equals("en", StringComparison.OrdinalIgnoreCase))))
        {
            foreach (var term in library.Terms.Where(t => t.Enabled))
            {
                var name = Normalize(term.Term);
                var hit = new KnowledgeHit(library.Id, library.Name, term);
                candidates.Add(hit);
                if (!preferred.TryAdd(name, hit) && preferred[name].Rule.Translation != term.Translation)
                    conflicts.Add(new(term.Term, preferred[name], hit));
            }
        }
        var spans = new List<(int Start, int End, KnowledgeHit Hit)>();
        foreach (var hit in preferred.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var words = hit.Rule.Term.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var compactScript = sourceLang.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
                sourceLang.Equals("ja", StringComparison.OrdinalIgnoreCase) || sourceLang.Equals("ko", StringComparison.OrdinalIgnoreCase);
            var boundary = compactScript ? @"A-Za-z0-9_À-ɏḀ-ỿ" : @"\p{L}\p{N}_";
            var pattern = "(?<![" + boundary + "])" + string.Join(@"\s+", words.Select(Regex.Escape)) + "(?![" + boundary + "])";
            foreach (Match match in Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                         TimeSpan.FromMilliseconds(100)))
                spans.Add((match.Index, match.Index + match.Length, hit));
        }
        var accepted = new List<(int Start, int End, KnowledgeHit Hit)>();
        foreach (var span in spans.OrderByDescending(s => s.End - s.Start).ThenBy(s => s.Start))
            if (!accepted.Any(a => span.Start < a.End && a.Start < span.End)) accepted.Add(span);
        var hits = accepted.OrderBy(s => s.Start).Select(s => s.Hit).DistinctBy(h => (h.LibraryId, h.Rule.Id)).ToArray();
        var names = hits.Select(hit=>Normalize(hit.Rule.Term)).ToHashSet();
        return new(hits, conflicts, candidates.Where(hit=>names.Contains(Normalize(hit.Rule.Term))).ToArray());
    }
}

public static class KnowledgeTermImport
{
    // Preview only: never modifies the database. Existing rules are never silently overwritten.
    public static TermImportPreview Parse(string content, char delimiter = ',', IEnumerable<KnowledgeTerm>? existing = null)
    {
        if (content.Length > 2 * 1024 * 1024) throw new ArgumentException("术语表不能超过 2 MB 字符。");
        if (delimiter is not (',' or '\t')) throw new ArgumentException("仅支持 CSV 或 TSV。");
        var terms = new List<KnowledgeTerm>();
        var errors = new List<TermImportError>();
        var names = new HashSet<string>((existing ?? []).Select(t => KnowledgeRules.Normalize(t.Term)), StringComparer.Ordinal);
        using var parser = new TextFieldParser(new StringReader(content.TrimStart('\uFEFF')))
        {
            TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true
        };
        parser.SetDelimiters(delimiter.ToString());
        try
        {
            if (parser.EndOfData) return new([], [new(1, "文件为空。")]);
            var header = parser.ReadFields()!;
            var termColumn = Array.FindIndex(header, h => h.Equals("term", StringComparison.OrdinalIgnoreCase));
            var translationColumn = Array.FindIndex(header, h => h.Equals("translation", StringComparison.OrdinalIgnoreCase));
            var noteColumn = Array.FindIndex(header, h => h.Equals("note", StringComparison.OrdinalIgnoreCase));
            if (termColumn < 0 || translationColumn < 0 || header.Distinct(StringComparer.OrdinalIgnoreCase).Count() != header.Length)
                return new([], [new(1, "表头需要唯一的 term、translation 列，可选 note 列。")]);
            while (!parser.EndOfData)
            {
                var line = parser.LineNumber;
                var row = parser.ReadFields()!;
                if (row.Length != header.Length) { errors.Add(new(line, "列数与表头不一致。")); continue; }
                var term = new KnowledgeTerm(Guid.NewGuid().ToString("N"), row[termColumn], row[translationColumn],
                    noteColumn < 0 ? "" : row[noteColumn]);
                try { KnowledgeRules.ValidateTerm(term); }
                catch (ArgumentException ex) { errors.Add(new(line, ex.Message)); continue; }
                if (!names.Add(KnowledgeRules.Normalize(term.Term))) { errors.Add(new(line, "术语与现有条目或前面的行重复。")); continue; }
                terms.Add(term);
                if (names.Count > KnowledgeRules.MaxTerms) { errors.Add(new(line, "每库最多 5000 条术语。")); break; }
            }
        }
        catch (MalformedLineException) { errors.Add(new(parser.ErrorLineNumber, "引号或分隔符格式错误。")); }
        return new(terms, errors);
    }
}

