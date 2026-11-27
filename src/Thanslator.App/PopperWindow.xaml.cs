using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Thanslator.Core;

namespace Thanslator.App;

public partial class PopperWindow : Window
{
    private const double DockThreshold = 24;
    private const double HandleThickness = 28;
    private const double HandleLength = 64;
    private const double ExpandedWidth = 310;

    private readonly AppSettings _settings;
    private readonly Func<AppSettings, Task> _save;
    private bool _loading;
    private bool _animating;
    private bool _initialized;
    private double _expandedHeight;

    public PopperWindow(AppSettings settings, Func<AppSettings, Task> save)
    {
        _settings = settings;
        _save = save;
        InitializeComponent();
        Loaded += OnLoaded;
        ContentRendered += (_, _) =>
        {
            if (!_initialized)
            {
                PositionFromSettings();
            }
        };
        SizeChanged += (_, _) =>
        {
            if (!_initialized || _animating)
            {
                return;
            }

            if (_settings.PopperDockEdge != PopperDockEdge.None && _settings.PopperDrawerOpen)
            {
                PositionDockedOpen();
            }
            else if (_settings.PopperDockEdge == PopperDockEdge.None)
            {
                if (_settings.PopperLeft is null || _settings.PopperTop is null)
                {
                    PositionFromSettings();
                }
                else
                {
                    ClampToWorkArea();
                }
            }
        };
    }

    public event Action<string>? KnowledgeComboChanged;
    public void SetTranslationContext(IEnumerable<(string Id,string Name)> combinations,string active)
    {
        _loading=true;
        try
        {
            UpdateTranslationButtons();
            KnowledgeComboPicker.Items.Clear();
            KnowledgeComboPicker.Items.Add(new ComboBoxItem{Content="不使用知识库",Tag=""});
            foreach(var combo in combinations)KnowledgeComboPicker.Items.Add(new ComboBoxItem{Content=combo.Name,Tag=combo.Id});
            KnowledgeComboPicker.SelectedItem=KnowledgeComboPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(item=>Equals(item.Tag,active))??KnowledgeComboPicker.Items[0];
            OcrToggle.IsChecked=_settings.OcrEnabled;
            ApplySurfaceUi();
        }
        finally{_loading=false;}
    }
    private void KnowledgeCombo_Changed(object sender,SelectionChangedEventArgs e)
    {if(!_loading&&KnowledgeComboPicker.SelectedItem is ComboBoxItem{Tag:string id})KnowledgeComboChanged?.Invoke(id);}

    public event EventHandler? MoreSettingsRequested;
    public event EventHandler? OcrSettingsRequested;
    private void MoreSettings_Click(object sender, RoutedEventArgs e) => MoreSettingsRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler? RegionOcrRequested;

    private void RegionOcrButton_Click(object sender, RoutedEventArgs e) =>
        RegionOcrRequested?.Invoke(this, EventArgs.Empty);

