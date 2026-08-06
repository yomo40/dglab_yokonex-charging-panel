using ChargingPanel.Contracts.Commands;

namespace ChargingPanel.Vendor.DGLab.Common;

/// <summary>
/// 波形帧调度器。以固定节拍逐帧发送队列中的波形帧；
/// 帧过期直接丢弃，绝不无限等待。
/// 调度器由驱动所有；安全停止时调用 <see cref="Stop"/> 清除全部待发帧。
/// </summary>
public sealed class WaveformPump : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<QueuedFrame> _queue = new();
    private readonly Func<byte[], CancellationToken, ValueTask> _send;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public WaveformPump(
        Func<byte[], CancellationToken, ValueTask> send,
        TimeProvider timeProvider,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _send = send;
        _timeProvider = timeProvider;
        _interval = interval ?? TimeSpan.FromMilliseconds(100);
    }

    /// <summary>入队一帧。deadline 之前必须发出，过期帧由调度器丢弃。</summary>
    public void Enqueue(byte[] frame, DateTimeOffset deadline)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            _queue.Enqueue(new QueuedFrame(frame, deadline));
            EnsureRunningLocked();
        }
    }

    /// <summary>清空所有待发帧，但保持调度器可继续接收新帧。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _queue.Clear();
        }
    }

    public bool HasPendingFrames
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count > 0;
            }
        }
    }

    /// <summary>停止调度并清空队列。停止后不再发送任何帧。</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            _queue.Clear();
            cts = _cts;
            _cts = null;
            _loop = null;
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，无需重复取消。
        }

        cts.Dispose();
    }

    public void Dispose() => Stop();

    private void EnsureRunningLocked()
    {
        if (_loop is not null && !_loop.IsCompleted)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => PumpAsync(token));
    }

    private async Task PumpAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            QueuedFrame frame;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    _loop = null;
                    _cts = null;
                    return;
                }

                frame = _queue.Peek();
            }

            if (frame.Deadline < _timeProvider.GetUtcNow())
            {
                // 帧已过期：不得补发，直接丢弃。
                lock (_gate)
                {
                    if (_queue.Count > 0)
                    {
                        _queue.Dequeue();
                    }
                }

                continue;
            }

            try
            {
                await _send(frame.Bytes, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // 单帧发送失败不终止调度；下一拍继续。
            }

            lock (_gate)
            {
                if (_queue.Count > 0)
                {
                    _queue.Dequeue();
                }
            }

            await Task.Delay(_interval, _timeProvider, token);
        }
    }

    private sealed record QueuedFrame(byte[] Bytes, DateTimeOffset Deadline);
}