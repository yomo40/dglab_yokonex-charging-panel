using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Devices.Transport;

namespace ChargingPanel.Core.Devices.Sessions;

/// <summary>
/// Session 协调器：连接、重连、故障转移与生命周期的唯一所有者。
/// Broker 与 Driver 只报告失败，绝不自行重连。
///
/// 关键安全约束：故障转移后只执行握手与安全配置，
/// 绝不重放上一次连接的强度、波形、泵或马达输出——
/// 否则设备会在用户未预期的时刻恢复物理动作。
/// </summary>
public sealed class SessionCoordinator
{
    private readonly ITransportBroker _broker;
    private readonly EndpointSelector _selector;
    private readonly TimeProvider _timeProvider;
    private readonly SessionRecoveryPolicy _recoveryPolicy;
    private readonly Random _random = new();
    private readonly object _gate = new();
    private readonly Dictionary<DeviceInstanceId, SessionSlot> _sessions = [];
    private long _sessionEpochSequence;

    public SessionCoordinator(
        ITransportBroker broker,
        EndpointSelector selector,
        TimeProvider timeProvider,
        SessionRecoveryPolicy? recoveryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _broker = broker;
        _selector = selector;
        _timeProvider = timeProvider;
        _recoveryPolicy = recoveryPolicy ?? SessionRecoveryPolicy.Default;
    }

    /// <summary>
    /// 等待当前恢复循环结束。仅用于测试与关闭流程，
    /// 生产调用方通过 GetState/GetSession 观察结果。
    /// </summary>
    public async Task WaitForRecoveryAsync(DeviceInstanceId instanceId)
    {
        Task? recovery;
        lock (_gate)
        {
            recovery = _sessions.TryGetValue(instanceId, out var slot) ? slot.RecoveryTask : null;
        }

        if (recovery is not null)
        {
            try
            {
                await recovery;
            }
            catch
            {
                // 恢复循环内部已记录终态，此处不传播。
            }
        }
    }

