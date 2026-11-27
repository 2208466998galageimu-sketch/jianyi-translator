using System.Windows;
using System.Windows.Controls;
using Thanslator.Core;
using Thanslator.Translation;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;

namespace Thanslator.App;

public partial class OverlayWindow
{
    private TranslationDocument? _adjustDocument;
    private TranslationDocument? _beforeAdjustment;
    private KnowledgeMatches? _adjustMatches;
    private KnowledgeMatches? _beforeAdjustMatches;
    private Func<TranslationDocument,KnowledgeHit,bool,CancellationToken,Task<TranslationDocument>>? _adjust;
    private Func<string,string,string,CancellationToken,Task>? _confirmedEdit;
    private CancellationTokenSource? _adjustCancellation;

    public void ConfigureAdjustment(TranslationDocument document,KnowledgeMatches matches,
        Func<TranslationDocument,KnowledgeHit,bool,CancellationToken,Task<TranslationDocument>> apply,
        Func<string,string,string,CancellationToken,Task>? confirmedEdit = null)
    {
        if(CurrentSurface!=SurfaceMode.Panel)return;
        _adjustDocument=document;_adjustMatches=matches;_adjust=apply;_confirmedEdit=confirmedEdit;
        _adjustCancellation=new();
        AdjustTranslationButton.Visibility=Visibility.Visible;
    }

    private void ResetAdjustment()
    {
        _adjustCancellation?.Cancel();_adjustCancellation?.Dispose();_adjustCancellation=null;
        _adjustDocument=null;_beforeAdjustment=null;_adjustMatches=null;_beforeAdjustMatches=null;_adjust=null;_confirmedEdit=null;
        AdjustTranslationButton.Visibility=AdjustmentPanel.Visibility=Visibility.Collapsed;
        TermChoices.Children.Clear();TermSelector.Items.Clear();UnitSelector.Items.Clear();
        SaveComboPreference.IsChecked=false;
    }

    private void AdjustTranslation_Click(object sender,RoutedEventArgs e)
    {
        if(_snapshot is null || _adjustDocument is null || _adjustMatches is null)return;
        OcrEditing?.Invoke(_snapshot);Activate();
        if(AdjustmentPanel.Visibility==Visibility.Visible){AdjustmentPanel.Visibility=Visibility.Collapsed;return;}
        TermSelector.Items.Clear();
        foreach(var hit in _adjustMatches.Hits.DistinctBy(hit=>KnowledgeRules.Normalize(hit.Rule.Term)))TermSelector.Items.Add(hit.Rule.Term);
        TermSelector.SelectedIndex=TermSelector.Items.Count>0?0:-1;
        TermAdjustment.Visibility=TermSelector.Items.Count>0?Visibility.Visible:Visibility.Collapsed;
        TermAdjustment.IsExpanded=false;TextAdjustment.IsExpanded=TermSelector.Items.Count==0;
        UnitSelector.Items.Clear();
        foreach(var (unit,index) in _adjustDocument.Units.Select((unit,index)=>(unit,index)))
            if(!string.IsNullOrWhiteSpace(unit.Source))UnitSelector.Items.Add(new ComboBoxItem { Content=$"第 {index+1} 段 · {unit.Source[..Math.Min(28,unit.Source.Length)]}",Tag=index });
        UnitSelector.SelectedIndex=0;
        AdjustmentPanel.Visibility=Visibility.Visible;
    }

