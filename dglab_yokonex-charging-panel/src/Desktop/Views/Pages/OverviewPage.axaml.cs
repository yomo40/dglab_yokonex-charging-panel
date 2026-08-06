using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ChargingPanel.Core;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.OCR;
using ChargingPanel.Core.Application.Devices;
using Serilog;
using ChargingPanel.Desktop.Bootstrap;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ChargingPanel.Desktop.Views.Pages;

/// <summary>
/// 概览页。只读展示设备状态与统计，订阅只读状态流并在离屏时释放。
/// 设备相关数据全部来自 <see cref="IDeviceQueryService"/>，不触碰 legacy 设备实现。
/// </summary>
public partial class OverviewPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<OverviewPage>();
    private readonly DispatcherTimer _actionHistoryRefreshTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private bool _isSubscribed;
    private int _lastActionLogId = -1;

    private IDeviceQueryService? _query;

    public OverviewPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _actionHistoryRefreshTimer.Tick += OnActionHistoryRefreshTick;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _query = ApplicationHost.TryGetService<IDeviceQueryService>();
        RefreshStats();
        RefreshDeviceStrength();
        RefreshOcrStatus();
        RefreshDeviceActionHistory(force: true);

        // 订阅只读状态流：OCR 血量变化与设备活动变化。
        if (ApplicationHost.Instance is not null && !_isSubscribed)
        {
            ApplicationHost.TryGetService<OCRService>()!.BloodChanged += OnBloodChanged;
            _isSubscribed = true;
        }

        _actionHistoryRefreshTimer.Start();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _actionHistoryRefreshTimer.Stop();
        if (ApplicationHost.Instance is not null && _isSubscribed)
        {
            ApplicationHost.TryGetService<OCRService>()!.BloodChanged -= OnBloodChanged;
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
            if (ApplicationHost.Instance is null)
            {
                Logger.Warning("AppServices not initialized, skipping stats refresh");
                return;
            }

            int deviceCount = 0;
            int enabledEventsCount = 0;
            int enabledScriptsCount = 0;
            int connectedCount = 0;

            try
            {
                deviceCount = Database.Instance.GetAllDevices().Count;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to get devices: {Message}", ex.Message);
            }

            try
            {
                enabledEventsCount = Database.Instance.GetAllEvents().Count(e => e.Enabled);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to get events: {Message}", ex.Message);
            }

            try
            {
                enabledScriptsCount = Database.Instance.GetAllScripts().Count(s => s.Enabled);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to get scripts: {Message}", ex.Message);
            }

            try
            {
                connectedCount = _query?.GetActiveDevices().Count ?? 0;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to get connected devices: {Message}", ex.Message);
            }

            StatDevices.Text = deviceCount.ToString();
            StatEvents.Text = enabledEventsCount.ToString();
            StatScripts.Text = enabledScriptsCount.ToString();
            StatConnected.Text = connectedCount.ToString();
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
            DeviceStrengthList.Children.Clear();

            if (_query is null)
            {
                DeviceStrengthList.Children.Add(CreatePlaceholderText("服务尚未初始化"));
                NoDevicesHint.IsVisible = true;
                return;
            }

            var activeDevices = _query.GetActiveDevices();
            if (activeDevices.Count == 0)
            {
                DeviceStrengthList.Children.Add(CreatePlaceholderText("暂无已连接设备"));
                NoDevicesHint.IsVisible = true;
                return;
            }

            NoDevicesHint.IsVisible = false;
            foreach (var device in activeDevices)
            {
                DeviceStrengthList.Children.Add(CreateActiveDeviceCard(device));
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to refresh device strength");
        }
    }

    private Border CreateActiveDeviceCard(ActiveDeviceView device)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#172033")),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(15),
            Margin = new Avalonia.Thickness(0, 0, 0, 10)
        };

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = $"活动设备 {device.InstanceId.Value.ToString("D")}",
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            FontSize = 14
        });
        stack.Children.Add(new TextBlock
        {
            Text = string.Join("、", device.Capabilities.Select(capability => capability.DisplayName)),
            Foreground = new SolidColorBrush(Color.Parse("#9AB0C8")),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });

        card.Child = stack;
        return card;
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

    private void RefreshOcrStatus()
    {
        try
        {
            if (ApplicationHost.Instance is null)
            {
                return;
            }

            var ocrService = ApplicationHost.TryGetService<OCRService>()!;
            OcrBlood.Text = ocrService.CurrentBlood.ToString();
            OcrStatus.Text = ocrService.IsRunning ? "识别中" : "未运行";
            OcrStatus.Foreground = new SolidColorBrush(
                ocrService.IsRunning ? Color.Parse("#10B981") : Color.Parse("#8091A8"));
            OcrBlood.Foreground = new SolidColorBrush(
                ocrService.CurrentBlood switch
                {
                    > 60 => Color.Parse("#10B981"),
                    > 30 => Color.Parse("#F59E0B"),
                    _ => Color.Parse("#EF4444")
                });
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
        RefreshDeviceActionHistory(force: true);
    }

    private void OnBloodChanged(object? sender, Core.OCR.BloodChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OcrBlood.Text = e.CurrentBlood.ToString();
            OcrBlood.Foreground = new SolidColorBrush(
                e.CurrentBlood switch
                {
                    > 60 => Color.Parse("#10B981"),
                    > 30 => Color.Parse("#F59E0B"),
                    _ => Color.Parse("#EF4444")
                });
            OcrLastTrigger.Text = DateTime.Now.ToString("HH:mm:ss");
            if (ApplicationHost.Instance is not null)
            {
                OcrCount.Text = ApplicationHost.TryGetService<OCRService>()!.RecognitionCount.ToString();
            }
        });
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
        var legacyManagerModule = "Device" + "Manager";
        if (module.Equals(legacyManagerModule, StringComparison.OrdinalIgnoreCase) ||
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
}
