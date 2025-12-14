using Avalonia.Controls;
using Avalonia.Interactivity;
using ChargingPanel.Core;
using ChargingPanel.Desktop.Views.Pages;
using Serilog;
using System;
using System.Threading.Tasks;

namespace ChargingPanel.Desktop.Views;

public partial class MainWindow : Window
{
    private static readonly ILogger Logger = Log.ForContext<MainWindow>();
    
    private string _currentPage = "Overview";
    private Button? _activeNavButton;
    
    // 页面实例缓存
    private OverviewPage? _overviewPage;
    private DevicesPage? _devicesPage;
    private ControlPage? _controlPage;
    private OCRPage? _ocrPage;
    private EventsPage? _eventsPage;
    private PluginsPage? _pluginsPage;
    private LogsPage? _logsPage;
    private SettingsPage? _settingsPage;
    private RoomPage? _roomPage;

    public MainWindow()
    {
        InitializeComponent();
        
        // 初始化服务
        _ = InitializeServicesAsync();
        
        // 设置初始活动按钮
        _activeNavButton = NavOverview;
        
        // 加载初始页面
        SwitchPage("Overview");
        
        Logger.Information("MainWindow initialized");
    }

    private async Task InitializeServicesAsync()
    {
        try
        {
            // 初始化 AppServices
            AppServices.Initialize();
            await AppServices.Instance.StartAsync();
            
            Logger.Information("App services initialized");
            
            // 绑定设备事件
            AppServices.Instance.DeviceManager.DeviceStatusChanged += OnDeviceStatusChanged;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to initialize services");
            ShowStatus($"初始化失败: {ex.Message}");
        }
    }

    private void OnDeviceStatusChanged(object? sender, Core.Devices.DeviceStatusChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var connectedCount = AppServices.Instance.DeviceManager.GetConnectedDevices().Count;
            if (connectedCount > 0)
            {
                StatusIndicator.Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#10B981"));
                StatusText.Text = $"已连接 {connectedCount} 个设备";
            }
            else
            {
                StatusIndicator.Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#6B7280"));
                StatusText.Text = "未连接设备";
            }
        });
    }

    public void ShowStatus(string message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = message;
        });
    }

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string pageName)
            return;
        
        if (pageName == _currentPage)
            return;
        
        // 更新导航按钮样式
        if (_activeNavButton != null)
        {
            _activeNavButton.Classes.Remove("nav-active");
            _activeNavButton.Classes.Add("nav");
        }
        
        button.Classes.Remove("nav");
        button.Classes.Add("nav-active");
        _activeNavButton = button;
        
        // 切换页面
        SwitchPage(pageName);
    }

    private void SwitchPage(string pageName)
    {
        _currentPage = pageName;
        
        // 更新标题
        (PageTitle.Text, PageDescription.Text) = pageName switch
        {
            "Overview" => ("概览", "设备状态总览与快捷控制"),
            "Devices" => ("设备管理", "添加和管理你的设备"),
            "Control" => ("设备控制", "实时控制设备输出"),
            "OCR" => ("血量识别", "配置血量识别区域和参数"),
            "Events" => ("电击调节", "配置血量变化触发的电击规则"),
            "Plugins" => ("游戏适配", "管理游戏适配脚本"),
            "Room" => ("多人房间", "创建或加入房间与他人互动"),
            "Logs" => ("动作日志", "查看设备动作历史记录"),
            "Settings" => ("系统设置", "应用程序设置"),
            _ => ("概览", "设备状态总览与快捷控制")
        };
        
        // 加载页面
        Control page = pageName switch
        {
            "Overview" => _overviewPage ??= new OverviewPage(),
            "Devices" => _devicesPage ??= new DevicesPage(),
            "Control" => _controlPage ??= new ControlPage(),
            "OCR" => _ocrPage ??= new OCRPage(),
            "Events" => _eventsPage ??= new EventsPage(),
            "Plugins" => _pluginsPage ??= new PluginsPage(),
            "Room" => _roomPage ??= new RoomPage(),
            "Logs" => _logsPage ??= new LogsPage(),
            "Settings" => _settingsPage ??= new SettingsPage(),
            _ => _overviewPage ??= new OverviewPage()
        };
        
        PageContainer.Child = page;
        
        // 保存按钮只在设置/OCR/Events页面可见
        GlobalSaveButton.IsVisible = pageName is "Settings" or "OCR" or "Events";
        
        Logger.Debug("Switched to page: {Page}", pageName);
    }

    private async void OnEmergencyStopClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Logger.Warning("Emergency stop triggered!");
            ShowStatus("紧急停止!");
            
            if (AppServices.IsInitialized)
            {
                // 停止所有设备
                await AppServices.Instance.DeviceManager.EmergencyStopAllAsync();
                
                // 停止 OCR 服务
                AppServices.Instance.OCRService.Stop();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to emergency stop");
        }
    }
    
    private void OnGlobalSaveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            // 根据当前页面调用保存逻辑
            switch (_currentPage)
            {
                case "Settings":
                    _settingsPage?.SaveSettings();
                    break;
                case "OCR":
                    _ocrPage?.SaveSettings();
                    break;
                case "Events":
                    _eventsPage?.SaveSettings();
                    break;
                default:
                    // 保存所有设置
                    _settingsPage?.SaveSettings();
                    _ocrPage?.SaveSettings();
                    _eventsPage?.SaveSettings();
                    break;
            }
            
            ShowStatus("设置已保存 ✓");
            Logger.Information("Settings saved from global button");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to save settings");
            ShowStatus($"保存失败: {ex.Message}");
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        
        // 清理资源
        try
        {
            if (AppServices.IsInitialized)
            {
                AppServices.Instance.Dispose();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error during cleanup");
        }
    }
}
