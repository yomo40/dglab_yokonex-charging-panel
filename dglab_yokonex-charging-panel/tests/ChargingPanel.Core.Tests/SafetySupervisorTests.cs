using System.Text.Json;
using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Telemetry;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Core.Devices.Runtime;
using ChargingPanel.Core.Devices.Safety;
using Xunit;

namespace ChargingPanel.Core.Tests;

/// <summary>
/// 安全内核测试：急停必须先锁闭再停止，且锁闭后拒绝一切普通命令。
/// </summary>
public sealed class SafetySupervisorTests
{
    private static readonly DeviceInstanceId InstanceA = DeviceInstanceId.New();

    [Fact]
    public async Task EmergencyStopAllAsync_WhenCommandInFlight_LatchesBeforeDriverSafeStop()
    {
        var driver = new RecordingDriver();
        using var kernel = SafetyKernelHarness.Create(driver, InstanceA);
        await driver.BlockNextCommandAsync();
        var normal = kernel.Dispatcher.DispatchAsync(kernel.CreateCommand(50), CancellationToken.None);

        var stop = await kernel.Safety.EmergencyStopAllAsync("test", CancellationToken.None);

        Assert.True(kernel.Safety.Current.IsLatched);
        // 锁闭必须发生在 Driver SafeStop 之前，否则停止期间仍可入队新输出。
        Assert.True(driver.LatchedBeforeSafeStop);
        Assert.True(stop.DriverSafeStopAttempted);

        driver.ReleaseBlockedCommand();
        await normal;
    }

    [Fact]
    public async Task DispatchAsync_AfterLatch_RejectsNormalCommand()
    {
        var driver = new RecordingDriver();
        using var kernel = SafetyKernelHarness.Create(driver, InstanceA);
        await kernel.Safety.EmergencyStopAllAsync("test", CancellationToken.None);

        var result = await kernel.Dispatcher.DispatchAsync(kernel.CreateCommand(10), CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
        Assert.Equal("safety.latched", result.ReasonCode);
    }

    [Fact]
    public async Task EmergencyStopAllAsync_WhenCalled_IncrementsSafetyEpoch()
    {
        var driver = new RecordingDriver();
        using var kernel = SafetyKernelHarness.Create(driver, InstanceA);
        var before = kernel.Safety.Current.SafetyEpoch;

        await kernel.Safety.EmergencyStopAllAsync("test", CancellationToken.None);

        Assert.True(kernel.Safety.Current.SafetyEpoch > before);
    }

    [Fact]
    public async Task ResetAsync_WithoutExplicitAuthorization_KeepsLatched()
    {
        var driver = new RecordingDriver();
        using var kernel = SafetyKernelHarness.Create(driver, InstanceA);
        await kernel.Safety.EmergencyStopAllAsync("test", CancellationToken.None);

        var reset = await kernel.Safety.ResetAsync(new SafetyResetRequest(false, "auto"), CancellationToken.None);

        Assert.False(reset);
        Assert.True(kernel.Safety.Current.IsLatched);
    }

    [Fact]
    public async Task ResetAsync_WithExplicitAuthorization_ClearsLatchAndAllowsCommands()
    {
        var driver = new RecordingDriver();
        using var kernel = SafetyKernelHarness.Create(driver, InstanceA);
        await kernel.Safety.EmergencyStopAllAsync("test", CancellationToken.None);

        var reset = await kernel.Safety.ResetAsync(new SafetyResetRequest(true, "operator"), CancellationToken.None);
        var result = await kernel.Dispatcher.DispatchAsync(kernel.CreateCommand(10), CancellationToken.None);

        Assert.True(reset);
        Assert.False(kernel.Safety.Current.IsLatched);
        Assert.Equal(DeviceCommandStatus.Executed, result.Status);
    }

    [Fact]
    public async Task EmergencyStopAllAsync_WhenFirstDriverHangs_StillStopsOtherInstances()
    {
        var hangingInstance = DeviceInstanceId.New();
        var healthyInstance = DeviceInstanceId.New();
        var hangingDriver = new RecordingDriver { HangSafeStop = true };
        var healthyDriver = new RecordingDriver();
        var capability = SafetyKernelHarness.BuildCapability();
        var safety = new SafetySupervisor(TimeProvider.System);
        var registry = new StaticRuntimeRegistry(hangingInstance, hangingDriver, capability);
        registry.Add(healthyInstance, healthyDriver, capability);
        safety.AttachRuntime(registry);

        // 首个设备的 Driver 永久挂起时，其余设备仍必须被安全停止。
        var stop = await safety.EmergencyStopAllAsync("test", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(safety.Current.IsLatched);
        Assert.Contains("safestop", healthyDriver.Calls);
        var hangingOutcome = stop.Results.Single(result => result.InstanceId == hangingInstance);
        Assert.Equal(SafeStopStatus.OutcomeUnknown, hangingOutcome.Status);
        Assert.False(stop.AllConfirmedStopped);
    }

    [Fact]
    public async Task EmergencyStopAllAsync_WhenDriverSafeStopFails_ReportsPerInstanceFailure()
    {
        var driver = new RecordingDriver { SafeStopStatusOverride = SafeStopStatus.Failed };
        using var kernel = SafetyKernelHarness.Create(driver, InstanceA);

        var stop = await kernel.Safety.EmergencyStopAllAsync("test", CancellationToken.None);

        // 单实例停止失败不能被汇总成整体成功，也不能解除锁闭。
        Assert.Equal(SafeStopStatus.Failed, Assert.Single(stop.Results).Status);
        Assert.True(kernel.Safety.Current.IsLatched);
    }

    internal sealed class RecordingDriver : IProtocolDriver
    {
        private readonly List<string> _calls = [];
        private TaskCompletionSource? _block;
        private TaskCompletionSource? _blockObserved;

        public ProtocolProfileId ProfileId { get; } = new("dglab.coyote.v3.ble");

        public SafeStopStatus SafeStopStatusOverride { get; init; } = SafeStopStatus.ConfirmedStopped;

        /// <summary>模拟 Driver 永久挂起，用于验证急停不会被单个设备拖死。</summary>
        public bool HangSafeStop { get; init; }

        public bool LatchedBeforeSafeStop { get; private set; }

        public Func<bool>? IsLatchedProbe { get; set; }

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_calls)
                {
                    return _calls.ToArray();
                }
            }
        }

