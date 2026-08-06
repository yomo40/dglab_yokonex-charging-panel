using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Vendor.Yokonex.Catalog;
using ChargingPanel.Vendor.Yokonex.Common;

namespace ChargingPanel.Vendor.Yokonex;

/// <summary>
/// YOKONEX 厂商插件入口。贡献由 <see cref="YokonexCatalog"/> 汇总。
/// </summary>
public sealed class YokonexPlugin : IDevicePlugin
{
    private readonly DevicePluginContribution _contribution;

    public YokonexPlugin()
    {
        Manifest = new PluginManifest(
            new PluginId(YokonexStableIds.Vendor),
            "役次元厂商适配",
            new Version(1, 0),
            new HostApiVersionRange(new Version(1, 0), new Version(1, 0)));
        _contribution = YokonexCatalog.CreateContribution();
    }

    public PluginManifest Manifest { get; }

    public DevicePluginContribution Describe() => _contribution;
}
