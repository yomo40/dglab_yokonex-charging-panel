using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Application.Devices;
using ChargingPanel.Desktop.ViewModels.Devices;
using ChargingPanel.Desktop.Bootstrap;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChargingPanel.Desktop.ViewModels;

/// <summary>
/// 主窗口 ViewModel（备用壳）。只通过 <see cref="IDeviceQueryService"/> 与
/// <see cref="IDeviceApplicationService"/> 访问设备，不引用 legacy 设备实现。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IDeviceQueryService? _query;
    private readonly IDeviceApplicationService? _appService;

    [ObservableProperty]
    private string _currentPage = "Dashboard";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private int _connectedDeviceCount;

    [ObservableProperty]
    private int _totalDeviceCount;

    [ObservableProperty]
    private int _enabledEventCount;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    public ObservableCollection<DeviceInstanceViewModel> Devices { get; } = new();

    public MainViewModel()
    {
        _query = ApplicationHost.TryGetService<IDeviceQueryService>();
        _appService = ApplicationHost.TryGetService<IDeviceApplicationService>();
        _ = RefreshDataAsync();
    }

    [RelayCommand]
    private void NavigateTo(string page)
    {
        CurrentPage = page;
    }

    [RelayCommand]
    private async Task RefreshDataAsync()
    {
        try
        {
            if (_query is null)
            {
                StatusMessage = "设备查询服务不可用";
                return;
            }

            Devices.Clear();
            foreach (var instance in _query.GetDeviceSnapshot())
            {
                Devices.Add(new DeviceInstanceViewModel(instance, _query));
            }

            TotalDeviceCount = Devices.Count;
            ConnectedDeviceCount = _query.GetActiveDevices().Count;
            IsConnected = ConnectedDeviceCount > 0;
            StatusMessage = "就绪";
        }
        catch (Exception ex)
        {
            StatusMessage = $"刷新失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task EmergencyStopAsync()
    {
        try
        {
            if (_appService is null)
            {
                StatusMessage = "设备应用服务不可用";
                return;
            }

            await _appService.EmergencyStopAsync("主窗口急停", System.Threading.CancellationToken.None);
            StatusMessage = "紧急停止已执行";
        }
        catch (Exception ex)
        {
            StatusMessage = $"紧急停止失败: {ex.Message}";
        }
    }
}
