using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Core.Devices.Runtime;

namespace ChargingPanel.Core.Tests;

/// <summary>
/// 固定绑定的运行时注册表测试替身，仅存在于测试项目。
/// </summary>
internal sealed class StaticRuntimeRegistry : IDeviceRuntimeRegistry
{
    private readonly Dictionary<DeviceInstanceId, DeviceRuntimeBinding> _bindings = [];

    public StaticRuntimeRegistry(
        DeviceInstanceId instanceId,
        IProtocolDriver driver,
        CapabilityDescriptor capability,
        long sessionEpoch = 0)
    {
        _bindings[instanceId] = new DeviceRuntimeBinding(instanceId, driver, [capability], sessionEpoch);
    }

    public IReadOnlyList<DeviceInstanceId> ActiveInstanceIds => _bindings.Keys.ToArray();

    public void Add(
        DeviceInstanceId instanceId,
        IProtocolDriver driver,
        CapabilityDescriptor capability,
        long sessionEpoch = 0)
    {
        _bindings[instanceId] = new DeviceRuntimeBinding(instanceId, driver, [capability], sessionEpoch);
    }

    public void Remove(DeviceInstanceId instanceId) => _bindings.Remove(instanceId);

    public DeviceRuntimeBinding? Resolve(DeviceInstanceId instanceId) =>
        _bindings.TryGetValue(instanceId, out var binding) ? binding : null;
}
