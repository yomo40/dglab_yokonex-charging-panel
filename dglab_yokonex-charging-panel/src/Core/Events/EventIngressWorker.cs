using System.Threading.Channels;
using Serilog;

namespace ChargingPanel.Core.Events;

/// <summary>
/// 事件入口工作器：Rx 回调只做有界入队，实际业务处理在单一后台泵串行执行，
/// 彻底消除控制路径上的 async void 与无界并发。
///
/// 队列有界：入队失败（满）时如实丢弃并记录，绝不无限积压事件。
/// </summary>
public sealed class EventIngressWorker : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<EventIngressWorker>();

    private readonly Channel<Func<CancellationToken, Task>> _queue;
    private readonly Task _pump;
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _shutdownTimeout;
    private bool _disposed;

    public EventIngressWorker(int capacity = 256, TimeSpan? shutdownTimeout = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(5);
        _queue = Channel.CreateBounded<Func<CancellationToken, Task>>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>
    /// 尝试入队一个事件处理回调。队列已满或已关闭时返回 false。
    /// </summary>
    public bool TryEnqueue(Func<CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_cts.IsCancellationRequested)
        {
            return false;
        }

        if (_queue.Writer.TryWrite(handler))
        {
            return true;
        }

        Logger.Warning("事件入口队列已满，丢弃一个事件处理请求。");
        return false;
    }

    private async Task PumpAsync()
    {
        await foreach (var handler in _queue.Reader.ReadAllAsync(_cts.Token))
        {
            try
            {
                await handler(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 宿主关闭或事件处理被取消。
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "事件处理回调执行失败。");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Writer.TryComplete();
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，无需重复取消。
        }

        try
        {
            _pump.Wait(_shutdownTimeout);
        }
        catch (TimeoutException)
        {
            Logger.Warning("事件入口工作器停止超时。");
        }
        catch
        {
            // 泵任务异常不阻断关闭。
        }

        _cts.Dispose();
    }
}
