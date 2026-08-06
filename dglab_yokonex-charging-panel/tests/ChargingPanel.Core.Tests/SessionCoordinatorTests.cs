using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Core.Devices.Sessions;
using ChargingPanel.Core.Devices.Transport;
using Xunit;

namespace ChargingPanel.Core.Tests;

/// <summary>
/// Session 状态机测试：合法路径、失败进入 Recovering/Failed、
/// 用户断开不自动恢复、故障转移不恢复旧物理输出。
/// </summary>
public sealed class SessionCoordinatorTests
{
    [Fact]
    public async Task ConnectAsync_HappyPath_WalksExpectedStates()
    {
        var broker = new FakeTransportBroker();
        var harness = SessionHarness.Create(broker);

        var result = await harness.Coordinator.ConnectAsync(
            harness.InstanceId,
            harness.Candidates,
            CancellationToken.None);

        Assert.True(result.IsConnected);
        Assert.Equal(
            [
                SessionState.AcquiringLease,
                SessionState.Connecting,
                SessionState.Handshaking,
                SessionState.Ready
            ],
            harness.Coordinator.GetStateHistory(harness.InstanceId));
    }

    [Fact]
    public async Task ConnectAsync_LeaseFailure_EndsInFailedNotReady()
    {
        var broker = new FakeTransportBroker { FailAcquire = true };
        var harness = SessionHarness.Create(broker);

        var result = await harness.Coordinator.ConnectAsync(
            harness.InstanceId,
            harness.Candidates,
            CancellationToken.None);

        Assert.False(result.IsConnected);
        Assert.Equal(SessionState.Failed, harness.Coordinator.GetState(harness.InstanceId));
        Assert.DoesNotContain(SessionState.Ready, harness.Coordinator.GetStateHistory(harness.InstanceId));
    }

    [Fact]
    public async Task ConnectAsync_HandshakeFailure_ReleasesLeaseAndDoesNotConsumeEpoch()
    {
        // 握手失败发生在 Ready 之前的最后一步：租约已持有但会话未建立。
        // 此时必须释放租约（否则该 Endpoint 被永久占用），且不得递增 SessionEpoch。
        var broker = new FakeTransportBroker { FailHandshake = true };
        var harness = SessionHarness.Create(broker);

        var result = await harness.Coordinator.ConnectAsync(
            harness.InstanceId,
            harness.Candidates,
            CancellationToken.None);

        Assert.False(result.IsConnected);
        Assert.Equal(SessionState.Failed, harness.Coordinator.GetState(harness.InstanceId));
        Assert.Contains(SessionState.Handshaking, harness.Coordinator.GetStateHistory(harness.InstanceId));
        Assert.DoesNotContain(SessionState.Ready, harness.Coordinator.GetStateHistory(harness.InstanceId));
        // 已获取的租约必须归还，数量与获取次数一致。
        Assert.Equal(broker.AcquireAttempts, broker.ReleaseCount);
        Assert.Equal(0, harness.Coordinator.GetSession(harness.InstanceId)!.SessionEpoch);
    }

    [Fact]
    public async Task ConnectAsync_EachNewLease_IncrementsSessionEpoch()
    {
        var broker = new FakeTransportBroker();
        var harness = SessionHarness.Create(broker);

        var first = await harness.Coordinator.ConnectAsync(
            harness.InstanceId, harness.Candidates, CancellationToken.None);
        await harness.Coordinator.DisconnectAsync(harness.InstanceId, userInitiated: true, CancellationToken.None);
        var second = await harness.Coordinator.ConnectAsync(
            harness.InstanceId, harness.Candidates, CancellationToken.None);

        Assert.True(second.SessionEpoch > first.SessionEpoch);
    }

