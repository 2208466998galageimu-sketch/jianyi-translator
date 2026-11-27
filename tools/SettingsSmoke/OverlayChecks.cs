using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Thanslator.App;
using Thanslator.Core;
using Thanslator.Translation;

internal static class OverlayChecks
{
    public static async Task RunAsync(string output)
    {
        var overlay=new OverlayWindow();
        try
        {
            var snapshot=new SelectionSnapshot("sensor",[new RectD(300,200,120,30)],0,"fixture",SelectionCaptureMethod.UiaTextPattern,1);
            var settings=new AppSettings{SurfaceMode=SurfaceMode.Panel};
            var first=new KnowledgeHit("a","领域库",new("a1","sensor","传感器"));
            var other=new KnowledgeHit("b","个人库",new("b1","sensor","感应器"));
            var same=new KnowledgeHit("c","同义来源",new("c1","sensor","传感器"));
            var matches=new KnowledgeMatches([first],[new("sensor",first,other)],[first,other,same],"default");
            var doc=new TranslationDocument([new("sensor","传感器")]);
            var called=0;var saved=false;
            overlay.ShowTranslation(snapshot,doc.Text,settings);overlay.ShowKnowledge(matches);
            overlay.ConfigureAdjustment(doc,matches,(current,choice,save,token)=>{called++;saved=save;return Task.FromResult(current with{Units=[new("sensor",choice.Rule.Translation)]});});
            var panel=(Border)overlay.FindName("AdjustmentPanel");var button=(Button)overlay.FindName("AdjustTranslationButton");
            if(((WrapPanel)overlay.FindName("ResultActions")).Visibility!=Visibility.Visible || ((Button)overlay.FindName("CopyTranslationButton")).Visibility!=Visibility.Visible)
                throw new Exception("Persistent result must expose copy action.");
            if(panel.Visibility!=Visibility.Collapsed || button.Visibility!=Visibility.Visible)throw new Exception("Adjustment must start closed.");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if(((Expander)overlay.FindName("TermAdjustment")).IsExpanded || ((Expander)overlay.FindName("TextAdjustment")).IsExpanded)
                throw new Exception("Adjustment sub-tasks must be progressive.");
            var choices=((StackPanel)overlay.FindName("TermChoices")).Children.OfType<RadioButton>().ToArray();
            if(choices.Length!=2 || !choices[0].Content.ToString()!.Contains("同义来源"))throw new Exception("Candidate sources were not merged.");
            choices[1].IsChecked=true;((Button)overlay.FindName("ApplyTermButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(20);
            if(called!=1 || saved || ((TextBlock)overlay.FindName("DetailsText")).Text!="感应器")throw new Exception("Choice should apply only to this result.");
            ((Button)overlay.FindName("UndoAdjustmentButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if(((TextBlock)overlay.FindName("DetailsText")).Text!="传感器")throw new Exception("Undo failed.");
            overlay.HideOverlay();
            var delayed=new TaskCompletionSource<TranslationDocument>();
            overlay.ShowTranslation(snapshot,doc.Text,settings);overlay.ConfigureAdjustment(doc,matches,(_,_,_,_)=>delayed.Task);
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            ((Button)overlay.FindName("ApplyTermButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            overlay.ShowTranslation(snapshot,"新选区结果",settings);delayed.SetResult(new([new("sensor","过期结果")]));await Task.Delay(20);
            if(((TextBlock)overlay.FindName("DetailsText")).Text!="新选区结果")throw new Exception("Stale adjustment overwrote new result.");
            overlay.ShowTranslation(snapshot,doc.Text,new AppSettings{SurfaceMode=SurfaceMode.Tooltip});overlay.ConfigureAdjustment(doc,matches,(_,_,_,_)=>Task.FromResult(doc));
            if(button.Visibility!=Visibility.Collapsed || ((WrapPanel)overlay.FindName("ResultActions")).Visibility!=Visibility.Collapsed)throw new Exception("Read-only tooltip exposes an interactive adjustment.");
            overlay.ShowLookup(snapshot,DictionaryResult.NotFound("sensor","临时失败"),settings,openModelSettings:true);
            if(((Button)overlay.FindName("RetryButton")).Visibility!=Visibility.Visible)throw new Exception("Error panel does not offer retry.");
            var retried=false;overlay.RetryRequested+=_=>retried=true;
            ((Button)overlay.FindName("RetryButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if(!retried)throw new Exception("Retry button does not route back to translation.");
            System.IO.File.WriteAllText(System.IO.Path.Combine(output,"overlay-passed.txt"),"Collapsed candidates, merged sources, request-only choice, undo, stale-result rejection and read-only tooltip passed.");
        }
        finally{overlay.Close();}
    }
}
