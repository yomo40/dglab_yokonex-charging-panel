using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.Yokonex.Common;

namespace ChargingPanel.Vendor.Yokonex.Profiles;

/// <summary>
/// 电击器二代（YSKJ_EMS_BLE V2.0）协议 Profile。BLE 传输。
/// 二代新增同帧 A+B 固定/实时模式与频率列表模式。
/// </summary>
public static class EmsV20Profile
{
    public static ProtocolProfileDescriptor Create() =>
        new(
            new ProtocolProfileId(YokonexStableIds.ProfileEmsV20),
            new ModelId(YokonexStableIds.ModelEmsGen2),
            "电击器二代 V2.0（蓝牙低功耗）",
            [TransportKind.BluetoothLowEnergy],
            YokonexCapabilities.EmsV20Capabilities());
}
