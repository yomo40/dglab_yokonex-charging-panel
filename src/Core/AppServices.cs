using System;
using System.IO;
using System.Threading.Tasks;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Events;
using ChargingPanel.Core.Logging;
using ChargingPanel.Core.OCR;
using Serilog;

namespace ChargingPanel.Core;

/// <summary>
/// 应用服务容器
/// </summary>
public class AppServices : IDisposable
{
    private static AppServices? _instance;
    public static AppServices Instance => _instance ?? throw new InvalidOperationException("AppServices not initialized");
    public static bool IsInitialized => _instance != null;

    public DeviceManager DeviceManager { get; }
    public EventService EventService { get; }
    public OCRService OCRService { get; }
    
    private readonly string _dataPath;

    private AppServices(string dataPath)
    {
        _dataPath = dataPath;

        // 确保数据目录存在
        Directory.CreateDirectory(dataPath);

        // 初始化日志管理器
        var logPath = Path.Combine(dataPath, "logs");
        LogManager.Initialize(logPath);

        Log.Information("Application starting...");

        // 初始化数据库
        var dbPath = Path.Combine(dataPath, "device_adapter.db");
        Database.Initialize(dbPath);
        Log.Information("Database initialized at {Path}", dbPath);

        // 初始化服务
        DeviceManager = new DeviceManager();
        EventService = new EventService(DeviceManager);
        OCRService = new OCRService(EventService);

        Log.Information("Services initialized");
    }

    /// <summary>
    /// 初始化应用服务
    /// </summary>
    public static AppServices Initialize(string? dataPath = null)
    {
        dataPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ChargingPanel",
            "data");

        _instance = new AppServices(dataPath);
        return _instance;
    }

    /// <summary>
    /// 启动服务
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            // 从数据库加载设备
            await DeviceManager.LoadDevicesFromDatabaseAsync();

            // 如果 OCR 配置为启用，则启动
            if (OCRService.Config.Enabled)
            {
                OCRService.Start();
            }

            Log.Information("Services started");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start services");
            throw;
        }
    }

    /// <summary>
    /// 启动服务（同步包装，保持向后兼容）
    /// </summary>
    [Obsolete("Use StartAsync() instead")]
    public void Start()
    {
        _ = StartAsync();
    }

    /// <summary>
    /// 停止服务
    /// </summary>
    public async Task StopAsync()
    {
        try
        {
            OCRService.Stop();
            await DeviceManager.EmergencyStopAllAsync();
            Log.Information("Services stopped");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error stopping services");
        }
    }

    /// <summary>
    /// 停止服务（同步包装）
    /// </summary>
    [Obsolete("Use StopAsync() instead")]
    public void Stop()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Stop();
        OCRService.Dispose();
        DeviceManager.Dispose();
        Log.CloseAndFlush();
    }
}
