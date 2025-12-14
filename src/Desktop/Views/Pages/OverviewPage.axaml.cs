using Avalonia.Controls;
using Avalonia.Interactivity;
using ChargingPanel.Core;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices;
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
            if (!AppServices.IsInitialized) return;
            
            var deviceManager = AppServices.Instance.DeviceManager;
            var devices = deviceManager.GetAllDevices();
            
            StatDevices.Text = devices.Count().ToString();
            StatConnected.Text = devices.Count(d => d.Status == DeviceStatus.Connected).ToString();
            
            // 获取脚本和事件数量
            var scripts = Database.Instance.GetAllScripts();
            var events = Database.Instance.GetAllEvents();
            
            StatScripts.Text = scripts.Count(s => s.Enabled).ToString();
            StatEvents.Text = events.Count(e => e.Enabled).ToString();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to refresh stats");
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
                var info = AppServices.Instance.DeviceManager.GetDeviceInfo(device.Id);
                var deviceCard = CreateDeviceStrengthCard(device.Name, device.Type, info.State.Strength);
                DeviceStrengthList.Children.Add(deviceCard);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to refresh device strength");
        }
    }

    private Border CreateDeviceStrengthCard(string name, DeviceType type, StrengthInfo strength)
    {
        var card = new Border
        {
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1E1E2E")),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(15),
            Margin = new Avalonia.Thickness(0, 0, 0, 10)
        };

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

        card.Child = grid;
        return card;
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
            
            var enabledEvents = Database.Instance.GetAllEvents().Count(e => e.Enabled);
            OcrRules.Text = enabledEvents.ToString();
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
        });
    }
}
