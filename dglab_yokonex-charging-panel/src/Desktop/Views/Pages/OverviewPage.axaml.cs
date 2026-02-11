using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ChargingPanel.Core;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.DGLab;
using ChargingPanel.Core.Devices.Yokonex;
using ChargingPanel.Core.Protocols;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ChargingPanel.Desktop.Views.Pages;

public partial class OverviewPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<OverviewPage>();
    private readonly DispatcherTimer _actionHistoryRefreshTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private bool _isSubscribed;
    private int _lastActionLogId = -1;

    public OverviewPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _actionHistoryRefreshTimer.Tick += OnActionHistoryRefreshTick;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RefreshStats();
        RefreshDeviceStrength();
        RefreshOcrStatus();
        RefreshQuickControls();
        RefreshDeviceActionHistory(force: true);
        
        // 订阅事件
        if (AppServices.IsInitialized && !_isSubscribed)
        {
            AppServices.Instance.DeviceManager.DeviceStatusChanged += OnDeviceStatusChanged;
            AppServices.Instance.OCRService.BloodChanged += OnBloodChanged;
            _isSubscribed = true;
        }

        _actionHistoryRefreshTimer.Start();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _actionHistoryRefreshTimer.Stop();
        if (AppServices.IsInitialized && _isSubscribed)
        {
            AppServices.Instance.DeviceManager.DeviceStatusChanged -= OnDeviceStatusChanged;
            AppServices.Instance.OCRService.BloodChanged -= OnBloodChanged;
            _isSubscribed = false;
        }
    }

    private void OnActionHistoryRefreshTick(object? sender, EventArgs e)
    {
        RefreshDeviceActionHistory();
    }

    private void RefreshStats()
    {
        try
        {
            // 检查 AppServices 是否已初始化
            if (!AppServices.IsInitialized)
            {
                Logger.Warning("AppServices not initialized, skipping stats refresh");
                return;
            }
            
            // 分别获取数据，捕获每个操作的异常
            int deviceCount = 0;
            int enabledEventsCount = 0;
            int enabledScriptsCount = 0;
            int connectedCount = 0;
            
            try
            {
                var registeredDevices = Database.Instance.GetAllDevices();
                deviceCount = registeredDevices.Count;
                Logger.Information("Loaded {Count} registered devices", deviceCount);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to get devices: {Message}", ex.Message);
            }
            
            try
            {
                var events = Database.Instance.GetAllEvents();
                enabledEventsCount = events.Count(e => e.Enabled);
                Logger.Information("Loaded {Count} events, {Enabled} enabled", events.Count, enabledEventsCount);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to get events: {Message}", ex.Message);
            }
            
            try
            {
                var scripts = Database.Instance.GetAllScripts();
                enabledScriptsCount = scripts.Count(s => s.Enabled);
                Logger.Information("Loaded {Count} scripts, {Enabled} enabled", scripts.Count, enabledScriptsCount);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to get scripts: {Message}", ex.Message);
            }
            
            try
            {
                connectedCount = AppServices.Instance.DeviceManager.GetAllDevices()
                    .Count(d => d.Status == DeviceStatus.Connected);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to get connected devices: {Message}", ex.Message);
            }
            
            // 更新 UI
            StatDevices.Text = deviceCount.ToString();
            StatEvents.Text = enabledEventsCount.ToString();
            StatScripts.Text = enabledScriptsCount.ToString();
            StatConnected.Text = connectedCount.ToString();
            
            Logger.Information("Stats updated: devices={Devices}, events={Events}, scripts={Scripts}, connected={Connected}", 
                deviceCount, enabledEventsCount, enabledScriptsCount, connectedCount);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to refresh stats: {Message}", ex.Message);
        }
    }

    private void RefreshDeviceStrength()
    {
        try
        {
            if (!AppServices.IsInitialized) return;
            
            var devices = AppServices.Instance.DeviceManager.GetAllDevices()
                .Where(d => d.Status == DeviceStatus.Connected)
                .ToList();
            
            DeviceStrengthList.Children.Clear();
            
            if (devices.Count == 0)
            {
                DeviceStrengthList.Children.Add(new TextBlock
                {
                    Text = "暂无已连接设备",
                    Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8091A8")),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                });
                return;
            }

            foreach (var device in devices)
            {
                var actualDevice = AppServices.Instance.DeviceManager.GetDevice(device.Id);
                var deviceCard = CreateDeviceStrengthCard(device.Name, device.Type, device.State.Strength, actualDevice);
                DeviceStrengthList.Children.Add(deviceCard);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to refresh device strength");
        }
    }

    private Border CreateDeviceStrengthCard(string name, DeviceType type, StrengthInfo strength, IDevice? device = null)
    {
        var card = new Border
        {
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#172033")),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(15),
            Margin = new Avalonia.Thickness(0, 0, 0, 10)
        };

        var mainStack = new StackPanel { Spacing = 8 };
        
        // 第一行：设备信息
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto")
        };

        // 设备图标
        var icon = new TextBlock
        {
            Text = type == DeviceType.DGLab ? "郊狼" : "役次元",
            FontSize = 20,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(icon, 0);

        // 设备名称
        var nameBlock = new TextBlock
        {
            Text = name,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.White),
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetColumn(nameBlock, 1);

        // 通道A
        var channelA = CreateChannelIndicator("A", strength.ChannelA, "#0EA5E9");
        Grid.SetColumn(channelA, 2);

        // 通道B
        var channelB = CreateChannelIndicator("B", strength.ChannelB, "#14B8A6");
        Grid.SetColumn(channelB, 3);

        grid.Children.Add(icon);
        grid.Children.Add(nameBlock);
        grid.Children.Add(channelA);
        grid.Children.Add(channelB);
        
        mainStack.Children.Add(grid);
        
        // 第二行：设备模式信息
        var modePanel = new StackPanel 
        { 
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 12,
            Margin = new Avalonia.Thickness(30, 0, 0, 0)
        };
        
        // 根据设备类型显示不同的模式信息
        if (type == DeviceType.DGLab)
        {
            modePanel.Children.Add(CreateModeBadge("波形模式", "#0EA5E9"));
            modePanel.Children.Add(CreateModeBadge($"上限: {strength.LimitA}/{strength.LimitB}", "#314764"));
        }
        else
        {
            modePanel.Children.Add(CreateModeBadge("震动模式", "#14B8A6"));
            modePanel.Children.Add(CreateModeBadge("电击模式", "#F59E0B"));
        }
        
        mainStack.Children.Add(modePanel);
        
        // 第三行：役次元设备特有信息 (通道连接状态、计步器、角度)
        if (type == DeviceType.Yokonex && device is Core.Devices.Yokonex.IYokonexEmsDevice emsDevice)
        {
            var yokonexPanel = new StackPanel 
            { 
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 12,
                Margin = new Avalonia.Thickness(30, 4, 0, 0)
            };
            
            // 通道连接状态
            var connState = emsDevice.ChannelConnectionState;
            var connAColor = connState.ChannelA ? "#10B981" : "#EF4444";
            var connBColor = connState.ChannelB ? "#10B981" : "#EF4444";
            yokonexPanel.Children.Add(CreateConnectionIndicator("A", connState.ChannelA, connAColor));
            yokonexPanel.Children.Add(CreateConnectionIndicator("B", connState.ChannelB, connBColor));
            
            // 计步器
            yokonexPanel.Children.Add(CreateModeBadge($"{emsDevice.StepCount} 步", "#314764"));
            
            // 角度
            var angle = emsDevice.CurrentAngle;
            yokonexPanel.Children.Add(CreateModeBadge($"X:{angle.X:F0}° Y:{angle.Y:F0}° Z:{angle.Z:F0}°", "#314764"));
            
            mainStack.Children.Add(yokonexPanel);
        }

        card.Child = mainStack;
        return card;
    }
    
    private StackPanel CreateConnectionIndicator(string channel, bool connected, string color)
    {
        var panel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4
        };
        
        // 连接状态指示灯
        var indicator = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(color)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        
        var label = new TextBlock
        {
            Text = $"{channel}:{(connected ? "接入" : "断开")}",
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9AB0C8")),
            FontSize = 11,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        
        panel.Children.Add(indicator);
        panel.Children.Add(label);
        return panel;
    }
    
    private Border CreateModeBadge(string text, string bgColor)
    {
        return new Border
        {
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(bgColor)),
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(8, 4),
            Child = new TextBlock
            {
                Text = text,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.White),
                FontSize = 11,
                FontWeight = Avalonia.Media.FontWeight.SemiBold
            }
        };
    }

    private StackPanel CreateChannelIndicator(string channel, int value, string color)
    {
        var panel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Margin = new Avalonia.Thickness(15, 0, 0, 0)
        };

        panel.Children.Add(new TextBlock
        {
            Text = $"{channel}: ",
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9AB0C8")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });

        panel.Children.Add(new TextBlock
        {
            Text = value.ToString(),
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(color)),
            FontWeight = Avalonia.Media.FontWeight.Bold,
            FontSize = 16,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });

        return panel;
    }

    private void RefreshOcrStatus()
    {
        try
        {
            if (!AppServices.IsInitialized) return;
            
            var ocrService = AppServices.Instance.OCRService;
            
            OcrBlood.Text = ocrService.CurrentBlood.ToString();
            OcrStatus.Text = ocrService.IsRunning ? "识别中" : "未运行";
            OcrStatus.Foreground = new Avalonia.Media.SolidColorBrush(
                ocrService.IsRunning 
                    ? Avalonia.Media.Color.Parse("#10B981") 
                    : Avalonia.Media.Color.Parse("#8091A8"));
            
            // 血量颜色
            OcrBlood.Foreground = new Avalonia.Media.SolidColorBrush(
                ocrService.CurrentBlood switch
                {
                    > 60 => Avalonia.Media.Color.Parse("#10B981"),
                    > 30 => Avalonia.Media.Color.Parse("#F59E0B"),
                    _ => Avalonia.Media.Color.Parse("#EF4444")
                });
            
            // OcrCount 显示识别次数
            OcrCount.Text = ocrService.RecognitionCount.ToString();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to refresh OCR status");
        }
    }

    private void OnRefreshDeviceStrength(object? sender, RoutedEventArgs e)
    {
        RefreshStats();
        RefreshDeviceStrength();
        RefreshQuickControls();
        RefreshDeviceActionHistory(force: true);
    }

    private void OnDeviceStatusChanged(object? sender, DeviceStatusChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RefreshStats();
            RefreshDeviceStrength();
            RefreshQuickControls();
            RefreshDeviceActionHistory(force: true);
        });
    }

    private void OnBloodChanged(object? sender, Core.OCR.BloodChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            OcrBlood.Text = e.CurrentBlood.ToString();
            OcrBlood.Foreground = new Avalonia.Media.SolidColorBrush(
                e.CurrentBlood switch
                {
                    > 60 => Avalonia.Media.Color.Parse("#10B981"),
                    > 30 => Avalonia.Media.Color.Parse("#F59E0B"),
                    _ => Avalonia.Media.Color.Parse("#EF4444")
                });
            
            OcrLastTrigger.Text = DateTime.Now.ToString("HH:mm:ss");
            
            // 更新识别次数
            if (AppServices.IsInitialized)
            {
                OcrCount.Text = AppServices.Instance.OCRService.RecognitionCount.ToString();
            }
        });
    }

    private void RefreshQuickControls()
    {
        try
        {
            QuickControlList.Children.Clear();

            if (!AppServices.IsInitialized)
            {
                QuickControlList.Children.Add(CreatePlaceholderText("服务尚未初始化"));
                return;
            }

            var manager = AppServices.Instance.DeviceManager;
            var connectedDevices = manager.GetAllDevices()
                .Where(d => d.Status == DeviceStatus.Connected)
                .Where(d => d switch
                {
                    { Type: DeviceType.DGLab, DGLabVersion: DGLabVersion.V3WirelessSensor or DGLabVersion.PawPrints } => false,
                    { Type: DeviceType.Yokonex, YokonexType: YokonexDeviceType.SmartLock } => false,
                    _ => true
                })
                .ToList();

            if (connectedDevices.Count == 0)
            {
                QuickControlList.Children.Add(CreatePlaceholderText("暂无已连接设备"));
                return;
            }

            var waveformPresets = Database.Instance.GetAllWaveformPresets();
            foreach (var info in connectedDevices)
            {
                var device = manager.GetDevice(info.Id);
                QuickControlList.Children.Add(CreateQuickControlCard(info, device, waveformPresets));
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to refresh quick controls");
            QuickControlList.Children.Clear();
            QuickControlList.Children.Add(CreatePlaceholderText("快捷控制加载失败"));
        }
    }

    private static TextBlock CreatePlaceholderText(string text)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.Parse("#8091A8")),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 13
        };
    }

    private Border CreateQuickControlCard(DeviceInfo info, IDevice device, IReadOnlyList<WaveformPresetRecord> waveformPresets)
    {
        var root = new StackPanel { Spacing = 10 };
        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#172033")),
            CornerRadius = new Avalonia.CornerRadius(10),
            Padding = new Avalonia.Thickness(12),
            Margin = new Avalonia.Thickness(0, 0, 0, 10),
            Child = root
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        var typeTag = GetDeviceTypeLabel(info, device);
        var title = new TextBlock
        {
            Text = info.Name,
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 0);

        var badge = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1F2B3E")),
            CornerRadius = new Avalonia.CornerRadius(5),
            Padding = new Avalonia.Thickness(8, 3),
            Child = new TextBlock
            {
                Text = typeTag,
                Foreground = new SolidColorBrush(Color.Parse("#9AB0C8")),
                FontSize = 11
            }
        };
        Grid.SetColumn(badge, 1);
        header.Children.Add(title);
        header.Children.Add(badge);
        root.Children.Add(header);

        if (IsElectroshockDevice(info, device))
        {
            var strength = info.State.Strength;
            root.Children.Add(CreateElectroStrengthRow("A", info.Id, Channel.A, strength.ChannelA, strength.LimitA));
            root.Children.Add(CreateElectroStrengthRow("B", info.Id, Channel.B, strength.ChannelB, strength.LimitB));
            root.Children.Add(CreateWaveformControlRow(info.Id, waveformPresets));
        }
        else
        {
            root.Children.Add(CreateGenericStrengthRow(info));
            root.Children.Add(CreateGenericModeRow(info.Id, device));
        }

        return card;
    }

    private Grid CreateElectroStrengthRow(string channelName, string deviceId, Channel channel, int currentValue, int limitValue)
    {
        var max = Math.Max(limitValue, 100);
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            ColumnSpacing = 8
        };

        var channelText = new TextBlock
        {
            Text = $"通道{channelName}",
            Foreground = new SolidColorBrush(Color.Parse("#9AB0C8")),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(channelText, 0);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = max,
            Value = Math.Clamp(currentValue, 0, max),
            MinWidth = 180,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetColumn(slider, 1);

        var valueText = new TextBlock
        {
            Text = ((int)slider.Value).ToString(),
            Foreground = Brushes.White,
            Width = 36,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(valueText, 2);

        slider.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty)
            {
                valueText.Text = ((int)Math.Round(slider.Value)).ToString();
            }
        };

        var applyButton = new Button
        {
            Content = "发送",
            Padding = new Avalonia.Thickness(10, 4),
            Background = new SolidColorBrush(Color.Parse("#0EA5E9")),
            Foreground = Brushes.White
        };
        applyButton.Click += async (_, _) =>
        {
            await ApplyStrengthAsync(deviceId, channel, (int)Math.Round(slider.Value));
        };
        Grid.SetColumn(applyButton, 3);

        row.Children.Add(channelText);
        row.Children.Add(slider);
        row.Children.Add(valueText);
        row.Children.Add(applyButton);
        return row;
    }

    private Grid CreateWaveformControlRow(string deviceId, IReadOnlyList<WaveformPresetRecord> waveformPresets)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            ColumnSpacing = 8
        };

        var label = new TextBlock
        {
            Text = "波形",
            Foreground = new SolidColorBrush(Color.Parse("#9AB0C8")),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 0);

        var waveformSelect = new ComboBox
        {
            MinWidth = 180,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var preset in waveformPresets)
        {
            waveformSelect.Items.Add(new ComboBoxItem
            {
                Content = $"{preset.Icon} {preset.Name}",
                Tag = preset.Id
            });
        }

        if (waveformSelect.Items.Count > 0)
        {
            waveformSelect.SelectedIndex = 0;
        }
        Grid.SetColumn(waveformSelect, 1);

        var sendButton = new Button
        {
            Content = "发送波形",
            Padding = new Avalonia.Thickness(10, 4),
            Background = new SolidColorBrush(Color.Parse("#0EA5E9")),
            Foreground = Brushes.White
        };
        sendButton.Click += async (_, _) =>
        {
            await SendSelectedWaveformAsync(deviceId, waveformSelect);
        };
        Grid.SetColumn(sendButton, 2);

        var clearButton = new Button
        {
            Content = "清空",
            Padding = new Avalonia.Thickness(10, 4),
            Background = new SolidColorBrush(Color.Parse("#475569")),
            Foreground = Brushes.White
        };
        clearButton.Click += async (_, _) =>
        {
            if (!AppServices.IsInitialized)
            {
                return;
            }

            try
            {
                await AppServices.Instance.DeviceManager.ClearWaveformQueueAsync(deviceId, Channel.A);
                await AppServices.Instance.DeviceManager.ClearWaveformQueueAsync(deviceId, Channel.B);
                RefreshDeviceActionHistory(force: true);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to clear waveform queue for quick control");
            }
        };
        Grid.SetColumn(clearButton, 3);

        row.Children.Add(label);
        row.Children.Add(waveformSelect);
        row.Children.Add(sendButton);
        row.Children.Add(clearButton);
        return row;
    }

    private Grid CreateGenericStrengthRow(DeviceInfo info)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            ColumnSpacing = 8
        };

        var label = new TextBlock
        {
            Text = "强度",
            Foreground = new SolidColorBrush(Color.Parse("#9AB0C8")),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 0);

        var max = 100;
        var current = Math.Max(info.State.Strength.ChannelA, info.State.Strength.ChannelB);
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = max,
            Value = Math.Clamp(current, 0, max),
            MinWidth = 180,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetColumn(slider, 1);

        var valueText = new TextBlock
        {
            Text = ((int)slider.Value).ToString(),
            Foreground = Brushes.White,
            Width = 36,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        slider.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty)
            {
                valueText.Text = ((int)Math.Round(slider.Value)).ToString();
            }
        };
        Grid.SetColumn(valueText, 2);

        var applyButton = new Button
        {
            Content = "发送",
            Padding = new Avalonia.Thickness(10, 4),
            Background = new SolidColorBrush(Color.Parse("#10B981")),
            Foreground = Brushes.White
        };
        applyButton.Click += async (_, _) =>
        {
            await ApplyStrengthAsync(info.Id, Channel.AB, (int)Math.Round(slider.Value));
        };
        Grid.SetColumn(applyButton, 3);

        row.Children.Add(label);
        row.Children.Add(slider);
        row.Children.Add(valueText);
        row.Children.Add(applyButton);
        return row;
    }

    private Grid CreateGenericModeRow(string deviceId, IDevice device)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 8
        };

        var label = new TextBlock
        {
            Text = "模式",
            Foreground = new SolidColorBrush(Color.Parse("#9AB0C8")),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 0);

        var modeSelect = new ComboBox
        {
            MinWidth = 180,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        if (device is IYokonexToyDevice)
        {
            for (var mode = 1; mode <= 8; mode++)
            {
                modeSelect.Items.Add(new ComboBoxItem { Content = $"固定模式 {mode}", Tag = mode.ToString() });
            }
        }
        else if (device is IYokonexEnemaDevice)
        {
            modeSelect.Items.Add(new ComboBoxItem { Content = "注入启动", Tag = "inject_start" });
            modeSelect.Items.Add(new ComboBoxItem { Content = "注入停止", Tag = "inject_stop" });
            modeSelect.Items.Add(new ComboBoxItem { Content = "振动低速", Tag = "vibration_low" });
            modeSelect.Items.Add(new ComboBoxItem { Content = "振动高速", Tag = "vibration_high" });
        }
        else
        {
            modeSelect.Items.Add(new ComboBoxItem { Content = "不支持模式控制", Tag = "unsupported" });
        }

        modeSelect.SelectedIndex = 0;
        Grid.SetColumn(modeSelect, 1);

        var applyButton = new Button
        {
            Content = "应用模式",
            Padding = new Avalonia.Thickness(10, 4),
            Background = new SolidColorBrush(Color.Parse("#F59E0B")),
            Foreground = Brushes.White
        };
        applyButton.Click += async (_, _) =>
        {
            var selectedTag = (modeSelect.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            await ApplyGenericModeAsync(deviceId, selectedTag);
        };
        Grid.SetColumn(applyButton, 2);

        row.Children.Add(label);
        row.Children.Add(modeSelect);
        row.Children.Add(applyButton);
        return row;
    }

    private async Task ApplyStrengthAsync(string deviceId, Channel channel, int value)
    {
        if (!AppServices.IsInitialized)
        {
            return;
        }

        try
        {
            await AppServices.Instance.DeviceManager.SetStrengthAsync(deviceId, channel, Math.Max(0, value), StrengthMode.Set, "OverviewQuickControl");
            RefreshDeviceStrength();
            RefreshDeviceActionHistory(force: true);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to apply quick control strength");
        }
    }

    private async Task SendSelectedWaveformAsync(string deviceId, ComboBox waveformSelect)
    {
        if (!AppServices.IsInitialized)
        {
            return;
        }

        var presetId = (waveformSelect.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(presetId))
        {
            return;
        }

        try
        {
            var preset = Database.Instance.GetWaveformPreset(presetId);
            if (preset == null)
            {
                return;
            }

            var waveform = WaveformPresetExchangeService.BuildWaveformData(preset);
            var channel = preset.Channel switch
            {
                "A" => Channel.A,
                "B" => Channel.B,
                _ => Channel.AB
            };

            await AppServices.Instance.DeviceManager.SendWaveformAsync(deviceId, channel, waveform);
            RefreshDeviceActionHistory(force: true);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to send quick control waveform");
        }
    }

    private async Task ApplyGenericModeAsync(string deviceId, string? selectedTag)
    {
        if (!AppServices.IsInitialized || string.IsNullOrWhiteSpace(selectedTag))
        {
            return;
        }

        try
        {
            var manager = AppServices.Instance.DeviceManager;
            var device = manager.GetDevice(deviceId);

            if (device is IYokonexToyDevice toyDevice && int.TryParse(selectedTag, out var toyMode))
            {
                await toyDevice.SetFixedModeAsync(Math.Clamp(toyMode, 1, 8));
                manager.RecordDeviceAction(
                    deviceId,
                    "模式设置",
                    new { mode = toyMode, deviceType = "toy" },
                    "OverviewQuickControl");
            }
            else if (device is IYokonexEnemaDevice enemaDevice)
            {
                switch (selectedTag)
                {
                    case "inject_start":
                        await enemaDevice.StartInjectionAsync();
                        manager.RecordDeviceAction(deviceId, "模式设置", new { mode = "inject_start" }, "OverviewQuickControl");
                        break;
                    case "inject_stop":
                        await enemaDevice.StopInjectionAsync();
                        manager.RecordDeviceAction(deviceId, "模式设置", new { mode = "inject_stop" }, "OverviewQuickControl");
                        break;
                    case "vibration_low":
                        await enemaDevice.SetVibrationStrengthAsync(30);
                        manager.RecordDeviceAction(deviceId, "模式设置", new { mode = "vibration_low" }, "OverviewQuickControl");
                        break;
                    case "vibration_high":
                        await enemaDevice.SetVibrationStrengthAsync(80);
                        manager.RecordDeviceAction(deviceId, "模式设置", new { mode = "vibration_high" }, "OverviewQuickControl");
                        break;
                }
            }

            RefreshDeviceActionHistory(force: true);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to apply quick control mode");
        }
    }

    private void RefreshDeviceActionHistory(bool force = false)
    {
        try
        {
            var logs = Database.Instance.GetRecentLogs(300)
                .Where(IsDeviceActionLog)
                .Take(40)
                .ToList();

            var latestId = logs.Count > 0 ? logs[0].Id : -1;
            if (!force && latestId == _lastActionLogId)
            {
                return;
            }

            _lastActionLogId = latestId;
            DeviceActionHistoryList.Children.Clear();

            if (logs.Count == 0)
            {
                DeviceActionHistoryList.Children.Add(CreatePlaceholderText("暂无设备动作记录"));
                return;
            }

            foreach (var log in logs)
            {
                DeviceActionHistoryList.Children.Add(CreateDeviceActionHistoryItem(log));
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to refresh device action history");
        }
    }

    private static bool IsDeviceActionLog(LogRecord log)
    {
        var module = (log.Module ?? string.Empty).Trim();
        if (module.Equals("DeviceAction", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var message = log.Message ?? string.Empty;
        if (module.Equals("DeviceManager", StringComparison.OrdinalIgnoreCase) ||
            module.Equals("ControlPage", StringComparison.OrdinalIgnoreCase) ||
            module.Equals("RoomService", StringComparison.OrdinalIgnoreCase) ||
            module.Equals("RoomPage", StringComparison.OrdinalIgnoreCase))
        {
            return message.Contains("SetStrength", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("设置", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("波形", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("mode", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("模式", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("紧急停止", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("注入", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("马达", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("pump", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private Border CreateDeviceActionHistoryItem(LogRecord log)
    {
        var created = DateTime.TryParse(log.CreatedAt, out var createdAt)
            ? createdAt.ToLocalTime()
            : log.Timestamp;
        var displayText = string.IsNullOrWhiteSpace(log.Message) ? "(空动作)" : log.Message;
        if (displayText.Length > 90)
        {
            displayText = $"{displayText[..90]}...";
        }

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 10
        };
        row.Children.Add(new TextBlock
        {
            Text = created.ToString("HH:mm:ss"),
            Foreground = new SolidColorBrush(Color.Parse("#94A3B8")),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top
        });

        var text = new TextBlock
        {
            Text = displayText,
            Foreground = new SolidColorBrush(Color.Parse("#E2E8F0")),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#11182780")),
            BorderBrush = new SolidColorBrush(Color.Parse("#334155")),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(8, 6),
            Margin = new Avalonia.Thickness(0, 0, 0, 6),
            Child = row
        };
    }

    private static bool IsElectroshockDevice(DeviceInfo info, IDevice device)
    {
        if (info.Type == DeviceType.DGLab || info.Type == DeviceType.Virtual)
        {
            return true;
        }

        if (info.Type != DeviceType.Yokonex)
        {
            return false;
        }

        var yokonexType = ResolveYokonexType(info, device);
        return yokonexType == YokonexDeviceType.Estim;
    }

    private static YokonexDeviceType ResolveYokonexType(DeviceInfo info, IDevice device)
    {
        if (info.YokonexType.HasValue)
        {
            return info.YokonexType.Value;
        }

        if (device is IYokonexDevice yokonexDevice)
        {
            return yokonexDevice.YokonexType;
        }

        if (device is IYokonexEnemaDevice)
        {
            return YokonexDeviceType.Enema;
        }

        if (device is IYokonexToyDevice)
        {
            return YokonexDeviceType.Vibrator;
        }

        return YokonexDeviceType.Estim;
    }

    private static string GetDeviceTypeLabel(DeviceInfo info, IDevice device)
    {
        if (info.Type == DeviceType.DGLab)
        {
            return "郊狼电击";
        }

        if (info.Type == DeviceType.Virtual)
        {
            return "虚拟设备";
        }

        var yokonexType = ResolveYokonexType(info, device);
        return yokonexType switch
        {
            YokonexDeviceType.Estim => "役次元电击",
            YokonexDeviceType.Enema => "灌肠器",
            YokonexDeviceType.Vibrator => "跳蛋",
            YokonexDeviceType.Cup => "飞机杯",
            YokonexDeviceType.SmartLock => "智能锁(预留)",
            _ => "役次元设备"
        };
    }
}


