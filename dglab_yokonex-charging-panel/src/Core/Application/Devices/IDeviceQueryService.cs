using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Devices.Catalog;
using ChargingPanel.Core.Devices.Registry;

namespace ChargingPanel.Core.Application.Devices;

/// <summary>
/// 活动实例的只读视图。只暴露业务模块需要的能力与 Session 信息，
/// 不暴露 Driver 引用，避免业务代码触碰传输实现。
/// </summary>
public sealed record ActiveDeviceView
{
    public ActiveDeviceView(
        DeviceInstanceId instanceId,
        long sessionEpoch,
        IReadOnlyList<CapabilityDescriptor> capabilities)
    {
        _ = instanceId.Value;
        if (sessionEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionEpoch));
        }

        ArgumentNullException.ThrowIfNull(capabilities);
        InstanceId = instanceId;
        SessionEpoch = sessionEpoch;
        Capabilities = capabilities;
    }

    public DeviceInstanceId InstanceId { get; }

    public long SessionEpoch { get; }

    public IReadOnlyList<CapabilityDescriptor> Capabilities { get; }

    public CapabilityDescriptor? FindCapability(CapabilityId capabilityId) =>
        Capabilities.FirstOrDefault(capability => capability.Id == capabilityId);
}

/// <summary>
/// 最窄的设备存在性查询：只回答"是否存在已注册/已配置设备"。
///
/// 单独抽出该接口是为了让迁移期的调用方（如 Room）既不依赖全局 AppServices，
/// 也不必为了满足更宽的查询接口而把 legacy 设备伪造成 v2 DeviceInstance 身份。
/// 语义对齐旧行为：包含已配置但当前未连接的设备，不等于"已连接"。
/// </summary>
public interface IDevicePresenceQuery
{
    bool HasRegisteredDevices();
}

public interface IDeviceQueryService : IDevicePresenceQuery
{
    IReadOnlyList<DeviceModelDescriptor> GetModels(bool includeHiddenReserved = false);

    IReadOnlyList<CatalogDiagnostic> GetCatalogDiagnostics();

    IReadOnlyList<DeviceInstance> GetDeviceSnapshot();

    /// <summary>
    /// 当前具有活动 Session 的设备视图。没有连接任何设备时返回空集合——
    /// 这是真实状态而非占位，业务模块必须如实处理"无可用目标"。
    /// </summary>
    IReadOnlyList<ActiveDeviceView> GetActiveDevices();

    /// <summary>按实例 ID 解析活动设备；无活动 Session 时返回 null。</summary>
    ActiveDeviceView? GetActiveDevice(DeviceInstanceId instanceId);
}
