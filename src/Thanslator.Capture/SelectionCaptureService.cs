using Thanslator.Core;

namespace Thanslator.Capture;

/// <summary>统一取词路由；UIA 与剪贴板分别在无窗口 MTA 和 STA 工作者执行。</summary>
public sealed class SelectionCaptureService : ISelectionCapture, IDisposable
{
    private readonly UiaSelectionReader _uia = new();
    private readonly ClipboardSelectionReader _clipboard = new();
    private readonly CaptureWorker _uiaWorker = new(ApartmentState.MTA);
    private readonly CaptureWorker _clipboardWorker = new(ApartmentState.STA);

    public async Task<CaptureResult> CaptureAsync(SelectionCaptureRequest request, CancellationToken cancellationToken = default)
    {
        var result = await ReadUiaAsync(request, cancellationToken);
        if (result.Status == CaptureStatus.Unsupported && !result.HasSelection)
        {
            await Task.Delay(120, cancellationToken);
            result = await ReadUiaAsync(request, cancellationToken);
        }
        if (!request.ClipboardFallback || !result.CanUseClipboard(request.ExplicitRequest)) return result;

        // 再查一次安全目标与选区，避免把 UIA 的禁止读取混成复制回退。
        var checkedResult = await ReadUiaAsync(request, cancellationToken);
        if (checkedResult.Status == CaptureStatus.Success) return checkedResult;
        if (!checkedResult.CanUseClipboard(request.ExplicitRequest)) return checkedResult;
        result = checkedResult;
        var copied = await _clipboardWorker.RunAsync(token => _clipboard.Capture(request.ForegroundHwnd, token),
            TimeSpan.FromSeconds(3), cancellationToken);
        if (result.Snapshot is { HasPreciseGeometry: true } geometry &&
            copied.Status is CaptureStatus.Success or CaptureStatus.Unsupported or CaptureStatus.ClipboardUnavailable)
            return copied with { Snapshot = copied.Snapshot is null ? geometry : copied.Snapshot with
                { LineRects = geometry.LineRects, HasPreciseGeometry = true }, HasSelection = result.HasSelection || copied.HasSelection };
        return copied;
    }

    public Task<CaptureResult> CheckOcrRegionAsync(nint hwnd, RectD region, CancellationToken cancellationToken) =>
        _uiaWorker.RunAsync(token => _uia.CheckOcrRegion(hwnd, region, token), TimeSpan.FromSeconds(2), cancellationToken);

    private Task<CaptureResult> ReadUiaAsync(SelectionCaptureRequest request, CancellationToken cancellationToken) =>
        _uiaWorker.RunAsync(token => _uia.Capture(request.ForegroundHwnd, request.RequireNearCursor, token),
            TimeSpan.FromSeconds(2), cancellationToken);

    public void Dispose()
    {
        _uiaWorker.Dispose();
        _clipboardWorker.Dispose();
    }
}
