using System.Collections.ObjectModel;
using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Application.Devices;
using ChargingPanel.Core.Devices.Catalog;
using ChargingPanel.Core.Devices.Discovery;
using ChargingPanel.Core.Devices.Registry;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChargingPanel.Desktop.ViewModels.Devices;

/// <summary>
/// 设备发现视图模型。唯一的发现入口：把外部发现的广播交给
/// <see cref="DiscoveryCoordinator"/> 做目录匹配与实例注册。
/// 本类绝不构造传输对象（如 BLE 传输），也没有任何厂商适配器引用。
/// </summary>
public sealed partial class DeviceDiscoveryViewModel : ObservableObject
{
    private readonly DiscoveryCoordinator _coordinator;
    private readonly IDeviceQueryService _query;

    public DeviceDiscoveryViewModel(DiscoveryCoordinator coordinator, IDeviceQueryService query)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _query = query ?? throw new ArgumentNullException(nameof(query));
    }

    public ObservableCollection<DiscoveryResultItem> Results { get; } = new();

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// 处理一条发现广播。命中 UpgradeDiagnostic 时只产生升级提示卡片，
    /// 绝不生成可添加设备；命中 Public 模型时注册实例并返回可添加项。
    /// </summary>
    public DiscoveryResultItem HandleAdvertisement(DiscoveryAdvertisement advertisement)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        var result = _coordinator.Observe(advertisement);
        DiscoveryResultItem item;

        if (result.Match.Availability == DeviceAvailability.UpgradeDiagnostic)
        {
            item = DiscoveryResultItem.UpgradeDiagnostic(result.Match);
            StatusMessage = "发现需要升级的设备，仅显示升级提示。";
        }
        else if (result.DeviceInstance is not null)
        {
            item = DiscoveryResultItem.FromInstance(result.DeviceInstance, _query);
            StatusMessage = $"已注册设备：{item.Title}";
        }
        else
        {
            item = DiscoveryResultItem.NoMatch(result.Match);
            StatusMessage = "未匹配到任何目录模型。";
        }

        Results.Insert(0, item);
        return item;
    }
}

/// <summary>发现结果行。可以是可添加实例、升级诊断或无匹配提示。</summary>
public sealed class DiscoveryResultItem
{
    private DiscoveryResultItem(
        DeviceInstance? instance,
        DeviceModelDescriptor? model,
        string title,
        string subtitle,
        bool isUpgradeDiagnostic)
    {
        Instance = instance;
        Model = model;
        Title = title;
        Subtitle = subtitle;
        IsUpgradeDiagnostic = isUpgradeDiagnostic;
    }

    public DeviceInstance? Instance { get; }

    public DeviceModelDescriptor? Model { get; }

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>是否为升级诊断卡片。是则不是可添加设备。</summary>
    public bool IsUpgradeDiagnostic { get; }

    public bool IsAddable => Instance is not null && !IsUpgradeDiagnostic;

    public static DiscoveryResultItem UpgradeDiagnostic(CatalogMatchResult match)
    {
        var message = match.Diagnostics.Count > 0
            ? string.Join("；", match.Diagnostics.Select(diagnostic => diagnostic.Message))
            : "该设备需要升级适配后才能创建会话。";
        return new DiscoveryResultItem(
            instance: null,
            model: null,
            title: "需要升级的设备",
            subtitle: message,
            isUpgradeDiagnostic: true);
    }

    public static DiscoveryResultItem FromInstance(DeviceInstance instance, IDeviceQueryService query)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(query);
        var model = query.GetModels(includeHiddenReserved: true)
            .FirstOrDefault(candidate => candidate.Id == instance.ModelId);
        var title = model?.DisplayName ?? instance.ModelId.Value;
        var subtitle = $"{instance.Id.Value.ToString("D")} · {instance.ProfileId.Value}";
        return new DiscoveryResultItem(instance, model, title, subtitle, isUpgradeDiagnostic: false);
    }

    public static DiscoveryResultItem NoMatch(CatalogMatchResult match)
    {
        var message = match.Diagnostics.Count > 0
            ? string.Join("；", match.Diagnostics.Select(diagnostic => diagnostic.Message))
            : "未找到匹配的设备发现规则。";
        return new DiscoveryResultItem(
            instance: null,
            model: null,
            title: "未匹配设备",
            subtitle: message,
            isUpgradeDiagnostic: false);
    }
}
