using System.Runtime.InteropServices;

namespace Thanslator.Capture;

public enum MouseHookEventKind
{
    Move,
    LeftDown,
    LeftUp,

    /// <summary>
    /// 轮询不产生滚轮事件；由 CaptureInvalidationMonitor 的 Raw Input 补充。
    /// </summary>
    Wheel,
    Escape,
    Unavailable
}

public sealed class MouseHookEventArgs(int x, int y, MouseHookEventKind kind) : EventArgs
{
    public int X { get; } = x;
    public int Y { get; } = y;

    public MouseHookEventKind Kind { get; } = kind;
}

/// <summary>
/// 轮询式鼠标监测：在专用线程上按固定间隔读取光标位置与左键状态，
/// 合成 Move / LeftDown / LeftUp 事件。事件在轮询线程上触发，
/// UI 订阅者必须自行切回自己的线程并立刻返回。坐标是物理屏幕像素。
/// </summary>
/// <remarks>
/// 历史：这里原本是 WH_MOUSE_LL 全局钩子。2026-11-08 实测发现钩子在本进程
/// 装得上（SetWindowsHookEx 返回有效句柄、消息泵可收到 WM_NULL），但回调
/// 一次都不触发，导致「选中后悬停”完全无反应；改为轮询后不再依赖钩子机制。
/// 见 design.md ADR-014。
/// </remarks>
public sealed class MouseHook : IDisposable
{
    /// <summary>轮询间隔（毫秒）。8ms 足以覆盖人手点击（60–150ms）。</summary>
    public const int PollIntervalMs = 8;

    private const int VkLeftButton = 0x01;

    private readonly MouseInputTracker _tracker = new();
    private readonly Func<(int X, int Y, bool LeftDown, bool EscapeDown)?> _readSample;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _stop = new(false);
    private Thread? _thread;
    private bool _disposed;

    /// <summary>
    /// 诊断回调（可空）：只报安装/停止与失败原因，绝不记录鼠标轨迹或原文。
    /// </summary>
    public static Action<string>? Diagnostic { get; set; }

    public event EventHandler<MouseHookEventArgs>? MouseEvent;

    public MouseHook() : this(ReadNativeSample) { }

    /// <summary>替换平台采样源；null 采样表示当前桌面暂不可访问。</summary>
    public MouseHook(Func<(int X, int Y, bool LeftDown, bool EscapeDown)?> readSample)
    {
        ArgumentNullException.ThrowIfNull(readSample);
        _readSample = readSample;
    }

    public bool Start()
    {
        if (_thread is not null)
        {
            return true;
        }

        _stop.Reset();
        _ready.Reset();
        _tracker.Reset();
        _thread = new Thread(PollThread)
        {
            IsBackground = true,
            Name = "Thanslator.MousePoll"
        };
        _thread.Start();

        return _ready.Wait(TimeSpan.FromSeconds(2));
    }

    public void Stop()
    {
        var thread = _thread;
        if (thread is null)
        {
            return;
        }

        _thread = null;
        _stop.Set();
        thread.Join(TimeSpan.FromSeconds(2));
    }

    private void PollThread()
    {
        try
        {
            Diagnostic?.Invoke($"poll started interval={PollIntervalMs}ms");
            var ticks = 0;
            var escapeDown = false;
            var unavailable = false;
            while (!_stop.IsSet)
            {
                if (_readSample() is { } point)
                {
                    if (unavailable)
                    {
                        Diagnostic?.Invoke("poll resumed");
                        unavailable = false;
                    }
                    var isDown = point.LeftDown;
                    _tracker.Observe(point.X, point.Y, isDown, Environment.TickCount64, Raise);
                    var escape = point.EscapeDown;
                    if (escape && !escapeDown) Raise(MouseHookEventKind.Escape, point.X, point.Y);
                    escapeDown = escape;
                    _ready.Set();
                    if (++ticks % 500 == 0)
                    {
                        Diagnostic?.Invoke($"poll heartbeat x={point.X} y={point.Y} down={isDown}");
                    }
                }
                else
                {
                    if (!unavailable)
                    {
                        Diagnostic?.Invoke($"poll paused err={Marshal.GetLastWin32Error()}");
                        unavailable = true;
                        _tracker.Reset();
                        escapeDown = false;
                        Raise(MouseHookEventKind.Unavailable, 0, 0);
                    }
                    _ready.Set();
                    // 安全桌面/锁屏可能暂时拒绝采样；不切换桌面，等原桌面恢复。
                    _stop.Wait(250);
                    continue;
                }

                _stop.Wait(PollIntervalMs);
            }
        }
        catch (Exception ex)
        {
            // 轮询线程不能把异常抛给 CLR：那会直接结束进程。
            Diagnostic?.Invoke($"poll crashed {ex.GetType().Name}");
        }
        finally
        {
            _ready.Set();
        }
    }

    private static (int X, int Y, bool LeftDown, bool EscapeDown)? ReadNativeSample() =>
        GetCursorPos(out var point)
            ? (point.X, point.Y, (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0, (GetAsyncKeyState(0x1B) & 0x8000) != 0)
            : null;

    private void Raise(MouseHookEventKind kind, int x, int y)
    {
        try
        {
            MouseEvent?.Invoke(this, new MouseHookEventArgs(x, y, kind));
        }
        catch (Exception)
        {
            // 订阅者异常不能打断采样循环。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _ready.Dispose();
        _stop.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
