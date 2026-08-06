using System.Reflection;
using System.Runtime.Loader;
using ChargingPanel.Contracts.Plugins;

namespace ChargingPanel.Infrastructure.Windows.Plugins;

/// <summary>
/// 单个第三方插件的独立 AssemblyLoadContext。
///
/// 信任模型 C 明确说明：ALC 不是安全沙箱，无法隔离恶意 DLL。
/// 它只负责按目录确定性解析依赖，并把宿主 Contracts 强制解析到宿主唯一程序集，
/// 避免插件打包的第二份 Contracts 造成类型身份分裂。
///
/// v1 不做热卸载（<see cref="AssemblyLoadContext.Unload"/>），
/// 因此本上下文在整个进程生命周期内保持存活。
/// </summary>
public sealed class DevicePluginLoadContext : AssemblyLoadContext
{
    private static readonly string ContractsAssemblyName = typeof(IPluginContext).Assembly.GetName().Name!;

    private readonly string _pluginDirectory;

    public DevicePluginLoadContext(string pluginDirectory)
        : base($"ChargingPanel.Plugin:{Path.GetFileName(pluginDirectory)}", isCollectible: false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        _pluginDirectory = pluginDirectory;
    }

    public string PluginDirectory => _pluginDirectory;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);

        // 强制 Contracts 解析到宿主唯一程序集；忽略插件目录里打包的第二份副本。
        if (string.Equals(assemblyName.Name, ContractsAssemblyName, StringComparison.Ordinal))
        {
            return typeof(IPluginContext).Assembly;
        }

        // 其余依赖只从插件目录按文件名解析；找不到则回退默认上下文。
        var candidate = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
        if (File.Exists(candidate))
        {
            return LoadFromAssemblyPath(candidate);
        }

        return null;
    }
}
