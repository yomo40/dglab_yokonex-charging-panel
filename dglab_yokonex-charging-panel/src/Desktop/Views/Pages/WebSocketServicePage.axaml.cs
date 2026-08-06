using Avalonia.Controls;
using Avalonia.Interactivity;
using ChargingPanel.Core;
using ChargingPanel.Core.Data;
using Serilog;
using ChargingPanel.Desktop.Bootstrap;
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

    /// <summary>从组合根解析业务服务宿主；未初始化时返回 null。</summary>
    private static BusinessServicesHost? ResolveHost() => ApplicationHost.TryGetService<BusinessServicesHost>();

    private static BusinessServicesHost RequireHost()
    {
        return ResolveHost() ?? throw new InvalidOperationException("业务服务尚未初始化。");
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        LoadSettings();
        RefreshServerStatus();
        RefreshModBridgeStatus();
    }

    private void LoadSettings()
    {
        if (ApplicationHost.Instance is null)
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
        if (ApplicationHost.Instance is null)
        {
            return;
        }

        Database.Instance.SetSetting("server.coyote.enabled", (AutoStartEnabled.IsChecked ?? false).ToString().ToLowerInvariant());
        Database.Instance.SetSetting("server.coyote.port", PortInput.Text ?? "9000");
        Database.Instance.SetSetting("modbridge.enabled", (ModBridgeEnabled.IsChecked ?? false).ToString().ToLowerInvariant());
    }

    private async void OnStartServerClick(object? sender, RoutedEventArgs e)
    {
        if (ApplicationHost.Instance is null)
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
            await RequireHost().StartWebSocketServerAsync(port);
            RefreshServerStatus();
            UpdateHint($"郊狼socket服务器已启动: ws://0.0.0.0:{port}（局域网可访问）");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to start websocket server");
            UpdateHint($"启动失败: {ex.Message}");
        }
    }

    private async void OnStopServerClick(object? sender, RoutedEventArgs e)
    {
        if (ApplicationHost.Instance is null)
        {
            UpdateHint("服务尚未初始化");
            return;
        }

        try
        {
            await RequireHost().StopWebSocketServerAsync();
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
        if (ApplicationHost.Instance is null)
        {
            UpdateModBridgeHint("服务尚未初始化");
            return;
        }

        try
        {
            ModBridgeEnabled.IsChecked = true;
            SaveSettings();
            await RequireHost().StartModBridgeFixedServersAsync();
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
        if (ApplicationHost.Instance is null)
        {
            UpdateModBridgeHint("服务尚未初始化");
            return;
        }

        try
        {
            ModBridgeEnabled.IsChecked = false;
            SaveSettings();
            await RequireHost().StopModBridgeFixedServersAsync(stopWholeService: true);
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
        if (ApplicationHost.Instance is null)
        {
            DiagnosticsText.Text = "服务尚未初始化";
            return;
        }

        try
        {
            DiagnosticsText.Text = "正在诊断，请稍候...";
            await Task.Delay(50);
            var sb = new StringBuilder();
            sb.AppendLine("=== 官方中转服务诊断 ===");
            sb.AppendLine("迁移期说明：诊断已交由 Runtime v2 传输层执行。");
            sb.AppendLine("当前运行时未建立活动会话，如实报告未连接。");
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
        var server = ResolveHost()?.WebSocketServer;
        var running = server?.IsRunning == true;

        ServerStatusText.Text = running ? "运行中" : "未启动";
        ServerStatusText.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(running ? "#10B981" : "#EF4444"));

        ServerUrlText.Text = running ? server?.LocalUrl ?? "-" : "-";
        ClientCountText.Text = running ? (server?.GetConnectedClients().Count() ?? 0).ToString() : "0";
    }

    private void RefreshModBridgeStatus()
    {
        var modBridge = ResolveHost()?.ModBridgeService;
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
