using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Vendor.Yokonex.Common;
using ChargingPanel.Vendor.Yokonex.Discovery;
using ChargingPanel.Vendor.Yokonex.Drivers;
using ChargingPanel.Vendor.Yokonex.Profiles;

namespace ChargingPanel.Vendor.Yokonex.Catalog;

/// <summary>
/// YOKONEX 厂商目录：汇总全部设备模型、协议 Profile、发现规则与驱动工厂。
/// </summary>
public static class YokonexCatalog
{
    /// <summary>构造 YOKONEX 插件完整贡献。</summary>
    public static DevicePluginContribution CreateContribution()
    {
        var vendors = new List<VendorDescriptor>();
        var models = new List<DeviceModelDescriptor>();
        var profiles = new List<ProtocolProfileDescriptor>();
        var rules = new List<DiscoveryRuleDescriptor>();
        var factories = new List<IProtocolDriverFactory>();

        AddEms(vendors, models, profiles, rules, factories);
        AddEnema(vendors, models, profiles, rules, factories);
        AddToy(vendors, models, profiles, rules, factories);
        AddImTarget(vendors, models, profiles, factories);
        AddApiBridge(vendors, models, profiles, factories);

        return new DevicePluginContribution(vendors, models, profiles, rules, factories);
    }

    private static void AddEms(
        ICollection<VendorDescriptor> vendors,
        ICollection<DeviceModelDescriptor> models,
        ICollection<ProtocolProfileDescriptor> profiles,
        ICollection<DiscoveryRuleDescriptor> rules,
        ICollection<IProtocolDriverFactory> factories)
    {
        var vendorId = new VendorId(YokonexStableIds.Vendor);
        vendors.Add(new VendorDescriptor(vendorId, "役次元"));

        var v16ModelId = new ModelId(YokonexStableIds.ModelEmsGen1);
        var v20ModelId = new ModelId(YokonexStableIds.ModelEmsGen2);
        var v16ProfileId = new ProtocolProfileId(YokonexStableIds.ProfileEmsV16);
        var v20ProfileId = new ProtocolProfileId(YokonexStableIds.ProfileEmsV20);

        models.Add(new DeviceModelDescriptor(
            v16ModelId,
            vendorId,
            "电击器一代（YKJ）",
            DeviceAvailability.Public,
            [v16ProfileId]));
        models.Add(new DeviceModelDescriptor(
            v20ModelId,
            vendorId,
            "电击器二代（YSKJ）",
            DeviceAvailability.Public,
            [v20ProfileId]));

        profiles.Add(EmsV16Profile.Create());
        profiles.Add(EmsV20Profile.Create());

        rules.Add(YokonexBleDiscoveryRules.CreateEmsV16BleRule());
        rules.Add(YokonexBleDiscoveryRules.CreateEmsV20BleRule());

        factories.Add(new YokonexDriverFactory(
            v16ProfileId,
            context => new EmsDriver(v16ProfileId, new Protocol.EmsV16Codec(), context.Logger)));
        factories.Add(new YokonexDriverFactory(
            v20ProfileId,
            context => new EmsDriver(v20ProfileId, new Protocol.EmsV20Codec(), context.Logger)));
    }

    private static void AddEnema(
        ICollection<VendorDescriptor> vendors,
        ICollection<DeviceModelDescriptor> models,
        ICollection<ProtocolProfileDescriptor> profiles,
        ICollection<DiscoveryRuleDescriptor> rules,
        ICollection<IProtocolDriverFactory> factories)
    {
        var vendorId = new VendorId(YokonexStableIds.Vendor);
        var modelId = new ModelId(YokonexStableIds.ModelEnemaGen1);
        var profileId = new ProtocolProfileId(YokonexStableIds.ProfileEnemaV10);

        models.Add(new DeviceModelDescriptor(
            modelId,
            vendorId,
            "灌肠机一代",
            DeviceAvailability.Public,
            [profileId]));

        profiles.Add(EnemaV10Profile.Create());
        rules.Add(YokonexBleDiscoveryRules.CreateEnemaV10BleRule());
        factories.Add(new YokonexDriverFactory(
            profileId,
            context => new EnemaV10Driver(profileId, context.Logger)));
    }

    private static void AddToy(
        ICollection<VendorDescriptor> vendors,
        ICollection<DeviceModelDescriptor> models,
        ICollection<ProtocolProfileDescriptor> profiles,
        ICollection<DiscoveryRuleDescriptor> rules,
        ICollection<IProtocolDriverFactory> factories)
    {
        var vendorId = new VendorId(YokonexStableIds.Vendor);
        var modelId = new ModelId(YokonexStableIds.ModelToyV11);
        var profileId = new ProtocolProfileId(YokonexStableIds.ProfileToyV11);

        models.Add(new DeviceModelDescriptor(
            modelId,
            vendorId,
            "跳蛋/飞机杯 V1.1",
            DeviceAvailability.Public,
            [profileId]));

        profiles.Add(ToyV11Profile.Create());
        rules.Add(YokonexBleDiscoveryRules.CreateToyV11BleRule());
        factories.Add(new YokonexDriverFactory(
            profileId,
            context => new ToyV11Driver(profileId, context.Logger)));
    }

    private static void AddImTarget(
        ICollection<VendorDescriptor> vendors,
        ICollection<DeviceModelDescriptor> models,
        ICollection<ProtocolProfileDescriptor> profiles,
        ICollection<IProtocolDriverFactory> factories)
    {
        var vendorId = new VendorId(YokonexStableIds.Vendor);
        var modelId = new ModelId(YokonexStableIds.ModelImTarget);
        var profileId = new ProtocolProfileId(YokonexStableIds.ProfileImTarget);

        models.Add(new DeviceModelDescriptor(
            modelId,
            vendorId,
            "役次元 IM 目标",
            DeviceAvailability.Public,
            [profileId]));

        profiles.Add(YokonexImTargetProfile.Create());
        factories.Add(new YokonexDriverFactory(
            profileId,
            context => new YokonexImTargetDriver(profileId, context.Logger)));
    }

    private static void AddApiBridge(
        ICollection<VendorDescriptor> vendors,
        ICollection<DeviceModelDescriptor> models,
        ICollection<ProtocolProfileDescriptor> profiles,
        ICollection<IProtocolDriverFactory> factories)
    {
        var vendorId = new VendorId(YokonexStableIds.Vendor);
        var modelId = new ModelId(YokonexStableIds.ModelApiBridge);
        var profileId = new ProtocolProfileId(YokonexStableIds.ProfileApiBridge);

        models.Add(new DeviceModelDescriptor(
            modelId,
            vendorId,
            "役次元 API Bridge",
            DeviceAvailability.Public,
            [profileId]));

        profiles.Add(YokonexApiBridgeProfile.Create());
        factories.Add(new YokonexDriverFactory(
            profileId,
            context => new YokonexApiBridgeDriver(profileId, context.Logger)));
    }
}
