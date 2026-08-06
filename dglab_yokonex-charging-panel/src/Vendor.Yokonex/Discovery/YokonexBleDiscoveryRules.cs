using System.Text.Json;
using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.Yokonex.Common;

namespace ChargingPanel.Vendor.Yokonex.Discovery;

/// <summary>
/// YOKONEX BLE 设备发现规则。
/// 一代/二代电击器共用服务 UUID FF30，通过蓝牙列表名（YKJ/YSKJ）区分代际；
/// 灌肠机匹配 FFB0，玩具匹配 FF40。
/// </summary>
public static class YokonexBleDiscoveryRules
{
    private const string EmsServiceUuid = "0000ff30-0000-1000-8000-00805f9b34fb";
    private const string EnemaServiceUuid = "0000ffb0-0000-1000-8000-00805f9b34fb";
    private const string ToyServiceUuid = "0000ff40-0000-1000-8000-00805f9b34fb";

    public static DiscoveryRuleDescriptor CreateEmsV16BleRule() =>
        new(
            "yokonex.ems.v1_6.ble",
            new ProtocolProfileId(YokonexStableIds.ProfileEmsV16),
            TransportKind.BluetoothLowEnergy,
            NameAndServiceSchema("YKJ", EmsServiceUuid));

    public static DiscoveryRuleDescriptor CreateEmsV20BleRule() =>
        new(
            "yokonex.ems.v2_0.ble",
            new ProtocolProfileId(YokonexStableIds.ProfileEmsV20),
            TransportKind.BluetoothLowEnergy,
            NameAndServiceSchema("YSKJ", EmsServiceUuid));

    public static DiscoveryRuleDescriptor CreateEnemaV10BleRule() =>
        new(
            "yokonex.enema.v1_0.ble",
            new ProtocolProfileId(YokonexStableIds.ProfileEnemaV10),
            TransportKind.BluetoothLowEnergy,
            ServiceSchema(EnemaServiceUuid));

    public static DiscoveryRuleDescriptor CreateToyV11BleRule() =>
        new(
            "yokonex.toy.v1_1.ble",
            new ProtocolProfileId(YokonexStableIds.ProfileToyV11),
            TransportKind.BluetoothLowEnergy,
            ServiceSchema(ToyServiceUuid));

    internal static VersionedJsonSchema NameAndServiceSchema(string name, string serviceUuid)
    {
        using var document = JsonDocument.Parse(
            $"{{\"name\":\"{name}\",\"properties\":{{\"serviceUuids\":\"{serviceUuid}\"}}}}");
        return new VersionedJsonSchema("1.0", document.RootElement.Clone());
    }

    internal static VersionedJsonSchema ServiceSchema(string serviceUuid)
    {
        using var document = JsonDocument.Parse($"{{\"properties\":{{\"serviceUuids\":\"{serviceUuid}\"}}}}");
        return new VersionedJsonSchema("1.0", document.RootElement.Clone());
    }
}
