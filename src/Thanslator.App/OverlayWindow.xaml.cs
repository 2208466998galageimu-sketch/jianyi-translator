using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using Thanslator.App.Interop;
using Thanslator.Core;

namespace Thanslator.App;

public partial class OverlayWindow : Window
{
    private SelectionSnapshot? _snapshot;
    private readonly System.Windows.Threading.DispatcherTimer _lifetime = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private long _lifetimeTick;
    private double _remainingMs;

    public void StartManualLifetime(SelectionSnapshot snapshot, AppSettings settings)
    {
        if (!IsVisible || !ReferenceEquals(_snapshot, snapshot)) return;
        var count = System.Text.RegularExpressions.Regex.Matches(snapshot.Text,
            @"[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}]|[\p{L}\p{N}_]+").Count;
        var shortLimit = Math.Clamp(settings.OcrShortWords, 1, 10000);
        var mediumLimit = Math.Clamp(settings.OcrMediumWords, shortLimit + 1, 10001);
        var seconds = count <= shortLimit ? settings.OcrShortSeconds :
            count <= mediumLimit ? settings.OcrMediumSeconds : settings.OcrLongSeconds;
        _remainingMs = Math.Clamp(seconds, 1, 86400) * 1000.0;
        _lifetimeTick = Environment.TickCount64;
        _lifetime.Start();
    }

    private void LifetimeTick(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        var elapsed = now - _lifetimeTick;
        _lifetimeTick = now;
        if (IsMouseOver || OcrOriginalText.IsKeyboardFocusWithin || AdjustmentPanel.IsKeyboardFocusWithin) return;
        _remainingMs -= elapsed;
        if (_remainingMs <= 0)
        {
            HideOverlay();
            Dismissed?.Invoke(this, EventArgs.Empty);
        }
    }

    public event Action<SelectionSnapshot>? OcrEditing;
    public event Action<SelectionSnapshot, string>? OcrTextCorrected;
    public OverlayWindow()
    {
        InitializeComponent();
        _lifetime.Tick += LifetimeTick;
        Closed += (_, _) => _lifetime.Stop();
    }

    public event EventHandler? Dismissed;
    public event EventHandler? ModelSettingsRequested;

    public Func<RectD?>? ManualOcrAnchor { get; set; }
    public bool IsManualOcrVisible => IsVisible && _snapshot?.IsManualOcr == true;

    public void RepositionManualOcr()
    {
        if (IsManualOcrVisible && ManualOcrAnchor?.Invoke() is { } anchor)
            PositionBelowSelection(new WindowInteropHelper(this).Handle, anchor);
    }

    private static SurfaceMode GetSurface(SelectionSnapshot snapshot, AppSettings settings) =>
        snapshot.IsManualOcr ? SurfaceMode.Panel : settings.SurfaceMode;

    public SurfaceMode CurrentSurface { get; private set; } = SurfaceMode.Tooltip;

    public void ShowSnapshot(SelectionSnapshot snapshot, AppSettings settings)
    {
        var details = "取词成功；词典结果将在下一次查询中显示。";
        ShowCore(
            settings.LiteralMode ? "生硬 · M2 取词" : "M2 取词",
            snapshot.Text,
            details,
            BuildHint(snapshot, "取词"),
            GetSurface(snapshot, settings),
            snapshot);
    }

    public void ShowTranslation(
        SelectionSnapshot snapshot,
        string translation,
        AppSettings settings,
        string providerLabel = "OPUS-MT · 本地 MT", string badge = "本地 MT", bool isLocal = true,
        bool openModelSettings = false)
    {
        ShowCore(
            badge,
            snapshot.Text,
            translation,
            $"{providerLabel}{(isLocal ? " · 本机" : string.Empty)} · {snapshot.ProcessName} · {MethodLabel(snapshot.Method)}",
            GetSurface(snapshot, settings),
            snapshot,
            openModelSettings);
    }

    public void ShowLiteral(SelectionSnapshot snapshot, LiteralGlossResult result, AppSettings settings)
    {
        var hint = $"逐词直译 · 未调整语序 · {result.WordCount} 词 · 未收录 {result.UnknownCount} · " +
                   $"{snapshot.ProcessName} · {MethodLabel(snapshot.Method)}";
        ShowCore(
            "生硬直译 · 本地词典",
            result.OriginalText,
            string.Empty,
            hint,
            GetSurface(snapshot, settings),
            snapshot);
        RenderLiteral(result, GetSurface(snapshot, settings));
    }

