using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.Yokonex.Common;

namespace ChargingPanel.Vendor.Yokonex.Profiles;

/// <summary>
/// 灌肠机一代（TDL_YISKJ-003 V1.0）协议 Profile。BLE 传输，AES-128 ECB 加密。
/// </summary>
public static class EnemaV10Profile
{
    public static ProtocolProfileDescriptor Create() =>
        new(
            new ProtocolProfileId(YokonexStableIds.ProfileEnemaV10),
            new ModelId(YokonexStableIds.ModelEnemaGen1),
            "灌肠机一代（蓝牙低功耗）",
            [TransportKind.BluetoothLowEnergy],
            YokonexCapabilities.EnemaV10Capabilities());
}
