using Avalonia.Controls;
using Avalonia.Interactivity;
using ChargingPanel.Core;
using ChargingPanel.Core.Data;
using Serilog;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChargingPanel.Desktop.Views.Pages;

public partial class WebSocketServicePage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<WebSocketServicePage>();

    public WebSocketServicePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        LoadSettings();
        RefreshServerStatus();
        RefreshModBridgeStatus();
    }

    private void LoadSettings()
    {
        if (!AppServices.IsInitialized)
        {
            return;
        }

        var settings = Database.Instance.GetAllSettings();
        AutoStartEnabled.IsChecked = GetBoolSetting(settings, "server.coyote.enabled", true);
        PortInput.Text = GetStringSetting(settings, "server.coyote.port", "9000");
        ModBridgeEnabled.IsChecked = GetBoolSetting(settings, "modbridge.enabled", true);
    }

    private static bool GetBoolSetting(System.Collections.Generic.Dictionary<string, string> settings, string key, bool defaultValue)
    {
        return settings.TryGetValue(key, out var value)
            ? value.Equals("true", StringComparison.OrdinalIgnoreCase)
            : defaultValue;
    }

    private static string GetStringSetting(System.Collections.Generic.Dictionary<string, string> settings, string key, string defaultValue)
    {
        return settings.TryGetValue(key, out var value) ? value : defaultValue;
    }

    public void SaveSettings()
    {
        if (!AppServices.IsInitialized)
        {
            return;
        }

        Database.Instance.SetSetting("server.coyote.enabled", (AutoStartEnabled.IsChecked ?? false).ToString().ToLowerInvariant());
        Database.Instance.SetSetting("server.coyote.port", PortInput.Text ?? "9000");
        Database.Instance.SetSetting("modbridge.enabled", (ModBridgeEnabled.IsChecked ?? false).ToString().ToLowerInvariant());
    }

    private async void OnStartServerClick(object? sender, RoutedEventArgs e)
    {
        if (!AppServices.IsInitialized)
        {
            UpdateHint("服务尚未初始化");
            return;
        }

        if (!int.TryParse(PortInput.Text, out var port) || port is < 1024 or > 65535)
        {
            UpdateHint("端口范围应为 1024 - 65535");
            return;
        }

        try
        {
            SaveSettings();
            await AppServices.Instance.StartWebSocketServerAsync(port);
            RefreshServerStatus();
            UpdateHint($"郊狼socket服务器已启动: ws://localhost:{port}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to start websocket server");
            UpdateHint($"启动失败: {ex.Message}");
        }
    }

    private async void OnStopServerClick(object? sender, RoutedEventArgs e)
    {
        if (!AppServices.IsInitialized)
        {
            UpdateHint("服务尚未初始化");
            return;
        }

        try
        {
            await AppServices.Instance.StopWebSocketServerAsync();
            RefreshServerStatus();
            UpdateHint("郊狼socket服务器已停止");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to stop websocket server");
            UpdateHint($"停止失败: {ex.Message}");
        }
    }

    private void OnRefreshStatusClick(object? sender, RoutedEventArgs e)
    {
        SaveSettings();
        RefreshServerStatus();
        RefreshModBridgeStatus();
        UpdateHint("状态已刷新");
    }

    private async void OnStartModBridgeClick(object? sender, RoutedEventArgs e)
    {
        if (!AppServices.IsInitialized)
        {
            UpdateModBridgeHint("服务尚未初始化");
            return;
        }

        try
        {
            ModBridgeEnabled.IsChecked = true;
            SaveSettings();
            await AppServices.Instance.StartModBridgeFixedServersAsync();
            RefreshModBridgeStatus();
            UpdateModBridgeHint("MOD 接入服务已启动");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to start mod bridge server");
            UpdateModBridgeHint($"启动失败: {ex.Message}");
        }
    }

    private async void OnStopModBridgeClick(object? sender, RoutedEventArgs e)
    {
        if (!AppServices.IsInitialized)
        {
            UpdateModBridgeHint("服务尚未初始化");
            return;
        }

        try
        {
            ModBridgeEnabled.IsChecked = false;
            SaveSettings();
            await AppServices.Instance.StopModBridgeFixedServersAsync(stopWholeService: true);
            RefreshModBridgeStatus();
            UpdateModBridgeHint("MOD 接入服务已停止");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to stop mod bridge server");
            UpdateModBridgeHint($"停止失败: {ex.Message}");
        }
    }

    private void OnRefreshModBridgeStatusClick(object? sender, RoutedEventArgs e)
    {
        SaveSettings();
        RefreshModBridgeStatus();
        UpdateModBridgeHint("状态已刷新");
    }

    private async void OnDiagnoseOfficialClick(object? sender, RoutedEventArgs e)
    {
        if (!AppServices.IsInitialized)
        {
            DiagnosticsText.Text = "服务尚未初始化";
            return;
        }

        try
        {
            DiagnosticsText.Text = "正在诊断，请稍候...";
            var result = await AppServices.Instance.DeviceManager.Diagnostics.DiagnoseWebSocketAsync();
            var sb = new StringBuilder();
            sb.AppendLine("=== 官方中转服务诊断 ===");
            sb.AppendLine($"目标地址: {result.Url}");
            sb.AppendLine($"连接结果: {(result.CanConnect ? "成功" : "失败")}");
            sb.AppendLine($"连接时间: {(result.ConnectionTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-")}");

            if (result.Issues.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("问题:");
                foreach (var issue in result.Issues)
                {
                    sb.AppendLine($"- {issue}");
                }
            }

            if (result.Suggestions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("建议:");
                foreach (var suggestion in result.Suggestions)
                {
                    sb.AppendLine($"- {suggestion}");
                }
            }

            DiagnosticsText.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Diagnose websocket failed");
            DiagnosticsText.Text = $"诊断失败: {ex.Message}";
        }
    }

    private void RefreshServerStatus()
    {
        var server = AppServices.IsInitialized ? AppServices.Instance.WebSocketServer : null;
        var running = server?.IsRunning == true;

        ServerStatusText.Text = running ? "运行中" : "未启动";
        ServerStatusText.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(running ? "#10B981" : "#EF4444"));

        ServerUrlText.Text = running ? server?.LocalUrl ?? "-" : "-";
        ClientCountText.Text = running ? (server?.GetConnectedClients().Count() ?? 0).ToString() : "0";
    }

    private void RefreshModBridgeStatus()
    {
        var modBridge = AppServices.IsInitialized ? AppServices.Instance.ModBridgeService : null;
        var wsRunning = modBridge?.IsWebSocketRunning == true;
        var httpRunning = modBridge?.IsHttpRunning == true;

        ModBridgeWsStatusText.Text = wsRunning ? "运行中" : "未启动";
        ModBridgeWsStatusText.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(wsRunning ? "#10B981" : "#EF4444"));

        ModBridgeHttpStatusText.Text = httpRunning ? "运行中" : "未启动";
        ModBridgeHttpStatusText.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(httpRunning ? "#10B981" : "#EF4444"));
    }

    private void UpdateHint(string text)
    {
        StatusHintText.Text = text;
    }

    private void UpdateModBridgeHint(string text)
    {
        ModBridgeHintText.Text = text;
    }
}
