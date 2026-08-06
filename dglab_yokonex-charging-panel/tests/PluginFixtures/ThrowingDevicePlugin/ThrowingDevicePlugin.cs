using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;

namespace PluginFixtures.Throwing;

/// <summary>
/// 构造抛错插件：构造函数立即抛出，用于验证宿主隔离单插件故障，
/// 且不阻止其他插件加载。
/// </summary>
public sealed class ThrowingDevicePlugin : IDevicePlugin
{
    public const string PluginIdValue = "fixtures.throwing";

    public ThrowingDevicePlugin() =>
        throw new InvalidOperationException("测试插件构造抛错。");

    public PluginManifest Manifest =>
        throw new InvalidOperationException("测试插件不应被读取 Manifest。");

    public DevicePluginContribution Describe() =>
        throw new InvalidOperationException("测试插件不应被调用 Describe。");
}
