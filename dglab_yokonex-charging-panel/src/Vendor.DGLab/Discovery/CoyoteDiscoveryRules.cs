using System.Text.Json;
using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.DGLab.Common;

namespace ChargingPanel.Vendor.DGLab.Discovery;

/// <summary>
/// 郊狼系列设备发现规则。
/// </summary>
public static class CoyoteDiscoveryRules
{
    public static DiscoveryRuleDescriptor CreateV3BleRule() =>
        new(
            "dglab.coyote.v3.ble.name",
            new ProtocolProfileId(DGLabStableIds.ProfileCoyoteV3Ble),
            TransportKind.BluetoothLowEnergy,
            NameSchema("47L121000"));

    public static DiscoveryRuleDescriptor CreateV2BleRule() =>
        new(
            "dglab.coyote.v2.ble.name",
            new ProtocolProfileId(DGLabStableIds.ProfileCoyoteV2Ble),
            TransportKind.BluetoothLowEnergy,
            NameSchema("D-LAB ESTIM01"));

    public static DiscoveryRuleDescriptor CreateV3WebSocketRule() =>
        new(
            "dglab.coyote.v3.ws.identity",
            new ProtocolProfileId(DGLabStableIds.ProfileCoyoteV3WebSocket),
            TransportKind.WebSocket,
            PropertiesSchema("serviceIdentity", "47L121000"));

    internal static VersionedJsonSchema NameSchema(string name)
    {
        using var document = JsonDocument.Parse($"{{\"name\":\"{name}\"}}");
        return new VersionedJsonSchema("1.0", document.RootElement.Clone());
    }

    internal static VersionedJsonSchema PropertiesSchema(string key, string value)
    {
        using var document = JsonDocument.Parse($"{{\"properties\":{{\"{key}\":\"{value}\"}}}}");
        return new VersionedJsonSchema("1.0", document.RootElement.Clone());
    }
}