    /// <summary>未收录词保留原文并加下划线；不再用 '?' 之类的字符标记。</summary>
    private void RenderLiteral(LiteralGlossResult result, SurfaceMode surface)
    {
        DetailsText.Inlines.Clear();
        var budget = surface == SurfaceMode.Tooltip ? 700 : int.MaxValue;

        if (surface == SurfaceMode.Panel)
        {
            foreach (var unit in result.Units)
            {
                if (budget <= 0)
                {
                    break;
                }

                if (unit.Kind == TextTokenKind.Whitespace)
                {
                    continue;
                }

                if (unit.Kind == TextTokenKind.Punctuation)
                {
                    budget = AddRun(unit.Source, null, budget);
                    DetailsText.Inlines.Add(new LineBreak());
                    continue;
                }

                budget = AddRun(unit.Source, null, budget);
                budget = AddRun("  →  ", null, budget);
                budget = AddRun(unit.Gloss, unit.IsUnknown ? TextDecorations.Underline : null, budget);
                DetailsText.Inlines.Add(new LineBreak());
            }
        }
        else
        {
            foreach (var unit in result.Units)
            {
                if (budget <= 0)
                {
                    break;
                }

                budget = AddRun(unit.Gloss, unit.IsUnknown ? TextDecorations.Underline : null, budget);
            }
        }

        if (DetailsText.Inlines.Count == 0)
        {
            DetailsText.Inlines.Add(new Run("没有可显示的逐词释义"));
        }
    }

    private int AddRun(string text, TextDecorationCollection? decorations, int budget)
    {
        if (string.IsNullOrEmpty(text))
        {
            return budget;
        }

        var value = text;
        var truncated = false;
        if (value.Length > budget)
        {
            value = budget > 0 ? value[..budget] + "…" : "…";
            truncated = true;
        }

        var run = new Run(value);
        if (decorations is not null)
        {
            run.TextDecorations = decorations;
        }

        DetailsText.Inlines.Add(run);
        return truncated ? 0 : budget - value.Length;
    }

    public void ShowLookup(SelectionSnapshot snapshot, DictionaryResult result, AppSettings settings,
        string? providerLabel = null, bool openModelSettings = false)
    {
        var maxSenses = GetSurface(snapshot, settings) == SurfaceMode.Panel ? 6 : 3;
        var source = result.Found ? result.Headword ?? snapshot.Text : snapshot.Text;
        if (!string.IsNullOrWhiteSpace(result.Phonetic))
        {
            source = $"{source}  {result.Phonetic}";
        }

        string details;
        if (result.Found)
        {
            details = FormatSenses(result.Senses.Take(maxSenses));
        }
        else
        {
            details = result.Message ?? "本地词典未找到释义";
        }

        var sourceLabel = providerLabel ?? (result.Found ? result.Source : "本地词典");
        ShowCore(
            providerLabel ?? (settings.LiteralMode ? "生硬 · 本地词典" : "本地词典"),
            source,
            details,
            BuildHint(snapshot, sourceLabel),
            GetSurface(snapshot, settings),
            snapshot,
            openModelSettings);
        if(!result.Found){CopyTranslationButton.Visibility=Visibility.Collapsed;RetryButton.Visibility=Visibility.Visible;}
    }

    public void ShowKnowledge(KnowledgeMatches matches)
    {
        if (matches.Hits.Count == 0) return;
        KnowledgeDetails.Header = $"已提供领域术语 · {matches.Hits.Count} 条";
        KnowledgeText.Text = string.Join("\n", matches.Hits.Select(hit =>
            $"{hit.Rule.Term} → {hit.Rule.Translation}（{hit.LibraryName}）")) +
            (matches.Conflicts.Any(c => matches.Hits.Any(hit =>
                KnowledgeRules.Normalize(hit.Rule.Term) == KnowledgeRules.Normalize(c.Term)))
                ? "\n本次命中存在同词冲突，已按库优先级选择。" : "") +
            "\n以上规则已提供给模型，请核对译文是否采用。";
        KnowledgeDetails.Visibility = Visibility.Visible;
    }

    public void HideOverlay()
    {
        ResetAdjustment();
        _lifetime.Stop();
        KnowledgeDetails.Visibility = Visibility.Collapsed;
        KnowledgeText.Text = "";
        Hide();
    }

