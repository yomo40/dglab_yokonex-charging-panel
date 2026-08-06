using ChargingPanel.Core.Data;
using ChargingPanel.Core.Events;
using ChargingPanel.Core.Network;
using ChargingPanel.Core.OCR;
using ChargingPanel.Core.Scripting;
using ChargingPanel.Core.Services;
using ChargingPanel.Core.Automation;
using Serilog;

namespace ChargingPanel.Desktop.Bootstrap;

/// <summary>
/// 业务服务编排宿主（Task 22）。
///
/// 旧 <c>AppServices</c> 被删除后，事件/OCR/脚本/传感器/对战/房间/MOD 桥接等
/// 业务服务的启动、停止与固定端口服务器编排迁移到组合根，由 DI 容器创建并注入。
/// 设备输出只经由 <see cref="ChargingPanel.Core.Application.Devices.IDeviceApplicationService"/>，
/// 本宿主不触碰任何 legacy 设备实现。
/// </summary>
public sealed class BusinessServicesHost
{
    private const string BuiltinModBridgeScriptId = "__builtin_mod_bridge__";
    private static readonly ILogger Logger = Log.ForContext<BusinessServicesHost>();

    private readonly EventService _eventService;
    private readonly EventProcessor _eventProcessor;
    private readonly OCRService _ocrService;
    private readonly BattleModeService _battleModeService;
    private readonly YokonexSensorService _yokonexSensorService;
    private readonly SensorRuleService _sensorRuleService;
    private readonly ModBridgeService _modBridgeService;
    private readonly ScriptEngine _scriptEngine;
    private readonly RoomService _roomService;
    private readonly DeviceCommandScheduler _commandScheduler;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private bool _started;

    public BusinessServicesHost(
        EventService eventService,
        EventProcessor eventProcessor,
        OCRService ocrService,
        BattleModeService battleModeService,
        YokonexSensorService yokonexSensorService,
        SensorRuleService sensorRuleService,
        ModBridgeService modBridgeService,
        ScriptEngine scriptEngine,
        RoomService roomService,
        DeviceCommandScheduler commandScheduler)
    {
        _eventService = eventService ?? throw new ArgumentNullException(nameof(eventService));
        _eventProcessor = eventProcessor ?? throw new ArgumentNullException(nameof(eventProcessor));
        _ocrService = ocrService ?? throw new ArgumentNullException(nameof(ocrService));
        _battleModeService = battleModeService ?? throw new ArgumentNullException(nameof(battleModeService));
        _yokonexSensorService = yokonexSensorService ?? throw new ArgumentNullException(nameof(yokonexSensorService));
        _sensorRuleService = sensorRuleService ?? throw new ArgumentNullException(nameof(sensorRuleService));
        _modBridgeService = modBridgeService ?? throw new ArgumentNullException(nameof(modBridgeService));
        _scriptEngine = scriptEngine ?? throw new ArgumentNullException(nameof(scriptEngine));
        _roomService = roomService ?? throw new ArgumentNullException(nameof(roomService));
        _commandScheduler = commandScheduler ?? throw new ArgumentNullException(nameof(commandScheduler));
    }

    /// <summary>当前已实际启动的宿主；未启动或已停止时为 null。</summary>
    public static BusinessServicesHost? StartedInstance { get; private set; }

    /// <summary>内置郊狼 WebSocket 服务器实例。未启动时为 null。</summary>
    public DGLabWebSocketServer? WebSocketServer { get; private set; }

    /// <summary>MOD 桥接服务。</summary>
    public ModBridgeService ModBridgeService => _modBridgeService;

