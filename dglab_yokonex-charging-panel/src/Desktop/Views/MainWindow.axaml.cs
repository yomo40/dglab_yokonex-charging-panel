using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ChargingPanel.Core;
using ChargingPanel.Core.Data;
using ChargingPanel.Desktop.Views.Pages;
using Serilog;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace ChargingPanel.Desktop.Views;

public partial class MainWindow : Window
{
    private static readonly ILogger Logger = Log.ForContext<MainWindow>();
    
    private string _currentPage = "Overview";
    private Button? _activeNavButton;
    private readonly DispatcherTimer _deviceStatusDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _pendingTopStatusRefresh;
    private bool _shutdownCleanupStarted;
    
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
    private SensorPage? _sensorPage;
    private WebSocketServicePage? _webSocketServicePage;
    private SkinPage? _skinPage;
    private DocsPage? _docsPage;

    public MainWindow()
    {
        InitializeComponent();
        _deviceStatusDebounceTimer.Tick += OnDeviceStatusDebounceTick;
        
        // 初始化服务
        _ = InitializeServicesAsync();
        
        // 设置初始活动按钮
        _activeNavButton = NavOverview;

        // 版本徽标与程序集版本同步，避免手动改 XAML。
        AppVersionBadge.Text = $"v{ResolveAppVersion()}";
        ApplySkinPreferences();
        
        // 加载初始页面
        SwitchPage("Overview");
        
        Logger.Information("MainWindow initialized");
    }

