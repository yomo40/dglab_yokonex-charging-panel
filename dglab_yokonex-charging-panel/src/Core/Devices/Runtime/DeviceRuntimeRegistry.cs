using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.Core.Devices.Runtime;

/// <summary>
/// 运行时绑定注册表。只登记"已建立 Session 且拥有 Driver"的实例。
///
/// 空集合是真实状态而非占位：没有任何设备连接时，
/// 急停枚举到零个目标、Dispatcher 解析不到绑定，都是正确结果。
/// Session 建立后由 Session 拥有方登记，断开时必须注销，
/// 否则急停会向已失效的 Driver 下发命令。
/// </summary>
public sealed class DeviceRuntimeRegistry : IDeviceRuntimeRegistry
{
    private readonly ConcurrentDictionary<DeviceInstanceId, DeviceRuntimeBinding> _bindings = new();

    public IReadOnlyList<DeviceInstanceId> ActiveInstanceIds =>
        new ReadOnlyCollection<DeviceInstanceId>(_bindings.Keys.ToArray());

    public DeviceRuntimeBinding? Resolve(DeviceInstanceId instanceId) =>
        _bindings.TryGetValue(instanceId, out var binding) ? binding : null;

    /// <summary>
    /// 登记或替换实例绑定。故障转移会以新 SessionEpoch 重新登记同一实例。
    /// </summary>
    public void Register(DeviceRuntimeBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        _bindings[binding.InstanceId] = binding;
    }

    /// <summary>注销实例绑定。Session 断开后必须调用，避免向失效 Driver 下发命令。</summary>
    public bool Unregister(DeviceInstanceId instanceId) => _bindings.TryRemove(instanceId, out _);
}
