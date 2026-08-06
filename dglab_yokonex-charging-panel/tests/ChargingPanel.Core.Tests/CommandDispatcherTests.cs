using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Devices.Runtime;
using ChargingPanel.Core.Devices.Safety;
using Xunit;

namespace ChargingPanel.Core.Tests;

/// <summary>
/// 命令入口测试：校验顺序、过期丢弃、Epoch 失配与不支持能力的拒绝语义。
/// </summary>
public sealed class CommandDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_UnknownInstance_RejectsWithoutTouchingDriver()
    {
        var driver = new SafetySupervisorTests.RecordingDriver();
        using var kernel = SafetySupervisorTests.SafetyKernelHarness.Create(driver, DeviceInstanceId.New());
        var foreignCommand = kernel.CreateCommandForInstance(DeviceInstanceId.New(), 20);

        var result = await kernel.Dispatcher.DispatchAsync(foreignCommand, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
        Assert.Equal("instance.unavailable", result.ReasonCode);
        Assert.Empty(driver.Calls);
    }

    [Fact]
    public async Task DispatchAsync_UnsupportedCapability_RejectsWithoutTouchingDriver()
    {
        var instanceId = DeviceInstanceId.New();
        var driver = new SafetySupervisorTests.RecordingDriver();
        using var kernel = SafetySupervisorTests.SafetyKernelHarness.Create(driver, instanceId);
        var command = kernel.CreateCommandWithCapability(new CapabilityId("pump.output"), 20);

        var result = await kernel.Dispatcher.DispatchAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
        Assert.Equal("capability.unsupported", result.ReasonCode);
        Assert.Empty(driver.Calls);
    }

    [Fact]
    public async Task DispatchAsync_ExpiredStreamingFrame_ReportsExpiredAndDoesNotResend()
    {
        var instanceId = DeviceInstanceId.New();
        var driver = new SafetySupervisorTests.RecordingDriver();
        using var kernel = SafetySupervisorTests.SafetyKernelHarness.Create(driver, instanceId);
        var expired = kernel.CreateCommand(
            30,
            CommandDelivery.StreamingFrame,
            expiresAt: DateTimeOffset.UnixEpoch);

        var result = await kernel.Dispatcher.DispatchAsync(expired, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Expired, result.Status);
        // 过期帧绝不能补发到 Driver。
        Assert.Empty(driver.Calls);
    }

    [Fact]
    public async Task DispatchAsync_StaleSessionEpoch_ReportsSuperseded()
    {
        var instanceId = DeviceInstanceId.New();
        var driver = new SafetySupervisorTests.RecordingDriver();
        using var kernel = SafetySupervisorTests.SafetyKernelHarness.Create(driver, instanceId, sessionEpoch: 7);
        var stale = kernel.CreateCommandWithSessionEpoch(3, 40);

        var result = await kernel.Dispatcher.DispatchAsync(stale, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Superseded, result.Status);
        Assert.Equal("session.epoch_mismatch", result.ReasonCode);
        Assert.Empty(driver.Calls);
    }

    [Fact]
    public async Task DispatchAsync_StaleSafetyEpoch_ReportsSuperseded()
    {
        var instanceId = DeviceInstanceId.New();
        var driver = new SafetySupervisorTests.RecordingDriver();
        using var kernel = SafetySupervisorTests.SafetyKernelHarness.Create(driver, instanceId);
        var stale = kernel.CreateCommandWithSafetyEpoch(99, 40);

        var result = await kernel.Dispatcher.DispatchAsync(stale, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Superseded, result.Status);
        Assert.Equal("safety.epoch_mismatch", result.ReasonCode);
        Assert.Empty(driver.Calls);
    }

    [Fact]
    public async Task DispatchAsync_WhenLatchOccursDuringDriverCall_StillRejectsQueuedCommands()
    {
        var instanceId = DeviceInstanceId.New();
        var driver = new SafetySupervisorTests.RecordingDriver();
        using var kernel = SafetySupervisorTests.SafetyKernelHarness.Create(driver, instanceId);

        // 首条命令在 Driver 内阻塞，期间发生急停。
        var gate = driver.BlockNextCommand();
        var inFlight = kernel.Dispatcher.DispatchAsync(kernel.CreateCommand(50), CancellationToken.None);
        await driver.WaitUntilBlockedAsync();

        // 排队中的第二条命令必须在急停后被拒绝，不能送达 Driver。
        var queued = kernel.Dispatcher.DispatchAsync(kernel.CreateCommand(60), CancellationToken.None);
        await kernel.Safety.EmergencyStopAllAsync("test", CancellationToken.None);
        gate.TrySetResult();

        await inFlight;
        var queuedResult = await queued;
        Assert.Equal(DeviceCommandStatus.Rejected, queuedResult.Status);
        Assert.Equal("safety.latched", queuedResult.ReasonCode);
        // Driver 只应看到第一条命令与安全停止，不含第二条。
        Assert.DoesNotContain("execute:set.level.60", driver.Calls);
    }

    [Fact]
    public async Task DispatchAsync_SameInstance_ExecutesStrictlyInOrder()
    {
        var instanceId = DeviceInstanceId.New();
        var driver = new SafetySupervisorTests.RecordingDriver();
        using var kernel = SafetySupervisorTests.SafetyKernelHarness.Create(driver, instanceId);

        var commands = Enumerable.Range(1, 12)
            .Select(index => kernel.CreateCommandWithOperation($"set.level.{index}"))
            .ToArray();
        var dispatches = commands
            .Select(command => kernel.Dispatcher.DispatchAsync(command, CancellationToken.None))
            .ToArray();
        await Task.WhenAll(dispatches);

        // 同实例必须严格 FIFO：入队顺序即执行顺序。
        Assert.Equal(
            commands.Select(command => $"execute:{command.OperationId}"),
            driver.Calls);
    }
}
