using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Thanslator.Capture;

namespace Thanslator.Tests;

public sealed class UiaSelectionReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeepControlTree_PreservesWindowAndPasswordChecks(bool protectedAncestor)
    {
        var ready = new TaskCompletionSource<(nint Handle, Dispatcher Dispatcher)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                UIElement content = new TextBox { Text = "Synthetic selected text" };
                for (var depth = 0; depth < 24; depth++)
                    content = new GroupBox { Header = $"Group {depth}", Content = content, Padding = new Thickness(0) };
                if (protectedAncestor) content = new ProtectedGroup { Content = content };
                window = new Window { Content = content, Width = 450, Height = 750,
                    ShowActivated = false, ShowInTaskbar = false, Title = "Jianyi synthetic ancestry test" };
                window.Show();
                ready.SetResult((new WindowInteropHelper(window).Handle, Dispatcher.CurrentDispatcher));
                Dispatcher.Run();
            }
            catch (Exception ex) { ready.TrySetException(ex); }
            finally { window?.Close(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var fixture = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var root = AutomationElement.FromHandle(fixture.Handle);
            var text = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            Assert.NotNull(text);
            var password = typeof(UiaSelectionReader).GetMethod("IsPassword", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.Equal(protectedAncestor, (bool)password.Invoke(null, [text])!);
            var belongs = typeof(UiaSelectionReader).GetMethod("BelongsToRoot", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.True((bool)belongs.Invoke(null, [text, root])!);
        }
        finally
        {
            fixture.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    private sealed class ProtectedGroup : GroupBox
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new ProtectedGroupPeer(this);
    }

    private sealed class ProtectedGroupPeer(GroupBox owner) : GroupBoxAutomationPeer(owner)
    {
        protected override bool IsPasswordCore() => true;
    }
}