    public RectD? GetScreenBounds()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        return IsVisible && Interop.NativeWindowStyles.TryGetWindowRect(hwnd, out var left, out var top, out var right, out var bottom)
            ? new RectD(left, top, right - left, bottom - top) : null;
    }

    public void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    public event EventHandler? HiddenToTray;

    /// <summary>
    /// 托盘图标是否可用。Windows 会拒绝低完整性进程（AI 沙箱、受保护模式等）注册托盘图标；
    /// 此时必须禁止隐藏，否则控制条消失且没有入口找回。
    /// </summary>
    public bool CanHideToTray { get; set; } = true;

    /// <summary>托盘右键：在鼠标位置打开控制条自己的右键菜单（与窗口内右键同一个菜单）。</summary>
    public void ShowContextMenuAtMouse()
    {
        if (RootGrid.ContextMenu is not { } menu)
        {
            return;
        }

        if (!IsVisible)
        {
            // 托盘右键时控制条可能还藏着；先显示出来再弹菜单，否则 Popup 没有可用的放置目标。
            ShowFromTray();
        }

        menu.PlacementTarget = RootGrid;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>Shows the Popper from the tray, expanding a docked drawer first.</summary>
    public void ShowFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (_settings.PopperDockEdge != PopperDockEdge.None)
        {
            _settings.PopperDrawerOpen = true;
            ExpandAtEdge(_settings.PopperDockEdge, _settings.PopperDockOffset, animate: true);
            PersistAsync();
            return;
        }

        ExpandedPanel.Visibility = Visibility.Visible;
        HandlePanel.Visibility = Visibility.Collapsed;
        SizeToContent = SizeToContent.Height;
        Width = ExpandedWidth;
        Activate();
    }

    public bool ContainsScreenPoint(int x, int y)
    {
        if (!IsVisible)
        {
            return false;
        }

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        return hwnd != 0 &&
               Interop.NativeWindowStyles.TryGetWindowRect(hwnd, out var left, out var top, out var right, out var bottom) &&
               x >= left && x <= right && y >= top && y <= bottom;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyToControls();
        PositionFromSettings();
        _initialized = true;
    }

    internal void ApplyToControls()
    {
        _loading = true;
        try
        {
            EnabledToggle.IsChecked = _settings.Enabled;
            OcrToggle.IsChecked = _settings.OcrEnabled;
            UpdateClipboardFallbackUi();
            ApplySurfaceUi();
            ApplyModeUi();
        }
        finally
        {
            _loading = false;
        }
    }

    private void PositionFromSettings()
    {
        if (_settings.PopperDockEdge != PopperDockEdge.None)
        {
            if (_settings.PopperDrawerOpen)
            {
                ExpandAtEdge(_settings.PopperDockEdge, _settings.PopperDockOffset, animate: false);
            }
            else
            {
                ApplyCollapsedLayout(
                    _settings.PopperDockEdge,
                    _settings.PopperDockOffset);
            }

            return;
        }

        ExpandedPanel.Visibility = Visibility.Visible;
        HandlePanel.Visibility = Visibility.Collapsed;
        SizeToContent = SizeToContent.Height;
        Width = ExpandedWidth;

        var workArea = SystemParameters.WorkArea;
        if (_settings.PopperLeft is { } left && _settings.PopperTop is { } top)
        {
            Left = left;
            Top = top;
            ClampToWorkArea();
            return;
        }

        Left = workArea.Right - Width - 24;
        Top = workArea.Top + 96;
        ClampToWorkArea();
    }

    private void ClampToWorkArea()
    {
        if (_settings.PopperDockEdge != PopperDockEdge.None)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        Left = Math.Clamp(Left, workArea.Left + 8, Math.Max(workArea.Left + 8, workArea.Right - Width - 8));
        Top = Math.Clamp(Top, workArea.Top + 8, Math.Max(workArea.Top + 8, workArea.Bottom - height - 8));
    }

    private async void PersistAsync()
    {
        try
        {
            await _save(_settings);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存失败：{ex.Message}";
        }
    }

    private void EnabledToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.Enabled = EnabledToggle.IsChecked == true;
        PersistAsync();
    }

    private void ModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var literal = ReferenceEquals(sender, ModeLiteralItem);
        if (_settings.LiteralMode == literal)
        {
            // Clicking the already-selected segment must not leave the pair half-checked.
            ApplyModeUi();
            return;
        }

        _settings.LiteralMode = literal;
        ApplyModeUi();
        PersistAsync();
    }

    private void PhraseToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.IncludePhrases = PhraseToggle.IsChecked == true;
        PersistAsync();
    }

    private void ApplyModeUi()
    {
        _loading = true;
        try
        {
            ModeLiteralItem.IsChecked = _settings.LiteralMode;
            ModeFluentItem.IsChecked = !_settings.LiteralMode;
            PhrasePanel.Visibility = _settings.LiteralMode ? Visibility.Visible : Visibility.Collapsed;
            KnowledgeComboPicker.Visibility=!_settings.LiteralMode&&_settings.TranslationMode!=SentenceTranslationMode.LocalMt?Visibility.Visible:Visibility.Collapsed;
            PhraseToggle.IsChecked = _settings.IncludePhrases;
        }
        finally
        {
            _loading = false;
        }
    }

    private void SurfaceItem_Click(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var panel = ReferenceEquals(sender, SurfacePanelItem);
        if ((_settings.SurfaceMode == SurfaceMode.Panel) == panel)
        {
            ApplySurfaceUi();
            return;
        }

        _settings.SurfaceMode = panel ? SurfaceMode.Panel : SurfaceMode.Tooltip;
        ApplySurfaceUi();
        PersistAsync();
    }

    private void ApplySurfaceUi()
    {
        _loading = true;
        try
        {
            SurfacePanelItem.IsChecked = _settings.SurfaceMode == SurfaceMode.Panel;
            SurfaceTooltipItem.IsChecked = _settings.SurfaceMode == SurfaceMode.Tooltip;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInteractive(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (HandlePanel.Visibility == Visibility.Visible)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (_animating)
        {
            return;
        }

        HandleDragCompleted();
    }

    private void HandleDragCompleted()
    {
        var edge = DetectNearbyEdge();
        if (edge != PopperDockEdge.None)
        {
            var offset = ComputeOffsetForEdge(edge);
            _settings.PopperDockEdge = edge;
            _settings.PopperDockOffset = offset;
            _settings.PopperDrawerOpen = false;
            _settings.PopperLeft = Left;
            _settings.PopperTop = Top;
            CollapseToHandle(edge, offset, animate: true);
            PersistAsync();
            return;
        }

        _settings.PopperDockEdge = PopperDockEdge.None;
        _settings.PopperDrawerOpen = false;
        _settings.PopperLeft = Left;
        _settings.PopperTop = Top;
        ExpandedPanel.Visibility = Visibility.Visible;
        HandlePanel.Visibility = Visibility.Collapsed;
        SizeToContent = SizeToContent.Height;
        Width = ExpandedWidth;
        SlideTransform.X = 0;
        SlideTransform.Y = 0;
        ClampToWorkArea();
        PersistAsync();
    }

    private void DockButton_Click(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        var leftDistance = Math.Abs(Left - workArea.Left);
        var rightDistance = Math.Abs(workArea.Right - (Left + ActualWidth));
        var edge = leftDistance <= rightDistance ? PopperDockEdge.Left : PopperDockEdge.Right;
        var offset = Math.Clamp(Top - workArea.Top, 8, Math.Max(8, workArea.Bottom - workArea.Top - HandleLength - 8));

        _settings.PopperDockEdge = edge;
        _settings.PopperDockOffset = offset;
        _settings.PopperDrawerOpen = false;
        _settings.PopperLeft = Left;
        _settings.PopperTop = Top;
        CollapseToHandle(edge, offset, animate: true);
        PersistAsync();
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanHideToTray)
        {
            SetStatus("系统托盘不可用，控制条保持显示；请直接双击 exe 启动");
            return;
        }

        Hide();
        HiddenToTray?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? QuitRequested;

    public event EventHandler? LocalMtSettingsRequested;

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SetSettingsPage(SettingsScroll.Visibility != Visibility.Visible);
    }

    private void SetSettingsPage(bool showSettings)
    {
        SettingsScroll.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        HomePage.Visibility = showSettings ? Visibility.Collapsed : Visibility.Visible;
        PageTitle.Text = showSettings ? "设置" : "简译";
        SettingsButton.ToolTip = showSettings ? "返回主界面" : "设置";
        System.Windows.Automation.AutomationProperties.SetName(SettingsButton, (string)SettingsButton.ToolTip);
    }

    private void OcrToggle_Click(object sender, RoutedEventArgs e)
    {
        var enable = OcrToggle.IsChecked == true;
        if (enable && !Thanslator.Capture.OcrHostClient.IsConfigured(
                _settings.OcrComponentPath ?? Thanslator.Capture.OcrHostClient.DefaultDirectory))
        {
            OcrToggle.IsChecked = _settings.OcrEnabled;
            OcrSettingsRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        _settings.OcrEnabled = enable;
        SetStatus(enable ? "自动识图已开启" : "自动识图已关闭");
        PersistAsync();
    }

    private void ShowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowFromTray();
        Activate();
    }

    private void HideMenuItem_Click(object sender, RoutedEventArgs e)
    {
        HideButton_Click(sender, e);
    }

    private void LocalMtMenuItem_Click(object sender, RoutedEventArgs e)
    {
        LocalMtSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ClipboardFallbackMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _settings.ClipboardFallback = ClipboardFallbackMenuItem.IsChecked;
        UpdateClipboardFallbackUi();
        PersistAsync();
    }

    private void UpdateClipboardFallbackUi()
    {
        ClipboardFallbackMenuItem.IsChecked = _settings.ClipboardFallback;
        ClipboardFallbackMenuItem.Header = _settings.ClipboardFallback
            ? "✓ 剪贴板取词（兼容 PDF/浏览器）"
            : "剪贴板取词（兼容 PDF/浏览器）";
    }

    private void QuitButton_Click(object sender, RoutedEventArgs e)
    {
        QuitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void QuitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        QuitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void HandleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.PopperDockEdge == PopperDockEdge.None)
        {
            return;
        }

        _settings.PopperDrawerOpen = true;
        ExpandAtEdge(_settings.PopperDockEdge, _settings.PopperDockOffset, animate: true);
        PersistAsync();
    }

    private PopperDockEdge DetectNearbyEdge()
    {
        var workArea = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : _expandedHeight;

        // Treat crossing past an edge as distance 0. When the pointer is dragged
        // quickly, the window can end up slightly outside the work area.
        var candidates = new List<(PopperDockEdge Edge, double Distance)>();
        if (Left <= workArea.Left + DockThreshold)
        {
            candidates.Add((PopperDockEdge.Left, Math.Max(0, workArea.Left - Left)));
        }

        if (Left + width >= workArea.Right - DockThreshold)
        {
            candidates.Add((PopperDockEdge.Right, Math.Max(0, Left + width - workArea.Right)));
        }

        if (Top <= workArea.Top + DockThreshold)
        {
            candidates.Add((PopperDockEdge.Top, Math.Max(0, workArea.Top - Top)));
        }

        if (Top + height >= workArea.Bottom - DockThreshold)
        {
            candidates.Add((PopperDockEdge.Bottom, Math.Max(0, Top + height - workArea.Bottom)));
        }

        return candidates.Count == 0
            ? PopperDockEdge.None
            : candidates.OrderBy(x => x.Distance).First().Edge;
    }

    private double ComputeOffsetForEdge(PopperDockEdge edge)
    {
        var workArea = SystemParameters.WorkArea;
        return edge switch
        {
            PopperDockEdge.Left or PopperDockEdge.Right =>
                Math.Clamp(Top - workArea.Top, 8, Math.Max(8, workArea.Bottom - workArea.Top - HandleLength - 8)),
            _ => Math.Clamp(Left - workArea.Left, 8, Math.Max(8, workArea.Right - workArea.Left - ExpandedWidth - 8))
        };
    }

    private void CollapseToHandle(PopperDockEdge edge, double offset, bool animate)
    {
        _expandedHeight = ActualHeight > 0 ? ActualHeight : _expandedHeight;
        if (!animate || !IsVisible)
        {
            ApplyCollapsedLayout(edge, offset);
            return;
        }

        _animating = true;
        var duration = TimeSpan.FromMilliseconds(170);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var animation = new DoubleAnimation(0, GetOffscreenDistance(edge), duration)
        {
            EasingFunction = easing
        };
        animation.Completed += (_, _) =>
        {
            SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
            SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
            SlideTransform.X = 0;
            SlideTransform.Y = 0;
            _animating = false;
            ApplyCollapsedLayout(edge, offset);
        };

        if (edge is PopperDockEdge.Left or PopperDockEdge.Right)
        {
            SlideTransform.BeginAnimation(TranslateTransform.XProperty, animation);
        }
        else
        {
            SlideTransform.BeginAnimation(TranslateTransform.YProperty, animation);
        }
    }

    private void ExpandAtEdge(PopperDockEdge edge, double offset, bool animate)
    {
        ExpandedPanel.Visibility = Visibility.Visible;
        HandlePanel.Visibility = Visibility.Collapsed;
        SizeToContent = SizeToContent.Height;
        Width = ExpandedWidth;
        UpdateLayout();

        var workArea = SystemParameters.WorkArea;
        var height = ActualHeight > 0 ? ActualHeight : 140;
        switch (edge)
        {
            case PopperDockEdge.Left:
                Left = workArea.Left;
                Top = Math.Clamp(workArea.Top + offset, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
                break;
            case PopperDockEdge.Right:
                Left = workArea.Right - Width;
                Top = Math.Clamp(workArea.Top + offset, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
                break;
            case PopperDockEdge.Top:
                Top = workArea.Top;
                Left = Math.Clamp(workArea.Left + offset, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
                break;
            case PopperDockEdge.Bottom:
                Top = workArea.Bottom - height;
                Left = Math.Clamp(workArea.Left + offset, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
                break;
        }

        if (!animate)
        {
            SlideTransform.X = 0;
            SlideTransform.Y = 0;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(190);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var start = GetOffscreenDistance(edge);
        if (edge is PopperDockEdge.Left or PopperDockEdge.Right)
        {
            SlideTransform.X = start;
            var animation = new DoubleAnimation(start, 0, duration) { EasingFunction = easing };
            animation.Completed += (_, _) => _animating = false;
            _animating = true;
            SlideTransform.BeginAnimation(TranslateTransform.XProperty, animation);
        }
        else
        {
            SlideTransform.Y = start;
            var animation = new DoubleAnimation(start, 0, duration) { EasingFunction = easing };
            animation.Completed += (_, _) => _animating = false;
            _animating = true;
            SlideTransform.BeginAnimation(TranslateTransform.YProperty, animation);
        }
    }

    private void ApplyCollapsedLayout(PopperDockEdge edge, double offset)
    {
        ExpandedPanel.Visibility = Visibility.Collapsed;
        HandlePanel.Visibility = Visibility.Visible;
        SizeToContent = SizeToContent.Manual;

        var workArea = SystemParameters.WorkArea;
        var vertical = edge is PopperDockEdge.Left or PopperDockEdge.Right;
        Width = vertical ? HandleThickness : HandleLength;
        Height = vertical ? HandleLength : HandleThickness;

        switch (edge)
        {
            case PopperDockEdge.Left:
                Left = workArea.Left;
                Top = ClampOffset(workArea.Top + offset, workArea.Top, workArea.Bottom - Height);
                break;
            case PopperDockEdge.Right:
                Left = workArea.Right - Width;
                Top = ClampOffset(workArea.Top + offset, workArea.Top, workArea.Bottom - Height);
                break;
            case PopperDockEdge.Top:
                Top = workArea.Top;
                Left = ClampOffset(workArea.Left + offset, workArea.Left, workArea.Right - Width);
                break;
            case PopperDockEdge.Bottom:
                Top = workArea.Bottom - Height;
                Left = ClampOffset(workArea.Left + offset, workArea.Left, workArea.Right - Width);
                break;
        }

        SlideTransform.X = 0;
        SlideTransform.Y = 0;
    }

    private void PositionDockedOpen()
    {
        if (_settings.PopperDockEdge == PopperDockEdge.None)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        var height = ActualHeight > 0 ? ActualHeight : 140;
        switch (_settings.PopperDockEdge)
        {
            case PopperDockEdge.Left:
                Left = workArea.Left;
                Top = Math.Clamp(workArea.Top + _settings.PopperDockOffset, workArea.Top,
                    Math.Max(workArea.Top, workArea.Bottom - height));
                break;
            case PopperDockEdge.Right:
                Left = workArea.Right - Width;
                Top = Math.Clamp(workArea.Top + _settings.PopperDockOffset, workArea.Top,
                    Math.Max(workArea.Top, workArea.Bottom - height));
                break;
            case PopperDockEdge.Top:
                Top = workArea.Top;
                Left = Math.Clamp(workArea.Left + _settings.PopperDockOffset, workArea.Left,
                    Math.Max(workArea.Left, workArea.Right - Width));
                break;
            case PopperDockEdge.Bottom:
                Top = workArea.Bottom - height;
                Left = Math.Clamp(workArea.Left + _settings.PopperDockOffset, workArea.Left,
                    Math.Max(workArea.Left, workArea.Right - Width));
                break;
        }
    }

    private double GetOffscreenDistance(PopperDockEdge edge) => edge switch
    {
        PopperDockEdge.Left => -Width,
        PopperDockEdge.Right => Width,
        PopperDockEdge.Top => -(ActualHeight > 0 ? ActualHeight : 140),
        PopperDockEdge.Bottom => ActualHeight > 0 ? ActualHeight : 140,
        _ => 0
    };

    private static double ClampOffset(double value, double min, double max) =>
        Math.Clamp(value, min, Math.Max(min, max));

    private static bool IsInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.ComboBox
                or System.Windows.Controls.Primitives.TextBoxBase
                or System.Windows.Controls.Primitives.ScrollBar
                or System.Windows.Controls.ComboBoxItem)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
