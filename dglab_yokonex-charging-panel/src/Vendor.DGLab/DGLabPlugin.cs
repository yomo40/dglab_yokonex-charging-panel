using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Vendor.DGLab.Catalog;
using ChargingPanel.Vendor.DGLab.Common;

namespace ChargingPanel.Vendor.DGLab;

/// <summary>
/// DG-LAB 厂商插件入口。贡献由 <see cref="DGLabCatalog"/> 汇总。
/// </summary>
public sealed class DGLabPlugin : IDevicePlugin
{
    private readonly DevicePluginContribution _contribution;

    public DGLabPlugin()
    {
        Manifest = new PluginManifest(
            new PluginId(DGLabStableIds.Vendor),
            "DG-LAB 厂商适配",
            new Version(1, 0),
            new HostApiVersionRange(new Version(1, 0), new Version(1, 0)));
        _contribution = DGLabCatalog.CreateContribution();
    }

    public PluginManifest Manifest { get; }

    public DevicePluginContribution Describe() => _contribution;
}