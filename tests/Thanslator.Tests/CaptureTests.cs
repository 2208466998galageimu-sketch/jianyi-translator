using Thanslator.Capture;
using Thanslator.Core;

namespace Thanslator.Tests;

public sealed class CaptureTests
{
    [Theory]
    [InlineData(CaptureStatus.NoSelection, true, false, null, true)]
    [InlineData(CaptureStatus.Unsupported, false, true, null, true)]
    [InlineData(CaptureStatus.ClipboardUnavailable, true, false, null, true)]
    [InlineData(CaptureStatus.NoSelection, false, false, null, false)]
    [InlineData(CaptureStatus.Unsupported, false, false, null, false)]
    [InlineData(CaptureStatus.Forbidden, true, true, null, false)]
    [InlineData(CaptureStatus.Canceled, true, true, null, false)]
    [InlineData(CaptureStatus.Timeout, true, true, null, false)]
    [InlineData(CaptureStatus.ClipboardUnavailable, true, true, "clipboard.restore_failed", false)]
    public void ManualOcrHint_RequiresSelectionAttemptAndRecoverableFailure(CaptureStatus status, bool dragged,
        bool hasSelection, string? warning, bool expected)
    {
        var failure = new CaptureResult(status, HasSelection: hasSelection, WarningCode: warning);
        Assert.Equal(expected, failure.CanSuggestManualOcr(dragged));
    }

    [Theory]
    [InlineData(CaptureStatus.Forbidden)]
    [InlineData(CaptureStatus.NoSelection)]
    [InlineData(CaptureStatus.Timeout)]
    [InlineData(CaptureStatus.Canceled)]
    public void Clipboard_IsNeverUsedAfterTerminalStatus(CaptureStatus status)
    {
        Assert.False(new CaptureResult(status, HasSelection: true).CanUseClipboard(true));
    }

    [Fact]
    public void Clipboard_RequiresSelectionEvidenceOrExplicitAction()
    {
        Assert.False(new CaptureResult(CaptureStatus.Unsupported).CanUseClipboard(false));
        Assert.True(new CaptureResult(CaptureStatus.Unsupported).CanUseClipboard(true));
        Assert.True(new CaptureResult(CaptureStatus.Unsupported, HasSelection: true).CanUseClipboard(false));
    }

    [Fact]
    public void HitTest_DoesNotTreatLineGapAsSelected()
    {
        var snapshot = new SelectionSnapshot("two lines", [new(0, 0, 100, 10), new(0, 40, 30, 10)], 1, "test", SelectionCaptureMethod.UiaTextPattern, 0);
        Assert.True(snapshot.ContainsPoint(5, 5));
        Assert.False(snapshot.ContainsPoint(50, 25));
    }

    [Fact]
    public void Clipboard_DoesNotOverwriteNewCopyOrUnsupportedHandleFormat()
    {
        Assert.False(ClipboardCapturePolicy.CanRestore(12, 1, 13, 1));
        Assert.False(ClipboardCapturePolicy.CanRestore(12, 1, 12, 2));
        Assert.True(ClipboardCapturePolicy.CanBackUp(15, null));
        Assert.True(ClipboardCapturePolicy.CanBackUp(0xc001, "HTML Format"));
        Assert.False(ClipboardCapturePolicy.CanBackUp(2, null));
        Assert.False(ClipboardCapturePolicy.CanBackUp(0xc002, "Ole Private Data"));
        Assert.True(ClipboardCapturePolicy.IsTerminal("WindowsTerminal"));
    }

    [Fact]
    public async Task Worker_UnexpectedFailureDoesNotPermitClipboardFallback()
    {
        using var worker = new CaptureWorker(ApartmentState.MTA);
        var result = await worker.RunAsync(_ => throw new ArgumentException(), TimeSpan.FromSeconds(1));
        Assert.Equal(CaptureStatus.Forbidden, result.Status);
        Assert.False(result.CanUseClipboard(true));
    }

    [Fact]
    public async Task Worker_TimesOutWithoutSpawningAnotherComThread()
    {
        using var worker = new CaptureWorker(ApartmentState.MTA);
        using var release = new ManualResetEventSlim();
        try
        {
            var first = await worker.RunAsync(_ => { release.Wait(); return new(CaptureStatus.NoSelection); }, TimeSpan.FromMilliseconds(80));
            Assert.Equal(CaptureStatus.Timeout, first.Status);
            var second = worker.RunAsync(_ => new(CaptureStatus.NoSelection), TimeSpan.FromMilliseconds(500));
            var third = await worker.RunAsync(_ => new(CaptureStatus.NoSelection), TimeSpan.FromMilliseconds(500));
            Assert.Equal(CaptureStatus.Busy, third.Status);
            release.Set();
            Assert.Equal(CaptureStatus.NoSelection, (await second).Status);
        }
        finally { release.Set(); }
    }
}
