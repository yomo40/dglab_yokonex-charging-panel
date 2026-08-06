using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;

namespace PluginFixtures.BundledContracts;

/// <summary>
/// 携带第二份 Contracts 的插件：正常引用 Contracts，但其发布目录被放入
/// 一份 ChargingPanel.Contracts.dll 副本。宿主必须检测并强制解析到宿主版本，
/// 避免类型身份分裂。
/// </summary>
public sealed class BundledContractsDevicePlugin : IDevicePlugin
{
    public const string PluginIdValue = "fixtures.bundledcontracts";
    public const string VendorIdValue = "fixtures.bundledcontracts";
    public const string ModelIdValue = "fixtures.bundledcontracts.device";
    public const string ProfileIdValue = "fixtures.bundledcontracts.profile";

    private readonly DevicePluginContribution _contribution;

    public BundledContractsDevicePlugin()
    {
        Manifest = new PluginManifest(
            new PluginId(PluginIdValue),
            "携带第二份 Contracts 插件",
            new Version(1, 0),
            new HostApiVersionRange(new Version(1, 0), new Version(1, 0)));
        _contribution = BuildContribution();
    }

    public PluginManifest Manifest { get; }

    public DevicePluginContribution Describe() => _contribution;

    private static DevicePluginContribution BuildContribution()
    {
        var profileId = new ProtocolProfileId(ProfileIdValue);
        return new DevicePluginContribution(
            [new VendorDescriptor(new VendorId(VendorIdValue), "打包 Contracts 测试厂商")],
            [
                new DeviceModelDescriptor(
                    new ModelId(ModelIdValue),
                    new VendorId(VendorIdValue),
                    "打包 Contracts 测试设备",
                    DeviceAvailability.Public,
                    [profileId])
            ],
            [
                new ProtocolProfileDescriptor(
                    profileId,
                    new ModelId(ModelIdValue),
                    "打包 Contracts 测试 Profile",
                    [TransportKind.Virtual],
                    [])
            ],
            [],
            []);
    }
}
