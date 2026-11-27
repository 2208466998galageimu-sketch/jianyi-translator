using System.Collections.Concurrent;
using Thanslator.Capture;

namespace Thanslator.Tests;

public sealed class MouseHookTests
{
    [Fact]
    public async Task Stop_WhileUnavailableDoesNotRestartSampling()
    {
        var samples = 0;
        using var hook = new MouseHook(() => { Interlocked.Increment(ref samples); return null; });
        Assert.True(hook.Start());
        hook.Stop();
        var stoppedAt = Volatile.Read(ref samples);
        await Task.Delay(300);
        Assert.Equal(stoppedAt, Volatile.Read(ref samples));
    }

    [Fact]
    public async Task Sampling_RecoversAfterUnavailablePositionWithoutFakeRelease()
    {
        var samples = 0;
        using var resumed = new ManualResetEventSlim();
        var events = new ConcurrentQueue<MouseHookEventKind>();
        using var hook = new MouseHook(() => Interlocked.Increment(ref samples) switch
        {
            1 => (10, 10, true, false),
            2 or 3 => null,
            4 => (20, 20, false, false),
            _ => (30, 30, false, false)
        });
        hook.MouseEvent += (_, e) =>
        {
            events.Enqueue(e.Kind);
            if (e.Kind == MouseHookEventKind.Move) resumed.Set();
        };
        Assert.True(hook.Start());
        Assert.True(await Task.Run(() => resumed.Wait(TimeSpan.FromSeconds(3))), "采样失败后没有恢复");
        hook.Stop();
        Assert.Single(events, kind => kind == MouseHookEventKind.Unavailable);
        Assert.DoesNotContain(MouseHookEventKind.LeftUp, events);
        var stoppedAt = Volatile.Read(ref samples);
        await Task.Delay(40);
        Assert.Equal(stoppedAt, Volatile.Read(ref samples));
    }
}
