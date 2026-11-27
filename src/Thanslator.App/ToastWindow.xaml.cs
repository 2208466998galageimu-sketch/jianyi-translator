using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Thanslator.App.Interop;

namespace Thanslator.App;

public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _closeTimer;

    public ToastWindow(string message, TimeSpan duration)
    {
        InitializeComponent();
        MessageText.Text = message;
        _closeTimer = new DispatcherTimer { Interval = duration };
        _closeTimer.Tick += OnCloseTimer;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        NativeWindowStyles.ApplyOverlayStyles(hwnd, clickThrough: true);
        UpdateLayout();

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 20;
        Top = workArea.Bottom - ActualHeight - 20;

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        _closeTimer.Start();
    }

    private void OnCloseTimer(object? sender, EventArgs e)
    {
        _closeTimer.Stop();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closeTimer.Stop();
        base.OnClosed(e);
    }
}