    [Fact]
    public async Task DisconnectAsync_UserInitiated_DoesNotAutoRecover()
    {
        var broker = new FakeTransportBroker();
        var harness = SessionHarness.Create(broker);
        await harness.Coordinator.ConnectAsync(harness.InstanceId, harness.Candidates, CancellationToken.None);

        await harness.Coordinator.DisconnectAsync(harness.InstanceId, userInitiated: true, CancellationToken.None);

        // 用户主动断开是明确意图，绝不能被自动恢复覆盖。
        Assert.Equal(SessionState.Disconnected, harness.Coordinator.GetState(harness.InstanceId));
        Assert.False(harness.Coordinator.IsRecoveryScheduled(harness.InstanceId));
    }

    [Fact]
    public async Task ReportTransportFailure_AfterUserDisconnect_DoesNotScheduleRecovery()
    {
        var broker = new FakeTransportBroker();
        var harness = SessionHarness.Create(broker);
        await harness.Coordinator.ConnectAsync(harness.InstanceId, harness.Candidates, CancellationToken.None);
        await harness.Coordinator.DisconnectAsync(harness.InstanceId, userInitiated: true, CancellationToken.None);

        harness.Coordinator.ReportTransportFailure(harness.InstanceId, "link.lost");

        Assert.False(harness.Coordinator.IsRecoveryScheduled(harness.InstanceId));
    }

    [Fact]
    public async Task ConnectAsync_WhenSessionAlreadyHoldsLease_ReleasesPreviousLease()
    {
        // 恢复路径会在已持有租约的 slot 上重新取得租约。
        // 若旧租约未被释放，BLE 的 WinRT/GATT 句柄会永久泄漏，
        // 同一 Endpoint 被占死；反复断链会累积泄漏。
        var broker = new FakeTransportBroker();
        var harness = SessionHarness.Create(broker);
        await harness.Coordinator.ConnectAsync(harness.InstanceId, harness.Candidates, CancellationToken.None);

        // 第二次连接：模拟恢复循环在未先断开的情况下重新取得租约。
        await harness.Coordinator.ConnectAsync(harness.InstanceId, harness.Candidates, CancellationToken.None);

        Assert.Equal(2, broker.AcquireAttempts);
        // 两次获取、当前仍持有一份，因此必须恰好归还一份（即旧租约）。
        Assert.Equal(1, broker.ReleaseCount);
    }

