using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;
using Thanslator.App.Interop;
using Thanslator.Core;

namespace Thanslator.App;

internal sealed class RegionSelectionWindow : Window
{
    private readonly Canvas _canvas;
    private readonly Rectangle _outline;
    private System.Windows.Point? _start;
    public RectD? SelectedRegion { get; private set; }

    public RegionSelectionWindow(RectD area)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Cursor = System.Windows.Input.Cursors.Cross;
        Background = System.Windows.Media.Brushes.Transparent;
        _canvas = new Canvas { Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(58, 0, 0, 0)) };
        _outline = new Rectangle
        {
            Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 177, 255)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 102, 177, 255)),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        _canvas.Children.Add(_outline);
        var hint = new TextBlock
        {
            Text = "拖动框选文字，按 Esc 取消",
            Background = System.Windows.Media.Brushes.Black,
            Foreground = System.Windows.Media.Brushes.White,
            Padding = new Thickness(9, 4, 9, 4),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(hint, 8);
        Canvas.SetTop(hint, 8);
        _canvas.Children.Add(hint);
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeWindowStyles.ApplyOverlayStyles(hwnd, clickThrough: false, show: false);
            NativeWindowStyles.Place(hwnd, area);
        };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            _start = Clamp(e.GetPosition(_canvas));
            if (!_canvas.CaptureMouse()) Close();
            e.Handled = true;
        };
        PreviewMouseMove += (_, e) =>
        {
            if (_start is { } origin) UpdateOutline(origin, Clamp(e.GetPosition(_canvas)));
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_start is not { } origin) return;
            var a = _canvas.PointToScreen(origin);
            var b = _canvas.PointToScreen(Clamp(e.GetPosition(_canvas)));
            var left = Math.Round(Math.Min(a.X, b.X));
            var top = Math.Round(Math.Min(a.Y, b.Y));
            SelectedRegion = new RectD(left, top, Math.Round(Math.Max(a.X, b.X)) - left,
                Math.Round(Math.Max(a.Y, b.Y)) - top);
            _start = null;
            Close();
            e.Handled = true;
        };
        LostMouseCapture += (_, _) =>
        {
            if (_start is null) return;
            _start = null;
            if (IsVisible) Close();
        };
        PreviewMouseRightButtonDown += (_, _) => Close();
        Closing += (_, _) =>
        {
            _start = null;
            if (_canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();
        };
    }

    private System.Windows.Point Clamp(System.Windows.Point p) =>
        new(Math.Clamp(p.X, 0, _canvas.ActualWidth), Math.Clamp(p.Y, 0, _canvas.ActualHeight));

    private void UpdateOutline(System.Windows.Point a, System.Windows.Point b)
    {
        Canvas.SetLeft(_outline, Math.Min(a.X, b.X));
        Canvas.SetTop(_outline, Math.Min(a.Y, b.Y));
        _outline.Width = Math.Abs(a.X - b.X);
        _outline.Height = Math.Abs(a.Y - b.Y);
        _outline.Visibility = Visibility.Visible;
    }
}
