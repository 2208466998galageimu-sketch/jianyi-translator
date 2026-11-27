using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Thanslator.Core;

namespace Thanslator.Dictionary;

/// <summary>
/// Local ECDICT lookup. The database is expected at
/// assets/dictionary/dict-en-zh.sqlite next to the application.
/// </summary>
public sealed partial class DictionaryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _databasePath;
    private readonly string _overridesPath;
    private readonly IReadOnlyList<string> _extraOverridesPaths;
    private readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<DictionarySense>>> _overrides;

    public DictionaryService(
        string? databasePath = null,
        string? overridesPath = null,
        string? extraOverridesPath = null)
    {
        _databasePath = databasePath ?? AppPaths.DictionaryDatabase;
        _overridesPath = overridesPath ?? AppPaths.FunctionWordsOverrides;

        var extras = new List<string>();
        if (overridesPath is null)
        {
            extras.Add(AppPaths.DictionaryOverrides);
            extras.Add(AppPaths.UserDictionaryOverrides);
        }

        if (!string.IsNullOrWhiteSpace(extraOverridesPath))
        {
            extras.Add(extraOverridesPath);
        }

        _extraOverridesPaths = extras;
        _overrides = new Lazy<IReadOnlyDictionary<string, IReadOnlyList<DictionarySense>>>(LoadOverrides);
    }

    public string DatabasePath => _databasePath;

    public bool IsAvailable => File.Exists(_databasePath);

    public async Task<DictionaryResult> LookupAsync(
        string text,
        string targetLang = "zh-Hans",
        CancellationToken cancellationToken = default)
    {
        var query = NormalizeQuery(text);
        if (query.Length == 0)
        {
            return DictionaryResult.NotFound(text, "没有可查询的文本");
        }

        if (!IsChineseTarget(targetLang))
        {
            return DictionaryResult.Unsupported(
                query,
                $"M2 本地词典暂只提供 zh-Hans 释义（当前目标语言：{targetLang}）");
        }

        var kind = TextClassifier.Classify(text);
        if (kind is TextKind.Sentence or TextKind.Paragraph)
        {
            return DictionaryResult.NotFound(
                query,
                "当前为本地词典模式：只处理单词/短语；句子可以在托盘菜单的「翻译模型」中配置翻译");
        }

        if (!IsAvailable)
        {
            return DictionaryResult.NotFound(
                query,
                "本地词典尚未安装（assets/dictionary/dict-en-zh.sqlite）");
        }

        var (exact, lemma) = await FindEntriesAsync(query, cancellationToken);
        var dbEntry = exact;
        var usedLemma = false;
        var parsedSenses = exact is null
            ? new List<DictionarySense>()
            : ParseTranslation(exact.Translation).ToList();

        // Many ECDICT inflected entries contain only "X的过去式" notes. In
        // that case the exact row has no usable glosses, so use its lemma.
        if (parsedSenses.Count == 0 && lemma is not null)
        {
            dbEntry = lemma;
            usedLemma = true;
            parsedSenses = ParseTranslation(lemma.Translation).ToList();
        }

        var hasOverride = _overrides.Value.TryGetValue(query, out var overrideSenses);
        if (!hasOverride && dbEntry is not null)
        {
            var lemmaKey = WordNormalizer.NormalizeLookup(dbEntry.Word);
            if (!string.Equals(lemmaKey, query, StringComparison.Ordinal))
            {
                hasOverride = _overrides.Value.TryGetValue(lemmaKey, out overrideSenses);
            }
        }

        if (dbEntry is null && !hasOverride)
        {
            return DictionaryResult.NotFound(query, "词典未收录");
        }

        var senses = new List<DictionarySense>();
        if (hasOverride && overrideSenses is not null)
        {
            senses.AddRange(overrideSenses);
        }

        senses.AddRange(parsedSenses);
        senses = MergeSenses(senses).Take(6).ToList();
        if (senses.Count == 0)
        {
            return DictionaryResult.NotFound(query, "词典条目没有可显示的释义");
        }

        return new DictionaryResult(
            query,
            dbEntry?.Word ?? query,
            dbEntry?.Phonetic,
            senses,
            true,
            hasOverride
                ? dbEntry is null ? "人工校订" : "ECDICT + 校订"
                : usedLemma ? "ECDICT（词形还原）" : "ECDICT");
    }

    private async Task<(DbEntry? Exact, DbEntry? Lemma)> FindEntriesAsync(
        string query,
        CancellationToken cancellationToken)
    {
        await using var connection = OpenReadOnly();
        await connection.OpenAsync(cancellationToken);

        var exact = await FindByWordLowerAsync(connection, query, cancellationToken);
        var formWord = await FindFormLemmaAsync(connection, query, cancellationToken);
        if (formWord is null)
        {
            return (exact, null);
        }

        var lemmaKey = WordNormalizer.NormalizeLookup(formWord);
        if (string.Equals(lemmaKey, query, StringComparison.Ordinal))
        {
            return (exact, null);
        }

        var lemma = await FindByWordLowerAsync(connection, lemmaKey, cancellationToken);
        return (exact, lemma);
    }

    private static async Task<DbEntry?> FindByWordLowerAsync(
        SqliteConnection connection,
        string wordLower,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT word, phonetic, translation
            FROM entries
            WHERE word_lower = $query
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$query", wordLower);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DbEntry(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2));
    }

    private static async Task<string?> FindFormLemmaAsync(
        SqliteConnection connection,
        string form,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT word FROM forms WHERE form = $query LIMIT 1;";
        command.Parameters.AddWithValue("$query", form);

        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private SqliteConnection OpenReadOnly()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true
        };
        return new SqliteConnection(builder.ToString());
    }

    private IReadOnlyDictionary<string, IReadOnlyList<DictionarySense>> LoadOverrides()
    {
        var result = new Dictionary<string, IReadOnlyList<DictionarySense>>(StringComparer.OrdinalIgnoreCase);
        LoadOverrideFile(result, _overridesPath);
        foreach (var path in _extraOverridesPaths)
        {
            if (!string.Equals(path, _overridesPath, StringComparison.OrdinalIgnoreCase))
            {
                LoadOverrideFile(result, path);
            }
        }

        return result;
    }

    private static void LoadOverrideFile(
        Dictionary<string, IReadOnlyList<DictionarySense>> target,
        string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            var raw = JsonSerializer.Deserialize<Dictionary<string, List<CuratedSense>>>(json, JsonOptions);
            if (raw is null)
            {
                return;
            }

            foreach (var (word, senses) in raw)
            {
                target[word] = senses
                    .Where(s => s.Glosses is { Length: > 0 })
                    .Select(s => new DictionarySense(
                        string.IsNullOrWhiteSpace(s.Pos) ? "其他" : s.Pos,
                        s.Glosses!))
                    .ToArray();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load dictionary overrides '{path}': {ex.Message}");
        }
    }

    private static IEnumerable<DictionarySense> ParseTranslation(string rawTranslation)
    {
        var normalized = rawTranslation
            .Replace("\\r\\n", "\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        foreach (var rawLine in normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            line = DomainTagRegex().Replace(line, string.Empty).Trim();
            if (line.Length == 0 || InflectionNoteRegex().IsMatch(line))
            {
                continue;
            }

            var pos = "其他";
            var match = PosRegex().Match(line);
            if (match.Success)
            {
                pos = NormalizePos(match.Groups["pos"].Value);
                line = match.Groups["gloss"].Value.Trim();
            }

            if (line.Length == 0)
            {
                continue;
            }

            var glosses = line
                .Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries)
                .Select(g => g.Trim().TrimStart('-', ' '))
                .Where(g => g.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Take(5)
                .ToArray();

            if (glosses.Length > 0)
            {
                yield return new DictionarySense(pos, glosses);
            }
        }
    }

    private static List<DictionarySense> MergeSenses(IEnumerable<DictionarySense> senses)
    {
        var merged = new List<DictionarySense>();
        foreach (var sense in senses)
        {
            var existingIndex = merged.FindIndex(s =>
                string.Equals(s.PartOfSpeech, sense.PartOfSpeech, StringComparison.Ordinal));
            if (existingIndex < 0)
            {
                merged.Add(sense);
                continue;
            }

            var existing = merged[existingIndex];
            var glosses = existing.Glosses
                .Concat(sense.Glosses)
                .Distinct(StringComparer.Ordinal)
                .Take(5)
                .ToArray();
            merged[existingIndex] = existing with { Glosses = glosses };
        }

        return merged;
    }

    private static string NormalizeQuery(string text)
    {
        var normalized = text
            .Replace('’', '\'')
            .Replace('‘', '\'')
            .Trim()
            .Trim(',', '!', '?', ';', ':', '，', '。', '！', '？', '；', '：')
            .TrimEnd('.');

        normalized = WhitespaceRegex().Replace(normalized, " ").Trim();
        return WordNormalizer.NormalizeLookup(normalized);
    }

    private static bool IsChineseTarget(string lang) =>
        lang.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase) ||
        lang.Equals("zh-CN", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePos(string pos) => pos.ToLowerInvariant() switch
    {
        "n" => "n.",
        "v" => "v.",
        "vt" => "vt.",
        "vi" => "vi.",
        "a" or "adj" => "adj.",
        "adv" => "adv.",
        "prep" => "prep.",
        "pron" => "pron.",
        "conj" => "conj.",
        "num" => "num.",
        "art" => "art.",
        "int" => "int.",
        "aux" => "aux.",
        "abbr" => "abbr.",
        var other => other + "."
    };

    [GeneratedRegex(@"^\s*\[[^\]]+\]\s*", RegexOptions.Compiled)]
    private static partial Regex DomainTagRegex();

    [GeneratedRegex(
        @"^(?<pos>n|v|vt|vi|a|adj|adv|prep|pron|conj|num|art|int|aux|abbr)\.\s*(?<gloss>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex PosRegex();

    [GeneratedRegex(
        @"的?(过去式|过去分词|现在分词|现在式|复数|比较级|最高级|缩写|名词复数|动词过去式|动词过去分词)",
        RegexOptions.Compiled)]
    private static partial Regex InflectionNoteRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();

    private sealed record DbEntry(string Word, string? Phonetic, string Translation);

    private sealed record CuratedSense(string Pos, string[] Glosses);
}