    [Fact]
    public async Task ConnectAsync_WhenCalledConcurrently_DoesNotLeakLease()
    {
        // 两个调用方（例如用户点击连接与恢复循环）同时对同一实例连接时，
        // 只有一份租约能留在 slot 上；其余必须归还，不得泄漏 Endpoint 句柄。
        var broker = new FakeTransportBroker();
        var harness = SessionHarness.Create(broker);

        var racers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            harness.Coordinator.ConnectAsync(
                harness.InstanceId, harness.Candidates, CancellationToken.None)));
        var results = await Task.WhenAll(racers);

        // 获取 N 次、当前持有 1 份，因此必须归还 N-1 份。
        Assert.Equal(broker.AcquireAttempts - 1, broker.ReleaseCount);
        Assert.Equal(SessionState.Ready, harness.Coordinator.GetState(harness.InstanceId));

        // slot 上的 Epoch 必须单调，且等于所有成功连接中的最大 Epoch。
        // 否则较早的连接可能在较晚的连接之后写入，使 slot 留下更小的 Epoch：
        // DeviceStateStore 依赖 Epoch 过滤遥测，Epoch 与当前租约不一致时
        // 新连接的遥测会被当作陈旧丢弃，或旧连接的遥测被当作设备现状接受。
        var epochs = results.Where(r => r.IsConnected).Select(r => r.SessionEpoch).ToArray();
        var session = harness.Coordinator.GetSession(harness.InstanceId)!;
        Assert.Equal(epochs.Max(), session.SessionEpoch);
    }

    [Fact]
    public async Task ReportTransportFailure_WhileReady_SchedulesRecoveryWithoutRestoringOutput()
    {
        var broker = new FakeTransportBroker();
        var harness = SessionHarness.Create(broker);
        await harness.Coordinator.ConnectAsync(harness.InstanceId, harness.Candidates, CancellationToken.None);

        harness.Coordinator.ReportTransportFailure(harness.InstanceId, "link.lost");

        Assert.True(harness.Coordinator.IsRecoveryScheduled(harness.InstanceId));
        // 恢复只做握手与安全配置，绝不重放上次的强度/波形/泵启动。
        Assert.Empty(broker.ReplayedOutputCommands);
    }

    [Fact]
    public async Task ReportTransportFailure_WithBackoff_ActuallyReconnectsAndLeavesRecovering()
    {
        // 前两次重连失败，第三次成功：验证真的有重试循环，而不是只置一个标志。
        var broker = new FakeTransportBroker { FailAcquireTimes = 2 };
        var harness = SessionHarness.Create(broker);
        await harness.Coordinator.ConnectAsync(harness.InstanceId, harness.Candidates, CancellationToken.None);
        var firstEpoch = harness.Coordinator.GetSession(harness.InstanceId)!.SessionEpoch;

        harness.Coordinator.ReportTransportFailure(harness.InstanceId, "link.lost");
        await harness.Coordinator.WaitForRecoveryAsync(harness.InstanceId);

        // Recovering 不能是死胡同状态。
        Assert.Equal(SessionState.Ready, harness.Coordinator.GetState(harness.InstanceId));
        Assert.True(harness.Coordinator.GetSession(harness.InstanceId)!.SessionEpoch > firstEpoch);
        Assert.True(broker.AcquireAttempts >= 3);
    }

    [Fact]
    public async Task ReportTransportFailure_ThenUserDisconnect_CancelsBackoffWithoutReconnecting()
    {
        var broker = new FakeTransportBroker { FailAcquireTimes = int.MaxValue };
        var harness = SessionHarness.Create(broker);
        await harness.Coordinator.ConnectAsync(harness.InstanceId, harness.Candidates, CancellationToken.None);
        var attemptsBefore = broker.AcquireAttempts;

        harness.Coordinator.ReportTransportFailure(harness.InstanceId, "link.lost");
        await harness.Coordinator.DisconnectAsync(harness.InstanceId, userInitiated: true, CancellationToken.None);
        await harness.Coordinator.WaitForRecoveryAsync(harness.InstanceId);

        // 用户断开必须取消退避；不得在断开后继续重连。
        Assert.Equal(SessionState.Disconnected, harness.Coordinator.GetState(harness.InstanceId));
        Assert.False(harness.Coordinator.IsRecoveryScheduled(harness.InstanceId));
        var attemptsAfter = broker.AcquireAttempts;
        await Task.Delay(150);
        Assert.Equal(attemptsAfter, broker.AcquireAttempts);
    }

    [Fact]
    public async Task RecoveryLoop_WhenItCompletesOnItsOwn_DoesNotRetainCancellationSource()
    {
        // 恢复循环自行结束（成功、耗尽、被取消）时必须释放取消源。
        // 只有 DisconnectAsync 释放的话，反复断链会持续累积 CancellationTokenSource；
        // BLE 环境下断链是常态，泄漏会随运行时长增长。
        var broker = new FakeTransportBroker { FailAcquireTimes = 1 };
        var harness = SessionHarness.Create(broker);
        await harness.Coordinator.ConnectAsync(harness.InstanceId, harness.Candidates, CancellationToken.None);

        harness.Coordinator.ReportTransportFailure(harness.InstanceId, "link.lost");
        await harness.Coordinator.WaitForRecoveryAsync(harness.InstanceId);

        Assert.Equal(SessionState.Ready, harness.Coordinator.GetState(harness.InstanceId));
        Assert.False(harness.Coordinator.HasRetainedRecoveryCts(harness.InstanceId));
    }

    [Fact]
    public async Task RecoveryLoop_WhenRetriesExhausted_DoesNotRetainCancellationSource()
    {
        var broker = new FakeTransportBroker { FailAcquireTimes = int.MaxValue };
        var harness = SessionHarness.Create(broker, maxRecoveryAttempts: 2);
        await harness.Coordinator.ConnectAsync(harness.InstanceId, harness.Candidates, CancellationToken.None);

        harness.Coordinator.ReportTransportFailure(harness.InstanceId, "link.lost");
        await harness.Coordinator.WaitForRecoveryAsync(harness.InstanceId);

        Assert.Equal(SessionState.Failed, harness.Coordinator.GetState(harness.InstanceId));
        Assert.False(harness.Coordinator.HasRetainedRecoveryCts(harness.InstanceId));
    }

    [Fact]
    public async Task RecoveryScope_AcrossConsecutiveRounds_EachRoundReleasesItsOwnCancellationSource()
    {
        // 上一轮恢复的收尾不得清掉后续新一轮的 RecoveryScheduled。
        // 否则第三次故障报告会看到 false 而启动并发的第三个恢复循环，
        // 覆盖 slot.RecoveryCts 并使第二轮的取消源变成无主对象（泄漏）。
        // 第一轮只重试一次即耗尽，落 Failed；随后第二轮必须能独立开始并独立收尾。
        var broker = new FakeTransportBroker { FailAcquireTimes = int.MaxValue };
        var harness = SessionHarness.Create(broker, maxRecoveryAttempts: 1);
        await harness.Coordinator.ConnectAsync(harness.InstanceId, harness.Candidates, CancellationToken.None);

        harness.Coordinator.ReportTransportFailure(harness.InstanceId, "link.lost");
        await harness.Coordinator.WaitForRecoveryAsync(harness.InstanceId);
        Assert.Equal(SessionState.Failed, harness.Coordinator.GetState(harness.InstanceId));
        // 第一轮已收尾：取消源必须已释放。
        Assert.False(harness.Coordinator.HasRetainedRecoveryCts(harness.InstanceId));

        // 第二轮：Failed 之后仍应允许恢复，且必须持有自己的取消源。
        harness.Coordinator.ReportTransportFailure(harness.InstanceId, "link.lost");

        Assert.True(harness.Coordinator.IsRecoveryScheduled(harness.InstanceId));
        Assert.True(harness.Coordinator.HasRetainedRecoveryCts(harness.InstanceId));
        await harness.Coordinator.WaitForRecoveryAsync(harness.InstanceId);
        // 第二轮同样必须完整收尾，不留取消源。
        Assert.False(harness.Coordinator.HasRetainedRecoveryCts(harness.InstanceId));
    }

    [Fact]
    public async Task ReportTransportFailure_WhenAllRetriesFail_EndsInFailedNotRecovering()
    {
        var broker = new FakeTransportBroker { FailAcquireTimes = int.MaxValue };
        var harness = SessionHarness.Create(broker, maxRecoveryAttempts: 2);
        await harness.Coordinator.ConnectAsync(harness.InstanceId, harness.Candidates, CancellationToken.None);

        harness.Coordinator.ReportTransportFailure(harness.InstanceId, "link.lost");
        await harness.Coordinator.WaitForRecoveryAsync(harness.InstanceId);

        // 重试耗尽后必须落到 Failed，不能永远停在 Recovering。
        Assert.Equal(SessionState.Failed, harness.Coordinator.GetState(harness.InstanceId));
        Assert.False(harness.Coordinator.IsRecoveryScheduled(harness.InstanceId));
    }

    [Fact]
    public async Task GetSession_WhenReady_ExposesImmutableSnapshotWithEpoch()
    {
        var broker = new FakeTransportBroker();
        var harness = SessionHarness.Create(broker);
        var connect = await harness.Coordinator.ConnectAsync(
            harness.InstanceId, harness.Candidates, CancellationToken.None);

        var session = harness.Coordinator.GetSession(harness.InstanceId);

        Assert.NotNull(session);
        Assert.Equal(connect.SessionEpoch, session!.SessionEpoch);
        Assert.Equal(SessionState.Ready, session.State);
        Assert.True(session.CanAcceptCommands);
        Assert.Equal(harness.Candidates[0].EndpointId, session.EndpointId);
    }

    [Fact]
    public async Task GetSession_AfterUserDisconnect_RejectsCommands()
    {
        var broker = new FakeTransportBroker();
        var harness = SessionHarness.Create(broker);
        await harness.Coordinator.ConnectAsync(harness.InstanceId, harness.Candidates, CancellationToken.None);
        await harness.Coordinator.DisconnectAsync(harness.InstanceId, userInitiated: true, CancellationToken.None);

        var session = harness.Coordinator.GetSession(harness.InstanceId);

        // 只有 Ready 状态可接受命令；断开后必须明确拒绝。
        Assert.NotNull(session);
        Assert.False(session!.CanAcceptCommands);
    }

    [Fact]
    public async Task ConnectAsync_NoUsableCandidate_ReportsPluginUnavailable()
    {
        var broker = new FakeTransportBroker();
        var harness = SessionHarness.Create(broker);

        var result = await harness.Coordinator.ConnectAsync(
            harness.InstanceId,
            [],
            CancellationToken.None);

        Assert.False(result.IsConnected);
        Assert.Equal(SessionState.PluginUnavailable, harness.Coordinator.GetState(harness.InstanceId));
    }

    internal sealed class SessionHarness
    {
        private SessionHarness(SessionCoordinator coordinator, DeviceInstanceId instanceId, IReadOnlyList<EndpointCandidate> candidates)
        {
            Coordinator = coordinator;
            InstanceId = instanceId;
            Candidates = candidates;
        }

        public SessionCoordinator Coordinator { get; }

        public DeviceInstanceId InstanceId { get; }

        public IReadOnlyList<EndpointCandidate> Candidates { get; }

        public static SessionHarness Create(FakeTransportBroker broker, int maxRecoveryAttempts = 5)
        {
            var selector = new EndpointSelector(TimeProvider.System);
            var coordinator = new SessionCoordinator(
                broker,
                selector,
                TimeProvider.System,
                new SessionRecoveryPolicy(
                    maxRecoveryAttempts,
                    TimeSpan.FromMilliseconds(5),
                    TimeSpan.FromMilliseconds(20)));
            var candidates = new[]
            {
                new EndpointCandidate(
                    EndpointId.New(),
                    new ProtocolProfileId("dglab.coyote.v3.ble"),
                    TransportKind.BluetoothLowEnergy,
                    0,
                    true)
            };
            return new SessionHarness(coordinator, DeviceInstanceId.New(), candidates);
        }
    }

    internal sealed class FakeTransportBroker : ITransportBroker
    {
        private readonly List<string> _replayed = [];

        private int _acquireAttempts;

        private int _releaseCount;

        public bool FailAcquire { get; init; }

        public bool FailHandshake { get; init; }

        /// <summary>前 N 次获取租约失败，之后成功；用于验证真实重试循环。</summary>
        public int FailAcquireTimes { get; init; }

        public int AcquireAttempts => Volatile.Read(ref _acquireAttempts);

        /// <summary>租约归还次数；用于验证失败路径不泄漏 Endpoint 占用。</summary>
        public int ReleaseCount => Volatile.Read(ref _releaseCount);

        public IReadOnlyList<string> ReplayedOutputCommands => _replayed.ToArray();

        public ValueTask<TransportLeaseHandle> AcquireAsync(
            EndpointCandidate candidate,
            CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _acquireAttempts);
            if (FailAcquire || attempt <= FailAcquireTimes)
            {
                throw new IOException("合成租约获取失败");
            }

            return ValueTask.FromResult(new TransportLeaseHandle(candidate.EndpointId, candidate.ProfileId));
        }

        public ValueTask<bool> HandshakeAsync(
            TransportLeaseHandle lease,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(!FailHandshake);

        public ValueTask ReleaseAsync(TransportLeaseHandle lease, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _releaseCount);
            return ValueTask.CompletedTask;
        }
    }
}
