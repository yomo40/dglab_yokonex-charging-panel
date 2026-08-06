using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.DGLab.Common;

namespace ChargingPanel.Vendor.DGLab.Discovery;

/// <summary>
/// 爪印无线按钮传感器发现规则。
/// V1.1 (47L120300) 可创建会话；V1.0 (47L120100) 仅产生升级诊断。
/// </summary>
public static class PawPrintsDiscoveryRule
{
    /// <summary>V1.0 扫描时面向用户的中文升级诊断。</summary>
    public const string V10UpgradeMessage = "请使用 DG-LAB 3.0 APP 将配件固件升级至 V1.1";

    public static DiscoveryRuleDescriptor CreateV11BleRule() =>
        new(
            "dglab.pawprints.v1.1.ble.name",
            new ProtocolProfileId(DGLabStableIds.ProfilePawPrintsV11Ble),
            TransportKind.BluetoothLowEnergy,
            CoyoteDiscoveryRules.NameSchema("47L120300"));

    public static DiscoveryRuleDescriptor CreateV10BleUpgradeRule() =>
        new(
            "dglab.pawprints.v1.0.ble.upgrade",
            new ProtocolProfileId(DGLabStableIds.ProfilePawPrintsV10Ble),
            TransportKind.BluetoothLowEnergy,
            CoyoteDiscoveryRules.NameSchema("47L120100"));
}