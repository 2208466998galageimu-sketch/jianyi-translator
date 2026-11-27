using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Thanslator.Core;
using Button=System.Windows.Controls.Button;
using Clipboard=System.Windows.Clipboard;

namespace Thanslator.App;

public partial class OverlayWindow
{
    public event Action<SelectionSnapshot>? RetryRequested;

    public void ShowPreferences(IReadOnlyList<TranslationPreference> rules,string source,string target)
    {
        ResultDetailsText.Text=$"{TranslationLanguages.Label(source)} → {TranslationLanguages.Label(target)}\n"+ResultDetailsText.Text+
            (rules.Count>0?"\n已提供翻译偏好：\n"+string.Join("\n",rules.Select(rule=>rule.Rule)):"\n本次未提供翻译偏好");
    }

    private void CopyTranslation_Click(object sender,RoutedEventArgs e) => CopyResult(new TextRange(DetailsText.ContentStart,DetailsText.ContentEnd).Text);
    private void CopyOriginal_Click(object sender,RoutedEventArgs e) => CopyResult(_snapshot?.Text??"");
    private void CopyResult(string text)
    {
        try {if(text.Length==0)return;Clipboard.SetText(text);HintText.Text="已复制";}
        catch(System.Runtime.InteropServices.ExternalException){HintText.Text="剪贴板正忙，请重试复制。";}
    }
    private void Retry_Click(object sender,RoutedEventArgs e)
    {
        if(_snapshot is null)return;
        OcrEditing?.Invoke(_snapshot);RetryRequested?.Invoke(_snapshot);
    }
    private void MoreResult_Click(object sender,RoutedEventArgs e)
    {
        var button=(Button)sender;button.ContextMenu.PlacementTarget=button;button.ContextMenu.IsOpen=true;
    }
    private void ResultDetails_Click(object sender,RoutedEventArgs e)
    {ResultDetails.Visibility=Visibility.Visible;ResultDetails.IsExpanded=!ResultDetails.IsExpanded;}
}
