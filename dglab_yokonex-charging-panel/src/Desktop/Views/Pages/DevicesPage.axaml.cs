using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ChargingPanel.Core;
using ChargingPanel.Core.Bluetooth;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.DGLab;
using ChargingPanel.Core.Devices.Yokonex;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace ChargingPanel.Desktop.Views.Pages;

public partial class DevicesPage : UserControl
{
    private enum YokonexConnectionTab
    {
        Bluetooth,
        TencentIM
    }

    private static readonly ILogger Logger = Log.ForContext<DevicesPage>();
    private const string ColorPanel = "#172033";
    private const string ColorPanelBorder = "#2B3B55";
    private const string ColorTextPrimary = "#E6EDF7";
    private const string ColorTextMuted = "#9AB0C8";
    
    private bool _isDGLabTabActive = true;
    private bool _isDGLabWSMode = true;
    private YokonexConnectionTab _yokonexConnectionMode = YokonexConnectionTab.Bluetooth;
    private bool _useOfficialServer = true;
    private bool _isScanning = false;
    private bool _eventsBound;
    private readonly DispatcherTimer _statusRefreshDebounceTimer;
    private bool _deviceRefreshRequested;
    private YokonexDeviceType _selectedYokonexType = YokonexDeviceType.Estim;
    private YokonexDeviceType _selectedVirtualYokonexType = YokonexDeviceType.Estim;
    private DGLabVersion _selectedDGLabVersion = DGLabVersion.V3;  // 默认V3
    
    public ObservableCollection<DeviceViewModel> Devices { get; } = new();

    public DevicesPage()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        _statusRefreshDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _statusRefreshDebounceTimer.Tick += OnStatusRefreshDebounceTick;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        ApplyVendorTabState();
        ApplyDGLabConnectionModeState();
        ApplyYokonexConnectionModeState();
        ApplyServerSelectionState();
        UpdateYokonexDeviceInfo();
        UpdateYokonexBTScanDesc();
        
