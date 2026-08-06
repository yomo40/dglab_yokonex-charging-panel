using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Telemetry;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Core.Application.Devices;
using ChargingPanel.Core.Devices.Runtime;
using ChargingPanel.Core.Devices.Safety;
using ChargingPanel.Core.Network.RemoteControl;
using Xunit;

namespace ChargingPanel.Core.Tests;

/// <summary>
/// Task 21 安全攻击场景测试：未认证、错误权限、重放、过期、乱序、
/// 过大载荷、速率超限、断线重用、急停、结果未知与最终 ACK 欺骗。
/// 全部经由 <see cref="RemoteCommandAuthenticator"/> 统一入口验证。
/// </summary>
public sealed class RemoteControlSecurityTests
{
    // ---------- 未认证 ----------

    [Fact]
    public async Task DispatchAsync_UnknownSession_Rejected()
    {
        using var harness = CreateHarness(ExecuteOk);
        var request = BuildRequest(
            session: null,
            timestamp: harness.Authenticator.TimeProvider.GetUtcNow(),
            nonce: 1,
            deviceInstanceId: harness.InstanceId,
            sessionId: "ses_does_not_exist");

        var result = await harness.Authenticator.DispatchAsync(request);

        Assert.Equal(RemoteCommandFinalStatus.Rejected, result.Status);
        Assert.Equal("auth.session_not_found", result.ReasonCode);
        Assert.Empty(harness.Driver.Calls);
    }

    [Fact]
    public async Task DispatchAsync_WrongAuthTag_Rejected()
    {
        var harness = CreateHarness(ExecuteOk);
        var session = harness.Authenticator.CreateSession("principal", RemotePermissionScope.All);
        var request = BuildRequest(
            session,
            harness.Authenticator.TimeProvider.GetUtcNow(),
            nonce: 1,
            harness.InstanceId,
            authTagOverride: new string('0', 64));

        var result = await harness.Authenticator.DispatchAsync(request);

        Assert.Equal(RemoteCommandFinalStatus.Rejected, result.Status);
        Assert.Equal("auth.tag_mismatch", result.ReasonCode);
        Assert.Empty(harness.Driver.Calls);
    }

    // ---------- 错误权限 ----------

    [Fact]
    public async Task DispatchAsync_WrongPermissionScope_Rejected()
    {
        var harness = CreateHarness(ExecuteOk);
        var scope = new RemotePermissionScope(capabilities: new[] { "ems.channel.output" });
        var session = harness.Authenticator.CreateSession("principal", scope);

        var request = BuildRequest(session, harness.Authenticator.TimeProvider.GetUtcNow(), nonce: 1, harness.InstanceId);
        var result = await harness.Authenticator.DispatchAsync(request);

        Assert.Equal(RemoteCommandFinalStatus.Rejected, result.Status);
        Assert.Equal("permission.denied", result.ReasonCode);
        Assert.Empty(harness.Driver.Calls);
    }

    // ---------- 重放 CommandId ----------

    [Fact]
    public async Task DispatchAsync_ReplayedCommandId_Rejected()
    {
        var harness = CreateHarness(ExecuteOk);
        var session = harness.Authenticator.CreateSession("principal", RemotePermissionScope.All);
        var request = BuildRequest(session, harness.Authenticator.TimeProvider.GetUtcNow(), nonce: 1, harness.InstanceId);

        var first = await harness.Authenticator.DispatchAsync(request);
        var second = await harness.Authenticator.DispatchAsync(request);

        Assert.Equal(RemoteCommandFinalStatus.Executed, first.Status);
        Assert.Equal(RemoteCommandFinalStatus.Rejected, second.Status);
        Assert.Equal("replay.command_id", second.ReasonCode);
    }

    // ---------- 过期时间戳 ----------

    [Fact]
    public async Task DispatchAsync_ExpiredTimestamp_Rejected()
    {
        var harness = CreateHarness(ExecuteOk);
        var session = harness.Authenticator.CreateSession("principal", RemotePermissionScope.All);
        var oldTimestamp = harness.Authenticator.TimeProvider.GetUtcNow() - TimeSpan.FromMinutes(5);

        var request = BuildRequest(session, oldTimestamp, nonce: 1, harness.InstanceId);
        var result = await harness.Authenticator.DispatchAsync(request);

        Assert.Equal(RemoteCommandFinalStatus.Rejected, result.Status);
        Assert.Equal("timestamp.expired", result.ReasonCode);
        Assert.Empty(harness.Driver.Calls);
    }

