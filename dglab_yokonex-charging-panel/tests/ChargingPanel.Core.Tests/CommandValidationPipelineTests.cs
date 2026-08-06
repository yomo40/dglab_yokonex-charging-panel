using System.Text.Json;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Application.Devices;
using ChargingPanel.Core.Devices.Runtime;
using ChargingPanel.Core.Devices.Safety;
using Xunit;

namespace ChargingPanel.Core.Tests;

/// <summary>
/// 应用层入口测试：业务模块只能通过 Application Command 触达设备，
/// 且值域越界必须在到达 Driver 之前被拒绝。
/// </summary>
public sealed class CommandValidationPipelineTests
{
    [Fact]
    public async Task SetIntensityAsync_ValueAboveValidRange_RejectsBeforeDriver()
    {
        var instanceId = DeviceInstanceId.New();
        var driver = new SafetySupervisorTests.RecordingDriver();
        using var kernel = SafetySupervisorTests.SafetyKernelHarness.Create(driver, instanceId);
        var service = new DeviceApplicationService(kernel.Dispatcher, kernel.Safety, TimeProvider.System);

        var result = await service.SetIntensityAsync(
            new SetIntensityRequest(instanceId, "main", 500, new CommandSource(CommandSourceKind.UserInterface, "test")),
            CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
        Assert.Equal("value.out_of_range", result.ReasonCode);
        Assert.Empty(driver.Calls);
    }

    [Fact]
    public async Task SetIntensityAsync_AboveDefaultSafetyRange_RejectsBeforeDriver()
    {
        var instanceId = DeviceInstanceId.New();
        var driver = new SafetySupervisorTests.RecordingDriver();
        using var kernel = SafetySupervisorTests.SafetyKernelHarness.Create(driver, instanceId);
        var service = new DeviceApplicationService(kernel.Dispatcher, kernel.Safety, TimeProvider.System);

        // 80 在 ValidRange(0,100) 内，但超出 DefaultSafetyRange(0,50)。
        // 声明了安全范围却不执行，会给调用方虚假的安全感。
        var result = await service.SetIntensityAsync(
            new SetIntensityRequest(instanceId, "main", 80, new CommandSource(CommandSourceKind.UserInterface, "test")),
            CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
        Assert.Equal("value.above_safety_range", result.ReasonCode);
        Assert.Empty(driver.Calls);
    }

    [Fact]
    public void ValidateActuatorValue_BelowSafetyRangeMinimum_DoesNotClaimValueIsAbove()
    {
        var capability = SafetySupervisorTests.SafetyKernelHarness.BuildCapabilityWithSafetyFloor();

        // 安全范围为 (10,50)：值 5 低于下限，
        // reason code 不能声称"高于安全范围"，否则直接误导调用方。
        var result = CommandValidationPipeline.ValidateActuatorValue(capability, "main", 5);

        Assert.False(result.IsValid);
        Assert.NotEqual("value.above_safety_range", result.ReasonCode);
        Assert.Equal("value.below_safety_range", result.ReasonCode);
    }

    [Fact]
    public async Task SetIntensityAsync_ValueWithinRange_ReachesDriver()
    {
        var instanceId = DeviceInstanceId.New();
        var driver = new SafetySupervisorTests.RecordingDriver();
        using var kernel = SafetySupervisorTests.SafetyKernelHarness.Create(driver, instanceId);
        var service = new DeviceApplicationService(kernel.Dispatcher, kernel.Safety, TimeProvider.System);

        var result = await service.SetIntensityAsync(
            new SetIntensityRequest(instanceId, "main", 30, new CommandSource(CommandSourceKind.UserInterface, "test")),
            CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Executed, result.Status);
        Assert.NotEmpty(driver.Calls);
    }

    [Fact]
    public async Task SetIntensityAsync_WhenLatched_RejectsWithSafetyReason()
    {
        var instanceId = DeviceInstanceId.New();
        var driver = new SafetySupervisorTests.RecordingDriver();
        using var kernel = SafetySupervisorTests.SafetyKernelHarness.Create(driver, instanceId);
        var service = new DeviceApplicationService(kernel.Dispatcher, kernel.Safety, TimeProvider.System);
        await kernel.Safety.EmergencyStopAllAsync("test", CancellationToken.None);
        driver.ClearCalls();

        var result = await service.SetIntensityAsync(
            new SetIntensityRequest(instanceId, "main", 10, new CommandSource(CommandSourceKind.Automation, "rule")),
            CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
        Assert.Equal("safety.latched", result.ReasonCode);
        Assert.Empty(driver.Calls);
    }

    [Fact]
    public async Task SetIntensityAsync_UnknownActuator_RejectsBeforeDriver()
    {
        var instanceId = DeviceInstanceId.New();
        var driver = new SafetySupervisorTests.RecordingDriver();
        using var kernel = SafetySupervisorTests.SafetyKernelHarness.Create(driver, instanceId);
        var service = new DeviceApplicationService(kernel.Dispatcher, kernel.Safety, TimeProvider.System);

        var result = await service.SetIntensityAsync(
            new SetIntensityRequest(instanceId, "unknown", 10, new CommandSource(CommandSourceKind.UserInterface, "test")),
            CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
        Assert.Equal("actuator.unknown", result.ReasonCode);
        Assert.Empty(driver.Calls);
    }

    [Fact]
    public async Task EmergencyStopAsync_AlwaysLatchesRegardlessOfDriverOutcome()
    {
        var instanceId = DeviceInstanceId.New();
        var driver = new SafetySupervisorTests.RecordingDriver
        {
            SafeStopStatusOverride = SafeStopStatus.Failed
        };
        using var kernel = SafetySupervisorTests.SafetyKernelHarness.Create(driver, instanceId);
        var service = new DeviceApplicationService(kernel.Dispatcher, kernel.Safety, TimeProvider.System);

        var report = await service.EmergencyStopAsync("user", CancellationToken.None);

        Assert.False(report.AllConfirmedStopped);
        Assert.True(kernel.Safety.Current.IsLatched);
    }
}
