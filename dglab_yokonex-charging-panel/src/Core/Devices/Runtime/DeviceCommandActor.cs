using System.Threading.Channels;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.Core.Devices.Runtime;

/// <summary>
/// 每实例单写者 Actor。同实例命令严格 FIFO 串行执行，不同实例互不阻塞。
/// 队列有界；非幂等命令不得因队列满被静默丢弃。
/// </summary>
public sealed class DeviceCommandActor : IAsyncDisposable
{
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<WorkItem> _queue;
    private readonly Task _pump;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly TimeSpan _shutdownTimeout;

    public DeviceCommandActor(
        DeviceInstanceId instanceId,
        int capacity = 64,
        TimeSpan? shutdownTimeout = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (shutdownTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeout));
        }

        _shutdownTimeout = shutdownTimeout ?? DefaultShutdownTimeout;

        InstanceId = instanceId;
        _queue = System.Threading.Channels.Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        _pump = Task.Run(PumpAsync);
    }

    public DeviceInstanceId InstanceId { get; }

    /// <summary>
    /// 入队并等待执行结果。队列满时等待而非丢弃，保证非幂等命令不丢失。
    /// </summary>
    public async Task<DeviceCommandResult> EnqueueAsync(
        DeviceCommandEnvelope command,
        Func<DeviceCommandEnvelope, CancellationToken, ValueTask<DeviceCommandResult>> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(handler);

        var completion = new TaskCompletionSource<DeviceCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(command, handler, completion, cancellationToken);
        try
        {
            await _queue.Writer.WriteAsync(item, cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return new DeviceCommandResult(
                command.CommandId,
                DeviceCommandStatus.Rejected,
                "actor.shutdown",
                "设备命令队列已关闭。");
        }

        return await completion.Task;
    }

    private async Task PumpAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync())
        {
            if (item.CancellationToken.IsCancellationRequested)
            {
                item.Completion.TrySetResult(new DeviceCommandResult(
                    item.Command.CommandId,
                    DeviceCommandStatus.Rejected,
                    "command.cancelled",
                    "命令在执行前已取消。"));
                continue;
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    item.CancellationToken,
                    _shutdownCts.Token);
                var result = await item.Handler(item.Command, linked.Token);
                item.Completion.TrySetResult(result);
            }
            catch (ObjectDisposedException)
            {
                // 纵深防御，当前释放纪律下不可达：_shutdownCts 仅在泵结束后释放，
                // 而泵结束后不再处理任何命令。保留此分支是为了万一将来改动释放顺序，
                // 宿主关闭不会被误归因为 Driver 故障（driver.threw 会把排障引向错误方向）。
                // 注意：调用方自身取消源被释放不会走到这里——捕获的令牌副本仍可安全使用。
                item.Completion.TrySetResult(new DeviceCommandResult(
                    item.Command.CommandId,
                    DeviceCommandStatus.Rejected,
                    "actor.shutdown",
                    "命令未执行：宿主已关闭设备命令队列。"));
            }
            catch (OperationCanceledException)
            {
                // 取消发生在 I/O 之后可能已产生物理效果，语义必须是结果未知。
                item.Completion.TrySetResult(new DeviceCommandResult(
                    item.Command.CommandId,
                    DeviceCommandStatus.OutcomeUnknown,
                    "command.cancelled_in_flight",
                    "命令在执行途中被取消，结果未知。"));
            }
            catch (Exception ex)
            {
                item.Completion.TrySetResult(new DeviceCommandResult(
                    item.Command.CommandId,
                    DeviceCommandStatus.TransportFailed,
                    "driver.threw",
                    ex.Message));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 先停止接单，再取消在途 Driver 调用。
        // 不取消就等待泵结束会在 Driver 挂起时永久死锁。
        _queue.Writer.TryComplete();
        try
        {
            await _shutdownCts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，无需重复取消。
        }

        var pumpCompleted = false;
        try
        {
            // 协作式取消对完全无视令牌的 Driver 无效，因此必须带超时。
            // 超时后放弃等待：泵任务会被遗留，但关闭流程不能被单个设备永久阻塞。
            await _pump.WaitAsync(_shutdownTimeout);
            pumpCompleted = true;
        }
        catch (TimeoutException)
        {
            // 泵仍在运行，见下方释放判定。
        }
        catch
        {
            // 泵已结束但抛出异常；关闭期间不向调用方传播。
            pumpCompleted = true;
        }

        // 只有泵确实结束后才释放取消源：遗留的泵仍会用它创建 linked token，
        // 提前释放会在后台线程抛 ObjectDisposedException，并把宿主关闭误报为 Driver 故障。
        //
        // 已知代价（刻意选择）：Driver 永久卡死时该取消源不会被释放，
        // 每个被放弃的 Actor 泄漏一个 CancellationTokenSource。
        // 在对同一个卡死 BLE 设备反复重连的场景下会累积。
        // 相比"关闭被单个设备永久阻塞"与"后台线程抛异常并误报故障归因"，
        // 这是更可接受的代价；根治需要 Driver 层强制超时（Task 9 Broker 范围）。
        if (pumpCompleted)
        {
            _shutdownCts.Dispose();
        }
    }

    private sealed record WorkItem(
        DeviceCommandEnvelope Command,
        Func<DeviceCommandEnvelope, CancellationToken, ValueTask<DeviceCommandResult>> Handler,
        TaskCompletionSource<DeviceCommandResult> Completion,
        CancellationToken CancellationToken);
}