    // ---------- 乱序 nonce ----------

    [Fact]
    public async Task DispatchAsync_OutOfOrderNonce_Rejected()
    {
        var harness = CreateHarness(ExecuteOk);
        var session = harness.Authenticator.CreateSession("principal", RemotePermissionScope.All);
        var now = harness.Authenticator.TimeProvider.GetUtcNow();

        var first = await harness.Authenticator.DispatchAsync(
            BuildRequest(session, now, nonce: 2, harness.InstanceId));
        var second = await harness.Authenticator.DispatchAsync(
            BuildRequest(session, now, nonce: 1, harness.InstanceId));

        Assert.Equal(RemoteCommandFinalStatus.Executed, first.Status);
        Assert.Equal(RemoteCommandFinalStatus.Rejected, second.Status);
        Assert.Equal("replay.nonce_out_of_order", second.ReasonCode);
    }

    // ---------- 过大 payload ----------

    [Fact]
    public async Task DispatchAsync_PayloadTooLarge_Rejected()
    {
        var harness = CreateHarness(ExecuteOk, maxPayloadBytes: 8);
        var session = harness.Authenticator.CreateSession("principal", RemotePermissionScope.All);
        using var doc = JsonDocument.Parse("{\"actuatorId\":\"main\",\"level\":42,\"extra\":\"data\"}");

        var request = BuildRequest(
            session,
            harness.Authenticator.TimeProvider.GetUtcNow(),
            nonce: 1,
            harness.InstanceId,
            payload: doc.RootElement.Clone());
        var result = await harness.Authenticator.DispatchAsync(request);

        Assert.Equal(RemoteCommandFinalStatus.Rejected, result.Status);
        Assert.Equal("payload.too_large", result.ReasonCode);
        Assert.Empty(harness.Driver.Calls);
    }

    // ---------- 速率超限 ----------

    [Fact]
    public async Task DispatchAsync_RateLimitExceeded_Rejected()
    {
        var harness = CreateHarness(ExecuteOk, maxCommandsPerWindow: 3);
        var session = harness.Authenticator.CreateSession("principal", RemotePermissionScope.All);
        var now = harness.Authenticator.TimeProvider.GetUtcNow();

        for (var i = 1; i <= 3; i++)
        {
            var ok = await harness.Authenticator.DispatchAsync(
                BuildRequest(session, now, nonce: i, harness.InstanceId));
            Assert.Equal(RemoteCommandFinalStatus.Executed, ok.Status);
        }

        var fourth = await harness.Authenticator.DispatchAsync(
            BuildRequest(session, now, nonce: 4, harness.InstanceId));

        Assert.Equal(RemoteCommandFinalStatus.Rejected, fourth.Status);
        Assert.Equal("rate_limited", fourth.ReasonCode);
    }

    // ---------- 断线后旧 Session 重用 ----------

    [Fact]
    public async Task DispatchAsync_DisconnectedSession_Rejected()
    {
        var harness = CreateHarness(ExecuteOk);
        var session = harness.Authenticator.CreateSession("principal", RemotePermissionScope.All);
        harness.Authenticator.DisconnectSession(session.SessionId);

        var request = BuildRequest(session, harness.Authenticator.TimeProvider.GetUtcNow(), nonce: 1, harness.InstanceId);
        var result = await harness.Authenticator.DispatchAsync(request);

        Assert.Equal(RemoteCommandFinalStatus.Rejected, result.Status);
        Assert.Equal("session.disconnected", result.ReasonCode);
        Assert.Empty(harness.Driver.Calls);
    }

    // ---------- 会话过期 ----------

    [Fact]
    public async Task DispatchAsync_ExpiredSession_Rejected()
    {
        var time = new MutableTimeProvider();
        var harness = CreateHarness(ExecuteOk, timeProvider: time);
        var session = harness.Authenticator.CreateSession("principal", RemotePermissionScope.All, TimeSpan.FromMinutes(1));
        time.Advance(TimeSpan.FromMinutes(2));

        var request = BuildRequest(session, time.GetUtcNow(), nonce: 1, harness.InstanceId);
        var result = await harness.Authenticator.DispatchAsync(request);

        Assert.Equal(RemoteCommandFinalStatus.Rejected, result.Status);
        Assert.Equal("session.expired", result.ReasonCode);
        Assert.Empty(harness.Driver.Calls);
    }

