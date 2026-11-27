namespace Thanslator.Core;

public enum SelectionCaptureMethod
{
    UiaTextPattern,
    IAccessible2,
    Clipboard,
    Hotkey,
    Ocr
}

/// <summary>Screen-space rectangle in physical pixels.</summary>
public readonly record struct RectD(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;

    public bool Contains(double x, double y, double slop = 0)
        => x >= Left - slop && x <= Right + slop && y >= Top - slop && y <= Bottom + slop;

    public RectD Union(RectD other)
    {
        var left = Math.Min(Left, other.Left);
        var top = Math.Min(Top, other.Top);
        var right = Math.Max(Right, other.Right);
        var bottom = Math.Max(Bottom, other.Bottom);
        return new RectD(left, top, right - left, bottom - top);
    }
}

public sealed record SelectionSnapshot(
    string Text,
    IReadOnlyList<RectD> LineRects,
    nint ForegroundHwnd,
    string ProcessName,
    SelectionCaptureMethod Method,
    long CapturedAtMs,
    bool HasPreciseGeometry = true,
    double? RecognitionConfidence = null,
    bool IsManualOcr = false,
    string? OcrTranslationText = null)
{
    /// <summary>按真实行矩形命中，避免多行包围盒把行间空白也当选区。</summary>
    public bool ContainsPoint(double x, double y, double slop = 0) =>
        LineRects.Any(rect => rect.Contains(x, y, slop));

    public RectD Bounds => LineRects.Count == 0
        ? new RectD(0, 0, 0, 0)
        : LineRects.Aggregate((a, b) => a.Union(b));
}
