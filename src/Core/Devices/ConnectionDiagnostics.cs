using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChargingPanel.Core.Bluetooth;
using Serilog;

namespace ChargingPanel.Core.Devices;

/// <summary>
/// 连接诊断服务
/// </summary>
public class ConnectionDiagnostics
{
    private static readonly ILogger Logger = Log.ForContext<ConnectionDiagnostics>();

    /// <summary>
    /// 诊断蓝牙状态
    /// </summary>
    public async Task<BluetoothDiagnosticsResult> DiagnoseBluetoothAsync()
    {
        var result = new BluetoothDiagnosticsResult();

        try
        {
            // 检查蓝牙适配器
            var status = await WindowsBluetoothTransport.CheckAdapterStatusAsync();

            result.AdapterAvailable = status.IsAvailable;
            result.AdapterEnabled = status.IsEnabled;
            result.SupportsBle = status.SupportsBle;
            result.AdapterName = status.AdapterName;

            // 获取 Windows 版本
            result.WindowsVersion = Environment.OSVersion.Version.ToString();

            // 检查问题
            if (!status.IsAvailable)
            {
                result.Issues.Add("未检测到蓝牙适配器");
                result.Suggestions.Add("请确保您的电脑有蓝牙功能");
                result.Suggestions.Add("如果是台式机，可能需要购买蓝牙适配器");
            }
            else if (!status.IsEnabled)
            {
                result.Issues.Add("蓝牙已关闭");
                result.Suggestions.Add("请在 Windows 设置 > 蓝牙和设备 中开启蓝牙");
                result.Suggestions.Add("或点击任务栏的蓝牙图标开启");
            }
            else if (!status.SupportsBle)
            {
                result.Issues.Add("蓝牙适配器不支持 BLE (低功耗蓝牙)");
                result.Suggestions.Add("郊狼和役次元设备需要 BLE 4.0+ 支持");
                result.Suggestions.Add("请更换支持 BLE 的蓝牙适配器");
            }

            // 检查 Windows 版本
            if (Environment.OSVersion.Version.Build < 17763)
            {
                result.Issues.Add($"Windows 版本过低 (当前: {result.WindowsVersion})");
                result.Suggestions.Add("请升级到 Windows 10 1809 或更高版本");
            }

            Logger.Information("蓝牙诊断完成: Available={Available}, Enabled={Enabled}, BLE={BLE}",
                result.AdapterAvailable, result.AdapterEnabled, result.SupportsBle);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "蓝牙诊断失败");
            result.Issues.Add($"诊断过程出错: {ex.Message}");
            result.Suggestions.Add("请检查蓝牙驱动是否正常安装");
        }

        return result;
    }

    /// <summary>
    /// 诊断 WebSocket 连接
    /// </summary>
    public async Task<WebSocketDiagnosticsResult> DiagnoseWebSocketAsync(string url = "wss://ws.dungeon-lab.cn")
    {
        var result = new WebSocketDiagnosticsResult { Url = url };

        try
        {
            using var ws = new System.Net.WebSockets.ClientWebSocket();
            ws.Options.SetRequestHeader("User-Agent", "ChargingPanel/1.0");

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));

            await ws.ConnectAsync(new Uri(url), cts.Token);
            result.CanConnect = true;
            result.ConnectionTime = DateTime.UtcNow;

            await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Test", cts.Token);

            Logger.Information("WebSocket 诊断成功: {Url}", url);
        }
        catch (Exception ex)
        {
            result.CanConnect = false;
            result.Issues.Add($"无法连接到 WebSocket 服务器: {ex.Message}");
            result.Suggestions.Add("请检查网络连接");
            result.Suggestions.Add("请确保防火墙未阻止 WebSocket 连接");

            Logger.Warning(ex, "WebSocket 诊断失败: {Url}", url);
        }

        return result;
    }
}

/// <summary>
/// 蓝牙诊断结果
/// </summary>
public class BluetoothDiagnosticsResult
{
    public bool AdapterAvailable { get; set; }
    public bool AdapterEnabled { get; set; }
    public bool SupportsBle { get; set; }
    public string? AdapterName { get; set; }
    public string? WindowsVersion { get; set; }
    public List<string> Issues { get; } = new();
    public List<string> Suggestions { get; } = new();

    public bool IsHealthy => AdapterAvailable && AdapterEnabled && SupportsBle && Issues.Count == 0;
}

/// <summary>
/// WebSocket 诊断结果
/// </summary>
public class WebSocketDiagnosticsResult
{
    public string Url { get; set; } = "";
    public bool CanConnect { get; set; }
    public DateTime? ConnectionTime { get; set; }
    public List<string> Issues { get; } = new();
    public List<string> Suggestions { get; } = new();

    public bool IsHealthy => CanConnect && Issues.Count == 0;
}
