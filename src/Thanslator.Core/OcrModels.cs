namespace Thanslator.Core;

/// <summary>OCR 检测到的可见文本行，坐标与输入图片一致。</summary>
public sealed record OcrLine(string Text, RectD Bounds, double Confidence);

/// <summary>用户指向或确实选中的文字块；分数不是准确率。</summary>
public sealed record OcrBlock(IReadOnlyList<OcrLine> Lines)
{
    public string Text => string.Join('\n', Lines.Select(line => string.IsNullOrWhiteSpace(line.Text) ? "\uFFFD" : line.Text));
    public double Confidence => Lines.Min(line => line.Confidence);

    /// <summary>仅供通序翻译使用；识别原文和行位置仍保留。</summary>
    public string GetTranslationText()
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            var current = string.IsNullOrWhiteSpace(line.Text) ? "\uFFFD" : line.Text;
            if (i == 0 || !CanJoin(Lines[i - 1], line))
            {
                if (i > 0) text.Append('\n');
                text.Append(current);
                continue;
            }

            var previous = Lines[i - 1].Text.TrimEnd();
            while (text.Length > 0 && char.IsWhiteSpace(text[^1])) text.Length--;
            if (previous.EndsWith('\u00AD')) text.Length--;
            else if (!previous.EndsWith('-')) text.Append(' ');
            text.Append(current.TrimStart());
        }
        return text.ToString();
    }

    private static bool CanJoin(OcrLine upper, OcrLine lower)
    {
        var a = upper.Bounds;
        var b = lower.Bounds;
        var height = Math.Min(a.Height, b.Height);
        var gap = b.Top - a.Bottom;
        if (upper.Confidence < .8 || lower.Confidence < .8 ||
            height < Math.Max(a.Height, b.Height) * .75 ||
            Math.Abs(a.Left - b.Left) > height * .4 ||
            a.Width < b.Width * .7 ||
            gap < -height * .2 || gap > height * .65) return false;

        var left = upper.Text.TrimEnd();
        var right = lower.Text.TrimStart();
        if (left.Length < 3 || right.Length < 2 ||
            !char.IsAsciiLetter(left[0]) || !char.IsAsciiLetterLower(right[0]) ||
            !left.Any(char.IsWhiteSpace) ||
            !left.All(IsProseCharacter) || !right.All(IsProseCharacter)) return false;

        // 避免吞掉句号、列表、标题和代码行。普通连字符可能是术语的一部分。
        if (left[^1] is '-' or '\u00AD')
            return char.IsAsciiLetter(left[^2]);
        return char.IsAsciiLetterOrDigit(left[^1]) || left[^1] == ',';
    }

    private static bool IsProseCharacter(char c) =>
        char.IsAsciiLetterOrDigit(c) || char.IsWhiteSpace(c) ||
        c is ',' or '.' or '!' or '?' or '\'' or '’' or '"' or '“' or '”' or '-' or '\u00AD';
}

public static class OcrCapturePolicy
{
    public static bool CanFallback(CaptureResult result, bool explicitRequest) =>
        result.Status is CaptureStatus.NoSelection or CaptureStatus.Unsupported or CaptureStatus.ClipboardUnavailable &&
        (explicitRequest || (result.Status is CaptureStatus.Unsupported or CaptureStatus.ClipboardUnavailable &&
            result is { HasSelection: true, Snapshot.HasPreciseGeometry: true }));
}

/// <summary>以命中行约束同栏邻行；不把全图 OCR 排序当作阅读顺序。</summary>
public static class OcrBlockSelector
{
    public static OcrBlock? AtPoint(IReadOnlyList<OcrLine> lines, double x, double y)
    {
        var seed = lines.Where(line => line.Bounds.Contains(x, y, 4))
            .OrderBy(line => line.Bounds.Width * line.Bounds.Height).FirstOrDefault();
        if (seed is null) return null;
        var column = lines.Where(line => SameColumn(seed.Bounds, line.Bounds))
            .OrderBy(line => line.Bounds.Top).ToArray();
        var index = Array.IndexOf(column, seed);
        var first = index;
        var last = index;
        // shortcut: 指向入口最多取 16 行；整页 OCR 应另设主动入口。
        while (first > 0 && last - first < 15 && Adjacent(column[first - 1].Bounds, column[first].Bounds)) first--;
        while (last + 1 < column.Length && last - first < 15 && Adjacent(column[last].Bounds, column[last + 1].Bounds)) last++;
        return new OcrBlock(column[first..(last + 1)]);
    }

    public static OcrBlock? InSelection(IReadOnlyList<OcrLine> lines, IReadOnlyList<RectD> selection)
    {
        var selected = lines.Where(line => selection.Any(rect => rect.Contains(
                line.Bounds.Left + line.Bounds.Width / 2, line.Bounds.Top + line.Bounds.Height / 2)))
            .OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left).ToArray();
        return selected.Length == 0 ? null : new OcrBlock(selected);
    }

    private static bool SameColumn(RectD seed, RectD other) =>
        Math.Abs(seed.Left - other.Left) <= Math.Max(12, Math.Min(seed.Height, other.Height)) ||
        Math.Min(seed.Right, other.Right) - Math.Max(seed.Left, other.Left) >= Math.Min(seed.Width, other.Width) * .7;

    private static bool Adjacent(RectD upper, RectD lower) => lower.Top >= upper.Top &&
        lower.Top - upper.Bottom <= Math.Max(8, Math.Min(upper.Height, lower.Height) * .9);
}
