using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.DGLab.Common;

namespace ChargingPanel.Vendor.DGLab.Profiles;

/// <summary>
/// 郊狼脉冲主机 V3 蓝牙协议 Profile。BLE 与公开 WS 是同一 Model 的不同 Profile。
/// </summary>
public static class CoyoteV3BleProfile
{
    public static ProtocolProfileDescriptor Create() =>
        new(
            new ProtocolProfileId(DGLabStableIds.ProfileCoyoteV3Ble),
            new ModelId(DGLabStableIds.ModelCoyoteV3),
            "郊狼脉冲主机 V3（蓝牙低功耗）",
            [TransportKind.BluetoothLowEnergy],
            DGLabCapabilities.CoyoteV3Capabilities());
}