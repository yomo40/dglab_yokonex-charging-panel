using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.Yokonex.Common;

namespace ChargingPanel.Vendor.Yokonex.Profiles;

/// <summary>
/// 役次元 API Bridge 协议 Profile。HTTP 与 WebSocket 两种传输。
/// 独立于 Native IM 驱动，Bridge 是单独 Endpoint。
/// </summary>
public static class YokonexApiBridgeProfile
{
    public static ProtocolProfileDescriptor Create() =>
        new(
            new ProtocolProfileId(YokonexStableIds.ProfileApiBridge),
            new ModelId(YokonexStableIds.ModelApiBridge),
            "役次元 API Bridge",
            [TransportKind.Http, TransportKind.WebSocket],
            YokonexCapabilities.ApiBridgeCapabilities());
}