    // ---------- 急停后控制 ----------

    [Fact]
    public async Task DispatchAsync_AfterEmergencyStop_RejectedWithLatched()
    {
        var harness = CreateHarness(ExecuteOk);
        await harness.Safety.EmergencyStopAllAsync("测试急停", CancellationToken.None);

        var session = harness.Authenticator.CreateSession("principal", RemotePermissionScope.All);
        var request = BuildRequest(session, harness.Authenticator.TimeProvider.GetUtcNow(), nonce: 1, harness.InstanceId);
        var result = await harness.Authenticator.DispatchAsync(request);

        Assert.Equal(RemoteCommandFinalStatus.Rejected, result.Status);
        Assert.Equal(SafetyRejection.LatchedReasonCode, result.ReasonCode);
        Assert.Empty(harness.Driver.Calls);
    }

    // ---------- 结果未知：传输成功不是执行成功 ----------

    [Fact]
    public async Task DispatchAsync_DriverTransportAck_MapsToOutcomeUnknown()
    {
        var harness = CreateHarness((command, ct) => ValueTask.FromResult(
            new DeviceCommandResult(command.CommandId, DeviceCommandStatus.AcceptedByTransport)));
        var session = harness.Authenticator.CreateSession("principal", RemotePermissionScope.All);

        var request = BuildRequest(session, harness.Authenticator.TimeProvider.GetUtcNow(), nonce: 1, harness.InstanceId);
        var result = await harness.Authenticator.DispatchAsync(request);

        Assert.Equal(RemoteCommandFinalStatus.OutcomeUnknown, result.Status);
        Assert.NotEqual(RemoteCommandFinalStatus.Executed, result.Status);
    }

    [Fact]
    public async Task DispatchAsync_DriverOutcomeUnknown_MapsToOutcomeUnknown()
    {
        var harness = CreateHarness((command, ct) => ValueTask.FromResult(
            new DeviceCommandResult(command.CommandId, DeviceCommandStatus.OutcomeUnknown, "driver.unknown", "执行结果未知")));
        var session = harness.Authenticator.CreateSession("principal", RemotePermissionScope.All);

        var request = BuildRequest(session, harness.Authenticator.TimeProvider.GetUtcNow(), nonce: 1, harness.InstanceId);
        var result = await harness.Authenticator.DispatchAsync(request);

        Assert.Equal(RemoteCommandFinalStatus.OutcomeUnknown, result.Status);
        Assert.NotEqual(RemoteCommandFinalStatus.Executed, result.Status);
    }

    // ---------- 最终 ACK 欺骗 ----------

    [Fact]
    public async Task DispatchAsync_ClientCannotForceExecutedAck_WhenDispatcherRejects()
    {
        // 客户端无论怎样伪造请求内容，最终状态只能来自 Dispatcher 的 Rejected。
        var harness = CreateHarness((command, ct) => ValueTask.FromResult(
            new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "driver.rejected", "驱动拒绝")));
        var session = harness.Authenticator.CreateSession("principal", RemotePermissionScope.All);

        var request = BuildRequest(session, harness.Authenticator.TimeProvider.GetUtcNow(), nonce: 1, harness.InstanceId);
        var result = await harness.Authenticator.DispatchAsync(request);

