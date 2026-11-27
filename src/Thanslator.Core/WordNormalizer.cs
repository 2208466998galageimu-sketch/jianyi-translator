namespace Thanslator.Core;

public static class WordNormalizer
{
    /// <summary>
    /// Normalizes a single surface token for dictionary lookup. Keeps the
    /// operation small and shared by normal lookup and literal glossing.
    /// </summary>
    public static string NormalizeLookup(string surface)
    {
        var normalized = surface.Replace('’', '\'').Trim();
        while (normalized.Length >= 2 && IsPair(normalized[0], normalized[^1]))
        {
            normalized = normalized[1..^1].Trim();
        }
        if (normalized.EndsWith("'s", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^2];
        }

        return normalized.ToLowerInvariant();
    }

    private static bool IsPair(char first, char last) => (first, last) is
        ('(', ')') or ('[', ']') or ('{', '}') or ('“', '”') or ('‘', '\'') or
        ('"', '"') or ('\'', '\'') or ('（', '）');
}
