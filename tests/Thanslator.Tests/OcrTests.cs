using Thanslator.Core;
using Thanslator.Capture;

namespace Thanslator.Tests;

public sealed class OcrTests
{
    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(2160, 2160)]
    [InlineData(1200, 700)]
    public void LargeRegion_RespectsWorkerLimitsAndAspectRatio(int width, int height)
    {
        var size = ScreenOcrCapture.GetRecognitionSize(width, height);
        Assert.InRange(size.Width, 1, 1600);
        Assert.InRange(size.Height, 1, 1000);
        Assert.True(size.Width * size.Height <= 1500000);
        Assert.InRange(Math.Abs((double)size.Width / size.Height - (double)width / height), 0, .005);
        if (width == 1200) Assert.Equal(new System.Drawing.Size(width, height), size);
    }

    [Theory]
    [InlineData(3840, 2160, true)]
    [InlineData(3841, 2160, false)]
    [InlineData(3840, 2161, false)]
    public void ManualRegion_Allows4kButRejectsOversizedCapture(int width, int height, bool expected)
    {
        Assert.Equal(expected, ScreenOcrCapture.IsValidRegion(new(0, 0, width, height), new(0, 0, 5000, 3000)));
    }

    [Theory]
    [InlineData("The system reads the selected", "text from the active window.", 10, 36, 200, .95, "The system reads the selected text from the active window.")]
    [InlineData("If the request has timed out,", "the application retries once.", 10, 36, 200, .95, "If the request has timed out, the application retries once.")]
    [InlineData("The device supports inter\u00AD", "national roaming.", 10, 36, 200, .95, "The device supports international roaming.")]
    [InlineData("The device supports real-", "time translation.", 10, 36, 200, .95, "The device supports real-time translation.")]
    [InlineData("The device supports inter-", "national roaming.", 10, 36, 200, .95, "The device supports inter-national roaming.")]
    [InlineData("Use a local model for", "inference.", 10, 36, 75, .95, "Use a local model for inference.")]
    [InlineData("The request timed out.", "the next request succeeded.", 10, 36, 200, .95, null)]
    [InlineData("The request timed out?", "the next request succeeded.", 10, 36, 200, .95, null)]
    [InlineData("Run the command:", "the tool opens a dialog.", 10, 36, 200, .95, null)]
    [InlineData("The processor completed", "Another task starts now.", 10, 36, 200, .95, null)]
    [InlineData("The processor completed", "several tasks in parallel.", 310, 36, 200, .95, null)]
    [InlineData("The processor completed", "several tasks in parallel.", 10, 10, 200, .95, null)]
    [InlineData("The processor completed", "several tasks in parallel.", 10, 66, 200, .95, null)]
    [InlineData("The processor completed", "several tasks in parallel.", 26, 36, 200, .95, null)]
    [InlineData("The processor completed", "several tasks in parallel.", 10, 36, 350, .95, null)]
    [InlineData("The processor completed", "several tasks in parallel.", 10, 36, 200, .6, null)]
    [InlineData("Open C:\\temp\\readme.txt", "with the default editor.", 10, 36, 200, .95, null)]
    [InlineData("if (status == Ready) {", "return result;", 10, 36, 200, .95, null)]
    [InlineData("- First item in the list", "continues on the next line", 10, 36, 200, .95, null)]
    [InlineData("The processor completed", "", 10, 36, 200, 0, null)]
    public void OcrTranslation_JoinsOnlyLikelyProseWraps(string upper, string lower, double x, double y,
        double width, double confidence, string? expected)
    {
        var block = new OcrBlock([new(upper, new(10, 10, 210, 20), .95),
            new(lower, new(x, y, width, 20), confidence)]);
        var raw = upper + "\n" + (lower.Length == 0 ? "\uFFFD" : lower);

        Assert.Equal(raw, block.Text);
        Assert.Equal(expected ?? raw, block.GetTranslationText());
    }

    [Fact]
    public void DetectedButUnreadableLine_RemainsVisibleInOriginal()
    {
        var block = new OcrBlock([new("first",new(0,0,100,20),.9),new("",new(0,30,20,20),0),new("last",new(0,60,100,20),.9)]);
        Assert.Equal("first\n�\nlast", block.Text);
        const string response = "{\"lines\":[{\"text\":\"\",\"x\":0,\"y\":0,\"width\":10,\"height\":10,\"confidence\":0}]}";
        Assert.Single(OcrHostClient.ParseResponse(response,100,100));
    }

    [Fact]
    public void Region_StaysInVisibleClientOnNegativeCoordinateMonitor()
    {
        var region = ScreenOcrCapture.PlanRegion(new(-1920, 0, 1700, 1000), new(-1920, 0, 1920, 1040), -1850, 40, null);
        Assert.Equal(-1920, region.Left);
        Assert.Equal(0, region.Top);
        Assert.InRange(region.Right, -1920, -220);
        Assert.True(region.Contains(-1850, 40));
        Assert.Equal(1200, region.Width);
    }

    [Theory]
    [InlineData(-1920, 20, 1200, 700, true)]
    [InlineData(-1921, 20, 100, 100, false)]
    [InlineData(-350, 20, 150, 100, false)]
    [InlineData(-1900, 5, 100, 100, false)]
    [InlineData(-1900, 20, 1201, 100, true)]
    [InlineData(-1900, 20, 7, 20, false)]
    [InlineData(-1899.5, 20, 100, 100, false)]
    public void ManualRegion_UsesExactVisiblePixelBounds(double x, double y, double width, double height, bool expected)
    {
        var visibleArea = new RectD(-1920, 20, 1700, 1000);
        Assert.Equal(expected, ScreenOcrCapture.IsValidRegion(new RectD(x, y, width, height), visibleArea));
    }

    [Theory]
    [InlineData("{\"lines\":[{\"text\":\"x\",\"x\":0,\"y\":0,\"width\":10,\"height\":10,\"confidence\":2}]}")]
    [InlineData("{\"lines\":[{\"text\":\"x\",\"x\":99,\"y\":0,\"width\":10,\"height\":10,\"confidence\":0.9}]}")]
    [InlineData("{\"error\":\"ocr.failed\"}")]
    public void Protocol_RejectsInvalidScoresBoundsAndFailures(string response)
    {
        Assert.Throws<IOException>(() => OcrHostClient.ParseResponse(response, 100, 100));
    }
    [Fact]
    public void PointSelection_KeepsColumnAndStopsAtParagraphGap()
    {
        OcrLine[] lines = [new("left one", new(10, 10, 180, 20), .99),
            new("right one", new(320, 10, 180, 20), .99), new("left two", new(10, 42, 180, 20), .99),
            new("right two", new(320, 42, 180, 20), .99), new("next paragraph", new(10, 110, 180, 20), .99)];
        var block = OcrBlockSelector.AtPoint(lines, 30, 48);
        Assert.NotNull(block);
        Assert.Equal("left one\nleft two", block.Text);
        Assert.Null(OcrBlockSelector.AtPoint(lines, 250, 30));
    }

    [Fact]
    public void SelectedLines_KeepLowScoresAndDoNotIncludeAnotherColumn()
    {
        OcrLine[] lines = [new("uncertain", new(10, 10, 100, 20), .32), new("other", new(300, 10, 100, 20), .99)];
        var block = OcrBlockSelector.InSelection(lines, [new(0, 0, 150, 40)]);
        Assert.NotNull(block);
        Assert.Equal("uncertain", block.Text);
        Assert.Equal(.32, block.Confidence);
    }

    [Fact]
    public void Routing_DoesNotScreenshotWithoutExplicitActionOrTrustedSelection()
    {
        Assert.False(OcrCapturePolicy.CanFallback(new(CaptureStatus.NoSelection), false));
        Assert.True(OcrCapturePolicy.CanFallback(new(CaptureStatus.NoSelection), true));
        var geometry = new SelectionSnapshot("", [new(1, 1, 20, 20)], 1, "test", SelectionCaptureMethod.UiaTextPattern, 0);
        Assert.True(OcrCapturePolicy.CanFallback(new(CaptureStatus.Unsupported, geometry, true), false));
        Assert.False(OcrCapturePolicy.CanFallback(new(CaptureStatus.NoSelection, geometry, true), false));
        Assert.False(OcrCapturePolicy.CanFallback(new(CaptureStatus.Success, geometry, true), true));
        foreach (var status in new[] { CaptureStatus.Forbidden, CaptureStatus.Canceled, CaptureStatus.Timeout, CaptureStatus.Busy })
            Assert.False(OcrCapturePolicy.CanFallback(new(status, geometry, true), true));
    }
}
