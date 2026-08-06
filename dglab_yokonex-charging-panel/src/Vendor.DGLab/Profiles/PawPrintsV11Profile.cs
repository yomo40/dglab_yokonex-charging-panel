using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.DGLab.Common;

namespace ChargingPanel.Vendor.DGLab.Profiles;

/// <summary>
/// 爪印无线按钮传感器 V1.1 蓝牙协议 Profile。
/// </summary>
public static class PawPrintsV11Profile
{
    public static ProtocolProfileDescriptor Create() =>
        new(
            new ProtocolProfileId(DGLabStableIds.ProfilePawPrintsV11Ble),
            new ModelId(DGLabStableIds.ModelPawPrintsV11),
            "爪印无线按钮传感器 V1.1",
            [TransportKind.BluetoothLowEnergy],
            DGLabCapabilities.PawPrintsV11Capabilities());
}