using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Vendor.DGLab.Common;
using ChargingPanel.Vendor.DGLab.Discovery;
using ChargingPanel.Vendor.DGLab.Drivers;
using ChargingPanel.Vendor.DGLab.Profiles;

namespace ChargingPanel.Vendor.DGLab.Catalog;

/// <summary>
/// DG-LAB 厂商目录：汇总全部设备模型、协议 Profile、发现规则与驱动工厂。
/// </summary>
public static class DGLabCatalog
{
    /// <summary>构造 DG-LAB 插件完整贡献。</summary>
    public static DevicePluginContribution CreateContribution()
    {
        var vendors = new List<VendorDescriptor>();
        var models = new List<DeviceModelDescriptor>();
        var profiles = new List<ProtocolProfileDescriptor>();
        var rules = new List<DiscoveryRuleDescriptor>();
        var factories = new List<IProtocolDriverFactory>();

        AddCoyoteV3(vendors, models, profiles, rules, factories);
        AddCoyoteV2(vendors, models, profiles, rules, factories);
        AddPawPrints(vendors, models, profiles, rules, factories);
        AddCivetAndOpossum(vendors, models, profiles, rules, factories);

        return new DevicePluginContribution(vendors, models, profiles, rules, factories);
    }

    private static void AddCivetAndOpossum(
        ICollection<VendorDescriptor> vendors,
        ICollection<DeviceModelDescriptor> models,
        ICollection<ProtocolProfileDescriptor> profiles,
        ICollection<DiscoveryRuleDescriptor> rules,
        ICollection<IProtocolDriverFactory> factories)
    {
        var civetModelId = new ModelId(DGLabStableIds.ModelCivetV1);
        var civetProfileId = new ProtocolProfileId(DGLabStableIds.ProfileCivetV1Ble);
        var opossumModelId = new ModelId(DGLabStableIds.ModelOpossumV1);
        var opossumProfileId = new ProtocolProfileId(DGLabStableIds.ProfileOpossumV1Ble);

        models.Add(new DeviceModelDescriptor(
            civetModelId,
            new VendorId(DGLabStableIds.Vendor),
            "灵猫边缘控制传感器 V1",
            DeviceAvailability.Public,
            [civetProfileId]));
        models.Add(new DeviceModelDescriptor(
            opossumModelId,
            new VendorId(DGLabStableIds.Vendor),
            "负鼠振动控制器 V1",
            DeviceAvailability.Public,
            [opossumProfileId]));

        profiles.Add(CivetV1Profile.Create());
        profiles.Add(OpossumV1Profile.Create());
        rules.Add(AccessoryDiscoveryRules.CreateCivetV1BleRule());
        rules.Add(AccessoryDiscoveryRules.CreateOpossumV1BleRule());
        factories.Add(new DGLabDriverFactory(civetProfileId, context => new CivetV1Driver(context)));
        factories.Add(new DGLabDriverFactory(opossumProfileId, context => new OpossumV1Driver(context)));
    }

    private static void AddPawPrints(
        ICollection<VendorDescriptor> vendors,
        ICollection<DeviceModelDescriptor> models,
        ICollection<ProtocolProfileDescriptor> profiles,
        ICollection<DiscoveryRuleDescriptor> rules,
        ICollection<IProtocolDriverFactory> factories)
    {
        var v11ModelId = new ModelId(DGLabStableIds.ModelPawPrintsV11);
        var v11ProfileId = new ProtocolProfileId(DGLabStableIds.ProfilePawPrintsV11Ble);
        var v10ModelId = new ModelId(DGLabStableIds.ModelPawPrintsV10);
        var v10ProfileId = new ProtocolProfileId(DGLabStableIds.ProfilePawPrintsV10Ble);

        models.Add(new DeviceModelDescriptor(
            v11ModelId,
            new VendorId(DGLabStableIds.Vendor),
            "爪印无线按钮传感器 V1.1",
            DeviceAvailability.Public,
            [v11ProfileId]));
        models.Add(new DeviceModelDescriptor(
            v10ModelId,
            new VendorId(DGLabStableIds.Vendor),
            "爪印无线按钮传感器 V1.0（需升级）",
            DeviceAvailability.UpgradeDiagnostic,
            [v10ProfileId]));

        profiles.Add(PawPrintsV11Profile.Create());
        profiles.Add(PawPrintsV10Profile.Create());
        rules.Add(PawPrintsDiscoveryRule.CreateV11BleRule());
        rules.Add(PawPrintsDiscoveryRule.CreateV10BleUpgradeRule());
        factories.Add(new DGLabDriverFactory(v11ProfileId, context => new PawPrintsV11Driver(context)));
    }

    private static void AddCoyoteV2(
        ICollection<VendorDescriptor> vendors,
        ICollection<DeviceModelDescriptor> models,
        ICollection<ProtocolProfileDescriptor> profiles,
        ICollection<DiscoveryRuleDescriptor> rules,
        ICollection<IProtocolDriverFactory> factories)
    {
        var modelId = new ModelId(DGLabStableIds.ModelCoyoteV2);
        var bleProfileId = new ProtocolProfileId(DGLabStableIds.ProfileCoyoteV2Ble);

        models.Add(new DeviceModelDescriptor(
            modelId,
            new VendorId(DGLabStableIds.Vendor),
            "郊狼脉冲主机 V2",
            DeviceAvailability.Public,
            [bleProfileId]));

        profiles.Add(CoyoteV2BleProfile.Create());
        rules.Add(CoyoteDiscoveryRules.CreateV2BleRule());
        factories.Add(new DGLabDriverFactory(bleProfileId, context => new CoyoteV2BleDriver(context)));
    }

    private static void AddCoyoteV3(
        ICollection<VendorDescriptor> vendors,
        ICollection<DeviceModelDescriptor> models,
        ICollection<ProtocolProfileDescriptor> profiles,
        ICollection<DiscoveryRuleDescriptor> rules,
        ICollection<IProtocolDriverFactory> factories)
    {
        vendors.Add(new VendorDescriptor(new VendorId(DGLabStableIds.Vendor), "DG-LAB"));
        var modelId = new ModelId(DGLabStableIds.ModelCoyoteV3);
        var bleProfileId = new ProtocolProfileId(DGLabStableIds.ProfileCoyoteV3Ble);
        var wsProfileId = new ProtocolProfileId(DGLabStableIds.ProfileCoyoteV3WebSocket);

        models.Add(new DeviceModelDescriptor(
            modelId,
            new VendorId(DGLabStableIds.Vendor),
            "郊狼脉冲主机 V3",
            DeviceAvailability.Public,
            [bleProfileId, wsProfileId]));

        profiles.Add(CoyoteV3BleProfile.Create());
        profiles.Add(CoyoteV3WebSocketProfile.Create());
        rules.Add(CoyoteDiscoveryRules.CreateV3BleRule());
        rules.Add(CoyoteDiscoveryRules.CreateV3WebSocketRule());
        factories.Add(new DGLabDriverFactory(bleProfileId, context => new CoyoteV3BleDriver(context)));
        factories.Add(new DGLabDriverFactory(wsProfileId, context => new CoyoteV3WebSocketDriver(context)));
    }
}