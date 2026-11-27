using System.Text;
using System.Text.RegularExpressions;

namespace Thanslator.Translation;

public sealed record LocalMtValidation(bool Allowed, string? Message)
{
    public static LocalMtValidation Ok { get; } = new(true, null);

    public static LocalMtValidation Block(string message) => new(false, message);
}

/// <summary>
/// 本地 MT 是英→中模型。调用前挡住明显不支持/无意义的输入，调用后挡住空译文、
/// 乱码和原样返回，避免把垃圾结果展示给用户；不依赖网络，也不编造内容。
/// </summary>
public static class LocalMtGuard
{
    public static LocalMtValidation ValidateInput(string text, string sourceLanguage = "en", string targetLanguage = "zh-Hans")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return LocalMtValidation.Block("没有可翻译的文本");
        }

        var latin = 0;
        var cjk = 0;
        var digits = 0;
        var otherLetters = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (IsLatin(value))
            {
                latin++;
            }
            else if (IsCjk(value))
            {
                cjk++;
            }
            else if (Rune.IsDigit(rune))
            {
                digits++;
            }
            else if (Rune.IsLetter(rune))
            {
                otherLetters++;
            }
        }

        if (latin == 0 && cjk == 0 && digits == 0 && otherLetters == 0)
        {
            return LocalMtValidation.Block("未识别到可翻译的文本（只有符号或空白）");
        }

        if (sourceLanguage is "en" or "auto" && targetLanguage == "zh-Hans" && cjk >= 2 && cjk > latin * 2)
        {
            return LocalMtValidation.Block("本地模型仅支持英→中，当前文本包含较多中文/日文/韩文，已跳过机器翻译");
        }

        return LocalMtValidation.Ok;
    }

    public static string? ValidateOutput(string source, string translation)
    {
        if (string.IsNullOrWhiteSpace(translation))
        {
            return "本地 MT 返回了空译文";
        }

        if (translation.Contains('�'))
        {
            return "本地 MT 输出包含乱码";
        }

        if (Regex.Matches(translation, @">>[^\r\n]*?<<|[⟦⟧]|\bT\d+\b|__JY[A-Za-z]*(?:_\d+__)?")
            .Any(match => TextProtector.CountOccurrences(translation, match.Value) >
                TextProtector.CountOccurrences(source, match.Value)))
        {
            return "本地 MT 输出包含损坏的内部标记";
        }

        if (Regex.IsMatch(translation, @"(.{4,80}?)\1{3,}", RegexOptions.Singleline) &&
            !Regex.IsMatch(source, @"(.{4,80}?)\1{3,}", RegexOptions.Singleline))
        {
            return "本地 MT 输出异常重复";
        }

        if (!translation.EnumerateRunes().Any(Rune.IsLetterOrDigit))
        {
            return "本地 MT 输出没有有效内容";
        }

        var sourceText = source.Trim();
        var translatedText = translation.Trim();
        if (string.Equals(sourceText, translatedText, StringComparison.OrdinalIgnoreCase) &&
            CountWords(sourceText) >= 2)
        {
            return "本地 MT 未能翻译该文本";
        }

        return null;
    }

    private static bool IsLatin(int value) =>
        (value >= 'A' && value <= 'Z') ||
        (value >= 'a' && value <= 'z') ||
        (value >= 0x00C0 && value <= 0x024F);

    private static bool IsCjk(int value) =>
        (value >= 0x4E00 && value <= 0x9FFF) ||
        (value >= 0x3400 && value <= 0x4DBF) ||
        (value >= 0x3040 && value <= 0x30FF) ||
        (value >= 0xAC00 && value <= 0xD7AF);

    private static int CountWords(string text) =>
        Regex.Split(text, @"\s+", RegexOptions.None).Count(part => part.Length > 0);
}
