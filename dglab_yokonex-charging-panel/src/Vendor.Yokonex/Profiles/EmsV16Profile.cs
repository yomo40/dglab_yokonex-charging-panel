using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.Yokonex.Common;

namespace ChargingPanel.Vendor.Yokonex.Profiles;

/// <summary>
/// 电击器一代（YSKJ_EMS_BLE V1.6）协议 Profile。BLE 传输。
/// </summary>
public static class EmsV16Profile
{
    public static ProtocolProfileDescriptor Create() =>
        new(
            new ProtocolProfileId(YokonexStableIds.ProfileEmsV16),
            new ModelId(YokonexStableIds.ModelEmsGen1),
            "电击器一代 V1.6（蓝牙低功耗）",
            [TransportKind.BluetoothLowEnergy],
            YokonexCapabilities.EmsV16Capabilities());
}
