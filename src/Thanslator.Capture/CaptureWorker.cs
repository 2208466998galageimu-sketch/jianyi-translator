using System.Collections.Concurrent;
using Thanslator.Core;

namespace Thanslator.Capture;

/// <summary>单一后台 apartment 与有界队列；超时不会不断创建替代 COM 线程。</summary>
public sealed class CaptureWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(1);
    private readonly Thread _thread;
    private bool _disposed;

    public CaptureWorker(ApartmentState apartment)
    {
        _thread = new Thread(() =>
        {
            foreach (var work in _queue.GetConsumingEnumerable()) work();
        }) { IsBackground = true, Name = $"Thanslator.Capture.{apartment}" };
        _thread.SetApartmentState(apartment);
        _thread.Start();
    }

    public async Task<CaptureResult> RunAsync(Func<CancellationToken, CaptureResult> capture,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        var completion = new TaskCompletionSource<CaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.TryAdd(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    completion.TrySetResult(capture(token));
                }
                catch (OperationCanceledException) { completion.TrySetCanceled(token); }
                catch (Exception) { completion.TrySetResult(new CaptureResult(CaptureStatus.Forbidden)); }
            })) return new CaptureResult(CaptureStatus.Busy);
        try { return await completion.Task.WaitAsync(token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CaptureResult(CaptureStatus.Timeout);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        // Provider 可能仍阻塞；后台线程不阻止退出，也不在 UI 线程 Join。
    }
}
