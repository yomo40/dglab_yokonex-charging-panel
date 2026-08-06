using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;

namespace PluginFixtures.Incompatible;

/// <summary>
/// Host API 不兼容插件：声明只支持 2.0-3.0，而宿主当前为 1.0。
/// 宿主必须在加载任何程序集之前拒绝它。
/// </summary>
public sealed class IncompatibleDevicePlugin : IDevicePlugin
{
    public const string PluginIdValue = "fixtures.incompatible";
    public const string VendorIdValue = "fixtures.incompatible";
    public const string ModelIdValue = "fixtures.incompatible.device";
    public const string ProfileIdValue = "fixtures.incompatible.profile";

    private readonly DevicePluginContribution _contribution;

    public IncompatibleDevicePlugin()
    {
        Manifest = new PluginManifest(
            new PluginId(PluginIdValue),
            "Host API 不兼容插件",
            new Version(1, 0),
            new HostApiVersionRange(new Version(2, 0), new Version(3, 0)));
        _contribution = BuildContribution();
    }

    public PluginManifest Manifest { get; }

    public DevicePluginContribution Describe() => _contribution;

    private static DevicePluginContribution BuildContribution()
    {
        var profileId = new ProtocolProfileId(ProfileIdValue);
        return new DevicePluginContribution(
            [new VendorDescriptor(new VendorId(VendorIdValue), "不兼容测试厂商")],
            [
                new DeviceModelDescriptor(
                    new ModelId(ModelIdValue),
                    new VendorId(VendorIdValue),
                    "不兼容测试设备",
                    DeviceAvailability.Public,
                    [profileId])
            ],
            [
                new ProtocolProfileDescriptor(
                    profileId,
                    new ModelId(ModelIdValue),
                    "不兼容测试 Profile",
                    [TransportKind.Virtual],
                    [])
            ],
            [],
            []);
    }
}
