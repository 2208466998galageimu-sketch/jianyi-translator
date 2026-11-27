using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Thanslator.App.Interop;
using Thanslator.Core;

namespace Thanslator.App;

/// <summary>识别完成后短暂标出实际文字块，点击穿透且不激活。</summary>
internal sealed class OcrRangeWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(650) };

    public OcrRangeWindow(SelectionSnapshot snapshot)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Content = new Border { BorderBrush = (System.Windows.Media.Brush)FindResource("BrandBlueBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        NativeWindowStyles.ApplyOverlayStyles(hwnd, clickThrough: true);
        var bounds = snapshot.Bounds;
        NativeWindowStyles.Move(hwnd, (int)bounds.Left - 2, (int)bounds.Top - 2);
        var scale = NativeWindowStyles.GetDpiScale(hwnd);
        Width = (bounds.Width + 4) / scale;
        Height = (bounds.Height + 4) / scale;
        _timer.Tick += (_, _) => Close();
        Closed += (_, _) => _timer.Stop();
    }

    public void ShowRange() { Show(); _timer.Start(); }
}