    public bool ContainsScreenPoint(int x, int y)
    {
        if (!IsVisible)
        {
            return false;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        return hwnd != 0 &&
               NativeWindowStyles.TryGetWindowRect(hwnd, out var left, out var top, out var right, out var bottom) &&
               x >= left && x <= right && y >= top && y <= bottom;
    }

    private void ShowCore(
        string badge,
        string source,
        string details,
        string hint,
        SurfaceMode surface,
        SelectionSnapshot snapshot,
        bool openModelSettings = false)
    {
        ResetAdjustment();
        _lifetime.Stop();
        KnowledgeDetails.Visibility = Visibility.Collapsed;
        KnowledgeDetails.IsExpanded = false;
        KnowledgeText.Text = "";
        if (openModelSettings) surface = SurfaceMode.Panel;
        ModelSettingsButton.Visibility = openModelSettings ? Visibility.Visible : Visibility.Collapsed;
        CurrentSurface = surface;
        ResultActions.Visibility=surface==SurfaceMode.Panel?Visibility.Visible:Visibility.Collapsed;
        CopyTranslationButton.Visibility=Visibility.Visible;RetryButton.Visibility=Visibility.Collapsed;
        ResultDetails.Visibility=Visibility.Collapsed;ResultDetails.IsExpanded=false;ResultDetailsText.Text=hint;
        var maxSource = surface == SurfaceMode.Tooltip ? 420 : int.MaxValue;
        var maxDetails = surface == SurfaceMode.Tooltip ? 700 : int.MaxValue;
        SourceText.Visibility = snapshot.IsManualOcr ? Visibility.Collapsed : Visibility.Visible;
        SourceText.Text = snapshot.IsManualOcr ? string.Empty : Truncate(source, maxSource);
        DetailsText.Text = Truncate(details, maxDetails);
        BadgeText.Text = badge;
        HintText.Text = hint;
        if (snapshot.Method == SelectionCaptureMethod.Ocr)
        {
            HintText.Text += snapshot.Text.Contains('\uFFFD') ? " · 含未识别行，请校对原文" :
                snapshot.RecognitionConfidence < .8 ? " · 含低分识别，请核对原文" : " · 仅可见区域，请核对原文";
            if (surface == SurfaceMode.Tooltip) HintText.Text += " · 常驻模式可校对";
            if (_snapshot?.Method != SelectionCaptureMethod.Ocr || _snapshot.Text != snapshot.Text ||
                _snapshot.CapturedAtMs != snapshot.CapturedAtMs) OcrOriginalText.Text = snapshot.Text;
        }
        OcrOriginal.Visibility = snapshot.Method == SelectionCaptureMethod.Ocr && surface == SurfaceMode.Panel
            ? Visibility.Visible : Visibility.Collapsed;
        if (_snapshot?.CapturedAtMs != snapshot.CapturedAtMs || _snapshot?.IsManualOcr != snapshot.IsManualOcr)
            OcrOriginal.IsExpanded = snapshot.IsManualOcr;
        _snapshot = snapshot;
        CloseButton.Visibility = surface == SurfaceMode.Panel
            ? Visibility.Visible
            : Visibility.Collapsed;
        Width = surface == SurfaceMode.Panel ? 440 : 360;

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        NativeWindowStyles.ApplyOverlayStyles(hwnd, clickThrough: surface == SurfaceMode.Tooltip);

        Opacity = 0;
        if (!IsVisible)
        {
            Show();
        }
        else
        {
            UpdateLayout();
        }

        var anchor = snapshot.IsManualOcr ? ManualOcrAnchor?.Invoke() : snapshot.Bounds;
        if (anchor is { } bounds) PositionBelowSelection(hwnd, bounds);
        Opacity = 1;
    }

    private static string BuildHint(SelectionSnapshot snapshot, string sourceLabel) =>
        $"{sourceLabel} · {snapshot.ProcessName} · {MethodLabel(snapshot.Method)} · {snapshot.LineRects.Count} 行";

    private static string FormatSenses(IEnumerable<DictionarySense> senses)
    {
        var builder = new StringBuilder();
        foreach (var sense in senses)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(sense.PartOfSpeech);
            builder.Append("  ");
            builder.AppendJoin("；", sense.Glosses);
        }

        return builder.Length == 0 ? "本地词典未找到可显示的释义" : builder.ToString();
    }

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        return text[..max] + "…";
    }

    private void PositionBelowSelection(nint hwnd, RectD selection)
    {
        UpdateLayout();
        var scale = NativeWindowStyles.GetDpiScale(hwnd);
        var widthPx = (int)Math.Ceiling(Width * scale);
        var heightPx = (int)Math.Ceiling(ActualHeight * scale);
        var gap = (int)Math.Round(8 * scale);

        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)selection.Left, (int)selection.Top));
        var work = screen.WorkingArea;

        var left = (int)Math.Round(selection.Left + selection.Width / 2 - widthPx / 2.0);
        var top = (int)Math.Round(selection.Bottom + gap);
        if (top + heightPx > work.Bottom - gap)
        {
            top = (int)Math.Round(selection.Top - heightPx - gap);
        }

        left = Math.Clamp(left, work.Left + gap, Math.Max(work.Left + gap, work.Right - widthPx - gap));
        top = Math.Clamp(top, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - heightPx - gap));
        NativeWindowStyles.Move(hwnd, left, top);
    }

    private static string MethodLabel(SelectionCaptureMethod method) => method switch
    {
        SelectionCaptureMethod.UiaTextPattern => "UIA TextPattern",
        SelectionCaptureMethod.IAccessible2 => "IAccessible2",
        SelectionCaptureMethod.Clipboard => "剪贴板",
        SelectionCaptureMethod.Ocr => "屏幕 OCR",
        _ => "快捷键"
    };

    private void ModelSettingsButton_Click(object sender, RoutedEventArgs e) =>
        ModelSettingsRequested?.Invoke(this, EventArgs.Empty);

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideOverlay();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void OcrOriginalText_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => Activate();

    private void OcrOriginalText_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (_snapshot is { Method: SelectionCaptureMethod.Ocr } snapshot) OcrEditing?.Invoke(snapshot);
    }

    private void OcrTranslate_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is { Method: SelectionCaptureMethod.Ocr } snapshot && !string.IsNullOrWhiteSpace(OcrOriginalText.Text))
            OcrTextCorrected?.Invoke(snapshot, OcrOriginalText.Text.Trim());
    }
}
