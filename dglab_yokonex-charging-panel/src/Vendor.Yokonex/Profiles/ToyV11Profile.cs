using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.Yokonex.Common;

namespace ChargingPanel.Vendor.Yokonex.Profiles;

/// <summary>
/// 跳蛋/飞机杯 V1.1（YSKJ_TOY_BLE V1.1）协议 Profile。BLE 传输。
/// </summary>
public static class ToyV11Profile
{
    public static ProtocolProfileDescriptor Create() =>
        new(
            new ProtocolProfileId(YokonexStableIds.ProfileToyV11),
            new ModelId(YokonexStableIds.ModelToyV11),
            "跳蛋/飞机杯 V1.1（蓝牙低功耗）",
            [TransportKind.BluetoothLowEnergy],
            YokonexCapabilities.ToyV11Capabilities());
}
