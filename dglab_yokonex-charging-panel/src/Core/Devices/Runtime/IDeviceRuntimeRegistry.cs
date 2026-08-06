using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;

namespace ChargingPanel.Core.Devices.Runtime;

/// <summary>
/// 运行时实例查询端口。Core 只依赖该抽象，不感知 Driver 具体来源与传输实现。
/// </summary>
public interface IDeviceRuntimeRegistry
{
    /// <summary>当前已知的活动实例；急停必须覆盖其中每一个。</summary>
    IReadOnlyList<DeviceInstanceId> ActiveInstanceIds { get; }

    /// <summary>解析实例的运行时绑定；实例不存在或无 Session 时返回 null。</summary>
    DeviceRuntimeBinding? Resolve(DeviceInstanceId instanceId);
}

/// <summary>
/// 实例的运行时绑定：Driver、能力声明与当前 SessionEpoch。
/// </summary>
public sealed record DeviceRuntimeBinding
{
    public DeviceRuntimeBinding(
        DeviceInstanceId instanceId,
        IProtocolDriver driver,
        IReadOnlyList<CapabilityDescriptor> capabilities,
        long sessionEpoch)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (sessionEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionEpoch));
        }

        InstanceId = instanceId;
        Driver = driver;
        Capabilities = capabilities;
        SessionEpoch = sessionEpoch;
    }

    public DeviceInstanceId InstanceId { get; }

    public IProtocolDriver Driver { get; }

    public IReadOnlyList<CapabilityDescriptor> Capabilities { get; }

    public long SessionEpoch { get; }
}
