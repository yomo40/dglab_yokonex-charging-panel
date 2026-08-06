using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;

namespace PluginFixtures.Compatible;

/// <summary>
/// 兼容测试插件：Host API 范围支持当前版本，贡献唯一 ID 的设备目录。
/// 只引用 Contracts，不依赖任何宿主实现。
/// </summary>
public sealed class CompatibleDevicePlugin : IDevicePlugin
{
    public const string PluginIdValue = "fixtures.compatible";
    public const string VendorIdValue = "fixtures.compatible";
    public const string ModelIdValue = "fixtures.compatible.device";
    public const string ProfileIdValue = "fixtures.compatible.profile";

    private readonly DevicePluginContribution _contribution;

    public CompatibleDevicePlugin()
    {
        Manifest = new PluginManifest(
            new PluginId(PluginIdValue),
            "兼容测试插件",
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
            [new VendorDescriptor(new VendorId(VendorIdValue), "兼容测试厂商")],
            [
                new DeviceModelDescriptor(
                    new ModelId(ModelIdValue),
                    new VendorId(VendorIdValue),
                    "兼容测试设备",
                    DeviceAvailability.Public,
                    [profileId])
            ],
            [
                new ProtocolProfileDescriptor(
                    profileId,
                    new ModelId(ModelIdValue),
                    "兼容测试 Profile",
                    [TransportKind.Virtual],
                    [])
            ],
            [],
            []);
    }
}
