using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.DGLab.Common;

namespace ChargingPanel.Vendor.DGLab.Profiles;

/// <summary>
/// 负鼠振动控制器 V1 蓝牙协议 Profile。
/// </summary>
public static class OpossumV1Profile
{
    public static ProtocolProfileDescriptor Create() =>
        new(
            new ProtocolProfileId(DGLabStableIds.ProfileOpossumV1Ble),
            new ModelId(DGLabStableIds.ModelOpossumV1),
            "负鼠振动控制器 V1",
            [TransportKind.BluetoothLowEnergy],
            DGLabCapabilities.OpossumV1Capabilities());
}