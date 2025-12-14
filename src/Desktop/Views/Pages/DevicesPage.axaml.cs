using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ChargingPanel.Core;
using ChargingPanel.Core.Bluetooth;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.DGLab;
using ChargingPanel.Core.Devices.Yokonex;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace ChargingPanel.Desktop.Views.Pages;

public partial class DevicesPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<DevicesPage>();
    
#pragma warning disable CS0414 // Field is assigned but never used - reserved for future use
    private bool _isDGLabTabActive = true;
    private bool _isDGLabWSMode = true;
    private bool _isYokonexIMMode = true;
    private bool _useOfficialServer = true;
#pragma warning restore CS0414
    private bool _isScanning = false;
    
    public ObservableCollection<DeviceViewModel> Devices { get; } = new();

    public DevicesPage()
    {
        InitializeComponent();
        
        // 延迟初始化，确保 AppServices 已完成初始化
        _ = InitializeAsync();
    }
    
    private async Task InitializeAsync()
    {
        // 等待 AppServices 初始化完成
        var retries = 0;
        while (!AppServices.IsInitialized && retries < 50)
        {
            await Task.Delay(100);
            retries++;
        }
        
        if (AppServices.IsInitialized)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                BindDeviceManagerEvents();
                RefreshDeviceList();
            });
        }
    }

    private void BindDeviceManagerEvents()
    {
        AppServices.Instance.DeviceManager.DeviceStatusChanged += OnDeviceStatusChanged;
    }

    private void OnDeviceStatusChanged(object? sender, DeviceStatusChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => RefreshDeviceList());
    }

    private void RefreshDeviceList()
    {
        if (!AppServices.IsInitialized) return;
        
        Devices.Clear();
        var devices = AppServices.Instance.DeviceManager.GetAllDevices();
        
        DeviceListPanel.Children.Clear();
        
        if (devices.Count == 0)
        {
            NoDevicesHint.IsVisible = true;
            return;
        }
        
        NoDevicesHint.IsVisible = false;
        
        foreach (var device in devices)
        {
            var card = CreateDeviceCard(device);
            DeviceListPanel.Children.Add(card);
        }
    }

    private Border CreateDeviceCard(DeviceInfo device)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1E1E2E")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(15),
            Margin = new Thickness(0, 0, 0, 10)
        };
        
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        
        var icon = new TextBlock
        {
            Text = device.Type == DeviceType.DGLab ? "⚡" : "📱",
            FontSize = 20,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(icon, 0);
        
        var info = new StackPanel();
        info.Children.Add(new TextBlock { Text = device.Name, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold });

        var statusColor = device.Status switch
        {
            DeviceStatus.Connected => "#10B981",
            DeviceStatus.Connecting => "#F59E0B",
            DeviceStatus.WaitingForBind => "#3B82F6",
            _ => "#6B7280"
        };
        var statusText = device.Status switch
        {
            DeviceStatus.Connected => "已连接",
            DeviceStatus.Connecting => "连接中...",
            DeviceStatus.WaitingForBind => "等待绑定",
            DeviceStatus.Disconnected => "未连接",
            DeviceStatus.Error => "错误",
            _ => "未知"
        };
        
        var statusPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5 };
        statusPanel.Children.Add(new Border
        {
            Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.Parse(statusColor)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });
        statusPanel.Children.Add(new TextBlock { Text = statusText, Foreground = new SolidColorBrush(Color.Parse("#A6ADC8")), FontSize = 12 });
        info.Children.Add(statusPanel);
        
        if (device.IsVirtual)
        {
            info.Children.Add(new TextBlock { Text = "🧪 虚拟设备", Foreground = new SolidColorBrush(Color.Parse("#8b5cf6")), FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });
        }
        Grid.SetColumn(info, 1);
        
        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var removeBtn = new Button { Content = "✕", Background = new SolidColorBrush(Color.Parse("#EF4444")), Foreground = Brushes.White, Padding = new Thickness(8, 4), Tag = device.Id };
        removeBtn.Click += OnRemoveDeviceClick;
        actions.Children.Add(removeBtn);
        Grid.SetColumn(actions, 2);
        
        grid.Children.Add(icon);
        grid.Children.Add(info);
        grid.Children.Add(actions);
        card.Child = grid;
        return card;
    }

    #region Tab Navigation

    private void OnTabDGLabClick(object? sender, RoutedEventArgs e)
    {
        _isDGLabTabActive = true;
        TabDGLab.Background = new SolidColorBrush(Color.Parse("#8b5cf6"));
        TabDGLab.Foreground = Brushes.White;
        TabDGLab.BorderBrush = new SolidColorBrush(Color.Parse("#8b5cf6"));
        TabYokonex.Background = new SolidColorBrush(Color.Parse("#313244"));
        TabYokonex.Foreground = new SolidColorBrush(Color.Parse("#A6ADC8"));
        TabYokonex.BorderBrush = new SolidColorBrush(Color.Parse("#45475A"));
        FormDGLab.IsVisible = true;
        FormYokonex.IsVisible = false;
    }

    private void OnTabYokonexClick(object? sender, RoutedEventArgs e)
    {
        _isDGLabTabActive = false;
        TabYokonex.Background = new SolidColorBrush(Color.Parse("#06b6d4"));
        TabYokonex.Foreground = Brushes.White;
        TabYokonex.BorderBrush = new SolidColorBrush(Color.Parse("#06b6d4"));
        TabDGLab.Background = new SolidColorBrush(Color.Parse("#313244"));
        TabDGLab.Foreground = new SolidColorBrush(Color.Parse("#A6ADC8"));
        TabDGLab.BorderBrush = new SolidColorBrush(Color.Parse("#45475A"));
        FormDGLab.IsVisible = false;
        FormYokonex.IsVisible = true;
    }

    #endregion

    #region Connection Mode Toggle

    private void OnDGLabConnModeWSClick(object? sender, RoutedEventArgs e)
    {
        _isDGLabWSMode = true;
        BtnDGLabWS.BorderBrush = new SolidColorBrush(Color.Parse("#8b5cf6"));
        BtnDGLabWS.BorderThickness = new Thickness(2);
        BtnDGLabBT.BorderBrush = new SolidColorBrush(Color.Parse("#45475A"));
        BtnDGLabBT.BorderThickness = new Thickness(1);
        DGLabWSForm.IsVisible = true;
        DGLabBTForm.IsVisible = false;
    }

    private void OnDGLabConnModeBTClick(object? sender, RoutedEventArgs e)
    {
        _isDGLabWSMode = false;
        BtnDGLabBT.BorderBrush = new SolidColorBrush(Color.Parse("#3B82F6"));
        BtnDGLabBT.BorderThickness = new Thickness(2);
        BtnDGLabWS.BorderBrush = new SolidColorBrush(Color.Parse("#45475A"));
        BtnDGLabWS.BorderThickness = new Thickness(1);
        DGLabWSForm.IsVisible = false;
        DGLabBTForm.IsVisible = true;
    }

    private void OnOfficialServerClick(object? sender, RoutedEventArgs e)
    {
        _useOfficialServer = true;
        BtnOfficialServer.BorderBrush = new SolidColorBrush(Color.Parse("#10B981"));
        BtnOfficialServer.BorderThickness = new Thickness(2);
        BtnOfficialServer.Foreground = Brushes.White;
        BtnCustomServer.BorderBrush = new SolidColorBrush(Color.Parse("#45475A"));
        BtnCustomServer.BorderThickness = new Thickness(1);
        BtnCustomServer.Foreground = new SolidColorBrush(Color.Parse("#A6ADC8"));
        CustomServerPanel.IsVisible = false;
    }

    private void OnCustomServerClick(object? sender, RoutedEventArgs e)
    {
        _useOfficialServer = false;
        BtnCustomServer.BorderBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
        BtnCustomServer.BorderThickness = new Thickness(2);
        BtnCustomServer.Foreground = Brushes.White;
        BtnOfficialServer.BorderBrush = new SolidColorBrush(Color.Parse("#45475A"));
        BtnOfficialServer.BorderThickness = new Thickness(1);
        BtnOfficialServer.Foreground = new SolidColorBrush(Color.Parse("#A6ADC8"));
        CustomServerPanel.IsVisible = true;
    }

    private void OnYokonexConnModeIMClick(object? sender, RoutedEventArgs e)
    {
        _isYokonexIMMode = true;
        BtnYokonexIM.BorderBrush = new SolidColorBrush(Color.Parse("#06b6d4"));
        BtnYokonexIM.BorderThickness = new Thickness(2);
        BtnYokonexBT.BorderBrush = new SolidColorBrush(Color.Parse("#45475A"));
        BtnYokonexBT.BorderThickness = new Thickness(1);
        YokonexIMForm.IsVisible = true;
        YokonexBTForm.IsVisible = false;
    }

    private void OnYokonexConnModeBTClick(object? sender, RoutedEventArgs e)
    {
        _isYokonexIMMode = false;
        BtnYokonexBT.BorderBrush = new SolidColorBrush(Color.Parse("#3B82F6"));
        BtnYokonexBT.BorderThickness = new Thickness(2);
        BtnYokonexIM.BorderBrush = new SolidColorBrush(Color.Parse("#45475A"));
        BtnYokonexIM.BorderThickness = new Thickness(1);
        YokonexIMForm.IsVisible = false;
        YokonexBTForm.IsVisible = true;
    }

    #endregion

    #region Virtual Devices

    private async void OnAddVirtualDGLabClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Logger.Information("Adding virtual DG-LAB device...");
            var deviceId = await AppServices.Instance.DeviceManager.AddDeviceAsync(DeviceType.DGLab, new ConnectionConfig(), "虚拟郊狼", isVirtual: true);
            await AppServices.Instance.DeviceManager.ConnectDeviceAsync(deviceId);
            RefreshDeviceList();
            ShowStatus("已添加虚拟郊狼设备");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to add virtual DG-LAB device");
            ShowStatus($"添加失败: {ex.Message}");
        }
    }

    private async void OnAddVirtualYokonexClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Logger.Information("Adding virtual Yokonex device...");
            var deviceId = await AppServices.Instance.DeviceManager.AddDeviceAsync(DeviceType.Yokonex, new ConnectionConfig(), "虚拟役次元", isVirtual: true);
            await AppServices.Instance.DeviceManager.ConnectDeviceAsync(deviceId);
            RefreshDeviceList();
            ShowStatus("已添加虚拟役次元设备");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to add virtual Yokonex device");
            ShowStatus($"添加失败: {ex.Message}");
        }
    }

    #endregion

    #region DG-LAB Connection

    private async void OnAddDGLabWSClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var name = DGLabDeviceName.Text ?? "郊狼设备";
            // 根据用户选择决定使用官方服务器还是自定义服务器
            string wsUrl;
            if (_useOfficialServer)
            {
                wsUrl = "wss://ws.dungeon-lab.cn";
            }
            else
            {
                wsUrl = string.IsNullOrWhiteSpace(DGLabWsUrl.Text) ? "wss://ws.dungeon-lab.cn" : DGLabWsUrl.Text.Trim();
            }
            
            Logger.Information("Connecting to DG-LAB via WebSocket: {Url}", wsUrl);
            ShowStatus("正在连接 WebSocket 服务器...");
            
            var config = new ConnectionConfig { WebSocketUrl = wsUrl, AutoReconnect = true };
            var deviceId = await AppServices.Instance.DeviceManager.AddDeviceAsync(DeviceType.DGLab, config, name);
            await AppServices.Instance.DeviceManager.ConnectDeviceAsync(deviceId);
            RefreshDeviceList();
            
            var device = AppServices.Instance.DeviceManager.GetDevice(deviceId);
            if (device is DGLabWebSocketAdapter wsAdapter)
            {
                var clientId = wsAdapter.ClientId;
                var qrContent = wsAdapter.GetQRCodeContent();
                ShowStatus($"等待 APP 扫码绑定，ClientID: {clientId}");
                await ShowQRCodeDialogAsync(qrContent, clientId ?? "", wsAdapter);
            }
            else
            {
                ShowStatus("设备已添加，等待连接...");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to connect DG-LAB via WebSocket");
            ShowStatus($"连接失败: {ex.Message}");
        }
    }
    
    private async Task ShowQRCodeDialogAsync(string qrContent, string clientId, DGLabWebSocketAdapter? wsAdapter = null)
    {
        var dialog = new Window
        {
            Title = "扫描二维码连接郊狼 APP",
            Width = 420, Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#1E1E2E")),
            CanResize = false
        };
        
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 15 };
        panel.Children.Add(new TextBlock { Text = "📱 使用郊狼 APP 扫描二维码", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeight.Bold, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });

        var steps = new StackPanel { Spacing = 8 };
        steps.Children.Add(new TextBlock { Text = "1. 打开郊狼 APP", Foreground = new SolidColorBrush(Color.Parse("#A6ADC8")), FontSize = 13 });
        steps.Children.Add(new TextBlock { Text = "2. 进入「Socket 控制」功能", Foreground = new SolidColorBrush(Color.Parse("#A6ADC8")), FontSize = 13 });
        steps.Children.Add(new TextBlock { Text = "3. 点击扫描按钮扫描下方二维码", Foreground = new SolidColorBrush(Color.Parse("#A6ADC8")), FontSize = 13 });
        steps.Children.Add(new TextBlock { Text = "4. 绑定成功后窗口会自动关闭", Foreground = new SolidColorBrush(Color.Parse("#A6ADC8")), FontSize = 13 });
        panel.Children.Add(steps);
        
        var qrBorder = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(8), Padding = new Thickness(10), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        try
        {
            var qrPngBytes = ChargingPanel.Core.Utils.QRCodeHelper.GeneratePng(qrContent, 8);
            using var ms = new System.IO.MemoryStream(qrPngBytes);
            var bitmap = new Avalonia.Media.Imaging.Bitmap(ms);
            qrBorder.Child = new Avalonia.Controls.Image { Source = bitmap, Width = 220, Height = 220 };
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "生成 QR 码失败");
            qrBorder.Child = new TextBlock { Text = "QR 码生成失败\n请复制下方链接", Foreground = Brushes.Red, TextAlignment = Avalonia.Media.TextAlignment.Center };
        }
        panel.Children.Add(qrBorder);
        
        // 状态显示
        var statusText = new TextBlock 
        { 
            Text = "⏳ 等待 APP 扫码绑定...", 
            Foreground = new SolidColorBrush(Color.Parse("#F59E0B")), 
            FontSize = 14, 
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Thickness(0, 5, 0, 5)
        };
        panel.Children.Add(statusText);
        
        panel.Children.Add(new TextBlock { Text = $"ClientID: {clientId}", Foreground = new SolidColorBrush(Color.Parse("#8b5cf6")), FontSize = 12, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBox { Text = qrContent, IsReadOnly = true, FontSize = 10, TextWrapping = TextWrapping.Wrap, MaxHeight = 60 });
        
        var closeBtn = new Button { Content = "关闭", Background = new SolidColorBrush(Color.Parse("#45475A")), Foreground = Brushes.White, Padding = new Thickness(30, 10), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        closeBtn.Click += (s, e) => dialog.Close();
        panel.Children.Add(closeBtn);
        
        dialog.Content = panel;
        
        // 监听绑定状态
        if (wsAdapter != null)
        {
            void OnStatusChanged(object? s, DeviceStatus status)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (status == DeviceStatus.Connected)
                    {
                        statusText.Text = "✅ 绑定成功！";
                        statusText.Foreground = new SolidColorBrush(Color.Parse("#10B981"));
                        ShowStatus("郊狼 APP 绑定成功！");
                        RefreshDeviceList();
                        // 1秒后自动关闭对话框
                        Task.Delay(1000).ContinueWith(_ => 
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => dialog.Close());
                        });
                    }
                    else if (status == DeviceStatus.Error || status == DeviceStatus.Disconnected)
                    {
                        statusText.Text = "❌ 连接失败，请重试";
                        statusText.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
                    }
                });
            }
            
            wsAdapter.StatusChanged += OnStatusChanged;
            dialog.Closed += (s, e) => wsAdapter.StatusChanged -= OnStatusChanged;
        }
        
        var parent = this.Parent;
        while (parent != null && parent is not Window) parent = (parent as Control)?.Parent;
        if (parent is Window parentWindow) await dialog.ShowDialog(parentWindow);
        else dialog.Show();
    }

    #endregion

    #region Yokonex Connection

    private async void OnAddYokonexIMClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var name = YokonexDeviceName.Text ?? "役次元设备";
            var uid = YokonexUid.Text ?? "";
            var token = YokonexToken.Text ?? "";
            var targetId = YokonexTargetId.Text ?? "";
            
            if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(token))
            {
                ShowStatus("请填写 UID 和 Token");
                return;
            }
            
            Logger.Information("Connecting to Yokonex via IM: {Uid}", uid);
            var config = new ConnectionConfig { UserId = uid, Token = token, TargetUserId = targetId, AutoReconnect = true };
            var deviceId = await AppServices.Instance.DeviceManager.AddDeviceAsync(DeviceType.Yokonex, config, name, isVirtual: false, mode: ConnectionMode.TencentIM, yokonexType: YokonexDeviceType.Estim);
            await AppServices.Instance.DeviceManager.ConnectDeviceAsync(deviceId);
            RefreshDeviceList();
            ShowStatus("役次元设备已添加");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to connect Yokonex via IM");
            ShowStatus($"连接失败: {ex.Message}");
        }
    }

    #endregion

    private async void OnRemoveDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string deviceId)
        {
            try
            {
                await AppServices.Instance.DeviceManager.RemoveDeviceAsync(deviceId);
                RefreshDeviceList();
                ShowStatus("设备已移除");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to remove device");
                ShowStatus($"移除失败: {ex.Message}");
            }
        }
    }

    private void ShowStatus(string message)
    {
        var parent = this.Parent;
        while (parent != null && parent is not MainWindow) parent = (parent as Control)?.Parent;
        if (parent is MainWindow mainWindow) mainWindow.ShowStatus(message);
    }

    #region Bluetooth Scanning

    private async void OnScanDGLabBTClick(object? sender, RoutedEventArgs e)
    {
        if (_isScanning) { ShowStatus("正在扫描中，请稍候..."); return; }
        await ScanBluetoothDevices(DeviceType.DGLab);
    }

    private async void OnScanYokonexBTClick(object? sender, RoutedEventArgs e)
    {
        if (_isScanning) { ShowStatus("正在扫描中，请稍候..."); return; }
        await ScanBluetoothDevices(DeviceType.Yokonex);
    }
    
    private async void OnDiagnoseBluetoothClick(object? sender, RoutedEventArgs e)
    {
        ShowStatus("正在诊断蓝牙...");
        try
        {
            var result = await AppServices.Instance.DeviceManager.Diagnostics.DiagnoseBluetoothAsync();
            var message = new System.Text.StringBuilder();
            message.AppendLine("=== 蓝牙诊断结果 ===");
            message.AppendLine($"适配器可用: {(result.AdapterAvailable ? "✓" : "✗")}");
            message.AppendLine($"蓝牙已开启: {(result.AdapterEnabled ? "✓" : "✗")}");
            message.AppendLine($"支持 BLE: {(result.SupportsBle ? "✓" : "✗")}");
            message.AppendLine($"Windows 版本: {result.WindowsVersion}");
            if (result.Issues.Count > 0) { message.AppendLine("\n问题:"); foreach (var issue in result.Issues) message.AppendLine($"  ⚠ {issue}"); }
            if (result.Suggestions.Count > 0) { message.AppendLine("\n建议:"); foreach (var suggestion in result.Suggestions) message.AppendLine($"  → {suggestion}"); }
            await ShowDiagnosticsDialogAsync("蓝牙诊断结果", message.ToString(), result.Issues.Count == 0);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Bluetooth diagnostics failed");
            ShowStatus($"诊断失败: {ex.Message}");
        }
    }
    
    private async Task ShowDiagnosticsDialogAsync(string title, string content, bool isSuccess)
    {
        var dialog = new Window { Title = title, Width = 450, Height = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.Parse("#1E1E2E")) };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 15 };
        panel.Children.Add(new TextBlock { Text = isSuccess ? "✅ 诊断通过" : "⚠️ 发现问题", Foreground = new SolidColorBrush(Color.Parse(isSuccess ? "#10B981" : "#F59E0B")), FontSize = 18, FontWeight = FontWeight.Bold, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });
        panel.Children.Add(new TextBox { Text = content, IsReadOnly = true, FontSize = 12, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 250, Background = new SolidColorBrush(Color.Parse("#313244")), Foreground = new SolidColorBrush(Color.Parse("#CDD6F4")) });
        var closeBtn = new Button { Content = "关闭", Background = new SolidColorBrush(Color.Parse("#8b5cf6")), Foreground = Brushes.White, Padding = new Thickness(30, 10), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        closeBtn.Click += (s, e) => dialog.Close();
        panel.Children.Add(closeBtn);
        dialog.Content = panel;
        var parent = this.Parent; while (parent != null && parent is not Window) parent = (parent as Control)?.Parent;
        if (parent is Window parentWindow) await dialog.ShowDialog(parentWindow); else dialog.Show();
    }

    private async Task ScanBluetoothDevices(DeviceType deviceType)
    {
        _isScanning = true;
        var scanButton = deviceType == DeviceType.DGLab ? BtnScanDGLab : BtnScanYokonex;
        var deviceList = deviceType == DeviceType.DGLab ? DGLabBTDeviceList : YokonexBTDeviceList;

        try
        {
            scanButton.Content = "⏳ 扫描中...";
            scanButton.IsEnabled = false;
            deviceList.Children.Clear();
            deviceList.Children.Add(new TextBlock { Text = "正在扫描蓝牙设备，请稍候...", Foreground = new SolidColorBrush(Color.Parse("#F59E0B")), FontSize = 12, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });

            using var transport = new WindowsBluetoothTransport();
            Guid? serviceFilter = deviceType == DeviceType.Yokonex ? Guid.Parse("0000ff30-0000-1000-8000-00805f9b34fb") : null;
            
            Logger.Information("Starting Bluetooth scan for {Type} devices...", deviceType);
            var devices = await transport.ScanAsync(serviceFilter: serviceFilter, namePrefix: null, timeoutMs: 8000);
            deviceList.Children.Clear();

            if (devices.Length == 0)
            {
                var noDevicePanel = new StackPanel { Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
                noDevicePanel.Children.Add(new TextBlock { Text = "未发现设备", Foreground = new SolidColorBrush(Color.Parse("#EF4444")), FontSize = 13, FontWeight = FontWeight.SemiBold });
                noDevicePanel.Children.Add(new TextBlock { Text = "• 设备是否已开启电源", Foreground = new SolidColorBrush(Color.Parse("#A6ADC8")), FontSize = 12 });
                noDevicePanel.Children.Add(new TextBlock { Text = "• 设备是否在蓝牙范围内", Foreground = new SolidColorBrush(Color.Parse("#A6ADC8")), FontSize = 12 });
                var diagnoseBtn = new Button { Content = "🔧 诊断蓝牙", Background = new SolidColorBrush(Color.Parse("#F59E0B")), Foreground = Brushes.White, Padding = new Thickness(12, 6), Margin = new Thickness(0, 10, 0, 0) };
                diagnoseBtn.Click += OnDiagnoseBluetoothClick;
                noDevicePanel.Children.Add(diagnoseBtn);
                deviceList.Children.Add(noDevicePanel);
            }
            else
            {
                var relevantDevices = devices.Where(d => deviceType == DeviceType.DGLab ? (d.Name.StartsWith("47L12", StringComparison.OrdinalIgnoreCase) || d.Name.StartsWith("D-LAB", StringComparison.OrdinalIgnoreCase)) : true).ToArray();
                if (relevantDevices.Length == 0)
                {
                    deviceList.Children.Add(new TextBlock { Text = $"发现 {devices.Length} 个蓝牙设备，但没有匹配的设备", Foreground = new SolidColorBrush(Color.Parse("#F59E0B")), FontSize = 12, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });
                }
                else
                {
                    foreach (var device in relevantDevices) deviceList.Children.Add(CreateBluetoothDeviceCard(device, deviceType));
                    ShowStatus($"发现 {relevantDevices.Length} 个设备");
                }
            }
            Logger.Information("Bluetooth scan completed, found {Count} devices", devices.Length);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Bluetooth scan failed");
            deviceList.Children.Clear();
            deviceList.Children.Add(new TextBlock { Text = $"扫描失败: {ex.Message}", Foreground = new SolidColorBrush(Color.Parse("#EF4444")), FontSize = 12, TextWrapping = TextWrapping.Wrap });
            ShowStatus($"蓝牙扫描失败: {ex.Message}");
        }
        finally
        {
            scanButton.Content = "🔍 扫描设备";
            scanButton.IsEnabled = true;
            _isScanning = false;
        }
    }

    private Border CreateBluetoothDeviceCard(BleDeviceInfo device, DeviceType deviceType)
    {
        var card = new Border { Background = new SolidColorBrush(Color.Parse("#1E1E2E")), CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Margin = new Thickness(0, 4, 0, 4) };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var info = new StackPanel();
        info.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(device.Name) ? "未知设备" : device.Name, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, FontSize = 13 });
        info.Children.Add(new TextBlock { Text = $"MAC: {device.MacAddress} | 信号: {device.Rssi} dBm", Foreground = new SolidColorBrush(Color.Parse("#A6ADC8")), FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(info, 0);
        var connectBtn = new Button { Content = "连接", Background = new SolidColorBrush(Color.Parse(deviceType == DeviceType.DGLab ? "#8b5cf6" : "#06b6d4")), Foreground = Brushes.White, Padding = new Thickness(12, 6), Tag = (device.Id, deviceType) };
        connectBtn.Click += OnConnectBluetoothDeviceClick;
        Grid.SetColumn(connectBtn, 1);
        grid.Children.Add(info);
        grid.Children.Add(connectBtn);
        card.Child = grid;
        return card;
    }

    private async void OnConnectBluetoothDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not (string deviceId, DeviceType deviceType)) return;
        try
        {
            btn.Content = "连接中...";
            btn.IsEnabled = false;
            Logger.Information("Connecting to Bluetooth device {Id} as {Type}", deviceId, deviceType);
            var config = new ConnectionConfig { Address = deviceId, AutoReconnect = true };
            YokonexDeviceType yokonexType = YokonexDeviceType.Estim;
            DGLabVersion dglabVersion = DGLabVersion.V3;
            if (deviceType == DeviceType.Yokonex && YokonexBTDeviceType.SelectedItem is ComboBoxItem item)
            {
                yokonexType = item.Tag?.ToString() switch { "Estim" => YokonexDeviceType.Estim, "Enema" => YokonexDeviceType.Enema, "Vibrator" => YokonexDeviceType.Vibrator, "Cup" => YokonexDeviceType.Cup, _ => YokonexDeviceType.Estim };
            }
            string name = deviceType == DeviceType.DGLab ? "蓝牙郊狼" : $"蓝牙役次元-{yokonexType}";
            var newDeviceId = await AppServices.Instance.DeviceManager.AddDeviceAsync(deviceType, config, name, isVirtual: false, mode: ConnectionMode.Bluetooth, dglabVersion: dglabVersion, yokonexType: yokonexType);
            await AppServices.Instance.DeviceManager.ConnectDeviceAsync(newDeviceId);
            RefreshDeviceList();
            ShowStatus($"蓝牙设备已连接: {name}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to connect Bluetooth device");
            ShowStatus($"连接失败: {ex.Message}");
            btn.Content = "连接";
            btn.IsEnabled = true;
        }
    }

    #endregion
}

public class DeviceViewModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DeviceType Type { get; set; }
    public DeviceStatus Status { get; set; }
    public bool IsVirtual { get; set; }
}
