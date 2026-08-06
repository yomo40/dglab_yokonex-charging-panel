using System.Collections.ObjectModel;
using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Core.Application.Devices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChargingPanel.Desktop.ViewModels.Devices;

/// <summary>
/// 设备目录视图模型。只读展示厂商中立模型清单，
/// 不暴露任何 legacy 适配器或传输实现。
/// 前端永远不展示 HiddenReserved 模型。
/// </summary>
public sealed partial class DeviceCatalogViewModel : ObservableObject
{
    private readonly IDeviceQueryService _query;

    public DeviceCatalogViewModel(IDeviceQueryService query)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
    }

    public ObservableCollection<DeviceModelItem> Models { get; } = new();

    public ObservableCollection<string> Diagnostics { get; } = new();

    /// <summary>
    /// 重新拉取目录。只暴露 Public 模型；
    /// HiddenReserved 与 UpgradeDiagnostic 模型不会作为可添加项出现。
    /// </summary>
    public void Refresh()
    {
        Models.Clear();
        foreach (var model in _query.GetModels(includeHiddenReserved: false))
        {
            Models.Add(new DeviceModelItem(model));
        }

        Diagnostics.Clear();
        foreach (var diagnostic in _query.GetCatalogDiagnostics())
        {
            Diagnostics.Add(
                $"{diagnostic.PluginId.Value}: {diagnostic.Code} {diagnostic.Message} ({diagnostic.ConflictingStableId})");
        }
    }
}

/// <summary>目录模型行。只承载展示所需字段。</summary>
public sealed class DeviceModelItem
{
    public DeviceModelItem(DeviceModelDescriptor model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
    }

    public DeviceModelDescriptor Model { get; }

    public string Id => Model.Id.Value;

    public string VendorId => Model.VendorId.Value;

    public string DisplayName => Model.DisplayName;

    public DeviceAvailability Availability => Model.Availability;
}