    public SessionState GetState(DeviceInstanceId instanceId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(instanceId, out var slot)
                ? slot.State
                : SessionState.Disconnected;
        }
    }

    public IReadOnlyList<SessionState> GetStateHistory(DeviceInstanceId instanceId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(instanceId, out var slot)
                ? slot.History.ToArray()
                : [];
        }
    }

    /// <summary>
    /// 取当前会话的不可变快照。在同一把锁内读取状态与 Epoch，
    /// 避免调用方读到"状态与 Epoch 不自洽"的撕裂结果。
    /// </summary>
    public DeviceSession? GetSession(DeviceInstanceId instanceId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(instanceId, out var slot))
            {
                return null;
            }

            return new DeviceSession(
                instanceId,
                slot.Lease?.EndpointId,
                slot.Lease?.ProfileId,
                slot.State,
                slot.SessionEpoch);
        }
    }

    public bool IsRecoveryScheduled(DeviceInstanceId instanceId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(instanceId, out var slot) && slot.RecoveryScheduled;
        }
    }

    /// <summary>
    /// 恢复取消源是否仍被 slot 持有。仅供测试断言资源释放纪律；
    /// 生产调用方不应依赖该内部状态。
    /// </summary>
    internal bool HasRetainedRecoveryCts(DeviceInstanceId instanceId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(instanceId, out var slot) && slot.RecoveryCts is not null;
        }
    }

    public async Task<SessionConnectResult> ConnectAsync(
        DeviceInstanceId instanceId,
        IReadOnlyList<EndpointCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var ordered = _selector.SelectCandidates(candidates);
        if (ordered.Count == 0)
        {
            // 没有可用候选：不得回退成其他厂商或伪造连接成功。
            Transition(instanceId, SessionState.PluginUnavailable, resetHistory: true);
            return new SessionConnectResult(false, 0, "session.no_usable_endpoint");
        }

        Transition(instanceId, SessionState.AcquiringLease, resetHistory: true);

        lock (_gate)
        {
            // 记录候选以便恢复循环复用同一组 Endpoint。
            // 必须在 Transition 之后：GetOrCreate 会创建带初始 Disconnected 的 slot，
            // 提前调用会把该初始状态写入历史并污染状态序列断言。
            _sessions[instanceId].LastCandidates = candidates;
        }

        foreach (var candidate in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransportLeaseHandle lease;
            try
            {
                lease = await _broker.AcquireAsync(candidate, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                _selector.RecordFailure(candidate.EndpointId);
                continue;
            }

            Transition(instanceId, SessionState.Connecting);
            Transition(instanceId, SessionState.Handshaking);

            bool handshaken;
            try
            {
                handshaken = await _broker.HandshakeAsync(lease, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await SafeReleaseAsync(lease);
                throw;
            }
            catch (Exception)
            {
                handshaken = false;
            }

            if (!handshaken)
            {
                await SafeReleaseAsync(lease);
                _selector.RecordFailure(candidate.EndpointId);
                continue;
            }

            // Epoch 分配与 slot 写入必须在同一临界区内完成。
            // 若在锁外先取 Epoch，两个并发调用方可能按与 Epoch 相反的顺序写入，
            // 使 slot 留下不属于当前租约的 Epoch；DeviceStateStore 依赖该 Epoch
            // 过滤遥测，错配会导致新连接遥测被丢弃或旧连接遥测被当作现状。
            long epoch;
            TransportLeaseHandle? supersededLease;
            lock (_gate)
            {
                var slot = GetOrCreate(instanceId);
                epoch = ++_sessionEpochSequence;
                // 恢复路径可能在仍持有租约时重新连接：旧租约必须归还，
                // 否则 Endpoint 句柄（BLE 上是 WinRT/GATT）永久泄漏且被占死。
                supersededLease = slot.Lease;
                slot.Lease = lease;
                slot.SessionEpoch = epoch;
                slot.UserDisconnected = false;
                slot.RecoveryScheduled = false;
                SetState(slot, SessionState.Ready);
            }

            if (supersededLease is not null)
            {
                // 在锁外释放：ReleaseAsync 是 I/O，不可在锁内 await。
                await SafeReleaseAsync(supersededLease);
            }

            _selector.RecordSuccess(candidate.EndpointId);
            return new SessionConnectResult(true, epoch, null);
        }

        Transition(instanceId, SessionState.Failed);
        return new SessionConnectResult(false, 0, "session.all_candidates_failed");
    }

    public async Task DisconnectAsync(
        DeviceInstanceId instanceId,
        bool userInitiated,
        CancellationToken cancellationToken)
    {
        TransportLeaseHandle? lease;
        CancellationTokenSource? recoveryCts;
        lock (_gate)
        {
            var slot = GetOrCreate(instanceId);
            lease = slot.Lease;
            slot.Lease = null;
            // 用户主动断开是明确意图，必须阻止后续自动恢复。
            slot.UserDisconnected = userInitiated;
            slot.RecoveryScheduled = false;
            recoveryCts = slot.RecoveryCts;
            slot.RecoveryCts = null;
            SetState(slot, SessionState.Disconnected);
        }

        // 在锁外取消退避：退避循环需要取回锁来观察 UserDisconnected。
        if (recoveryCts is not null)
        {
            try
            {
                await recoveryCts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // 已释放，无需重复取消。
            }

            recoveryCts.Dispose();
        }

        if (lease is not null)
        {
            await _broker.ReleaseAsync(lease, cancellationToken);
        }
    }

    /// <summary>
    /// 关闭所有已知 Session。按用户主动断开语义处理，避免关闭过程中触发自动恢复。
    /// 单个实例失败不阻止其余实例释放；全部尝试完成后再汇总抛出，
    /// 否则一个卡住的设备会让其他设备的租约永久泄漏。
    /// </summary>
    public async Task StopAllAsync(CancellationToken cancellationToken)
    {
        DeviceInstanceId[] instanceIds;
        lock (_gate)
        {
            instanceIds = _sessions.Keys.ToArray();
        }

        var failures = new List<Exception>();
        foreach (var instanceId in instanceIds)
        {
            try
            {
                await DisconnectAsync(instanceId, userInitiated: true, cancellationToken);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("一个或多个设备会话关闭失败。", failures);
        }
    }

    /// <summary>
    /// 由 Broker/Driver 报告传输故障。Coordinator 是唯一决定是否恢复的组件。
    /// 用户已主动断开时绝不安排恢复。
    /// </summary>
    public void ReportTransportFailure(DeviceInstanceId instanceId, string reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);

        IReadOnlyList<EndpointCandidate> candidates;
        CancellationToken token;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(instanceId, out var slot) ||
                slot.UserDisconnected ||
                slot.RecoveryScheduled)
            {
                return;
            }

            slot.RecoveryScheduled = true;
            slot.RecoveryCts = new CancellationTokenSource();
            token = slot.RecoveryCts.Token;
            candidates = slot.LastCandidates;
            SetState(slot, SessionState.Recovering);
            slot.RecoveryTask = RecoverAsync(instanceId, candidates, token);
        }
    }

    /// <summary>
    /// 恢复循环：指数退避 + jitter，可取消，有上限。
    /// Recovering 不是终态——重试成功进入 Ready，耗尽进入 Failed。
    /// 恢复只做握手与安全配置，绝不重放旧的强度/波形/泵输出。
    /// </summary>
    private async Task RecoverAsync(
        DeviceInstanceId instanceId,
        IReadOnlyList<EndpointCandidate> candidates,
        CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 1; attempt <= _recoveryPolicy.MaxAttempts; attempt++)
            {
                TimeSpan delay;
                lock (_gate)
                {
                    delay = _recoveryPolicy.ComputeDelay(attempt, _random);
                }

                await Task.Delay(delay, _timeProvider, cancellationToken);

                lock (_gate)
                {
                    // 退避期间用户可能已主动断开：此时必须停止重试。
                    if (!_sessions.TryGetValue(instanceId, out var slot) || slot.UserDisconnected)
                    {
                        return;
                    }
                }

                var result = await ConnectAsync(instanceId, candidates, cancellationToken);
                if (result.IsConnected)
                {
                    return;
                }
            }

            // 重试耗尽：落到 Failed，不得永远停在 Recovering。
            lock (_gate)
            {
                if (_sessions.TryGetValue(instanceId, out var slot) && !slot.UserDisconnected)
                {
                    SetState(slot, SessionState.Failed);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 用户断开或宿主关闭取消了退避；状态已由取消方设置。
        }
        finally
        {
            // 无论从哪条出口离开（成功、耗尽、取消、用户断开），
            // 都必须清标志并释放取消源，否则反复断链会累积
            // CancellationTokenSource；用 finally 统一收口，
            // 避免将来新增出口时再次漏掉释放。
            ReleaseRecoveryScope(instanceId, cancellationToken);
        }
    }

    /// <summary>
    /// 清理一轮恢复的作用域。仅当 slot 仍持有本轮的取消源时才释放：
    /// DisconnectAsync 可能已摘走并释放它，此时不得重复释放，
    /// 也不得误释放后续新一轮恢复创建的取消源。
    /// </summary>
    private void ReleaseRecoveryScope(DeviceInstanceId instanceId, CancellationToken ownToken)
    {
        CancellationTokenSource? owned = null;
        lock (_gate)
        {
            if (_sessions.TryGetValue(instanceId, out var slot))
            {
                slot.RecoveryScheduled = false;
                if (slot.RecoveryCts is { } cts && OwnsToken(cts, ownToken))
                {
                    owned = cts;
                    slot.RecoveryCts = null;
                }
            }
        }

        owned?.Dispose();
    }

    /// <summary>
    /// 判断取消源是否正是本轮恢复所持有的那个。
    /// 已被 DisconnectAsync 释放的取消源读取 Token 会抛 ObjectDisposedException，
    /// 此时归属方已完成释放，本轮无需再处理。
    /// </summary>
    private static bool OwnsToken(CancellationTokenSource cts, CancellationToken ownToken)
    {
        try
        {
            return cts.Token == ownToken;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task SafeReleaseAsync(TransportLeaseHandle lease)
    {
        try
        {
            await _broker.ReleaseAsync(lease, CancellationToken.None);
        }
        catch
        {
            // 释放失败不影响候选切换决策。
        }
    }

    private void Transition(DeviceInstanceId instanceId, SessionState state, bool resetHistory = false)
    {
        lock (_gate)
        {
            var slot = GetOrCreate(instanceId);
            if (resetHistory)
            {
                slot.History.Clear();
            }

            SetState(slot, state);
        }
    }

    private static void SetState(SessionSlot slot, SessionState state)
    {
        slot.State = state;
        slot.History.Add(state);
    }

    private SessionSlot GetOrCreate(DeviceInstanceId instanceId)
    {
        if (!_sessions.TryGetValue(instanceId, out var slot))
        {
            slot = new SessionSlot();
            _sessions[instanceId] = slot;
        }

        return slot;
    }

    private sealed class SessionSlot
    {
        public SessionState State { get; set; } = SessionState.Disconnected;

        public List<SessionState> History { get; } = [];

        public TransportLeaseHandle? Lease { get; set; }

        public long SessionEpoch { get; set; }

        public bool UserDisconnected { get; set; }

        public bool RecoveryScheduled { get; set; }

        /// <summary>最近一次连接使用的候选列表，恢复时复用同一组候选。</summary>
        public IReadOnlyList<EndpointCandidate> LastCandidates { get; set; } = [];

        public CancellationTokenSource? RecoveryCts { get; set; }

        public Task? RecoveryTask { get; set; }
    }
}
