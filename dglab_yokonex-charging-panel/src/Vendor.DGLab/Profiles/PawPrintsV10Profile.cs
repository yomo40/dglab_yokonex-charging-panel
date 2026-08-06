using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.DGLab.Common;

namespace ChargingPanel.Vendor.DGLab.Profiles;

/// <summary>
/// 爪印无线按钮传感器 V1.0 协议 Profile（升级诊断专用，不可创建会话）。
/// </summary>
public static class PawPrintsV10Profile
{
    public static ProtocolProfileDescriptor Create() =>
        new(
            new ProtocolProfileId(DGLabStableIds.ProfilePawPrintsV10Ble),
            new ModelId(DGLabStableIds.ModelPawPrintsV10),
            "爪印无线按钮传感器 V1.0（需升级）",
            [TransportKind.BluetoothLowEnergy],
            [DGLabCapabilities.BatteryStatus()]);
}