    /// <summary>
    /// 启动全部业务服务（幂等）。由生命周期在 schema v2 迁移后调用。
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_started)
            {
                return;
            }

            // 按配置自动启动内置 WebSocket 服务
            var wsAutoStart = Database.Instance.GetSetting<bool>("server.coyote.enabled", true);
            var wsPort = Database.Instance.GetSetting<int>("server.coyote.port", 9000);
            if (wsAutoStart)
            {
                try
                {
                    await StartWebSocketServerAsync(wsPort);
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "内置 WebSocket 服务启动失败（不阻断主程序）: port={Port}", wsPort);
                }
            }

            // 如果 OCR 配置为启用，则启动
            if (_ocrService.Config.Enabled)
            {
                _ocrService.Start();
            }

            // 启动对战模式服务
            _battleModeService.Start();

            // 启动役次元传感器服务
            _yokonexSensorService.Start();

            // 启动传感器规则服务
            _sensorRuleService.Start();

            // 启动 MOD 桥接服务（固定端口 39001/39002）
            var modBridgeEnabled = Database.Instance.GetSetting<bool>("modbridge.enabled", true);
            if (modBridgeEnabled)
            {
                try
                {
                    await StartModBridgeFixedServersAsync();
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "MOD 固定端口服务启动失败（不阻断主程序）");
                }
            }

            // 启动多人房间局域网自动发现（固定端口 49152）
            try
            {
                await _roomService.StartAutoDiscoveryAsync();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "房间局域网自动发现启动失败（不阻断主程序）");
            }

            // 启动脚本引擎
            _scriptEngine.Start();

            _started = true;
            StartedInstance = this;
            Logger.Information("业务服务已启动");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// 停止全部业务服务（幂等）。按依赖倒序停止外部接单，再停设备调度器。
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            StartedInstance = null;
        }
        finally
        {
            _lifecycleLock.Release();
        }

        try
        {
            _scriptEngine.Stop();
            await _modBridgeService.StopAsync();
            _sensorRuleService.Stop();
            _yokonexSensorService.Stop();
            _battleModeService.Stop();
            _ocrService.Stop();
            await StopWebSocketServerAsync();
            await _roomService.StopAutoDiscoveryAsync();
            await _commandScheduler.StopAsync();
            Logger.Information("业务服务已停止");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "停止业务服务失败");
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
            if (WebSocketServer.Port == port)
            {
                Logger.Warning("WebSocket 服务器已在运行，端口未变化: {Port}", port);
                return;
            }

            Logger.Information("检测到端口变更，重启 WebSocket 服务器: {OldPort} -> {NewPort}", WebSocketServer.Port, port);
            await StopWebSocketServerAsync();
        }

        WebSocketServer = new DGLabWebSocketServer();
        await WebSocketServer.StartAsync(port);
        Logger.Information("内置 WebSocket 服务器已启动: ws://0.0.0.0:{Port}", port);
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
            Logger.Information("内置 WebSocket 服务器已停止");
        }
    }

    /// <summary>
    /// 启动 MOD 固定端口接入服务（ws://127.0.0.1:39001 + http://127.0.0.1:39002/api/event）
    /// </summary>
    public async Task StartModBridgeFixedServersAsync()
    {
        await _modBridgeService.StartAsync();
        await _modBridgeService.RegisterScriptAsync(BuiltinModBridgeScriptId, "builtin.modbridge", "1.0");
        await _modBridgeService.StartWebSocketForScriptAsync(BuiltinModBridgeScriptId);
        await _modBridgeService.StartHttpForScriptAsync(BuiltinModBridgeScriptId);
        Logger.Information(
            "MOD 固定端口服务已启用: ws://127.0.0.1:{WsPort}, http://127.0.0.1:{HttpPort}/api/event",
            ChargingPanel.Core.Network.ModBridgeService.FixedWebSocketPort,
            ChargingPanel.Core.Network.ModBridgeService.FixedHttpPort);
    }

    /// <summary>
    /// 停止 MOD 固定端口接入服务
    /// </summary>
    public async Task StopModBridgeFixedServersAsync(bool stopWholeService = false)
    {
        await _modBridgeService.UnregisterScriptAsync(BuiltinModBridgeScriptId);

        if (stopWholeService)
        {
            await _modBridgeService.StopAsync();
        }

        Logger.Information("MOD 固定端口服务已停用");
    }
}
