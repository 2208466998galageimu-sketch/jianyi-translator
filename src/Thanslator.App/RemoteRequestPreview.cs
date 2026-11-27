using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Thanslator.App;

internal static class RemoteRequestPreview
{
    public static bool Confirm(Window owner, string title, Uri destination, string model, string content)
    {
        var window = new Window { Owner = owner, Title = title, Width = 720, Height = 560, MinWidth = 520, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(28,30,40)), Foreground = Brushes.White };
        var layout = new Grid { Margin = new Thickness(20) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1,GridUnitType.Star), GridLength.Auto })
            layout.RowDefinitions.Add(new RowDefinition { Height = height });
        layout.Children.Add(new TextBlock { Text = "确认本次发送内容", FontSize = 19, FontWeight = FontWeights.SemiBold });
        var endpoint = new TextBlock { Text = "接收服务：" + destination.AbsoluteUri + "\n模型：" + model,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,10,0,12) };
        Grid.SetRow(endpoint,1); layout.Children.Add(endpoint);
        var preview = new TextBox { Text = content, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = new SolidColorBrush(Color.FromRgb(23,25,35)), Foreground = Brushes.White, Padding = new Thickness(12), FontSize = 13 };
        Grid.SetRow(preview,2); layout.Children.Add(preview);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0,14,0,0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(18,7,18,7), Margin = new Thickness(0,0,10,0) };
        var send = new Button { Content = "确认发送", Padding = new Thickness(18,7,18,7) };
        cancel.Click += (_,_) => window.DialogResult = false;
        send.Click += (_,_) => window.DialogResult = true;
        actions.Children.Add(cancel); actions.Children.Add(send);
        Grid.SetRow(actions,3); layout.Children.Add(actions);
        window.Content = layout;
        return window.ShowDialog() == true;
    }
}
