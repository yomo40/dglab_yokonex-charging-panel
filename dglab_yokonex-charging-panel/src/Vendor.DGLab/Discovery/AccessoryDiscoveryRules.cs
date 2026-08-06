using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.DGLab.Common;

namespace ChargingPanel.Vendor.DGLab.Discovery;

/// <summary>
/// DG-LAB 配件（灵猫/负鼠）发现规则。
/// </summary>
public static class AccessoryDiscoveryRules
{
    public static DiscoveryRuleDescriptor CreateCivetV1BleRule() =>
        new(
            "dglab.civet.v1.ble.name",
            new ProtocolProfileId(DGLabStableIds.ProfileCivetV1Ble),
            TransportKind.BluetoothLowEnergy,
            CoyoteDiscoveryRules.NameSchema("47L124000"));

    public static DiscoveryRuleDescriptor CreateOpossumV1BleRule() =>
        new(
            "dglab.opossum.v1.ble.name",
            new ProtocolProfileId(DGLabStableIds.ProfileOpossumV1Ble),
            TransportKind.BluetoothLowEnergy,
            CoyoteDiscoveryRules.NameSchema("47L127000"));
}