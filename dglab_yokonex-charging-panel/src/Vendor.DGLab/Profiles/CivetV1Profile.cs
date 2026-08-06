using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.DGLab.Common;

namespace ChargingPanel.Vendor.DGLab.Profiles;

/// <summary>
/// 灵猫边缘控制传感器 V1 蓝牙协议 Profile。
/// </summary>
public static class CivetV1Profile
{
    public static ProtocolProfileDescriptor Create() =>
        new(
            new ProtocolProfileId(DGLabStableIds.ProfileCivetV1Ble),
            new ModelId(DGLabStableIds.ModelCivetV1),
            "灵猫边缘控制传感器 V1",
            [TransportKind.BluetoothLowEnergy],
            DGLabCapabilities.CivetV1Capabilities());
}