        // 延迟初始化，确保 AppServices 已完成初始化
        _ = InitializeAsync();
    }

    private void OnAttachedToVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (!AppServices.IsInitialized)
        {
            return;
        }

        BindDeviceManagerEvents();
        RefreshDeviceList();
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
                UpdateDGLabVersionUI();
            });
        }
    }

    private void BindDeviceManagerEvents()
    {
        if (_eventsBound || !AppServices.IsInitialized) return;
        AppServices.Instance.DeviceManager.DeviceStatusChanged += OnDeviceStatusChanged;
        _eventsBound = true;
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (_eventsBound && AppServices.IsInitialized)
        {
            AppServices.Instance.DeviceManager.DeviceStatusChanged -= OnDeviceStatusChanged;
        }
        _eventsBound = false;
        _statusRefreshDebounceTimer.Stop();
        _deviceRefreshRequested = false;
    }

    private void OnDeviceStatusChanged(object? sender, DeviceStatusChangedEventArgs e)
    {
        RequestDeviceListRefresh();
    }

    private void RequestDeviceListRefresh()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RequestDeviceListRefresh);
            return;
        }

        _deviceRefreshRequested = true;
        if (!_statusRefreshDebounceTimer.IsEnabled)
        {
            _statusRefreshDebounceTimer.Start();
        }
    }

    private void OnStatusRefreshDebounceTick(object? sender, EventArgs e)
    {
        _statusRefreshDebounceTimer.Stop();
        if (!_deviceRefreshRequested)
        {
            return;
        }

        _deviceRefreshRequested = false;
        RefreshDeviceList();
    }

    private void RefreshDeviceList()
    {
        if (!AppServices.IsInitialized) return;
        
        Devices.Clear();
        var devices = AppServices.Instance.DeviceManager.GetAllDevices()
            .Where(d => d switch
            {
                { Type: DeviceType.DGLab, DGLabVersion: DGLabVersion.V3WirelessSensor or DGLabVersion.PawPrints } => false,
                { Type: DeviceType.Yokonex, YokonexType: YokonexDeviceType.SmartLock } => false,
                _ => true
            })
            .OrderByDescending(d => d.Status == DeviceStatus.Connected)
            .ThenByDescending(d => d.Status == DeviceStatus.Connecting)
            .ThenBy(d => d.Name)
            .ToList();
        
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
        var (statusText, statusColor) = GetStatusMeta(device.Status);
        var tags = BuildDeviceTags(device);
        var canToggleConnection = device.Status is DeviceStatus.Connected or DeviceStatus.Disconnected or DeviceStatus.Error;
        var shouldConnect = device.Status is DeviceStatus.Disconnected or DeviceStatus.Error;
        var actionText = shouldConnect ? "连接" : "断开";
        var actionColor = shouldConnect ? "#10B981" : "#F59E0B";

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderBrush = Brush(ColorPanelBorder),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 10)
        };
        card.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.Parse("#182338"), 0),
                new GradientStop(Color.Parse("#151F30"), 1)
            }
        };
        
        var root = new StackPanel { Spacing = 10 };

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        top.Children.Add(new Border
        {
            Background = Brush(device.Type == DeviceType.DGLab ? "#0EA5E922" : "#f59e0b22"),
            CornerRadius = new CornerRadius(10),
            Width = 40,
            Height = 40,
            Child = new TextBlock
            {
                Text = device.Type == DeviceType.DGLab ? "郊狼" : "役次元",
                FontSize = 18,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            }
        });

        var info = new StackPanel { Margin = new Thickness(10, 0, 0, 0), Spacing = 4 };
        info.Children.Add(new TextBlock
        {
            Text = device.Name,
            Foreground = Brush(ColorTextPrimary),
            FontWeight = FontWeight.SemiBold,
            FontSize = 14
        });
        info.Children.Add(new TextBlock
        {
            Text = BuildDeviceSubTitle(device),
            Foreground = Brush(ColorTextMuted),
            FontSize = 11
        });
        Grid.SetColumn(info, 1);
        top.Children.Add(info);

        var statusBadge = new Border
        {
            Background = Brush($"{statusColor}22"),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 4),
            BorderBrush = Brush($"{statusColor}66"),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = statusText,
                Foreground = Brush(statusColor),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold
            },
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetColumn(statusBadge, 2);
        top.Children.Add(statusBadge);
        root.Children.Add(top);

        if (tags.Count > 0)
        {
            var tagWrap = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            foreach (var tag in tags)
            {
                tagWrap.Children.Add(CreateTagChip(tag.Text, tag.Background, tag.Foreground));
            }
            root.Children.Add(tagWrap);
        }

        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        var toggleBtn = new Button
        {
            Content = actionText,
            Background = Brush(actionColor),
            Foreground = Brushes.White,
            Padding = new Thickness(12, 6),
            CornerRadius = new CornerRadius(8),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Tag = (device.Id, shouldConnect),
            IsEnabled = canToggleConnection
        };
        toggleBtn.Click += OnToggleDeviceConnectionClick;
        actions.Children.Add(toggleBtn);

        var removeBtn = new Button
        {
            Content = "移除",
            Background = Brush("#EF4444"),
            Foreground = Brushes.White,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(8),
            FontSize = 12
        };
        removeBtn.Tag = device.Id;
        removeBtn.Click += OnRemoveDeviceClick;
        actions.Children.Add(removeBtn);

        root.Children.Add(actions);
        card.Child = root;
        return card;
    }

    private static (string Text, string Color) GetStatusMeta(DeviceStatus status) => status switch
    {
        DeviceStatus.Connected => ("已连接", "#10B981"),
        DeviceStatus.Connecting => ("连接中", "#F59E0B"),
        DeviceStatus.WaitingForBind => ("等待绑定", "#0EA5E9"),
        DeviceStatus.Disconnected => ("未连接", "#8091A8"),
        DeviceStatus.Error => ("连接异常", "#EF4444"),
        _ => ("未知状态", "#8091A8")
    };

    private string BuildDeviceSubTitle(DeviceInfo device)
    {
        if (device.Type == DeviceType.DGLab)
        {
            var version = device.DGLabVersion?.ToString() ?? "Unknown";
            return $"DG-LAB · {version}";
        }

        var subtype = device.YokonexType switch
        {
            YokonexDeviceType.Estim => "电击器",
            YokonexDeviceType.Enema => "灌肠器",
            YokonexDeviceType.Vibrator => "跳蛋",
            YokonexDeviceType.Cup => "飞机杯",
            _ => "设备"
        };
        return $"Yokonex · {subtype}";
    }

    private List<(string Text, string Background, string Foreground)> BuildDeviceTags(DeviceInfo device)
    {
        var tags = new List<(string Text, string Background, string Foreground)>
        {
            (device.ConnectionMode.ToString(), "#334155", "#E2E8F0")
        };

        if (device.IsVirtual)
        {
            tags.Add(("虚拟设备", "#5B21B6", "#F3E8FF"));
        }

        if (device.YokonexProtocolGeneration.HasValue)
        {
            tags.Add(($"协议 {device.YokonexProtocolGeneration}", "#0F766E", "#CCFBF1"));
        }

        if (device.State.BatteryLevel.HasValue)
        {
            tags.Add(($"电量 {device.State.BatteryLevel.Value}%", "#0B4A6F", "#D0F2FF"));
        }

        return tags;
    }

    private static Border CreateTagChip(string text, string bg, string fg)
    {
        return new Border
        {
            Background = Brush(bg),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 4),
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brush(fg),
                FontSize = 11,
                FontWeight = FontWeight.Medium
            }
        };
    }

    private static string NormalizeWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var segments = text
            .Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", segments);
    }

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));

    #region Tab Navigation

    private void OnTabDGLabClick(object? sender, RoutedEventArgs e)
    {
        _isDGLabTabActive = true;
        ApplyVendorTabState();
    }

    private void OnTabYokonexClick(object? sender, RoutedEventArgs e)
    {
        _isDGLabTabActive = false;
        ApplyVendorTabState();
    }
    
    private void OnYokonexDeviceTypeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string typeTag) return;
        
        _selectedYokonexType = typeTag switch
        {
            "Estim" => YokonexDeviceType.Estim,
            "Enema" => YokonexDeviceType.Enema,
            "Vibrator" => YokonexDeviceType.Vibrator,
            "Cup" => YokonexDeviceType.Cup,
            "SmartLock" => YokonexDeviceType.SmartLock,
            _ => YokonexDeviceType.Estim
        };
        
        // 更新按钮样式
        var buttons = new[] { BtnYokonexEstim, BtnYokonexEnema, BtnYokonexVibrator, BtnYokonexCup, BtnYokonexSmartLock };
        foreach (var b in buttons)
        {
            b.BorderBrush = new SolidColorBrush(Color.Parse("#314764"));
        }
        btn.BorderBrush = new SolidColorBrush(Color.Parse("#F59E0B"));

        if (_selectedYokonexType == YokonexDeviceType.SmartLock)
        {
            // 智能锁为预留接口，当前统一复用 IM 连接路径做占位联调。
            _yokonexConnectionMode = YokonexConnectionTab.TencentIM;
            ApplyYokonexConnectionModeState();
        }
        
        // 更新设备信息卡片
        UpdateYokonexDeviceInfo();
        
        // 更新蓝牙扫描描述
        UpdateYokonexBTScanDesc();
    }

    private void UpdateYokonexDeviceInfo()
    {
        var (icon, title, desc) = _selectedYokonexType switch
        {
            YokonexDeviceType.Estim => ("⚡", "役次元电击器", "支持双通道 EMS 控制 (276级强度)、16种固定模式、自定义模式、马达控制、计步器、角度传感器"),
            YokonexDeviceType.Enema => ("💧", "役次元灌肠器", "支持蠕动泵控制 (正转/反转)、抽水泵控制、压力传感器、AES-128 加密通信"),
            YokonexDeviceType.Vibrator => ("📳", "役次元跳蛋", "支持多马达独立控制 (最多3个)、20级力度等级、多种固定模式"),
            YokonexDeviceType.Cup => ("🌀", "役次元飞机杯", "支持多马达独立控制 (最多3个)、20级力度等级、多种固定模式"),
            YokonexDeviceType.SmartLock => ("🔒", "役次元智能锁（预留）", "当前为预留接口，建议先使用 IM 模式进行占位联调（连接生命周期/状态流验证）"),
            _ => ("🧩", "役次元设备", "")
        };
        
        YokonexDeviceIcon.Text = icon;
        YokonexDeviceTitle.Text = title;
        YokonexDeviceDesc.Text = desc;
    }
    
    private void UpdateYokonexBTScanDesc()
    {
        var desc = _selectedYokonexType switch
        {
            YokonexDeviceType.Estim => "扫描附近的役次元电击器设备 (服务 UUID: FF30)",
            YokonexDeviceType.Enema => "扫描附近的役次元灌肠器设备 (服务 UUID: FFB0)",
            YokonexDeviceType.Vibrator => "扫描附近的役次元跳蛋设备 (服务 UUID: FF40)",
            YokonexDeviceType.Cup => "扫描附近的役次元飞机杯设备 (服务 UUID: FF40)",
            YokonexDeviceType.SmartLock => "智能锁暂未上市，当前仅支持预留接口联调（不建议蓝牙扫描）",
            _ => "扫描附近的役次元设备"
        };
        YokonexBTScanDesc.Text = desc;
    }

    #endregion

    #region Connection Mode Toggle

    private void OnDGLabConnModeWSClick(object? sender, RoutedEventArgs e)
    {
        _isDGLabWSMode = true;
        ApplyDGLabConnectionModeState();
    }

    private void OnDGLabConnModeBTClick(object? sender, RoutedEventArgs e)
    {
        _isDGLabWSMode = false;
        ApplyDGLabConnectionModeState();
    }
    
    private void OnDGLabVersionV2Click(object? sender, RoutedEventArgs e)
    {
        _selectedDGLabVersion = DGLabVersion.V2;
        UpdateDGLabVersionUI();
    }
    
    private void OnDGLabVersionV3Click(object? sender, RoutedEventArgs e)
    {
        _selectedDGLabVersion = DGLabVersion.V3;
        UpdateDGLabVersionUI();
    }

    private void OnDGLabVersionV3SensorClick(object? sender, RoutedEventArgs e)
    {
        _selectedDGLabVersion = DGLabVersion.V3WirelessSensor;
        UpdateDGLabVersionUI();
    }

    private void OnDGLabVersionPawPrintsClick(object? sender, RoutedEventArgs e)
    {
        _selectedDGLabVersion = DGLabVersion.PawPrints;
        UpdateDGLabVersionUI();
    }
    
    private void UpdateDGLabVersionUI()
    {
        // 更新按钮样式
        var selectedColor = Color.Parse("#0EA5E9");
        var unselectedColor = Color.Parse("#314764");
        
        BtnDGLabV2.BorderBrush = new SolidColorBrush(_selectedDGLabVersion == DGLabVersion.V2 ? selectedColor : unselectedColor);
        BtnDGLabV3.BorderBrush = new SolidColorBrush(_selectedDGLabVersion == DGLabVersion.V3 ? selectedColor : unselectedColor);
        BtnDGLabV3Sensor.BorderBrush = new SolidColorBrush(_selectedDGLabVersion == DGLabVersion.V3WirelessSensor ? selectedColor : unselectedColor);
        BtnDGLabPawPrints.BorderBrush = new SolidColorBrush(_selectedDGLabVersion == DGLabVersion.PawPrints ? selectedColor : unselectedColor);
        
        // 更新提示文本
        var (hint, prefix) = _selectedDGLabVersion switch
        {
            DGLabVersion.V2 => ("扫描附近的 DG-LAB V2 设备 (D-LAB ESTIM01)", "D-LAB ESTIM01"),
            DGLabVersion.V3 => ("扫描附近的 DG-LAB V3 设备 (47L121000)", "47L121000"),
            DGLabVersion.V3WirelessSensor => ("扫描附近的 47L120100 无线传感器（预留协议）", "47L120100"),
            DGLabVersion.PawPrints => ("扫描附近的 PawPrints 外部电压配件（预留协议）", "PawPrints"),
            _ => ("扫描附近的 DG-LAB 设备", "47L")
        };
        
        if (this.FindControl<TextBlock>("DGLabBTVersionHint") is TextBlock hintText)
        {
            hintText.Text = hint;
        }
        
        Logger.Information("DG-LAB version selected: {Version}", _selectedDGLabVersion);
    }

    private void OnOfficialServerClick(object? sender, RoutedEventArgs e)
    {
        _useOfficialServer = true;
        ApplyServerSelectionState();
    }

    private void OnCustomServerClick(object? sender, RoutedEventArgs e)
    {
        _useOfficialServer = false;
        ApplyServerSelectionState();
    }

    private void OnYokonexConnModeIMClick(object? sender, RoutedEventArgs e)
    {
        _yokonexConnectionMode = YokonexConnectionTab.TencentIM;
        ApplyYokonexConnectionModeState();
    }

    private void OnYokonexConnModeBTClick(object? sender, RoutedEventArgs e)
    {
        _yokonexConnectionMode = YokonexConnectionTab.Bluetooth;
        ApplyYokonexConnectionModeState();
    }

    private void ApplyVendorTabState()
    {
        SetSelectionStyle(TabDGLab, _isDGLabTabActive, "#0EA5E9");
        SetSelectionStyle(TabYokonex, !_isDGLabTabActive, "#F59E0B");
        FormDGLab.IsVisible = _isDGLabTabActive;
        FormYokonex.IsVisible = !_isDGLabTabActive;
    }

    private void ApplyDGLabConnectionModeState()
    {
        SetSelectionStyle(BtnDGLabWS, _isDGLabWSMode, "#0EA5E9", true);
        SetSelectionStyle(BtnDGLabBT, !_isDGLabWSMode, "#0EA5E9", true);
        DGLabWSForm.IsVisible = _isDGLabWSMode;
        DGLabBTForm.IsVisible = !_isDGLabWSMode;
    }

    private void ApplyYokonexConnectionModeState()
    {
        SetSelectionStyle(BtnYokonexIM, _yokonexConnectionMode == YokonexConnectionTab.TencentIM, "#F59E0B", true);
        SetSelectionStyle(BtnYokonexBT, _yokonexConnectionMode == YokonexConnectionTab.Bluetooth, "#10B981", true);
        YokonexIMForm.IsVisible = _yokonexConnectionMode == YokonexConnectionTab.TencentIM;
        YokonexBTForm.IsVisible = _yokonexConnectionMode == YokonexConnectionTab.Bluetooth;
    }

    private void ApplyServerSelectionState()
    {
        SetSelectionStyle(BtnOfficialServer, _useOfficialServer, "#10B981", false);
        SetSelectionStyle(BtnCustomServer, !_useOfficialServer, "#F59E0B", false);
        CustomServerPanel.IsVisible = !_useOfficialServer;
    }

    private static void SetSelectionStyle(Button button, bool selected, string accentColor, bool keepThickness = false)
    {
        button.BorderBrush = Brush(selected ? accentColor : "#314764");
        button.BorderThickness = new Thickness(selected ? 2 : (keepThickness ? 2 : 1));
        button.Foreground = selected ? Brushes.White : Brush(ColorTextMuted);

        if (!keepThickness)
        {
            button.Background = selected ? Brush("#1F2B3E") : Brush("#1F2B3E");
        }
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
            var selectedType = await ShowVirtualYokonexTypePickerAsync();
            if (!selectedType.HasValue)
            {
                ShowStatus("已取消添加虚拟役次元设备");
                return;
            }

            _selectedVirtualYokonexType = selectedType.Value;

            var subtype = _selectedVirtualYokonexType switch
            {
                YokonexDeviceType.Enema => "灌肠器",
                YokonexDeviceType.Vibrator => "跳蛋",
                YokonexDeviceType.Cup => "飞机杯",
                _ => "电击器"
            };
            var name = $"虚拟役次元{subtype}";
            var config = new ConnectionConfig
            {
                ConnectionMode = ConnectionMode.Bluetooth,
                YokonexType = _selectedVirtualYokonexType
            };

            Logger.Information("Adding virtual Yokonex device: {Type}", _selectedVirtualYokonexType);
            var deviceId = await AppServices.Instance.DeviceManager.AddDeviceAsync(
                DeviceType.Yokonex,
                config,
                name,
                isVirtual: true,
                mode: ConnectionMode.Bluetooth,
                yokonexType: _selectedVirtualYokonexType);
            await AppServices.Instance.DeviceManager.ConnectDeviceAsync(deviceId);
            RefreshDeviceList();
            ShowStatus($"已添加{name}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to add virtual Yokonex device");
            ShowStatus($"添加失败: {ex.Message}");
        }
    }

    private async Task<YokonexDeviceType?> ShowVirtualYokonexTypePickerAsync()
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 0)
        };
        combo.Items.Add(new ComboBoxItem { Content = "电击器", Tag = "Estim" });
        combo.Items.Add(new ComboBoxItem { Content = "灌肠器", Tag = "Enema" });
        combo.Items.Add(new ComboBoxItem { Content = "跳蛋", Tag = "Vibrator" });
        combo.Items.Add(new ComboBoxItem { Content = "飞机杯", Tag = "Cup" });
        combo.SelectedIndex = _selectedVirtualYokonexType switch
        {
            YokonexDeviceType.Enema => 1,
            YokonexDeviceType.Vibrator => 2,
            YokonexDeviceType.Cup => 3,
            _ => 0
        };

        var dialog = new Window
        {
            Title = "选择虚拟役次元设备类型",
            Width = 520,
            Height = 340,
            MinWidth = 460,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#172033")),
            CanResize = false
        };

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
        panel.Children.Add(new TextBlock
        {
            Text = "请选择要模拟的设备类型",
            Foreground = new SolidColorBrush(Color.Parse("#E6EDF7")),
            FontSize = 18,
            FontWeight = FontWeight.Bold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "该窗口已优化为固定尺寸，避免在不同分辨率下选择区域过小。",
            Foreground = new SolidColorBrush(Color.Parse("#9AB0C8")),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(combo);

        var hint = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1F2B3E")),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Child = new TextBlock
            {
                Text = "支持类型：电击器 / 灌肠器 / 跳蛋 / 飞机杯",
                Foreground = new SolidColorBrush(Color.Parse("#9AB0C8")),
                FontSize = 12
            }
        };
        panel.Children.Add(hint);

        var buttonRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            Margin = new Thickness(0, 10, 0, 0)
        };

        var cancelBtn = new Button
        {
            Content = "取消",
            Background = new SolidColorBrush(Color.Parse("#314764")),
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 8, 0),
            Height = 40
        };
        var confirmBtn = new Button
        {
            Content = "确认添加",
            Background = new SolidColorBrush(Color.Parse("#0EA5E9")),
            Foreground = Brushes.White,
            Margin = new Thickness(8, 0, 0, 0),
            Height = 40
        };
        Grid.SetColumn(cancelBtn, 0);
        Grid.SetColumn(confirmBtn, 1);

        buttonRow.Children.Add(cancelBtn);
        buttonRow.Children.Add(confirmBtn);
        panel.Children.Add(buttonRow);
        dialog.Content = panel;

        cancelBtn.Click += (_, _) => dialog.Close(null);
        confirmBtn.Click += (_, _) =>
        {
            var selected = (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
            {
                "Enema" => YokonexDeviceType.Enema,
                "Vibrator" => YokonexDeviceType.Vibrator,
                "Cup" => YokonexDeviceType.Cup,
                _ => YokonexDeviceType.Estim
            };
            dialog.Close(selected);
        };

        var parent = this.Parent;
        while (parent != null && parent is not Window)
        {
            parent = (parent as Control)?.Parent;
        }

        if (parent is Window parentWindow)
        {
            return await dialog.ShowDialog<YokonexDeviceType?>(parentWindow);
        }

        return _selectedVirtualYokonexType;
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
            
            var config = new ConnectionConfig
            {
                WebSocketUrl = wsUrl,
                AutoReconnect = true,
                ConnectionMode = ConnectionMode.WebSocket
            };
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
            Background = new SolidColorBrush(Color.Parse("#172033")),
            CanResize = false
        };
        
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 15 };
        panel.Children.Add(new TextBlock { Text = "使用郊狼 APP 扫描二维码", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeight.Bold, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });

        var steps = new StackPanel { Spacing = 8 };
        steps.Children.Add(new TextBlock { Text = "1. 打开郊狼 APP", Foreground = new SolidColorBrush(Color.Parse("#9AB0C8")), FontSize = 13 });
        steps.Children.Add(new TextBlock { Text = "2. 进入「Socket 控制」功能", Foreground = new SolidColorBrush(Color.Parse("#9AB0C8")), FontSize = 13 });
        steps.Children.Add(new TextBlock { Text = "3. 点击扫描按钮扫描下方二维码", Foreground = new SolidColorBrush(Color.Parse("#9AB0C8")), FontSize = 13 });
        steps.Children.Add(new TextBlock { Text = "4. 绑定成功后窗口会自动关闭", Foreground = new SolidColorBrush(Color.Parse("#9AB0C8")), FontSize = 13 });
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
            Text = "等待 APP 扫码绑定...", 
            Foreground = new SolidColorBrush(Color.Parse("#F59E0B")), 
            FontSize = 14, 
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Thickness(0, 5, 0, 5)
        };
        panel.Children.Add(statusText);
        
        panel.Children.Add(new TextBlock { Text = $"ClientID: {clientId}", Foreground = new SolidColorBrush(Color.Parse("#0EA5E9")), FontSize = 12, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBox { Text = qrContent, IsReadOnly = true, FontSize = 10, TextWrapping = TextWrapping.Wrap, MaxHeight = 60 });
        
        var closeBtn = new Button { Content = "关闭", Background = new SolidColorBrush(Color.Parse("#314764")), Foreground = Brushes.White, Padding = new Thickness(30, 10), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
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
                        statusText.Text = "绑定成功！";
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
                        statusText.Text = "连接失败，请重试";
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
            var connectCode = NormalizeWhitespace(YokonexConnectCode.Text);
            var targetId = YokonexTargetId.Text ?? "";
            
            if (string.IsNullOrWhiteSpace(connectCode))
            {
                ShowStatus("请填写连接码 connect_code（格式：UID 空格 Token）");
                return;
            }
            
            Logger.Information("Connecting to Yokonex via IM: hasTarget={HasTarget}", !string.IsNullOrWhiteSpace(targetId));
            var config = new ConnectionConfig
            {
                ConnectCode = connectCode,
                TargetUserId = targetId,
                AutoReconnect = true,
                ConnectionMode = ConnectionMode.TencentIM,
                YokonexType = _selectedYokonexType,
                YokonexProtocolGeneration = YokonexProtocolGeneration.IMEvent
            };
            var deviceId = await AppServices.Instance.DeviceManager.AddDeviceAsync(
                DeviceType.Yokonex,
                config,
                name,
                isVirtual: false,
                mode: ConnectionMode.TencentIM,
                yokonexType: _selectedYokonexType);
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

    private async void OnToggleDeviceConnectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ValueTuple<string, bool> tag)
            return;

        var (deviceId, shouldConnect) = tag;
        try
        {
            btn.IsEnabled = false;
            btn.Content = shouldConnect ? "连接中..." : "断开中...";

            if (shouldConnect)
            {
                await AppServices.Instance.DeviceManager.ConnectDeviceAsync(deviceId);
                ShowStatus("设备连接成功");
            }
            else
            {
                await AppServices.Instance.DeviceManager.DisconnectDeviceAsync(deviceId);
                ShowStatus("设备已断开");
            }

            RefreshDeviceList();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Toggle device connection failed");
            ShowStatus($"操作失败: {ex.Message}");
            btn.IsEnabled = true;
            btn.Content = shouldConnect ? "连接" : "断开";
        }
    }

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
            message.AppendLine($"适配器可用: {(result.AdapterAvailable ? "是" : "否")}");
            message.AppendLine($"蓝牙已开启: {(result.AdapterEnabled ? "是" : "否")}");
            message.AppendLine($"支持 BLE: {(result.SupportsBle ? "是" : "否")}");
            message.AppendLine($"Windows 版本: {result.WindowsVersion}");
            if (result.Issues.Count > 0) { message.AppendLine("\n问题:"); foreach (var issue in result.Issues) message.AppendLine($"   {issue}"); }
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
        var dialog = new Window { Title = title, Width = 450, Height = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.Parse("#172033")) };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 15 };
        panel.Children.Add(new TextBlock { Text = isSuccess ? "诊断通过" : "发现问题", Foreground = new SolidColorBrush(Color.Parse(isSuccess ? "#10B981" : "#F59E0B")), FontSize = 18, FontWeight = FontWeight.Bold, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });
        panel.Children.Add(new TextBox { Text = content, IsReadOnly = true, FontSize = 12, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 250, Background = new SolidColorBrush(Color.Parse("#1F2B3E")), Foreground = new SolidColorBrush(Color.Parse("#E6EDF7")) });
        var closeBtn = new Button { Content = "关闭", Background = new SolidColorBrush(Color.Parse("#0EA5E9")), Foreground = Brushes.White, Padding = new Thickness(30, 10), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
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
            scanButton.Content = "扫描中...";
            scanButton.IsEnabled = false;
            deviceList.Children.Clear();
            deviceList.Children.Add(new TextBlock { Text = "正在扫描蓝牙设备，请稍候...", Foreground = new SolidColorBrush(Color.Parse("#F59E0B")), FontSize = 12, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });

            using var transport = new WindowsBluetoothTransport();
            
            // 根据设备类型选择服务 UUID
            Guid? serviceFilter = null;
            if (deviceType == DeviceType.Yokonex)
            {
                serviceFilter = _selectedYokonexType switch
                {
                    YokonexDeviceType.Estim => Guid.Parse("0000ff30-0000-1000-8000-00805f9b34fb"),
                    YokonexDeviceType.Enema => Guid.Parse("0000ffb0-0000-1000-8000-00805f9b34fb"),
                    YokonexDeviceType.Vibrator or YokonexDeviceType.Cup => Guid.Parse("0000ff40-0000-1000-8000-00805f9b34fb"),
                    YokonexDeviceType.SmartLock => null,
                    _ => Guid.Parse("0000ff30-0000-1000-8000-00805f9b34fb")
                };
            }
            
            Logger.Information("Starting Bluetooth scan for {Type} devices (YokonexType={YokonexType}, ServiceUUID={ServiceUUID})...", 
                deviceType, _selectedYokonexType, serviceFilter);
            var devices = await transport.ScanAsync(serviceFilter: serviceFilter, namePrefix: null, timeoutMs: 8000);
            deviceList.Children.Clear();

            if (devices.Length == 0)
            {
                var noDevicePanel = new StackPanel { Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
                noDevicePanel.Children.Add(new TextBlock { Text = "未发现设备", Foreground = new SolidColorBrush(Color.Parse("#EF4444")), FontSize = 13, FontWeight = FontWeight.SemiBold });
                noDevicePanel.Children.Add(new TextBlock { Text = "• 设备是否已开启电源", Foreground = new SolidColorBrush(Color.Parse("#9AB0C8")), FontSize = 12 });
                noDevicePanel.Children.Add(new TextBlock { Text = "• 设备是否在蓝牙范围内", Foreground = new SolidColorBrush(Color.Parse("#9AB0C8")), FontSize = 12 });
                noDevicePanel.Children.Add(new TextBlock { Text = "• 是否选择了正确的设备类型", Foreground = new SolidColorBrush(Color.Parse("#9AB0C8")), FontSize = 12 });
                var diagnoseBtn = new Button { Content = "诊断蓝牙", Background = new SolidColorBrush(Color.Parse("#F59E0B")), Foreground = Brushes.White, Padding = new Thickness(12, 6), Margin = new Thickness(0, 10, 0, 0) };
                diagnoseBtn.Click += OnDiagnoseBluetoothClick;
                noDevicePanel.Children.Add(diagnoseBtn);
                deviceList.Children.Add(noDevicePanel);
            }
            else
            {
                var relevantDevices = devices.Where(d =>
                {
                    if (deviceType != DeviceType.DGLab)
                    {
                        return true;
                    }

                    var expectedPrefix = DGLabBluetoothAdapter.GetDeviceNamePrefix(_selectedDGLabVersion);
                    if (!string.IsNullOrWhiteSpace(expectedPrefix))
                    {
                        return d.Name.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase);
                    }

                    return d.Name.StartsWith("47L12", StringComparison.OrdinalIgnoreCase) ||
                           d.Name.StartsWith("D-LAB", StringComparison.OrdinalIgnoreCase);
                }).ToArray();
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
            scanButton.Content = "扫描设备";
            scanButton.IsEnabled = true;
            _isScanning = false;
        }
    }

    private Border CreateBluetoothDeviceCard(BleDeviceInfo device, DeviceType deviceType)
    {
        var card = new Border { Background = new SolidColorBrush(Color.Parse("#172033")), CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = new Thickness(0, 4, 0, 4) };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var info = new StackPanel();
        info.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(device.Name) ? "未知设备" : device.Name, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, FontSize = 13 });
        info.Children.Add(new TextBlock { Text = $"MAC: {device.MacAddress} | 信号: {device.Rssi} dBm", Foreground = new SolidColorBrush(Color.Parse("#9AB0C8")), FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(info, 0);
        // 郊狼用紫色，役次元用橙色
        var connectBtn = new Button
        {
            Content = "连接",
            Background = new SolidColorBrush(Color.Parse(deviceType == DeviceType.DGLab ? "#0EA5E9" : "#F59E0B")),
            Foreground = Brushes.White,
            Padding = new Thickness(14, 8),
            Tag = (device.Id, device.Name, deviceType),
            CornerRadius = new CornerRadius(6)
        };
        connectBtn.Click += OnConnectBluetoothDeviceClick;
        Grid.SetColumn(connectBtn, 1);
        grid.Children.Add(info);
        grid.Children.Add(connectBtn);
        card.Child = grid;
        return card;
    }

    private async void OnConnectBluetoothDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not (string deviceId, string deviceName, DeviceType deviceType)) return;
        try
        {
            btn.Content = "连接中...";
            btn.IsEnabled = false;
            Logger.Information("Connecting to Bluetooth device {Id} as {Type} (DGLabVersion={DGLabVersion}, YokonexType={YokonexType})", 
                deviceId, deviceType, _selectedDGLabVersion, _selectedYokonexType);
            var yokonexProtocolGeneration = _selectedYokonexType switch
            {
                YokonexDeviceType.Estim => ResolveEstimProtocolGenerationByName(deviceName),
                YokonexDeviceType.Enema => YokonexProtocolGeneration.EnemaV1_0,
                YokonexDeviceType.Vibrator => YokonexProtocolGeneration.ToyV1_1,
                YokonexDeviceType.Cup => YokonexProtocolGeneration.ToyV1_1,
                YokonexDeviceType.SmartLock => YokonexProtocolGeneration.SmartLockReserved,
                _ => YokonexProtocolGeneration.EmsV1_6
            };

            var config = new ConnectionConfig
            {
                Address = deviceId,
                AutoReconnect = true,
                ConnectionMode = ConnectionMode.Bluetooth,
                DGLabVersion = deviceType == DeviceType.DGLab ? _selectedDGLabVersion : null,
                YokonexType = deviceType == DeviceType.Yokonex ? _selectedYokonexType : null,
                YokonexProtocolGeneration = deviceType == DeviceType.Yokonex ? yokonexProtocolGeneration : null
            };
            
            // 使用选择的DG-LAB版本
            var dglabVersion = _selectedDGLabVersion;
            var dglabVersionName = dglabVersion switch
            {
                DGLabVersion.V2 => "V2",
                DGLabVersion.V3 => "V3",
                DGLabVersion.V3WirelessSensor => "47L120100",
                DGLabVersion.PawPrints => "爪印",
                _ => "V3"
            };
            
            // 使用选择的役次元设备类型
            var yokonexType = _selectedYokonexType;
            var yokonexTypeName = yokonexType switch
            {
                YokonexDeviceType.Estim => "电击器",
                YokonexDeviceType.Enema => "灌肠器",
                YokonexDeviceType.Vibrator => "跳蛋",
                YokonexDeviceType.Cup => "飞机杯",
                _ => "设备"
            };
            string name = deviceType == DeviceType.DGLab ? $"蓝牙郊狼-{dglabVersionName}" : $"蓝牙役次元-{yokonexTypeName}";
            var newDeviceId = await AppServices.Instance.DeviceManager.AddDeviceAsync(
                deviceType,
                config,
                name,
                isVirtual: false,
                mode: ConnectionMode.Bluetooth,
                dglabVersion: dglabVersion,
                yokonexType: yokonexType);
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

    private static YokonexProtocolGeneration ResolveEstimProtocolGenerationByName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return YokonexProtocolGeneration.EmsV1_6;
        }

        // 厂商侧常见命名包含 2.0 / V2 时优先走 EmsV2_0
        if (deviceName.Contains("2.0", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Contains("v2", StringComparison.OrdinalIgnoreCase))
        {
            return YokonexProtocolGeneration.EmsV2_0;
        }

        return YokonexProtocolGeneration.EmsV1_6;
    }

    #region Quick Add Buttons

    /// <summary>
    /// 快速添加郊狼设备 (WebSocket 方式)
    /// </summary>
    private void OnQuickAddDGLabClick(object? sender, RoutedEventArgs e)
    {
        // 切换到 DG-LAB 选项卡
        OnTabDGLabClick(sender, e);
        // 切换到 WebSocket 模式
        OnDGLabConnModeWSClick(sender, e);
        // 触发添加
        OnAddDGLabWSClick(sender, e);
    }

    /// <summary>
    /// 快速添加役次元设备 (蓝牙扫描)
    /// </summary>
    private void OnQuickAddYokonexClick(object? sender, RoutedEventArgs e)
    {
        // 切换到役次元选项卡
        OnTabYokonexClick(sender, e);
        // 切换到蓝牙模式
        OnYokonexConnModeBTClick(sender, e);
        // 触发蓝牙扫描
        OnScanYokonexBTClick(sender, e);
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


