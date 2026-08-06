using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;

namespace PluginFixtures.DuplicateModel;

/// <summary>
/// 重复 Model ID 插件：贡献与内置 DG-LAB 郊狼 V3 相同的模型稳定 ID
/// （dglab.coyote.v3）。插件自身可加载，但合并进目录时必须产生
/// DuplicateModelId 诊断，且不得创建会话。
/// </summary>
public sealed class DuplicateModelDevicePlugin : IDevicePlugin
{
    public const string PluginIdValue = "fixtures.duplicatemodel";
    public const string VendorIdValue = "fixtures.duplicatemodel";
    public const string ModelIdValue = "dglab.coyote.v3";
    public const string ProfileIdValue = "fixtures.duplicatemodel.profile";

    private readonly DevicePluginContribution _contribution;

    public DuplicateModelDevicePlugin()
    {
        Manifest = new PluginManifest(
            new PluginId(PluginIdValue),
            "重复模型 ID 插件",
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
            [new VendorDescriptor(new VendorId(VendorIdValue), "重复模型测试厂商")],
            [
                new DeviceModelDescriptor(
                    new ModelId(ModelIdValue),
                    new VendorId(VendorIdValue),
                    "重复模型测试设备",
                    DeviceAvailability.Public,
                    [profileId])
            ],
            [
                new ProtocolProfileDescriptor(
                    profileId,
                    new ModelId(ModelIdValue),
                    "重复模型测试 Profile",
                    [TransportKind.Virtual],
                    [])
            ],
            [],
            []);
    }
}
