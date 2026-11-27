using Thanslator.Capture;

namespace Thanslator.Tests;

public sealed class MouseInputTrackerTests
{
    private static List<(MouseHookEventKind Kind, int X, int Y)> ObserveAll(
        MouseInputTracker tracker,
        params (int X, int Y, bool Down, long At)[] samples)
    {
        var events = new List<(MouseHookEventKind, int, int)>();
        foreach (var sample in samples)
        {
            tracker.Observe(sample.X, sample.Y, sample.Down, sample.At, (kind, x, y) => events.Add((kind, x, y)));
        }

        return events;
    }

    [Fact]
    public void FirstSample_EstablishesBaseline_WithoutEvents()
    {
        var tracker = new MouseInputTracker();

        var events = ObserveAll(tracker, (100, 100, true, 0));

        Assert.Empty(events);
    }

    [Fact]
    public void Movement_EmitsMove_WithPhysicalCoordinates()
    {
        var tracker = new MouseInputTracker();

        var events = ObserveAll(tracker, (10, 10, false, 0), (20, 30, false, 100));

        Assert.Equal([(MouseHookEventKind.Move, 20, 30)], events);
    }

    [Fact]
    public void Movement_WithinThrottleWindow_IsSuppressed()
    {
        var tracker = new MouseInputTracker();

        var events = ObserveAll(
            tracker,
            (10, 10, false, 0),
            (11, 11, false, 100),
            (12, 12, false, 108),
            (13, 13, false, 120));

        Assert.Equal([(MouseHookEventKind.Move, 11, 11), (MouseHookEventKind.Move, 13, 13)], events);
    }

    [Fact]
    public void ButtonTransitions_EmitDownAndUp()
    {
        var tracker = new MouseInputTracker();

        var events = ObserveAll(
            tracker,
            (10, 10, false, 0),
            (10, 10, true, 50),
            (10, 10, true, 150),
            (10, 10, false, 200));

        Assert.Equal(
            [(MouseHookEventKind.LeftDown, 10, 10), (MouseHookEventKind.LeftUp, 10, 10)],
            events);
    }

    [Fact]
    public void MoveAndPressInSameSample_EmitsMoveBeforeDown()
    {
        var tracker = new MouseInputTracker();

        var events = ObserveAll(tracker, (10, 10, false, 0), (40, 40, true, 100));

        Assert.Equal(
            [(MouseHookEventKind.Move, 40, 40), (MouseHookEventKind.LeftDown, 40, 40)],
            events);
    }

    [Fact]
    public void Reset_DiscardsBaseline_SoNextPressIsNotReported()
    {
        var tracker = new MouseInputTracker();
        ObserveAll(tracker, (10, 10, false, 0), (10, 10, true, 50));

        tracker.Reset();
        var events = ObserveAll(tracker, (10, 10, true, 100));

        Assert.Empty(events);
    }
}
