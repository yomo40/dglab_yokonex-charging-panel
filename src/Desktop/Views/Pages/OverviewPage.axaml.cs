using Avalonia.Controls;
using Avalonia.Interactivity;
using ChargingPanel.Core;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.Yokonex;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ChargingPanel.Desktop.Views.Pages;

public partial class OverviewPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<OverviewPage>();

    public OverviewPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RefreshStats();
        RefreshDeviceStrength();
        RefreshOcrStatus();
        
        // 订阅事件
        if (AppServices.IsInitialized)
        {
            AppServices.Instance.DeviceManager.DeviceStatusChanged += OnDeviceStatusChanged;
            AppServices.Instance.OCRService.BloodChanged += OnBloodChanged;
        }
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
                    Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#6B7280")),
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
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1E1E2E")),
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
            Text = type == DeviceType.DGLab ? "⚡" : "📱",
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
        var channelA = CreateChannelIndicator("A", strength.ChannelA, "#8b5cf6");
        Grid.SetColumn(channelA, 2);

        // 通道B
        var channelB = CreateChannelIndicator("B", strength.ChannelB, "#06b6d4");
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
            modePanel.Children.Add(CreateModeBadge("🌊 波形模式", "#8b5cf6"));
            modePanel.Children.Add(CreateModeBadge($"上限: {strength.LimitA}/{strength.LimitB}", "#45475A"));
        }
        else
        {
            modePanel.Children.Add(CreateModeBadge("📳 震动模式", "#06b6d4"));
            modePanel.Children.Add(CreateModeBadge("🔌 电击模式", "#F59E0B"));
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
            yokonexPanel.Children.Add(CreateModeBadge($"🚶 {emsDevice.StepCount} 步", "#45475A"));
            
            // 角度
            var angle = emsDevice.CurrentAngle;
            yokonexPanel.Children.Add(CreateModeBadge($"📐 X:{angle.X:F0}° Y:{angle.Y:F0}° Z:{angle.Z:F0}°", "#45475A"));
            
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
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#A6ADC8")),
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
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#A6ADC8")),
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
                    : Avalonia.Media.Color.Parse("#6B7280"));
            
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
    }

    private void OnDeviceStatusChanged(object? sender, DeviceStatusChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RefreshStats();
            RefreshDeviceStrength();
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
}
