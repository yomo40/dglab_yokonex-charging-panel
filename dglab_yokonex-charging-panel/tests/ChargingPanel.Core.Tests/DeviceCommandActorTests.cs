using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Devices.Runtime;
using Xunit;

namespace ChargingPanel.Core.Tests;

/// <summary>
/// 单写者 Actor 测试：同实例串行、不同实例并行、取消语义与背压不丢命令。
/// </summary>
public sealed class DeviceCommandActorTests
{
    [Fact]
    public async Task EnqueueAsync_DifferentInstances_RunConcurrently()
    {
        var instanceA = DeviceInstanceId.New();
        var instanceB = DeviceInstanceId.New();
        await using var actorA = new DeviceCommandActor(instanceA);
        await using var actorB = new DeviceCommandActor(instanceB);
        var blockA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pendingA = actorA.EnqueueAsync(
            CreateCommand(instanceA),
            async (command, _) =>
            {
                enteredA.TrySetResult();
                await blockA.Task;
                return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Executed);
            },
            CancellationToken.None);
        await enteredA.Task;

        // 实例 A 阻塞时，实例 B 必须仍能完成：不同实例不得互相阻塞。
        var resultB = await actorB.EnqueueAsync(
            CreateCommand(instanceB),
            (command, _) => ValueTask.FromResult(
                new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Executed)),
            CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Executed, resultB.Status);
        Assert.False(pendingA.IsCompleted);
        blockA.TrySetResult();
        Assert.Equal(DeviceCommandStatus.Executed, (await pendingA).Status);
    }

    [Fact]
    public async Task EnqueueAsync_SameInstance_ExecutesOneAtATime()
    {
        var instanceId = DeviceInstanceId.New();
        await using var actor = new DeviceCommandActor(instanceId);
        var concurrent = 0;
        var maxConcurrent = 0;
        var gate = new object();

        var tasks = Enumerable.Range(0, 20).Select(_ => actor.EnqueueAsync(
            CreateCommand(instanceId),
            async (command, _) =>
            {
                lock (gate)
                {
                    concurrent++;
                    maxConcurrent = Math.Max(maxConcurrent, concurrent);
                }

                await Task.Yield();
                lock (gate)
                {
                    concurrent--;
                }

                return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Executed);
            },
            CancellationToken.None)).ToArray();
        await Task.WhenAll(tasks);

        // 单写者：同实例并发度恒为 1。
        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task EnqueueAsync_AtMostOnceCancelledInFlight_ReportsOutcomeUnknownWithoutResend()
    {
        var instanceId = DeviceInstanceId.New();
        await using var actor = new DeviceCommandActor(instanceId);
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        var result = await actor.EnqueueAsync(
            CreateCommand(instanceId, CommandDelivery.AtMostOnce),
            async (_, token) =>
            {
                Interlocked.Increment(ref attempts);
                await cts.CancelAsync();
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("不应到达");
            },
            cts.Token);

        // 物理动作结果未知时必须是 OutcomeUnknown，且绝不自动重发。
        Assert.Equal(DeviceCommandStatus.OutcomeUnknown, result.Status);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task EnqueueAsync_WhenQueueFull_WaitsInsteadOfDroppingCommand()
    {
        var instanceId = DeviceInstanceId.New();
        await using var actor = new DeviceCommandActor(instanceId, capacity: 1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = 0;

        var first = actor.EnqueueAsync(
            CreateCommand(instanceId, CommandDelivery.AtMostOnce),
            async (command, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                Interlocked.Increment(ref executed);
                return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Executed);
            },
            CancellationToken.None);
        await entered.Task;

        var queued = Enumerable.Range(0, 4).Select(_ => actor.EnqueueAsync(
            CreateCommand(instanceId, CommandDelivery.AtMostOnce),
            (command, _) =>
            {
                Interlocked.Increment(ref executed);
                return ValueTask.FromResult(
                    new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Executed));
            },
            CancellationToken.None)).ToArray();

        release.TrySetResult();
        await first;
        await Task.WhenAll(queued);

        // 非幂等命令不能因队列满被静默丢弃：5 条全部执行。
        Assert.Equal(5, executed);
        Assert.All(queued, task => Assert.Equal(DeviceCommandStatus.Executed, task.Result.Status));
    }

    [Fact]
    public async Task DisposeAsync_WhileDriverHangs_CancelsInsteadOfDeadlocking()
    {
        var instanceId = DeviceInstanceId.New();
        var actor = new DeviceCommandActor(instanceId);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = actor.EnqueueAsync(
            CreateCommand(instanceId),
            async (command, token) =>
            {
                entered.TrySetResult();
                // 模拟 Driver 永久挂起但响应取消。
                await Task.Delay(Timeout.Infinite, token);
                return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Executed);
            },
            CancellationToken.None);
        await entered.Task;

        // Dispose 必须取消在途 Driver 调用，否则关闭会永久死锁。
        await actor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DeviceCommandStatus.OutcomeUnknown, result.Status);
    }

    [Fact]
    public async Task DisposeAsync_WhenDriverIgnoresCancellation_StillCompletesWithinTimeout()
    {
        var instanceId = DeviceInstanceId.New();
        var actor = new DeviceCommandActor(instanceId, shutdownTimeout: TimeSpan.FromSeconds(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = actor.EnqueueAsync(
            CreateCommand(instanceId),
            async (command, _) =>
            {
                entered.TrySetResult();
                // 完全无视取消令牌：模拟不响应取消的 Driver。
                await new TaskCompletionSource().Task;
                return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Executed);
            },
            CancellationToken.None);
        await entered.Task;

        // 协作式取消对无视令牌的 Driver 无效，关闭仍必须在超时后返回，不能永久挂起。
        await actor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task DisposeAsync_WhenAbandonedPumpResumesAfterDispose_DoesNotThrowOnBackgroundThread()
    {
        var instanceId = DeviceInstanceId.New();
        var actor = new DeviceCommandActor(instanceId, shutdownTimeout: TimeSpan.FromMilliseconds(200));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? backgroundFailure = null;

        // 第一条命令无视取消并挂起，迫使 Dispose 超时并遗留泵任务。
        var first = actor.EnqueueAsync(
            CreateCommand(instanceId),
            async (command, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Executed);
            },
            CancellationToken.None);

        // 第二条命令排队；泵在恢复后会为它创建 linked token source。
        var second = actor.EnqueueAsync(
            CreateCommand(instanceId),
            (command, _) => ValueTask.FromResult(
                new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Executed)),
            CancellationToken.None);

        await entered.Task;
        await actor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        // Dispose 已返回并释放 _shutdownCts；现在放开泵，它会继续处理第二条命令。
        release.TrySetResult();
        try
        {
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            await second.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            backgroundFailure = ex;
        }

        // 遗留的泵不得因访问已释放的 CancellationTokenSource 而抛出。
        Assert.False(
            backgroundFailure is ObjectDisposedException,
            $"遗留泵访问已释放的取消源: {backgroundFailure}");

        // 更关键的是结果语义：宿主关闭导致的失败不得被归因为 Driver 故障。
        var secondResult = await second;
        Assert.NotEqual("driver.threw", secondResult.ReasonCode);
    }

    [Fact]
    public async Task EnqueueAsync_WhenCallerTokenSourceDisposed_DoesNotBlameActorShutdown()
    {
        var instanceId = DeviceInstanceId.New();
        await using var actor = new DeviceCommandActor(instanceId);
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        cts.Dispose();

        // 调用方自己的取消源被释放，不能被误报为"宿主已关闭队列"。
        var result = await actor.EnqueueAsync(
            CreateCommand(instanceId),
            (command, _) => ValueTask.FromResult(
                new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Executed)),
            token);

        Assert.NotEqual("actor.shutdown", result.ReasonCode);
    }

    [Fact]
    public async Task EnqueueAsync_AfterDispose_RejectsWithoutExecuting()
    {
        var instanceId = DeviceInstanceId.New();
        var actor = new DeviceCommandActor(instanceId);
        await actor.DisposeAsync();
        var executed = false;

        var result = await actor.EnqueueAsync(
            CreateCommand(instanceId),
            (command, _) =>
            {
                executed = true;
                return ValueTask.FromResult(
                    new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Executed));
            },
            CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
        Assert.Equal("actor.shutdown", result.ReasonCode);
        Assert.False(executed);
    }

    private static DeviceCommandEnvelope CreateCommand(
        DeviceInstanceId instanceId,
        CommandDelivery delivery = CommandDelivery.Idempotent)
    {
        using var document = System.Text.Json.JsonDocument.Parse("{\"level\":10}");
        return new DeviceCommandEnvelope(
            CommandId.New(),
            instanceId,
            new CapabilityId("intensity.output"),
            "set.level",
            document.RootElement.Clone(),
            new CommandSource(CommandSourceKind.UserInterface, "test"),
            delivery,
            DateTimeOffset.UnixEpoch,
            null,
            0,
            0);
    }
}
