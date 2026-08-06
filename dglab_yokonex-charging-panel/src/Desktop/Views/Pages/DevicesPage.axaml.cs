using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Core.Application.Devices;
using ChargingPanel.Core.Devices.Catalog;
using ChargingPanel.Core.Devices.Discovery;
using ChargingPanel.Core.Devices.Registry;
using ChargingPanel.Desktop.ViewModels.Devices;
using Serilog;
using ChargingPanel.Desktop.Bootstrap;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ChargingPanel.Desktop.Views.Pages;

/// <summary>
/// 设备连接页。只通过 <see cref="DeviceCatalogViewModel"/> /
/// <see cref="DeviceDiscoveryViewModel"/> / <see cref="DeviceInstanceViewModel"/>
/// 访问设备目录、发现与实例列表。绝不构造传输对象或引用厂商适配器。
/// </summary>
public partial class DevicesPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<DevicesPage>();
    private const string ColorTextPrimary = "#E6EDF7";
    private const string ColorTextMuted = "#9AB0C8";
    private const string ColorPanelBorder = "#2B3B55";

    private readonly DeviceCatalogViewModel _catalogViewModel;
    private readonly DeviceDiscoveryViewModel _discoveryViewModel;
    private readonly IDeviceQueryService _query;
    private readonly ObservableCollection<DeviceInstanceViewModel> _instances = new();

    public DevicesPage()
    {
        InitializeComponent();

        _query = ResolveQueryService();
        _catalogViewModel = new DeviceCatalogViewModel(_query);
        _discoveryViewModel = new DeviceDiscoveryViewModel(ResolveDiscoveryCoordinator(), _query);

        DeviceListPanel.ItemsSource = _instances;
        CatalogList.ItemsSource = _catalogViewModel.Models;
        DiscoveryResultsPanel.ItemsSource = _discoveryViewModel.Results;

        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        RefreshAll();
    }

    private void RefreshAll()
    {
        _catalogViewModel.Refresh();
        RefreshInstances();
        StatusText.Text = "已刷新设备目录";
    }

    private void RefreshInstances()
    {
        _instances.Clear();
        foreach (var instance in _query.GetDeviceSnapshot())
        {
            _instances.Add(new DeviceInstanceViewModel(instance, _query));
        }

        NoDevicesHint.IsVisible = _instances.Count == 0;
    }

    private void OnDiscoverClick(object? sender, RoutedEventArgs e)
    {
        var name = DiscoveryNameInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText.Text = "请输入设备名称（例如 47L121000）";
            return;
        }

        try
        {
            var advertisement = new DiscoveryAdvertisement(
                TransportKind.BluetoothLowEnergy,
                name,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["address"] = "AA:BB:CC:DD:EE:FF",
                    ["serviceUuids"] = "0000180c-0000-1000-8000-00805f9b34fb"
                });
            var item = _discoveryViewModel.HandleAdvertisement(advertisement);
            if (item.IsAddable)
            {
                RefreshInstances();
            }

            StatusText.Text = _discoveryViewModel.StatusMessage;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "发现设备失败");
            StatusText.Text = $"发现失败: {ex.Message}";
        }
    }

    private void OnRefreshCatalogClick(object? sender, RoutedEventArgs e)
    {
        RefreshAll();
    }

    private static IDeviceQueryService ResolveQueryService()
    {
        return ApplicationHost.TryGetService<IDeviceQueryService>()
            ?? new DeviceQueryService(
                new DeviceCatalog(Array.Empty<IDevicePlugin>()),
                new DeviceRegistry(new DeviceCatalog(Array.Empty<IDevicePlugin>())));
    }

    /// <summary>
    /// 迁移期：页面拿不到组合根目录，使用空目录协调器——任何发现都如实
    /// 返回 NoMatch，绝不伪造可添加设备。T22 删除 legacy 运行时后由 DI 注入。
    /// </summary>
    private static DiscoveryCoordinator ResolveDiscoveryCoordinator()
    {
        var catalog = new DeviceCatalog(Array.Empty<IDevicePlugin>());
        return new DiscoveryCoordinator(catalog, new DeviceRegistry(catalog));
    }

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}
