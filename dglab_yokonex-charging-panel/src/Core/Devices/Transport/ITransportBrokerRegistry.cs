using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Core.Devices.Sessions;

namespace ChargingPanel.Core.Devices.Transport;

/// <summary>
/// 传输租约句柄。仅标识"当前占用了哪个 Endpoint"，不承载重连状态。
/// </summary>
public sealed record TransportLeaseHandle(EndpointId EndpointId, ProtocolProfileId ProfileId);

/// <summary>
/// 传输 Broker 抽象。Broker 只执行一次可取消 I/O：
/// 获取租约、握手、释放。它报告失败但绝不自行重连——
/// 重连是 SessionCoordinator 的唯一职责。
/// </summary>
public interface ITransportBroker
{
    /// <summary>获取 Endpoint 租约。失败时抛出，由 Coordinator 决定是否重试。</summary>
    ValueTask<TransportLeaseHandle> AcquireAsync(
        EndpointCandidate candidate,
        CancellationToken cancellationToken);

    /// <summary>执行协议握手。返回 false 表示握手未成功。</summary>
    ValueTask<bool> HandshakeAsync(
        TransportLeaseHandle lease,
        CancellationToken cancellationToken);

    /// <summary>释放租约。</summary>
    ValueTask ReleaseAsync(TransportLeaseHandle lease, CancellationToken cancellationToken);
}

/// <summary>
/// 按传输类型解析 Broker。Core 只依赖该抽象，
/// 具体 WinRT/WebSocket/IM 实现留在 Infrastructure。
/// </summary>
public interface ITransportBrokerRegistry
{
    ITransportBroker? Resolve(TransportKind transportKind);
}