    private void TermSelector_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(TermChoices is null)return;
        TermChoices.Children.Clear();
        SaveComboPreference.IsChecked=false;
        if(TermSelector.SelectedItem is not string term || _adjustMatches is null)return;
        var current=_adjustMatches.Hits.First(hit=>KnowledgeRules.Normalize(hit.Rule.Term)==KnowledgeRules.Normalize(term));
        var candidates=KnowledgeChoices.Candidates(_adjustMatches,term);
        var defaultTranslation=candidates.FirstOrDefault()?.Rule.Translation;
        foreach(var group in candidates.GroupBy(hit=>hit.Rule.Translation))
        {
            var preferred=group.First();
            TermChoices.Children.Add(new RadioButton { Content=group.Key+"\n"+string.Join("、",group.Select(hit=>hit.LibraryName).Distinct())+
                (group.Key==defaultTranslation?" · 默认":"")+(group.Key==current.Rule.Translation?" · 当前译法":""),Tag=preferred,GroupName="TermChoice",
                IsChecked=group.Key==current.Rule.Translation,Margin=new Thickness(0,5,0,5),Foreground=DetailsText.Foreground });
        }
    }

    private void UnitSelector_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(AdjustedText is not null && UnitSelector.SelectedItem is ComboBoxItem {Tag:int index} && _adjustDocument is not null)
            AdjustedText.Text=_adjustDocument.Units[index].Translation ?? _adjustDocument.Units[index].Source;
    }

    private async void ApplyTerm_Click(object sender,RoutedEventArgs e)
    {
        if(_adjustDocument is null || _adjust is null || _adjustCancellation is null ||
            TermChoices.Children.OfType<RadioButton>().FirstOrDefault(button=>button.IsChecked==true)?.Tag is not KnowledgeHit choice)return;
        var lifetime=_adjustCancellation;var before=_adjustDocument;var save=SaveComboPreference.IsChecked==true;
        AdjustmentStatus.Text="正在调整…";ApplyTermButton.IsEnabled=false;
        try
        {
            var result=await _adjust(before,choice,save,lifetime.Token);
            if(!ReferenceEquals(lifetime,_adjustCancellation) || lifetime.IsCancellationRequested)return;
            _beforeAdjustment=before;_beforeAdjustMatches=_adjustMatches;_adjustDocument=result;DetailsText.Text=result.Text;
            _adjustMatches=_adjustMatches! with {Hits=_adjustMatches.Hits.Select(hit=>KnowledgeRules.Normalize(hit.Rule.Term)==KnowledgeRules.Normalize(choice.Rule.Term)?choice:hit).ToArray()};
            TermSelector_Changed(TermSelector,new SelectionChangedEventArgs(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,Array.Empty<object>(),Array.Empty<object>()));
            UnitSelector_Changed(UnitSelector,new SelectionChangedEventArgs(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,Array.Empty<object>(),Array.Empty<object>()));
            AdjustmentStatus.Text=save?"已调整，并保存为此组合首选":"仅本次使用";
            UndoAdjustmentButton.IsEnabled=true;
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) {if(ReferenceEquals(lifetime,_adjustCancellation))AdjustmentStatus.Text=ex is LlmTranslationException or ArgumentException?ex.Message:"调整失败，已保留原译文。";}
        finally {ApplyTermButton.IsEnabled=true;}
    }

    private async void ConfirmEdit_Click(object sender,RoutedEventArgs e)
    {
        if(_adjustDocument is null || _adjustCancellation is null || UnitSelector.SelectedItem is not ComboBoxItem {Tag:int index} ||
            string.IsNullOrWhiteSpace(AdjustedText.Text) || AdjustedText.Text.Length>32000)return;
        var before=_adjustDocument;var unit=before.Units[index];var lifetime=_adjustCancellation;
        var text=AdjustedText.Text.Trim();
        if(!TextProtector.IsComplete(text,TextProtector.Protect(unit.Source)))
        { AdjustmentStatus.Text="请保留原文中的网址、代码和占位内容。";return; }
        try
        {
            if(_confirmedEdit is not null)await _confirmedEdit(unit.Source,unit.Translation??unit.Source,text,lifetime.Token);
            if(!ReferenceEquals(lifetime,_adjustCancellation) || lifetime.IsCancellationRequested)return;
            var units=before.Units.ToArray();units[index]=unit with {Translation=LlmTranslationClient.PreserveWhitespace(unit.Source,text)};
            _beforeAdjustment=before;_beforeAdjustMatches=_adjustMatches;_adjustDocument=before with {Units=units};DetailsText.Text=_adjustDocument.Text;
            AdjustmentStatus.Text="已确认本次修改";UndoAdjustmentButton.IsEnabled=true;
        }
        catch(OperationCanceledException) { }
        catch { if(ReferenceEquals(lifetime,_adjustCancellation))AdjustmentStatus.Text="保存失败，已保留原译文。"; }
    }

    private void UndoAdjustment_Click(object sender,RoutedEventArgs e)
    {
        if(_beforeAdjustment is null)return;
        _adjustDocument=_beforeAdjustment;_beforeAdjustment=null;DetailsText.Text=_adjustDocument.Text;
        if(_beforeAdjustMatches is not null)_adjustMatches=_beforeAdjustMatches;
        _beforeAdjustMatches=null;
        TermSelector_Changed(TermSelector,new SelectionChangedEventArgs(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,Array.Empty<object>(),Array.Empty<object>()));
        UnitSelector_Changed(UnitSelector,new SelectionChangedEventArgs(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,Array.Empty<object>(),Array.Empty<object>()));
        UndoAdjustmentButton.IsEnabled=false;AdjustmentStatus.Text="已撤销本次调整";
    }
}