        public void ClearCalls()
        {
            lock (_calls)
            {
                _calls.Clear();
            }
        }

        /// <summary>
        /// 让下一条命令在 Driver 内真正阻塞，并返回释放闸门。
        /// 返回 TCS 而非仅设置字段，避免出现"看起来在途、实际未阻塞"的空前提。
        /// </summary>
        public TaskCompletionSource BlockNextCommand()
        {
            _block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _blockObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _block;
        }

        /// <summary>等待命令确实进入 Driver 并阻塞，证明"命令在途"前提成立。</summary>
        public Task WaitUntilBlockedAsync() =>
            _blockObserved?.Task ?? Task.CompletedTask;

        public async Task BlockNextCommandAsync()
        {
            BlockNextCommand();
            await Task.CompletedTask;
        }

        public void ReleaseBlockedCommand() => _block?.TrySetResult();

        public ValueTask OpenAsync(IEndpointLease lease, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async ValueTask<DeviceCommandResult> ExecuteAsync(
            DeviceCommandEnvelope command,
            CancellationToken cancellationToken)
        {
            lock (_calls)
            {
                _calls.Add($"execute:{command.OperationId}");
            }

            if (_block is { } block)
            {
                _blockObserved?.TrySetResult();
                await block.Task.WaitAsync(cancellationToken);
            }

            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Executed);
        }

        public async ValueTask<SafeStopResult> SafeStopAsync(
            SafeStopContext context,
            CancellationToken cancellationToken)
        {
            LatchedBeforeSafeStop = IsLatchedProbe?.Invoke() ?? false;
            lock (_calls)
            {
                _calls.Add("safestop");
            }

            if (HangSafeStop)
            {
                // 永久挂起：模拟设备无响应且不理会取消。
                await new TaskCompletionSource().Task;
            }

            return SafeStopStatusOverride == SafeStopStatus.ConfirmedStopped
                ? new SafeStopResult(SafeStopStatus.ConfirmedStopped)
                : new SafeStopResult(SafeStopStatusOverride, "driver.safe_stop_failed", "合成停止失败");
        }

