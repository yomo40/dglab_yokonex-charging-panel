using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Application.Devices;
using ChargingPanel.Core.Devices.Registry;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChargingPanel.Desktop.ViewModels.Devices;

/// <summary>
/// 设备实例视图模型。包裹 <see cref="DeviceInstance"/> 的只读快照，
/// 并通过查询服务判断当前是否具有活动 Session。
/// 不持有 Driver，也不构造任何传输对象。
/// </summary>
public sealed partial class DeviceInstanceViewModel : ObservableObject
{
    private readonly IDeviceQueryService _query;

    public DeviceInstanceViewModel(DeviceInstance instance, IDeviceQueryService query)
    {
        Instance = instance ?? throw new ArgumentNullException(nameof(instance));
        _query = query ?? throw new ArgumentNullException(nameof(query));
    }

    public DeviceInstance Instance { get; }

    public DeviceInstanceId Id => Instance.Id;

    public string IdText => Instance.Id.Value.ToString("D");

    public string PluginId => Instance.PluginId.Value;

    public string ModelId => Instance.ModelId.Value;

    public string ProfileId => Instance.ProfileId.Value;

    public DeviceInstanceStatus Status => Instance.Status;

    /// <summary>当前是否具有活动 Session（真实连接状态，而非占位）。</summary>
    public bool IsActive => _query.GetActiveDevice(Instance.Id) is not null;

    public string StatusText => IsActive
        ? "已连接"
        : Status switch
        {
            DeviceInstanceStatus.PluginUnavailable => "插件不可用",
            _ => "未连接"
        };

    public string TransportText => Instance.Endpoints.Count == 0
        ? string.Empty
        : string.Join(", ", Instance.Endpoints.Select(endpoint => endpoint.TransportKind.ToString()));
}

/// <summary>
/// 目录可添加模型与发现结果共用的"行"基类。
/// 发现命中 UpgradeDiagnostic 时只呈现升级提示卡片，不产生可添加实例。
/// </summary>
public abstract class DeviceCatalogRow
{
    protected DeviceCatalogRow(DeviceModelDescriptor? model)
    {
        Model = model;
    }

    public DeviceModelDescriptor? Model { get; }

    public abstract string Title { get; }

    public abstract string Subtitle { get; }
}
