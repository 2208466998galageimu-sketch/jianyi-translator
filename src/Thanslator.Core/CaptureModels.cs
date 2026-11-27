namespace Thanslator.Core;

public enum CaptureStatus
{
    Success, NoSelection, Unsupported, Timeout, Forbidden, Busy, ClipboardUnavailable, Canceled,
    OcrUnavailable, OcrNoText, OcrOccluded, OcrFailed
}

/// <summary>取词状态与选区证据；禁止读取与普通不支持不能混为一谈。</summary>
public sealed record CaptureResult(CaptureStatus Status, SelectionSnapshot? Snapshot = null, bool HasSelection = false,
    string? WarningCode = null)
{
    public bool CanSuggestManualOcr(bool dragged) =>
        WarningCode != "clipboard.restore_failed" && (dragged || HasSelection) &&
        Status is CaptureStatus.NoSelection or CaptureStatus.Unsupported or CaptureStatus.ClipboardUnavailable;

    public bool CanUseClipboard(bool explicitRequest) =>
        Status is CaptureStatus.Unsupported && (HasSelection || explicitRequest);
}

public sealed record SelectionCaptureRequest(nint ForegroundHwnd, bool RequireNearCursor,
    bool ClipboardFallback, bool ExplicitRequest = false);

/// <summary>捕获只返回原文与来源，不请求翻译服务。</summary>
public interface ISelectionCapture
{
    Task<CaptureResult> CaptureAsync(SelectionCaptureRequest request, CancellationToken cancellationToken = default);
}
