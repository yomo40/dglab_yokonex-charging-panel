using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using ChargingPanel.Core;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Events;
using ChargingPanel.Core.OCR;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChargingPanel.Desktop.ViewModels;

/// <summary>
/// 主窗口 ViewModel
/// </summary>
public partial class MainViewModel : ObservableObject
{
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
    private int _currentBlood = 100;

    [ObservableProperty]
    private bool _isOCRRunning;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();
    public ObservableCollection<EventViewModel> RecentEvents { get; } = new();

    public MainViewModel()
    {
        // 初始化
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            StatusMessage = "正在初始化...";
            
            // 初始化应用服务
            var services = AppServices.Initialize();
            await services.StartAsync();

            // 订阅事件
            services.DeviceManager.DeviceStatusChanged += OnDeviceStatusChanged;
            services.EventService.EventTriggered += OnEventTriggered;
            services.OCRService.BloodChanged += OnBloodChanged;

            // 加载数据
            await RefreshDataAsync();
            
            StatusMessage = "就绪";
        }
        catch (Exception ex)
        {
            StatusMessage = $"初始化失败: {ex.Message}";
        }
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
            var services = AppServices.Instance;

            // 更新设备列表
            var devices = services.DeviceManager.GetAllDevices();
            Devices.Clear();
            foreach (var device in devices)
            {
                Devices.Add(new DeviceViewModel(device));
            }

            TotalDeviceCount = devices.Count;
            ConnectedDeviceCount = services.DeviceManager.GetConnectedDevices().Count;

            // 更新事件数量
            EnabledEventCount = services.EventService.GetEnabledEvents().Count;

            // 更新 OCR 状态
            IsOCRRunning = services.OCRService.IsRunning;
            CurrentBlood = services.OCRService.CurrentBlood;
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
            await AppServices.Instance.DeviceManager.EmergencyStopAllAsync();
            StatusMessage = "紧急停止已执行";
        }
        catch (Exception ex)
        {
            StatusMessage = $"紧急停止失败: {ex.Message}";
        }
    }

    private void OnDeviceStatusChanged(object? sender, DeviceStatusChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var device = Devices.FirstOrDefault(d => d.Id == e.DeviceId);
            if (device != null)
            {
                device.Status = e.Status;
            }
            ConnectedDeviceCount = AppServices.Instance.DeviceManager.GetConnectedDevices().Count;
        });
    }

    private void OnEventTriggered(object? sender, EventTriggeredEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RecentEvents.Insert(0, new EventViewModel
            {
                EventId = e.EventId,
                EventName = e.EventName,
                Action = e.Action,
                Value = e.Value,
                Channel = e.Channel,
                Timestamp = e.Timestamp
            });

            // 保留最近 20 条
            while (RecentEvents.Count > 20)
            {
                RecentEvents.RemoveAt(RecentEvents.Count - 1);
            }
        });
    }

    private void OnBloodChanged(object? sender, BloodChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CurrentBlood = e.CurrentBlood;
        });
    }
}

/// <summary>
/// 设备 ViewModel
/// </summary>
public partial class DeviceViewModel : ObservableObject
{
    public string Id { get; }
    
    [ObservableProperty]
    private string _name = "";
    
    [ObservableProperty]
    private DeviceType _type;
    
    [ObservableProperty]
    private DeviceStatus _status;
    
    [ObservableProperty]
    private int _strengthA;
    
    [ObservableProperty]
    private int _strengthB;
    
    [ObservableProperty]
    private int _limitA = 200;
    
    [ObservableProperty]
    private int _limitB = 200;
    
    [ObservableProperty]
    private bool _isVirtual;

    public string TypeName => Type == DeviceType.DGLab ? "郊狼" : "役次元";
    public string StatusText => Status switch
    {
        DeviceStatus.Connected => "已连接",
        DeviceStatus.Connecting => "连接中...",
        DeviceStatus.WaitingForBind => "等待绑定",
        DeviceStatus.Error => "错误",
        _ => "未连接"
    };

    public DeviceViewModel(DeviceInfo info)
    {
        Id = info.Id;
        Name = info.Name;
        Type = info.Type;
        Status = info.Status;
        IsVirtual = info.IsVirtual;
        
        if (info.State?.Strength != null)
        {
            StrengthA = info.State.Strength.ChannelA;
            StrengthB = info.State.Strength.ChannelB;
            LimitA = info.State.Strength.LimitA;
            LimitB = info.State.Strength.LimitB;
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        try
        {
            await AppServices.Instance.DeviceManager.ConnectDeviceAsync(Id);
        }
        catch { }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        try
        {
            await AppServices.Instance.DeviceManager.DisconnectDeviceAsync(Id);
        }
        catch { }
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        try
        {
            await AppServices.Instance.DeviceManager.RemoveDeviceAsync(Id);
        }
        catch { }
    }
}

/// <summary>
/// 事件 ViewModel
/// </summary>
public class EventViewModel
{
    public string EventId { get; set; } = "";
    public string EventName { get; set; } = "";
    public string Action { get; set; } = "";
    public int Value { get; set; }
    public string Channel { get; set; } = "";
    public DateTime Timestamp { get; set; }
    
    public string TimeText => Timestamp.ToString("HH:mm:ss");
}
