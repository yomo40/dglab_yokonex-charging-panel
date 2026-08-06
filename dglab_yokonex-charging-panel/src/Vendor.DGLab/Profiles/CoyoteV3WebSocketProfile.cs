using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.DGLab.Common;

namespace ChargingPanel.Vendor.DGLab.Profiles;

/// <summary>
/// 郊狼脉冲主机 V3 公开 WebSocket 桥接协议 Profile。
/// </summary>
public static class CoyoteV3WebSocketProfile
{
    public static ProtocolProfileDescriptor Create() =>
        new(
            new ProtocolProfileId(DGLabStableIds.ProfileCoyoteV3WebSocket),
            new ModelId(DGLabStableIds.ModelCoyoteV3),
            "郊狼脉冲主机 V3（公开 WebSocket 桥接）",
            [TransportKind.WebSocket],
            DGLabCapabilities.CoyoteV3Capabilities());
}