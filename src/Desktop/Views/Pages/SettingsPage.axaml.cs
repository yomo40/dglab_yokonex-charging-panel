using Avalonia.Controls;
using Avalonia.Interactivity;
using ChargingPanel.Core;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Logging;
using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ChargingPanel.Desktop.Views.Pages;

public partial class SettingsPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<SettingsPage>();

    public SettingsPage()
    {
        InitializeComponent();
        
        if (AppServices.IsInitialized)
        {
            LoadSettings();
        }
    }

    private void LoadSettings()
    {
        var settings = Database.Instance.GetAllSettings();
        
        HttpServerEnabled.IsChecked = GetBoolSetting(settings, "server.http.enabled", true);
        HttpPort.Text = GetStringSetting(settings, "server.http.port", "3000");
        WsPort.Text = GetStringSetting(settings, "server.ws.port", "3001");
        CoyoteServerEnabled.IsChecked = GetBoolSetting(settings, "server.coyote.enabled", false);
        CoyotePort.Text = GetStringSetting(settings, "server.coyote.port", "9000");
        
        DefaultMaxStrength.Text = GetStringSetting(settings, "device.maxStrength", "200");
        MaxWaveformQueue.Text = GetStringSetting(settings, "device.maxWaveformQueue", "500");
        AutoReconnect.IsChecked = GetBoolSetting(settings, "device.autoReconnect", true);
        HeartbeatInterval.Text = GetStringSetting(settings, "device.heartbeatInterval", "10000");
        
        ThemeSelector.SelectedIndex = GetStringSetting(settings, "ui.theme", "dark") switch
        {
            "dark" => 0,
            "light" => 1,
            "system" => 2,
            _ => 0
        };
        MinimizeToTray.IsChecked = GetBoolSetting(settings, "ui.minimizeToTray", false);
        
        LogLevel.SelectedIndex = GetStringSetting(settings, "log.level", "Verbose") switch
        {
            "Verbose" => 0,
            "Debug" => 1,
            "Information" => 2,
            "Warning" => 3,
            "Error" => 4,
            _ => 0  // 默认 Verbose
        };
        
        // 日志保留天数设置
        var retentionDays = GetStringSetting(settings, "log.retentionDays", "7");
        LogRetentionDays.SelectedIndex = retentionDays switch
        {
            "7" => 0,
            "10" => 1,
            "30" => 2,
            _ => 0
        };
        
        // 刷新日志信息
        RefreshLogInfo();
    }

    private bool GetBoolSetting(System.Collections.Generic.Dictionary<string, string> settings, string key, bool defaultValue)
    {
        return settings.TryGetValue(key, out var value) 
            ? value.Equals("true", StringComparison.OrdinalIgnoreCase) 
            : defaultValue;
    }

    private string GetStringSetting(System.Collections.Generic.Dictionary<string, string> settings, string key, string defaultValue)
    {
        return settings.TryGetValue(key, out var value) ? value : defaultValue;
    }

    /// <summary>
    /// 保存设置（公共方法，供外部调用）
    /// </summary>
    public void SaveSettings()
    {
        Database.Instance.SetSetting("server.http.enabled", (HttpServerEnabled.IsChecked ?? true).ToString().ToLower());
        Database.Instance.SetSetting("server.http.port", HttpPort.Text ?? "3000");
        Database.Instance.SetSetting("server.ws.port", WsPort.Text ?? "3001");
        Database.Instance.SetSetting("server.coyote.enabled", (CoyoteServerEnabled.IsChecked ?? false).ToString().ToLower());
        Database.Instance.SetSetting("server.coyote.port", CoyotePort.Text ?? "9000");
        
        Database.Instance.SetSetting("device.maxStrength", DefaultMaxStrength.Text ?? "200");
        Database.Instance.SetSetting("device.maxWaveformQueue", MaxWaveformQueue.Text ?? "500");
        Database.Instance.SetSetting("device.autoReconnect", (AutoReconnect.IsChecked ?? true).ToString().ToLower());
        Database.Instance.SetSetting("device.heartbeatInterval", HeartbeatInterval.Text ?? "10000");
        
        Database.Instance.SetSetting("ui.theme", ThemeSelector.SelectedIndex switch
        {
            0 => "dark",
            1 => "light",
            2 => "system",
            _ => "dark"
        });
        
        // 应用主题
        var theme = ThemeSelector.SelectedIndex switch
        {
            0 => "dark",
            1 => "light",
            2 => "system",
            _ => "dark"
        };
        App.SetTheme(theme);
        
        Database.Instance.SetSetting("ui.minimizeToTray", (MinimizeToTray.IsChecked ?? false).ToString().ToLower());
        
        Database.Instance.SetSetting("log.level", LogLevel.SelectedIndex switch
        {
            0 => "Verbose",
            1 => "Debug",
            2 => "Information",
            3 => "Warning",
            4 => "Error",
            _ => "Debug"
        });
        
        // 保存日志保留天数
        var retentionDays = (LogRetentionDays.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "7";
        Database.Instance.SetSetting("log.retentionDays", retentionDays);
        
        Logger.Information("Settings saved");
    }

    private void OnSaveSettingsClick(object? sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    private void OnOpenLogsFolderClick(object? sender, RoutedEventArgs e)
    {
        var logsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ChargingPanel", "data", "logs");
        
        if (!Directory.Exists(logsPath))
        {
            Directory.CreateDirectory(logsPath);
        }
        
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start("explorer.exe", logsPath);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", logsPath);
            }
            else
            {
                Process.Start("xdg-open", logsPath);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open logs folder");
        }
    }

    private void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        // 检查更新功能已移除
    }

    private void OnGitHubClick(object? sender, RoutedEventArgs e)
    {
        OpenUrl("https://github.com/yomo40/dglab_yokonex-charging-panel");
    }
    
    private void OnGitHubLinkClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        OpenUrl("https://github.com/yomo40/dglab_yokonex-charging-panel");
    }
    
    private void OnIssuesClick(object? sender, RoutedEventArgs e)
    {
        OpenUrl("https://github.com/yomo40/dglab_yokonex-charging-panel/issues");
    }
    
    private void OnDocsClick(object? sender, RoutedEventArgs e)
    {
        // 打开本地文档
        var docsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "docs", "使用说明.html");
        
        if (File.Exists(docsPath))
        {
            OpenUrl(docsPath);
        }
        else
        {
            // 如果本地文档不存在，打开在线文档
            OpenUrl("https://github.com/yomo40/dglab_yokonex-charging-panel#readme");
        }
    }
    
    private void OpenUrl(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start("xdg-open", url);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open URL: {Url}", url);
        }
    }
    
    /// <summary>
    /// 刷新日志信息
    /// </summary>
    private void RefreshLogInfo()
    {
        try
        {
            if (!AppServices.IsInitialized) 
            {
                DatabaseLogInfo.Text = "服务未初始化";
                FileLogInfo.Text = "";
                return;
            }
            
            // 检查 LogManager 是否已初始化
            try
            {
                var stats = Core.Logging.LogManager.Instance.GetStatistics();
                
                DatabaseLogInfo.Text = $"数据库日志：{stats.DatabaseLogCount:N0} 条，约 {FormatSize(stats.DatabaseLogSizeKB)}";
                FileLogInfo.Text = $"文件日志：{stats.FileLogCount} 个文件，共 {FormatSize(stats.FileLogTotalSizeKB)}";
            }
            catch (InvalidOperationException)
            {
                // LogManager 未初始化
                DatabaseLogInfo.Text = "日志管理器未初始化";
                FileLogInfo.Text = "";
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to refresh log info");
            DatabaseLogInfo.Text = $"获取日志信息失败: {ex.Message}";
            FileLogInfo.Text = "";
        }
    }
    
    private string FormatSize(long kb)
    {
        if (kb < 1024)
            return $"{kb} KB";
        return $"{kb / 1024.0:F2} MB";
    }
    
    private void OnRefreshLogInfoClick(object? sender, RoutedEventArgs e)
    {
        RefreshLogInfo();
    }
    
    private void OnClearDatabaseLogsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var retentionDays = (LogRetentionDays.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "7";
            var days = int.TryParse(retentionDays, out var d) ? d : 7;
            
            var deleted = Core.Logging.LogManager.Instance.ClearDatabaseLogs(days);
            RefreshLogInfo();
            
            Logger.Information("Cleared {Count} database log entries, keeping {Days} days", deleted, days);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to clear database logs");
        }
    }
    
    private void OnClearAllDatabaseLogsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var deleted = Core.Logging.LogManager.Instance.ClearAllDatabaseLogs();
            RefreshLogInfo();
            
            Logger.Information("Cleared all {Count} database log entries", deleted);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to clear all database logs");
        }
    }
    
    private void OnManualCleanLogFilesClick(object? sender, RoutedEventArgs e)
    {
        // 打开日志文件夹，由用户手动清理
        OnOpenLogsFolderClick(sender, e);
    }
}
