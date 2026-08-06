using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.Core.Devices.Sessions;

/// <summary>
/// 不可变会话快照。供 Dispatcher 与查询方读取当前 Endpoint 与 SessionEpoch，
/// 而不触碰 SessionCoordinator 的内部可变状态。
///
/// 快照一经创建即不再变化：调用方拿到的 Epoch 与状态始终自洽，
/// 不会出现"读到 Ready 但 Epoch 已被故障转移改写"的撕裂读。
/// </summary>
public sealed record DeviceSession
{
    public DeviceSession(
        DeviceInstanceId instanceId,
        EndpointId? endpointId,
        ProtocolProfileId? profileId,
        SessionState state,
        long sessionEpoch)
    {
        if (sessionEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionEpoch));
        }

        InstanceId = instanceId;
        EndpointId = endpointId;
        ProfileId = profileId;
        State = state;
        SessionEpoch = sessionEpoch;
    }

    public DeviceInstanceId InstanceId { get; }

    /// <summary>当前占用的 Endpoint；未连接时为 null。</summary>
    public EndpointId? EndpointId { get; }

    public ProtocolProfileId? ProfileId { get; }

    public SessionState State { get; }

    public long SessionEpoch { get; }

    /// <summary>
    /// 仅 Ready 状态可接受命令。Recovering、Failed、PluginUnavailable
    /// 与用户断开都必须拒绝，避免命令发往不存在的连接。
    /// </summary>
    public bool CanAcceptCommands => State == SessionState.Ready;
}
