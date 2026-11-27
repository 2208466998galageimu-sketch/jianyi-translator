using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Thanslator.Core;

namespace Thanslator.Capture;

/// <summary>在独立 MTA 工作者读取 UIA；优先就近目标，后备遍历有界。</summary>
public sealed class UiaSelectionReader
{
    // shortcut: 祖先最多 128 层；若真实界面仍触顶，再评估独立取词 helper。
    private const int MaxAncestorDepth = 128;
    public static Action<string>? Diagnostic { get; set; }

    public CaptureResult Capture(nint hwnd, bool requireNearCursor, CancellationToken cancellationToken)
    {
        var allowed = CaptureSecurity.CheckWindow(hwnd);
        if (allowed != CaptureStatus.Success) return new CaptureResult(allowed);
        var privacyChecked = false;
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return new CaptureResult(CaptureStatus.Forbidden);
            var focused = AutomationElement.FocusedElement;
            var pointed = NativeMethods.GetCursorPos(out var cursor)
                ? AutomationElement.FromPoint(new System.Windows.Point(cursor.X, cursor.Y)) : null;
            // 密码检查在任何 GetSelection/GetText 之前，失败不得转剪贴板。
            var focusedHere = BelongsToRoot(focused, root);
            var pointedHere = BelongsToRoot(pointed, root);
            if ((!focusedHere && !pointedHere) || (focusedHere && IsPassword(focused)) ||
                (pointedHere && IsPassword(pointed)))
                return new CaptureResult(CaptureStatus.Forbidden);
            privacyChecked = true;

            var noSelection = false;
            CaptureResult? evidence = null;
            foreach (var element in EnumerateCandidates(root, focused, pointed, requireNearCursor, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsPassword(element)) continue;
                var result = Read(element, hwnd, requireNearCursor, cancellationToken);
                if (result.Status == CaptureStatus.Success) return result;
                var primary = requireNearCursor ? pointed : focused;
                noSelection |= result.Status == CaptureStatus.NoSelection && primary is not null &&
                    Automation.Compare(element, primary);
                if (result.HasSelection) evidence = result;
            }
            return evidence ?? new CaptureResult(noSelection ? CaptureStatus.NoSelection : CaptureStatus.Unsupported);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or
            System.Runtime.InteropServices.COMException)
        {
            Diagnostic?.Invoke($"uia exception {ex.GetType().Name}");
            return new CaptureResult(privacyChecked ? CaptureStatus.Unsupported : CaptureStatus.Forbidden);
        }
    }

    internal CaptureResult CheckOcrRegion(nint hwnd, RectD region, CancellationToken token)
    {
        var status = CaptureSecurity.CheckWindow(hwnd);
        if (status != CaptureStatus.Success) return new(status);
        var root = AutomationElement.FromHandle(hwnd);
        if (root is null || IsPassword(root)) return new(CaptureStatus.Forbidden);
        var protectedFields = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsPasswordProperty, true));
        foreach (AutomationElement field in protectedFields)
        {
            token.ThrowIfCancellationRequested();
            if (field.Current.IsOffscreen) continue;
            var bounds = field.Current.BoundingRectangle;
            if (bounds.IsEmpty ||
                (bounds.Left < region.Right && bounds.Right > region.Left &&
                 bounds.Top < region.Bottom && bounds.Bottom > region.Top))
                return new(CaptureStatus.Forbidden);
        }
        return new(CaptureSecurity.CheckWindow(hwnd));
    }

    private static IEnumerable<AutomationElement> EnumerateCandidates(AutomationElement root,
        AutomationElement? focused, AutomationElement? pointed, bool nearCursor, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var first = nearCursor ? pointed : focused;
        var second = nearCursor ? focused : pointed;
        foreach (var origin in new[] { first, second, root })
        {
            if (!BelongsToRoot(origin, root)) continue;
            var current = origin;
            for (var depth = 0; current is not null && depth < MaxAncestorDepth; depth++)
            {
                token.ThrowIfCancellationRequested();
                if (watch.ElapsedMilliseconds > 700) yield break;
                if (seen.Add(string.Join(',', current.GetRuntimeId()))) yield return current;
                if (Automation.Compare(current, root)) break;
                current = Parent(current);
            }
        }

        // 不调用 FindAll(Descendants)：限制实际访问节点数，而不只是结果条数。
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);
        var visited = 0;
        while (queue.Count > 0 && visited < 96 && watch.ElapsedMilliseconds <= 700)
        {
            var child = Child(queue.Dequeue());
            while (child is not null && visited++ < 96 && watch.ElapsedMilliseconds <= 700)
            {
                token.ThrowIfCancellationRequested();
                if (seen.Add(string.Join(',', child.GetRuntimeId()))) yield return child;
                queue.Enqueue(child);
                child = Sibling(child);
            }
        }
    }

    private static CaptureResult Read(AutomationElement element, nint hwnd, bool nearCursor, CancellationToken token)
    {
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) || pattern is not TextPattern textPattern)
                return new CaptureResult(CaptureStatus.Unsupported);
            var ranges = textPattern.GetSelection();
            var rects = new List<RectD>();
            var texts = new List<string>();
            var selected = false;
            foreach (var range in ranges)
            {
                token.ThrowIfCancellationRequested();
                if (range.CompareEndpoints(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End) == 0) continue;
                selected = true;
                foreach (var rect in range.GetBoundingRectangles())
                    if (rect.Width > 0 && rect.Height > 0 && double.IsFinite(rect.Left) && double.IsFinite(rect.Top) &&
                        double.IsFinite(rect.Width) && double.IsFinite(rect.Height))
                        rects.Add(new RectD(rect.Left, rect.Top, rect.Width, rect.Height));
                try
                {
                    var value = range.GetText(-1);
                    if (!string.IsNullOrWhiteSpace(value)) texts.Add(value.TrimEnd('\r', '\n'));
                }
                catch (InvalidOperationException) { }
                catch (System.Runtime.InteropServices.COMException) { }
            }
            if (!selected) return new CaptureResult(CaptureStatus.NoSelection);
            var precise = rects.Count > 0;
            if (nearCursor && (!NativeMethods.GetCursorPos(out var point) ||
                !rects.Any(rect => rect.Contains(point.X, point.Y, 8))))
                return new CaptureResult(CaptureStatus.Unsupported);
            if (!precise && NativeMethods.GetCursorPos(out var anchor)) rects.Add(new RectD(anchor.X, anchor.Y, 1, 1));
            var text = string.Join(Environment.NewLine, texts).Trim();
            var snapshot = new SelectionSnapshot(text, rects, hwnd, ProcessName(hwnd),
                SelectionCaptureMethod.UiaTextPattern, Environment.TickCount64, precise);
            return new CaptureResult(text.Length > 0 ? CaptureStatus.Success : CaptureStatus.Unsupported, snapshot, true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or
            System.Runtime.InteropServices.COMException)
        {
            Diagnostic?.Invoke($"uia read exception {ex.GetType().Name}");
            return new CaptureResult(CaptureStatus.Unsupported);
        }
    }

    private static bool BelongsToRoot(AutomationElement? element, AutomationElement root)
    {
        for (var depth = 0; element is not null && depth < MaxAncestorDepth; depth++)
        {
            if (Automation.Compare(element, root)) return true;
            element = Parent(element);
        }
        return false;
    }

    private static bool IsPassword(AutomationElement? element)
    {
        for (var depth = 0; element is not null && depth < MaxAncestorDepth; depth++)
        {
            try
            {
                if (element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty) is true) return true;
                element = Parent(element);
            }
            catch (ElementNotAvailableException) { return true; }
        }
        return false;
    }

    private static AutomationElement? Parent(AutomationElement element) => Navigate(() => TreeWalker.ControlViewWalker.GetParent(element));
    private static AutomationElement? Child(AutomationElement element) => Navigate(() => TreeWalker.ControlViewWalker.GetFirstChild(element));
    private static AutomationElement? Sibling(AutomationElement element) => Navigate(() => TreeWalker.ControlViewWalker.GetNextSibling(element));
    private static AutomationElement? Navigate(Func<AutomationElement?> read)
    {
        try { return read(); }
        catch (ElementNotAvailableException) { return null; }
    }

    private static string ProcessName(nint hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        try { using var process = Process.GetProcessById((int)pid); return process.ProcessName; }
        catch (ArgumentException) { return "unknown"; }
    }
}