    private static string ResolveAppVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version == null)
        {
            return "--";
        }

        return $"{version.Major}.{version.Minor}.{version.Build}";
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

            // 服务初始化后再应用一次皮肤，确保数据库配置生效。
            ApplySkinPreferences();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to initialize services");
            ShowStatus($"初始化失败: {ex.Message}");
        }
    }

    private void OnDeviceStatusChanged(object? sender, Core.Devices.DeviceStatusChangedEventArgs e)
    {
        RequestTopStatusRefresh();
    }

    private void RequestTopStatusRefresh()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RequestTopStatusRefresh);
            return;
        }

        _pendingTopStatusRefresh = true;
        if (!_deviceStatusDebounceTimer.IsEnabled)
        {
            _deviceStatusDebounceTimer.Start();
        }
    }

    private void OnDeviceStatusDebounceTick(object? sender, EventArgs e)
    {
        _deviceStatusDebounceTimer.Stop();
        if (!_pendingTopStatusRefresh || !AppServices.IsInitialized)
        {
            return;
        }

        _pendingTopStatusRefresh = false;
        try
        {
            var connectedCount = AppServices.Instance.DeviceManager.GetConnectedDevices().Count;
            if (connectedCount > 0)
            {
                TopStatusIndicator.Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#10B981"));
                TopStatusText.Text = $"已连接 {connectedCount} 个设备";
            }
            else
            {
                TopStatusIndicator.Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8091A8"));
                TopStatusText.Text = "未连接设备";
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "更新顶部设备状态失败");
        }
    }

    public void ShowStatus(string message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            TopStatusText.Text = message;
        });
    }

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string pageName)
            return;

        NavigateToPage(pageName, button);
    }

    public void NavigateToPage(string pageName)
    {
        NavigateToPage(pageName, GetNavButtonByPage(pageName));
    }

    private void NavigateToPage(string pageName, Button? navButton)
    {
        if (pageName == _currentPage)
            return;

        var previousButton = _activeNavButton;
        try
        {
            SwitchPage(pageName);

            if (_activeNavButton != null)
            {
                _activeNavButton.Classes.Remove("nav-active");
                _activeNavButton.Classes.Add("nav");
            }

            if (navButton != null)
            {
                navButton.Classes.Remove("nav");
                navButton.Classes.Add("nav-active");
                _activeNavButton = navButton;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "导航页面失败: {Page}", pageName);
            ShowStatus($"页面加载失败: {pageName}");

            // 恢复原激活按钮，避免 UI 状态与当前页面不一致。
            _activeNavButton = previousButton;
            if (_activeNavButton != null)
            {
                _activeNavButton.Classes.Remove("nav");
                _activeNavButton.Classes.Add("nav-active");
            }
        }
    }

    private Button? GetNavButtonByPage(string pageName)
    {
        return pageName switch
        {
            "Overview" => NavOverview,
            "Devices" => NavDevices,
            "Control" => NavControl,
            "Sensor" => NavSensor,
            "Events" => NavEvents,
            "Plugins" => NavPlugins,
            "OCR" => NavOCR,
            "Room" => NavRoom,
            "Logs" => NavLogs,
            "WebSocket" => NavWebSocket,
            "Skins" => NavSkins,
            "Docs" => NavDocs,
            "Settings" => NavSettings,
            _ => null
        };
    }

    private void SwitchPage(string pageName)
    {
        // 更新标题
        (PageTitle.Text, PageDescription.Text) = pageName switch
        {
            "Overview" => ("概览", "设备状态总览与快捷控制"),
            "Devices" => ("设备连接", "添加和管理设备连接"),
            "Control" => ("设备控制", "实时控制设备并维护默认参数"),
            "OCR" => ("血量识别", "配置血量识别区域和参数"),
            "Events" => ("触发规则", "配置游戏事件、OCR、传感器触发动作"),
            "Sensor" => ("传感器", "配置设备传感器的触发规则"),
            "Plugins" => ("游戏适配", "管理游戏适配脚本"),
            "Room" => ("多人房间", "创建或加入房间与他人互动"),
            "Logs" => ("日志", "查看软件运行日志"),
            "WebSocket" => ("内置服务器", "管理郊狼 Socket 服务与 MOD 接入服务"),
            "Skins" => ("皮肤模块", "自定义主题、颜色、图标和字体"),
            "Docs" => ("使用文档", "在客户端内查看完整使用说明"),
            "Settings" => ("系统设置", "应用程序全局设置"),
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
            "Sensor" => _sensorPage ??= new SensorPage(),
            "Plugins" => _pluginsPage ??= new PluginsPage(),
            "Room" => _roomPage ??= new RoomPage(),
            "Logs" => _logsPage ??= new LogsPage(),
            "WebSocket" => _webSocketServicePage ??= new WebSocketServicePage(),
            "Skins" => _skinPage ??= new SkinPage(),
            "Docs" => _docsPage ??= new DocsPage(),
            "Settings" => _settingsPage ??= new SettingsPage(),
            _ => _overviewPage ??= new OverviewPage()
        };
        
        // 简单页面过渡：切页时淡入，减少突兀闪烁感。
        PageContainer.Opacity = 0;
        PageContainer.Child = page;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => PageContainer.Opacity = 1,
            Avalonia.Threading.DispatcherPriority.Render);
        
        // 保存按钮在部分需要持久化配置的页面可见
        GlobalSaveButton.IsVisible = pageName is "Settings" or "Control" or "WebSocket" or "Skins";
        _currentPage = pageName;
        
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
                case "Control":
                    _controlPage?.SaveDeviceDefaults();
                    break;
                case "WebSocket":
                    _webSocketServicePage?.SaveSettings();
                    break;
                case "Skins":
                    _skinPage?.SaveAndApply();
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
                    _controlPage?.SaveDeviceDefaults();
                    _webSocketServicePage?.SaveSettings();
                    _skinPage?.SaveAndApply();
                    _ocrPage?.SaveSettings();
                    _eventsPage?.SaveSettings();
                    break;
            }
            
            ShowStatus("设置已保存 ");
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

        // 不要在 UI 线程同步执行重清理，否则会卡住窗口关闭（表现为点击 X 无法退出）。
        _deviceStatusDebounceTimer.Stop();
        if (AppServices.IsInitialized)
        {
            AppServices.Instance.DeviceManager.DeviceStatusChanged -= OnDeviceStatusChanged;
        }

        BeginBestEffortCleanup();
    }

    private void BeginBestEffortCleanup()
    {
        if (_shutdownCleanupStarted)
        {
            return;
        }

        _shutdownCleanupStarted = true;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!AppServices.IsInitialized)
                {
                    return;
                }

                var stopTask = AppServices.Instance.StopAsync();
                var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                if (completed != stopTask)
                {
                    Logger.Warning("Service stop timed out during window closing; shutdown continues.");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Best-effort service stop failed during window closing");
            }

            try
            {
                if (AppServices.IsInitialized)
                {
                    AppServices.Instance.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Best-effort dispose failed during window closing");
            }
        });
    }

        public void ApplySkinPreferences()
    {
        try
        {
            if (!AppServices.IsInitialized)
            {
                return;
            }

            var db = Database.Instance;
            var accentHex = db.GetSetting<string>("ui.skin.accent", "#0EA5E9") ?? "#0EA5E9";
            var windowBgHex = db.GetSetting<string>("ui.skin.windowBackground", "#0B1421") ?? "#0B1421";
            var backgroundImagePath = db.GetSetting<string>("ui.skin.backgroundImagePath", "") ?? "";
            var overlayHex = db.GetSetting<string>("ui.skin.backgroundOverlayColor", windowBgHex) ?? windowBgHex;
            var overlayOpacity = db.GetSetting<int>("ui.skin.backgroundOverlayOpacity", 70);
            var textBoxOpacity = db.GetSetting<int>("ui.skin.textBoxOpacity", 100);
            var fontFamily = db.GetSetting<string>("ui.skin.fontFamily", "") ?? "";
            var logoIcon = db.GetSetting<string>("ui.skin.logoIcon", "🐺") ?? "🐺";

            if (TryParseColor(windowBgHex, out var windowColor))
            {
                Background = new SolidColorBrush(windowColor);
            }

            ApplyBackgroundSkin(backgroundImagePath, overlayHex, overlayOpacity, windowBgHex);

            if (!string.IsNullOrWhiteSpace(fontFamily))
            {
                FontFamily = new FontFamily(fontFamily);
            }

            if (!string.IsNullOrWhiteSpace(logoIcon))
            {
                AppLogoIcon.Text = logoIcon;
            }

            if (TryParseColor(accentHex, out var accentColor))
            {
                var accentBrush = new SolidColorBrush(accentColor);
                Resources["SkinAccentBrush"] = accentBrush;
                AppVersionBadge.Foreground = accentBrush;
                AppVersionBadgeContainer.Background = new SolidColorBrush(Color.FromArgb(0x30, accentColor.R, accentColor.G, accentColor.B));
            }

            Resources["SkinTextBoxOpacity"] = Math.Clamp(textBoxOpacity, 0, 100) / 100d;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to apply skin preferences");
        }
    }

    private void ApplyBackgroundSkin(string imagePath, string overlayHex, int overlayOpacity, string fallbackHex)
    {
        try
        {
            SkinBackgroundLayer.Background = Brushes.Transparent;
            if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
            {
                SkinBackgroundLayer.Background = new ImageBrush(new Bitmap(imagePath))
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                };
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to apply background image: {Path}", imagePath);
            SkinBackgroundLayer.Background = Brushes.Transparent;
        }

        if (!TryParseColor(overlayHex, out var overlayColor) &&
            !TryParseColor(fallbackHex, out overlayColor))
        {
            overlayColor = Color.Parse("#0B1421");
        }

        var clampedOpacity = Math.Clamp(overlayOpacity, 0, 100);
        var alpha = (byte)Math.Round(255 * (clampedOpacity / 100.0));
        SkinOverlayLayer.Background = new SolidColorBrush(
            Color.FromArgb(alpha, overlayColor.R, overlayColor.G, overlayColor.B));
    }

    private static bool TryParseColor(string? value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            color = Color.Parse(value);
            return true;
        }
        catch
        {
            return false;
        }
    }
}



