using System.Text.Json;

namespace Thanslator.Translation;

/// <summary>
/// 生硬模式的优先释义表：词形归一化后的词 → 优先使用的中文释义。
/// 用于覆盖 ECDICT 第一义项不是「本义/最常用义」的情况；用户表优先于内置表。
/// </summary>
public sealed class PrimaryMeaningTable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IReadOnlyDictionary<string, string> _entries;

    private PrimaryMeaningTable(IReadOnlyDictionary<string, string> entries) => _entries = entries;

    public static PrimaryMeaningTable Empty { get; } =
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public static PrimaryMeaningTable FromEntries(IReadOnlyDictionary<string, string> entries) =>
        new(new Dictionary<string, string>(entries, StringComparer.OrdinalIgnoreCase));

    public static PrimaryMeaningTable Load(string path)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        MergeFile(merged, path);
        return new PrimaryMeaningTable(merged);
    }

    public static PrimaryMeaningTable LoadDefault()
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        MergeFile(merged, Core.AppPaths.PrimaryMeanings);
        MergeFile(merged, Core.AppPaths.UserPrimaryMeanings);
        return new PrimaryMeaningTable(merged);
    }

    public bool TryGet(string normalizedWord, out string gloss) =>
        _entries.TryGetValue(normalizedWord, out gloss!);

    private static void MergeFile(Dictionary<string, string> target, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
            if (entries is null)
            {
                return;
            }

            foreach (var (word, gloss) in entries)
            {
                if (!string.IsNullOrWhiteSpace(word) && !string.IsNullOrWhiteSpace(gloss))
                {
                    target[word.Trim()] = gloss.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load primary meanings '{path}': {ex.Message}");
        }
    }
}
