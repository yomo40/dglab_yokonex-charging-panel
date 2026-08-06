using ChargingPanel.Contracts.Commands;
using ChargingPanel.Core.Devices.Runtime;
using Serilog;

namespace ChargingPanel.Core.Automation;

/// <summary>
/// 命令调度器：负责"带时长动作"执行结束后的归零/释放命令。
///
/// 关键安全约束：
/// - 每个调度任务都持有 CancellationToken（宿主关闭可取消）；
/// - 命令信封在调度时以当前 SafetyEpoch/SessionEpoch 构造，急停或会话切换后
///   派发会被 Dispatcher 以 Superseded 拒绝，绝不恢复物理输出；
/// - 关闭时等待所有在途归零任务结束，绝不静默丢弃（也绝不无限阻塞）。
/// </summary>
public sealed class DeviceCommandScheduler : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<DeviceCommandScheduler>();
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly CommandDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _shutdownTimeout;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly object _gate = new();
    private readonly List<Task> _pending = [];
    private bool _disposed;

    public DeviceCommandScheduler(
        CommandDispatcher dispatcher,
        TimeProvider timeProvider,
        TimeSpan? shutdownTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (shutdownTimeout is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeout));
        }

        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
        _shutdownTimeout = shutdownTimeout ?? DefaultShutdownTimeout;
    }

    /// <summary>
    /// 在 <paramref name="delay"/> 后派发 <paramref name="releaseCommand"/>。
    /// 命令信封必须携带调度时刻的 Epoch；Epoch 失配时由 Dispatcher 拒绝。
    /// </summary>
    public void ScheduleAfter(TimeSpan delay, DeviceCommandEnvelope releaseCommand)
    {
        ArgumentNullException.ThrowIfNull(releaseCommand);
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        ObjectDisposedException.ThrowIf(_disposed, this);

        var token = _shutdownCts.Token;
        lock (_gate)
        {
            var task = RunScheduledAsync(releaseCommand, delay, token);
            _pending.Add(task);
            _ = task.ContinueWith(
                _ => RemovePending(task),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        // 不 await：归零是后台行为，但任务被跟踪，关闭时统一收口。
    }

    private async Task RunScheduledAsync(
        DeviceCommandEnvelope releaseCommand,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, cancellationToken);
            await _dispatcher.DispatchAsync(releaseCommand, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 宿主关闭或取消：归零任务不再派发。
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "归零命令派发失败: {CommandId}", releaseCommand.CommandId);
        }
    }

    private void RemovePending(Task task)
    {
        lock (_gate)
        {
            _pending.Remove(task);
        }
    }

    /// <summary>
    /// 等待所有在途归零任务结束。带超时：个别 Driver 卡死不阻塞宿主关闭。
    /// </summary>
    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await _shutdownCts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，无需重复取消。
        }

        Task[] pending;
        lock (_gate)
        {
            pending = _pending.ToArray();
        }

        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(_shutdownTimeout);
        }
        catch (TimeoutException)
        {
            Logger.Warning("等待归零任务结束超时，放弃等待 {Count} 个任务。", pending.Length);
        }
        catch
        {
            // 单个归零任务异常已在任务内捕获，此处只负责收口。
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _shutdownCts.Dispose();
    }
}
