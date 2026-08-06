using Avalonia.Controls;
using Avalonia.Interactivity;
using ChargingPanel.Core;
using ChargingPanel.Core.Data;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ChargingPanel.Desktop.Views.Pages;

public partial class LogsPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<LogsPage>();
    private List<LogRecord> _allLogs = new();

    public LogsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        FilterType.SelectionChanged += OnFilterChanged;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        LoadLogs();
    }

    private void LoadLogs()
    {
        try
        {
            _allLogs = Database.Instance.GetRecentLogs(500).ToList();
            RefreshLogList();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to load logs");
        }
    }

    private void RefreshLogList()
    {
        LogList.Children.Clear();
        
        var filter = (FilterType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部";
        
        var filteredLogs = filter switch
        {
            "强度变化" => _allLogs.Where(l => l.Action.Contains("strength") || l.Action.Contains("强度")),
            "波形发送" => _allLogs.Where(l => l.Action.Contains("waveform") || l.Action.Contains("波形")),
            "设备连接" => _allLogs.Where(l => l.Action.Contains("connect") || l.Action.Contains("连接")),
            "OCR触发" => _allLogs.Where(l => l.Action.Contains("ocr") || l.Action.Contains("血量")),
            _ => _allLogs
        };
        
        var logs = filteredLogs.Take(100).ToList();
        
        if (logs.Count == 0)
        {
            LogList.Children.Add(new TextBlock
            {
                Text = "暂无日志记录",
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8091A8")),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 50, 0, 0)
            });
            return;
        }

        foreach (var log in logs)
        {
            var row = CreateLogRow(log);
            LogList.Children.Add(row);
        }
    }

    private Border CreateLogRow(LogRecord log)
    {
        var row = new Border
        {
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Transparent),
            Padding = new Avalonia.Thickness(15, 10),
            Margin = new Avalonia.Thickness(0, 0, 0, 2)
        };

        // 鼠标悬停效果
        row.PointerEntered += (s, e) => row.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#172033"));
        row.PointerExited += (s, e) => row.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Transparent);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("150,150,*,100")
        };

        // 时间
        var timeBlock = new TextBlock
        {
            Text = log.Timestamp.ToString("MM-dd HH:mm:ss"),
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9AB0C8")),
            FontSize = 13,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetColumn(timeBlock, 0);

        // 设备
        var deviceBlock = new TextBlock
        {
            Text = log.DeviceName ?? "-",
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.White),
            FontSize = 13,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetColumn(deviceBlock, 1);

        // 动作
        var actionColor = GetActionColor(log.Action);
        var actionBlock = new TextBlock
        {
            Text = log.Action,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(actionColor)),
            FontSize = 13,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(actionBlock, 2);

        // 来源
        var sourceBlock = new TextBlock
        {
            Text = log.Source ?? "系统",
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8091A8")),
            FontSize = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetColumn(sourceBlock, 3);

        grid.Children.Add(timeBlock);
        grid.Children.Add(deviceBlock);
        grid.Children.Add(actionBlock);
        grid.Children.Add(sourceBlock);

        row.Child = grid;
        return row;
    }

    private string GetActionColor(string action)
    {
        if (action.Contains("强度") || action.Contains("strength")) return "#0EA5E9";
        if (action.Contains("波形") || action.Contains("waveform")) return "#14B8A6";
        if (action.Contains("连接") || action.Contains("connect")) return "#10B981";
        if (action.Contains("断开") || action.Contains("disconnect")) return "#EF4444";
        if (action.Contains("血量") || action.Contains("ocr")) return "#F59E0B";
        return "#9AB0C8";
    }

    private void OnFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshLogList();
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        LoadLogs();
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Database.Instance.ClearLogs();
            _allLogs.Clear();
            RefreshLogList();
            Logger.Information("Logs cleared");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to clear logs");
        }
    }
}

