using System.Text.RegularExpressions;
using Thanslator.Core;

namespace Thanslator.Translation;

/// <summary>
/// Order-preserving English tokenizer for literal glossing. Whitespace and
/// punctuation are retained as tokens so the output can keep the original
/// layout; URLs are treated as opaque.
/// </summary>
public static partial class TextTokenizer
{
    public static IReadOnlyList<TextToken> Tokenize(string text)
    {
        var tokens = new List<TextToken>();
        foreach (Match match in TokenRegex().Matches(text))
        {
            if (match.Groups["ws"].Success)
            {
                tokens.Add(new TextToken(match.Value, TextTokenKind.Whitespace));
            }
            else if (match.Groups["url"].Success ||
                     match.Groups["email"].Success ||
                     match.Groups["path"].Success ||
                     match.Groups["code"].Success ||
                     match.Groups["file"].Success)
            {
                tokens.Add(new TextToken(match.Value, TextTokenKind.Opaque));
            }
            else if (match.Groups["word"].Success)
            {
                tokens.Add(new TextToken(match.Value, TextTokenKind.Word));
            }
            else if (match.Groups["num"].Success)
            {
                tokens.Add(new TextToken(match.Value, TextTokenKind.Number));
            }
            else
            {
                tokens.Add(new TextToken(match.Value, TextTokenKind.Punctuation));
            }
        }

        return tokens;
    }

    public static string NormalizeForLookup(string surface) => WordNormalizer.NormalizeLookup(surface);

    [GeneratedRegex(
        @"(?<ws>\s+)" +
        @"|(?<url>https?://\S+)" +
        @"|(?<email>[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,})" +
        @"|(?<path>[A-Za-z]:\\[^\s]+|\\\\[^\s]+)" +
        @"|(?<code>`[^`\r\n]+`)" +
        @"|(?<file>\b[\w.\-]+\.(?:exe|dll|json|txt|md|log|db|zip|png|jpg|jpeg|gif|pdf|cs|xaml|py|js|ts|html|css|xml|yml|yaml|ini|cfg|sqlite)\b)" +
        @"|(?<word>[A-Za-z]+(?:['’\-._&][A-Za-z0-9]+)*(?:[+#]+)?)" +
        @"|(?<num>\d+(?:[.,:]\d+)*)" +
        @"|(?<punct>[^\s])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();
}
