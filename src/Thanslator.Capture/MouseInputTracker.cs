namespace Thanslator.Capture;

/// <summary>
/// 纯逻辑的鼠标状态跟踪器：把「定时采样的光标位置 + 左键状态」转换成
/// Move / LeftDown / LeftUp 事件，不依赖任何 Win32 调用，便于单元测试。
/// </summary>
/// <remarks>
/// 为什么不是 WH_MOUSE_LL：低级鼠标钩子在回调超时（默认约 300ms）时会被
/// Windows 静默摘除，此后再也不回调；2026-11-08 实测在本程序进程里
/// SetWindowsHookEx 返回有效句柄、消息泵正常、WM_NULL 可收到，但回调一次都不触发。
/// 轮询不依赖钩子机制，行为可预测，代价是采样精度约 8–16ms 且拿不到滚轮。
/// </remarks>
public sealed class MouseInputTracker
{
    /// <summary>两次 Move 之间的最小间隔（毫秒），避免高回报率鼠标刷爆事件队列。</summary>
    public const int MoveThrottleMs = 16;

    private int _lastX;
    private int _lastY;
    private bool _hasSample;
    private bool _wasDown;
    private long _lastMoveAtMs = long.MinValue / 2;

    /// <summary>丢弃上一次采样状态；下一次观察不会产生按下/抬起事件。</summary>
    public void Reset()
    {
        _hasSample = false;
        _wasDown = false;
        _lastMoveAtMs = long.MinValue / 2;
    }

    /// <summary>
    /// 观察一次采样，并按需通过 <paramref name="emit"/> 产出事件。
    /// 同一个采样里既移动又按键时，先发 Move 再发按键，贴近真实事件顺序。
    /// </summary>
    public void Observe(int x, int y, bool isDown, long nowMs, Action<MouseHookEventKind, int, int> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);

        if (!_hasSample)
        {
            // 首个采样只建立基线：启动瞬间鼠标已经按着，不应产生一次伪 LeftDown。
            _hasSample = true;
            _wasDown = isDown;
            _lastX = x;
            _lastY = y;
            return;
        }

        var moved = x != _lastX || y != _lastY;
        _lastX = x;
        _lastY = y;

        if (moved && nowMs - _lastMoveAtMs >= MoveThrottleMs)
        {
            _lastMoveAtMs = nowMs;
            emit(MouseHookEventKind.Move, x, y);
        }

        if (isDown != _wasDown)
        {
            _wasDown = isDown;
            emit(isDown ? MouseHookEventKind.LeftDown : MouseHookEventKind.LeftUp, x, y);
        }
    }
}