        Assert.Equal(RemoteCommandFinalStatus.Rejected, result.Status);
        Assert.Equal("driver.rejected", result.ReasonCode);
        Assert.False(result.IsExecuted);
    }

    // ---------- 正常执行（安全套件内的 happy path） ----------

    [Fact]
    public async Task DispatchAsync_ValidRequest_ExecutesOnDriver()
    {
        var harness = CreateHarness(ExecuteOk);
        var session = harness.Authenticator.CreateSession("principal", RemotePermissionScope.All);

        var request = BuildRequest(session, harness.Authenticator.TimeProvider.GetUtcNow(), nonce: 1, harness.InstanceId);
        var result = await harness.Authenticator.DispatchAsync(request);

        Assert.Equal(RemoteCommandFinalStatus.Executed, result.Status);
        Assert.True(result.IsExecuted);
        Assert.Single(harness.Driver.Calls);
    }

    // ---------- 工具 ----------

    private static ValueTask<DeviceCommandResult> ExecuteOk(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Executed));

    private static RemoteCommandRequest BuildRequest(
        RemoteControlSession? session,
        DateTimeOffset timestamp,
        long nonce,
        DeviceInstanceId deviceInstanceId,
        JsonElement? payload = null,
        CommandId? commandId = null,
        string? authTagOverride = null,
        string? sessionId = null)
    {
        var cmdId = commandId ?? CommandId.New();
        var payloadElement = payload ?? DefaultPayload();
        var payloadBytes = Encoding.UTF8.GetBytes(payloadElement.GetRawText());
        var actualSessionId = sessionId ?? session!.SessionId;
        var tag = authTagOverride
            ?? (session is null
                ? new string('a', 64)
                : session.ComputeAuthTag(cmdId, nonce, timestamp, deviceInstanceId, "intensity.output", "set.level", payloadBytes));

        return new RemoteCommandRequest(
            cmdId,
            actualSessionId,
            nonce,
            timestamp,
            deviceInstanceId,
            "intensity.output",
            "set.level",
            payloadElement,
            tag);
    }

    private static JsonElement DefaultPayload()
    {
        using var doc = JsonDocument.Parse("{\"actuatorId\":\"main\",\"level\":42}");
        return doc.RootElement.Clone();
    }

    private static TestHarness CreateHarness(
        Func<DeviceCommandEnvelope, CancellationToken, ValueTask<DeviceCommandResult>> execute,
        TimeProvider? timeProvider = null,
        int maxPayloadBytes = 16 * 1024,
        int maxCommandsPerWindow = 30)
    {
        var instanceId = DeviceInstanceId.New();
        var driver = new ConfigurableDriver(execute);
        var safety = new SafetySupervisor(timeProvider ?? TimeProvider.System);
        var registry = new DeviceRuntimeRegistry();
        safety.AttachRuntime(registry);
        var dispatcher = new CommandDispatcher(safety, registry, timeProvider ?? TimeProvider.System);
        var appService = new DeviceApplicationService(dispatcher, safety, timeProvider ?? TimeProvider.System);
        var capability = Capabilities.BuildDglabIntensity();
        registry.Register(new DeviceRuntimeBinding(instanceId, driver, [capability], 1));
        var authenticator = new RemoteCommandAuthenticator(
            appService,
            timeProvider,
            maxPayloadBytes: maxPayloadBytes,
            maxCommandsPerWindow: maxCommandsPerWindow);
        return new TestHarness(safety, appService, driver, instanceId, authenticator);
    }

    private sealed record TestHarness(
        SafetySupervisor Safety,
        DeviceApplicationService AppService,
        ConfigurableDriver Driver,
        DeviceInstanceId InstanceId,
        RemoteCommandAuthenticator Authenticator)
        : IDisposable
    {
        public void Dispose()
        {
            Authenticator.TerminateSessions();
        }
    }

    /// <summary>可配置执行结果的假 Driver。</summary>
    private sealed class ConfigurableDriver : IProtocolDriver
    {
        private readonly Func<DeviceCommandEnvelope, CancellationToken, ValueTask<DeviceCommandResult>> _execute;
        private readonly object _gate = new();
        private readonly List<string> _calls = new();

        public ConfigurableDriver(
            Func<DeviceCommandEnvelope, CancellationToken, ValueTask<DeviceCommandResult>> execute)
        {
            _execute = execute;
        }

        public ProtocolProfileId ProfileId { get; } = new("test.profile.v1");

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_gate)
                {
                    return _calls.ToArray();
                }
            }
        }

        public ValueTask OpenAsync(IEndpointLease lease, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<DeviceCommandResult> ExecuteAsync(
            DeviceCommandEnvelope command,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _calls.Add($"{command.OperationId}:{command.Payload.GetRawText()}");
            }

            return _execute(command, cancellationToken);
        }

        public ValueTask<SafeStopResult> SafeStopAsync(SafeStopContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SafeStopResult(SafeStopStatus.ConfirmedStopped));

        public async IAsyncEnumerable<DeviceTelemetryEnvelope> ReadTelemetryAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>可控时间的 TimeProvider。</summary>
    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public MutableTimeProvider(DateTimeOffset? now = null)
        {
            _now = now ?? DateTimeOffset.UtcNow;
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
