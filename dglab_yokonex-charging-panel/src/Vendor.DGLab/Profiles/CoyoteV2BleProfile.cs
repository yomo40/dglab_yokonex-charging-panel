using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.DGLab.Common;

namespace ChargingPanel.Vendor.DGLab.Profiles;

/// <summary>
/// 郊狼脉冲主机 V2 蓝牙协议 Profile。
/// </summary>
public static class CoyoteV2BleProfile
{
    public static ProtocolProfileDescriptor Create() =>
        new(
            new ProtocolProfileId(DGLabStableIds.ProfileCoyoteV2Ble),
            new ModelId(DGLabStableIds.ModelCoyoteV2),
            "郊狼脉冲主机 V2（蓝牙低功耗）",
            [TransportKind.BluetoothLowEnergy],
            DGLabCapabilities.CoyoteV2Capabilities());
}