        public async IAsyncEnumerable<DeviceTelemetryEnvelope> ReadTelemetryAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class SafetyKernelHarness : IDisposable
    {
        private readonly DeviceInstanceId _instanceId;
        private readonly long _sessionEpoch;

        private SafetyKernelHarness(
            SafetySupervisor safety,
            CommandDispatcher dispatcher,
            DeviceInstanceId instanceId,
            long sessionEpoch)
        {
            Safety = safety;
            Dispatcher = dispatcher;
            _instanceId = instanceId;
            _sessionEpoch = sessionEpoch;
        }

        public SafetySupervisor Safety { get; }

        public CommandDispatcher Dispatcher { get; }

        public static SafetyKernelHarness Create(
            RecordingDriver driver,
            DeviceInstanceId instanceId,
            long sessionEpoch = 0)
        {
            var capability = BuildCapability();
            var safety = new SafetySupervisor(TimeProvider.System);
            var registry = new StaticRuntimeRegistry(instanceId, driver, capability, sessionEpoch);
            var dispatcher = new CommandDispatcher(safety, registry, TimeProvider.System);
            driver.IsLatchedProbe = () => safety.Current.IsLatched;
            safety.AttachRuntime(registry);
            return new SafetyKernelHarness(safety, dispatcher, instanceId, sessionEpoch);
        }

        public DeviceCommandEnvelope CreateCommand(
            int level,
            CommandDelivery delivery = CommandDelivery.Idempotent,
            DateTimeOffset? expiresAt = null) =>
            Build(
                _instanceId,
                new CapabilityId("intensity.output"),
                "set.level",
                level,
                delivery,
                expiresAt,
                Safety.Current.SafetyEpoch,
                _sessionEpoch);

        public DeviceCommandEnvelope CreateCommandForInstance(DeviceInstanceId instanceId, int level) =>
            Build(instanceId, new CapabilityId("intensity.output"), "set.level", level,
                CommandDelivery.Idempotent, null, Safety.Current.SafetyEpoch, _sessionEpoch);

        public DeviceCommandEnvelope CreateCommandWithCapability(CapabilityId capabilityId, int level) =>
            Build(_instanceId, capabilityId, "set.level", level,
                CommandDelivery.Idempotent, null, Safety.Current.SafetyEpoch, _sessionEpoch);

        public DeviceCommandEnvelope CreateCommandWithSessionEpoch(long sessionEpoch, int level) =>
            Build(_instanceId, new CapabilityId("intensity.output"), "set.level", level,
                CommandDelivery.Idempotent, null, Safety.Current.SafetyEpoch, sessionEpoch);

        public DeviceCommandEnvelope CreateCommandWithSafetyEpoch(long safetyEpoch, int level) =>
            Build(_instanceId, new CapabilityId("intensity.output"), "set.level", level,
                CommandDelivery.Idempotent, null, safetyEpoch, _sessionEpoch);

        public DeviceCommandEnvelope CreateCommandWithOperation(string operationId) =>
            Build(_instanceId, new CapabilityId("intensity.output"), operationId, 10,
                CommandDelivery.Idempotent, null, Safety.Current.SafetyEpoch, _sessionEpoch);

        private DeviceCommandEnvelope Build(
            DeviceInstanceId instanceId,
            CapabilityId capabilityId,
            string operationId,
            int level,
            CommandDelivery delivery,
            DateTimeOffset? expiresAt,
            long safetyEpoch,
            long sessionEpoch)
        {
            using var document = JsonDocument.Parse($"{{\"level\":{level}}}");
            return new DeviceCommandEnvelope(
                CommandId.New(),
                instanceId,
                capabilityId,
                operationId,
                document.RootElement.Clone(),
                new CommandSource(CommandSourceKind.UserInterface, "test"),
                delivery,
                DateTimeOffset.UnixEpoch,
                expiresAt,
                safetyEpoch,
                sessionEpoch);
        }

        /// <summary>
        /// 安全范围带非零下限（10,50）的能力，用于验证"低于下限"与"高于上限"
        /// 必须给出不同且不误导的 reason code。
        /// </summary>
        internal static CapabilityDescriptor BuildCapabilityWithSafetyFloor()
        {
            using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
            using var safeStopPayload = JsonDocument.Parse("{\"level\":0}");
            return new CapabilityDescriptor(
                new CapabilityId("intensity.output"),
                "强度输出",
                CapabilityStability.Stable,
                [
                    new CapabilityActuatorDescriptor(
                        "main",
                        "主输出",
                        "level",
                        new NumericRange(0, 100),
                        new NumericRange(10, 50),
                        [new CapabilityChannelDescriptor("a", "A 通道")])
                ],
                new VersionedJsonSchema("1.0", schema.RootElement.Clone()),
                new VersionedJsonSchema("1.0", schema.RootElement.Clone()),
                new CapabilityCommandPolicy(
                    CommandDelivery.Idempotent,
                    CommandAcknowledgement.DeviceAcknowledgement,
                    null,
                    TimeSpan.FromSeconds(2),
                    true),
                new SafeStopPlanDescriptor("set.level", safeStopPayload.RootElement.Clone(), ["main"]),
                new VersionedJsonSchema("1.0", schema.RootElement.Clone()));
        }

        internal static CapabilityDescriptor BuildCapability()
        {
            using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
            using var safeStopPayload = JsonDocument.Parse("{\"level\":0}");
            return new CapabilityDescriptor(
                new CapabilityId("intensity.output"),
                "强度输出",
                CapabilityStability.Stable,
                [
                    new CapabilityActuatorDescriptor(
                        "main",
                        "主输出",
                        "level",
                        new NumericRange(0, 100),
                        new NumericRange(0, 50),
                        [new CapabilityChannelDescriptor("a", "A 通道")])
                ],
                new VersionedJsonSchema("1.0", schema.RootElement.Clone()),
                new VersionedJsonSchema("1.0", schema.RootElement.Clone()),
                new CapabilityCommandPolicy(
                    CommandDelivery.Idempotent,
                    CommandAcknowledgement.DeviceAcknowledgement,
                    null,
                    TimeSpan.FromSeconds(2),
                    true),
                new SafeStopPlanDescriptor("set.level", safeStopPayload.RootElement.Clone(), ["main"]),
                new VersionedJsonSchema("1.0", schema.RootElement.Clone()));
        }

        public void Dispose() => Dispatcher.Dispose();
    }
}
