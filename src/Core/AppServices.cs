using System;
using System.IO;
using System.Threading.Tasks;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.DGLab;
using ChargingPanel.Core.Events;
using ChargingPanel.Core.Logging;
using ChargingPanel.Core.OCR;
using ChargingPanel.Core.Scripting;
using ChargingPanel.Core.Services;
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
    public EventBus EventBus { get; }
    public OCRService OCRService { get; }
    public BattleModeService BattleModeService { get; }
    public YokonexSensorService YokonexSensorService { get; }
    public SensorRuleService SensorRuleService { get; }
    public ScriptEngine ScriptEngine { get; }
    public DGLabWebSocketServer? WebSocketServer { get; private set; }
    
    private readonly string _dataPath;
    
    /// <summary>
    /// 获取服务实例
    /// </summary>
    public static T? GetService<T>() where T : class
    {
        if (!IsInitialized) return null;
        
        var instance = Instance;
        
        if (typeof(T) == typeof(DeviceManager)) return instance.DeviceManager as T;
        if (typeof(T) == typeof(EventService)) return instance.EventService as T;
        if (typeof(T) == typeof(EventBus)) return instance.EventBus as T;
        if (typeof(T) == typeof(OCRService)) return instance.OCRService as T;
        if (typeof(T) == typeof(BattleModeService)) return instance.BattleModeService as T;
        if (typeof(T) == typeof(YokonexSensorService)) return instance.YokonexSensorService as T;
        if (typeof(T) == typeof(SensorRuleService)) return instance.SensorRuleService as T;
        if (typeof(T) == typeof(ScriptEngine)) return instance.ScriptEngine as T;
        if (typeof(T) == typeof(DGLabWebSocketServer)) return instance.WebSocketServer as T;
        
        return null;
    }

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
        
        // 导入默认脚本
        var scriptsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts");
        Database.Instance.ImportDefaultScripts(scriptsPath);

        // 初始化服务
        DeviceManager = new DeviceManager();
        EventBus = new EventBus();
        EventService = new EventService(DeviceManager);
        OCRService = new OCRService(EventService);
        BattleModeService = new BattleModeService(DeviceManager, EventBus, OCRService);
        
        // 初始化役次元传感器服务
        YokonexSensorService = new YokonexSensorService(DeviceManager);
        
        // 初始化传感器规则服务
        SensorRuleService = new SensorRuleService(DeviceManager, EventService, YokonexSensorService);
        
        // 初始化脚本引擎
        ScriptEngine = new ScriptEngine(DeviceManager, EventService);

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
            
            // 启动对战模式服务
            BattleModeService.Start();
            
            // 启动役次元传感器服务
            YokonexSensorService.Start();
            
            // 启动传感器规则服务
            SensorRuleService.Start();
            
            // 启动脚本引擎
            ScriptEngine.Start();

            Log.Information("Services started");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start services");
            throw;
        }
    }
    
    /// <summary>
    /// 启动内置 WebSocket 服务器
    /// </summary>
    /// <param name="port">监听端口，默认 3000</param>
    public async Task StartWebSocketServerAsync(int port = 3000)
    {
        if (WebSocketServer != null && WebSocketServer.IsRunning)
        {
            Log.Warning("WebSocket 服务器已在运行");
            return;
        }
        
        WebSocketServer = new DGLabWebSocketServer();
        await WebSocketServer.StartAsync(port);
        Log.Information("内置 WebSocket 服务器已启动: ws://localhost:{Port}", port);
    }
    
    /// <summary>
    /// 停止内置 WebSocket 服务器
    /// </summary>
    public async Task StopWebSocketServerAsync()
    {
        if (WebSocketServer != null)
        {
            await WebSocketServer.StopAsync();
            WebSocketServer.Dispose();
            WebSocketServer = null;
            Log.Information("内置 WebSocket 服务器已停止");
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
            ScriptEngine.Stop();
            SensorRuleService.Stop();
            YokonexSensorService.Stop();
            BattleModeService.Stop();
            OCRService.Stop();
            await StopWebSocketServerAsync();
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
        ScriptEngine.Dispose();
        SensorRuleService.Dispose();
        YokonexSensorService.Dispose();
        BattleModeService.Dispose();
        OCRService.Dispose();
        DeviceManager.Dispose();
        Log.CloseAndFlush();
    }
}
