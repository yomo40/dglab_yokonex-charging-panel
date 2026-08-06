using System.Collections.Concurrent;
using System.Timers;
using ChargingPanel.Core.Application.Devices;
using ChargingPanel.Core.Events;
using Serilog;

namespace ChargingPanel.Core.Services;

/// <summary>
/// 役次元传感器服务
/// 跟踪计步器和角度传感器数据变化，触发相应事件。
///
/// 迁移说明：不再订阅 legacy 适配器事件，改为订阅 EventBus 上的传感器遥测
/// <see cref="GameEvent"/>（由 v2 遥测桥或后续 telemetry 管线发布）。
/// 自己发布的"变化通知"事件带 __yokonexSensorSelf 标记，收到时跳过，避免自环。
/// </summary>
public class YokonexSensorService : IDisposable
{
    private const string SelfMarkerKey = "__yokonexSensorSelf";

    private readonly ILogger _logger = Log.ForContext<YokonexSensorService>();
    private readonly IDeviceQueryService _queryService;
    private readonly IDeviceApplicationService _applicationService;
    private readonly ConcurrentDictionary<string, SensorState> _deviceStates = new();
    private readonly System.Timers.Timer _checkTimer;
    private IDisposable? _gameEventSubscription;

    // 配置参数
    public int StepChangeThreshold { get; set; } = 10;  // 步数变化阈值
    public float AngleChangeThreshold { get; set; } = 15.0f;  // 角度变化阈值 (度)
    public int CheckIntervalMs { get; set; } = 1000;  // 检查间隔 (毫秒)

    private static YokonexSensorService? _instance;
    public static YokonexSensorService? Instance => _instance;

    /// <summary>
    /// 步数变化事件 (DeviceId, StepCount)
    /// </summary>
    public event EventHandler<(string DeviceId, int StepCount)>? StepCountChanged;

    /// <summary>
    /// 角度变化事件 (DeviceId, X, Y, Z)
    /// </summary>
    public event EventHandler<(string DeviceId, float X, float Y, float Z)>? AngleChanged;

    /// <summary>
    /// 压力变化事件 (DeviceId, PressureA, PressureB)
    /// </summary>
    public event EventHandler<(string DeviceId, int PressureA, int PressureB)>? PressureChanged;

    public YokonexSensorService(IDeviceQueryService queryService, IDeviceApplicationService applicationService)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _applicationService = applicationService ?? throw new ArgumentNullException(nameof(applicationService));

        // 定时检查传感器数据变化
        _checkTimer = new System.Timers.Timer(CheckIntervalMs);
        _checkTimer.Elapsed += OnCheckTimerElapsed;
        _checkTimer.AutoReset = true;

        _instance = this;
        _logger.Information("YokonexSensorService initialized");
    }

    public void Start()
    {
        _gameEventSubscription?.Dispose();
        _gameEventSubscription = EventBus.Instance.Subscribe<GameEvent>(OnGameEvent);
        _checkTimer.Start();
        _logger.Information("YokonexSensorService started");
    }

    public void Stop()
    {
        _checkTimer.Stop();
        _gameEventSubscription?.Dispose();
        _gameEventSubscription = null;
        _logger.Information("YokonexSensorService stopped");
    }

    private void OnGameEvent(GameEvent evt)
    {
        // 跳过自己发布的"变化通知"，避免自环。
        if (evt.Data.ContainsKey(SelfMarkerKey))
        {
            return;
        }

        var deviceId = evt.TargetDeviceId;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        switch (evt.Type)
        {
            case GameEventType.StepCountChanged:
                HandleStepTelemetry(deviceId, evt.NewValue);
                break;
            case GameEventType.AngleChanged:
                HandleAngleTelemetry(deviceId, evt.NewValue);
                break;
            case GameEventType.PressureChanged:
                HandlePressureTelemetry(deviceId, evt);
                break;
            case GameEventType.EnemaStatusChanged:
                HandleEnemaTelemetry(deviceId, evt);
                break;
            case GameEventType.DeviceBatteryChanged:
                HandleBatteryTelemetry(deviceId, evt);
                break;
            case GameEventType.ToyDeviceInfoChanged:
                HandleToyInfoTelemetry(deviceId, evt);
                break;
            case GameEventType.ChannelConnected:
            case GameEventType.ChannelDisconnected:
                HandleChannelTelemetry(deviceId, evt);
                break;
        }
    }

    private void HandleStepTelemetry(string deviceId, int stepCount)
    {
        var state = GetOrCreateState(deviceId);
        var delta = stepCount - state.LastStepCount;
        state.CurrentStepCount = stepCount;
        state.StepCountDelta += delta;
        state.LastStepCount = stepCount;

        StepCountChanged?.Invoke(this, (deviceId, stepCount));
        _logger.Debug("Step count telemetry: {DeviceId} delta={Delta}, total={Total}", deviceId, delta, state.StepCountDelta);
    }

    private void HandleAngleTelemetry(string deviceId, int angle)
    {
        var state = GetOrCreateState(deviceId);
        var newAngle = (float)angle;
        state.AngleDelta += Math.Abs(newAngle - state.LastAngle.X);
        state.LastAngle = (newAngle, 0, 0);

        AngleChanged?.Invoke(this, (deviceId, newAngle, 0, 0));
        _logger.Debug("Angle telemetry: {DeviceId} total={Total}", deviceId, state.AngleDelta);
    }

    private void HandlePressureTelemetry(string deviceId, GameEvent evt)
    {
        var state = GetOrCreateState(deviceId);
        var a = ResolveInt(evt, "pressureA");
        var b = ResolveInt(evt, "pressureB");
        state.LastPressureA = state.CurrentPressureA;
        state.LastPressureB = state.CurrentPressureB;
        state.CurrentPressureA = a;
        state.CurrentPressureB = b;
        PressureChanged?.Invoke(this, (deviceId, a, b));

        var maxPressure = Math.Max(a, b);
        TriggerPressureEvent(deviceId, a, b, maxPressure);
    }

    private void HandleEnemaTelemetry(string deviceId, GameEvent evt)
    {
        var state = GetOrCreateState(deviceId);
        var working = ToBool(evt, "isWorking");
        state.HasEnemaStatus = true;
        state.IsEnemaWorking = working;

        var status = new EnemaDeviceStatus
        {
            PeristalticPumpState = working ? PeristalticPumpState.Forward : PeristalticPumpState.Stop,
            WaterPumpState = working ? WaterPumpState.Forward : WaterPumpState.Stop
        };
        TriggerEnemaStatusEvent(deviceId, status, !working, working);
    }

    private void HandleBatteryTelemetry(string deviceId, GameEvent evt)
    {
        var state = GetOrCreateState(deviceId);
        var battery = Math.Clamp(ResolveInt(evt, "battery"), 0, 100);
        var oldBattery = state.CurrentBattery;
        state.HasBattery = true;
        state.LastBattery = oldBattery;
        state.CurrentBattery = battery;
        TriggerDeviceBatteryEvent(deviceId, oldBattery, battery, ResolveDeviceTelemetryType(deviceId));
    }

    private void HandleToyInfoTelemetry(string deviceId, GameEvent evt)
    {
        var state = GetOrCreateState(deviceId);
        var info = new ToyDeviceInfo
        {
            ProductId = ResolveInt(evt, "productId"),
            Version = ResolveInt(evt, "version"),
            MotorAModeCount = ResolveInt(evt, "motorAModeCount"),
            MotorBModeCount = ResolveInt(evt, "motorBModeCount"),
            MotorCModeCount = ResolveInt(evt, "motorCModeCount")
        };
        var previous = state.LastToyDeviceInfo;
        var changed = previous == null ||
                      previous.ProductId != info.ProductId ||
                      previous.Version != info.Version ||
                      previous.MotorAModeCount != info.MotorAModeCount ||
                      previous.MotorBModeCount != info.MotorBModeCount ||
                      previous.MotorCModeCount != info.MotorCModeCount;
        state.LastToyDeviceInfo = info;
        if (changed)
        {
            TriggerToyDeviceInfoEvent(deviceId, info, previous);
        }
    }

    private void HandleChannelTelemetry(string deviceId, GameEvent evt)
    {
        var connected = evt.Type == GameEventType.ChannelConnected;
        var channel = GetString(evt, "channel") ?? "A";
        var state = GetOrCreateState(deviceId);
        if (channel.Equals("A", StringComparison.OrdinalIgnoreCase))
        {
            state.LastChannelConnection = connected
                ? (state.LastChannelConnection.ChannelA, state.LastChannelConnection.ChannelB)
                : (state.LastChannelConnection.ChannelA, state.LastChannelConnection.ChannelB);
        }

        TriggerChannelEvent(deviceId, channel, connected);
    }

    private SensorState GetOrCreateState(string deviceId)
    {
        return _deviceStates.GetOrAdd(deviceId, _ => new SensorState { DeviceId = deviceId });
    }

    private void TriggerChannelEvent(string deviceId, string channel, bool connected)
    {
        var eventType = connected ? GameEventType.ChannelConnected : GameEventType.ChannelDisconnected;
        var eventId = connected ? "channel-connected" : "channel-disconnected";

        var evt = new GameEvent
        {
            Type = eventType,
            EventId = eventId,
            Source = "YokonexSensor",
            TargetDeviceId = deviceId,
            Data = new Dictionary<string, object>
            {
                [SelfMarkerKey] = true,
                ["channel"] = channel,
                ["connected"] = connected
            }
        };

        EventBus.Instance.PublishGameEvent(evt);
        _logger.Information("Channel {Channel} {State} on device {DeviceId}",
            channel, connected ? "connected" : "disconnected", deviceId);
    }

    private void OnCheckTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        foreach (var kvp in _deviceStates)
        {
            var state = kvp.Value;

            // 检查步数变化是否超过阈值
            if (state.StepCountDelta >= StepChangeThreshold)
            {
                TriggerStepCountEvent(state.DeviceId, state.StepCountDelta);
                state.StepCountDelta = 0;
            }

            // 检查角度变化是否超过阈值
            if (state.AngleDelta >= AngleChangeThreshold)
            {
                TriggerAngleEvent(state.DeviceId, state.AngleDelta);
                state.AngleDelta = 0;
            }

            state.LastUpdateTime = DateTime.UtcNow;
        }
    }

    private void TriggerStepCountEvent(string deviceId, int delta)
    {
        var evt = new GameEvent
        {
            Type = GameEventType.StepCountChanged,
            EventId = "step-count-changed",
            Source = "YokonexSensor",
            TargetDeviceId = deviceId,
            OldValue = 0,
            NewValue = delta,
            Data = new Dictionary<string, object>
            {
                [SelfMarkerKey] = true,
                ["stepDelta"] = delta
            }
        };

        EventBus.Instance.PublishGameEvent(evt);
        _logger.Information("Step count changed by {Delta} on device {DeviceId}", delta, deviceId);
    }

    private void TriggerAngleEvent(string deviceId, float delta)
    {
        var evt = new GameEvent
        {
            Type = GameEventType.AngleChanged,
            EventId = "angle-changed",
            Source = "YokonexSensor",
            TargetDeviceId = deviceId,
            OldValue = 0,
            NewValue = (int)delta,
            Data = new Dictionary<string, object>
            {
                [SelfMarkerKey] = true,
                ["angleDelta"] = delta
            }
        };

        EventBus.Instance.PublishGameEvent(evt);
        _logger.Information("Angle changed by {Delta:F2}° on device {DeviceId}", delta, deviceId);
    }

    private void TriggerPressureEvent(string deviceId, int pressureA, int pressureB, int maxPressure)
    {
        var evt = new GameEvent
        {
            Type = GameEventType.PressureChanged,
            EventId = "pressure-changed",
            Source = "YokonexSensor",
            TargetDeviceId = deviceId,
            OldValue = 0,
            NewValue = maxPressure,
            Data = new Dictionary<string, object>
            {
                [SelfMarkerKey] = true,
                ["pressureA"] = pressureA,
                ["pressureB"] = pressureB,
                ["pressure"] = maxPressure
            }
        };

        EventBus.Instance.PublishGameEvent(evt);
        _logger.Information("Pressure changed on device {DeviceId}: A={A}, B={B}", deviceId, pressureA, pressureB);
    }

    private void TriggerEnemaStatusEvent(string deviceId, EnemaDeviceStatus status, bool oldWorking, bool newWorking)
    {
        var evt = new GameEvent
        {
            Type = GameEventType.EnemaStatusChanged,
            EventId = "enema-status-changed",
            Source = "YokonexSensor",
            TargetDeviceId = deviceId,
            OldValue = oldWorking ? 1 : 0,
            NewValue = newWorking ? 1 : 0,
            Data = new Dictionary<string, object>
            {
                [SelfMarkerKey] = true,
                ["peristalticPumpState"] = status.PeristalticPumpState.ToString(),
                ["waterPumpState"] = status.WaterPumpState.ToString(),
                ["isWorking"] = newWorking,
                ["multiplier"] = 1
            }
        };

        EventBus.Instance.PublishGameEvent(evt);
        _logger.Information(
            "Enema status changed on device {DeviceId}: Peristaltic={Peristaltic}, Water={Water}",
            deviceId,
            status.PeristalticPumpState,
            status.WaterPumpState);
    }

    private void TriggerDeviceBatteryEvent(string deviceId, int oldBattery, int newBattery, string deviceTelemetryType)
    {
        var evt = new GameEvent
        {
            Type = GameEventType.DeviceBatteryChanged,
            EventId = "device-battery-changed",
            Source = "YokonexSensor",
            TargetDeviceId = deviceId,
            OldValue = oldBattery,
            NewValue = newBattery,
            Data = new Dictionary<string, object>
            {
                [SelfMarkerKey] = true,
                ["deviceTelemetryType"] = deviceTelemetryType,
                ["battery"] = newBattery,
                ["batteryChange"] = newBattery - oldBattery,
                ["multiplier"] = 1
            }
        };

        EventBus.Instance.PublishGameEvent(evt);
        _logger.Information("Battery changed on device {DeviceId}: {Old}% -> {New}%", deviceId, oldBattery, newBattery);
    }

    private void TriggerToyDeviceInfoEvent(string deviceId, ToyDeviceInfo info, ToyDeviceInfo? previous)
    {
        var oldModeCount = previous == null
            ? 0
            : previous.MotorAModeCount + previous.MotorBModeCount + previous.MotorCModeCount;
        var newModeCount = info.MotorAModeCount + info.MotorBModeCount + info.MotorCModeCount;

        var evt = new GameEvent
        {
            Type = GameEventType.ToyDeviceInfoChanged,
            EventId = "toy-device-info-changed",
            Source = "YokonexSensor",
            TargetDeviceId = deviceId,
            OldValue = oldModeCount,
            NewValue = newModeCount,
            Data = new Dictionary<string, object>
            {
                [SelfMarkerKey] = true,
                ["productId"] = info.ProductId,
                ["version"] = info.Version,
                ["motorAModeCount"] = info.MotorAModeCount,
                ["motorBModeCount"] = info.MotorBModeCount,
                ["motorCModeCount"] = info.MotorCModeCount,
                ["hasMotorA"] = info.HasMotorA,
                ["hasMotorB"] = info.HasMotorB,
                ["hasMotorC"] = info.HasMotorC,
                ["multiplier"] = 1
            }
        };

        EventBus.Instance.PublishGameEvent(evt);
        _logger.Information(
            "Toy device info updated on {DeviceId}: Product={ProductId}, Version={Version}, Modes=({A},{B},{C})",
            deviceId,
            info.ProductId,
            info.Version,
            info.MotorAModeCount,
            info.MotorBModeCount,
            info.MotorCModeCount);
    }

    private string ResolveDeviceTelemetryType(string deviceId)
    {
        var active = ResolveActiveDevice(deviceId);
        if (active is null)
        {
            return "yokonex";
        }

        if (active.FindCapability(new("enema.pump.control")) is not null)
        {
            return "enema";
        }

        if (active.FindCapability(new("toy.motor.control")) is not null ||
            active.FindCapability(new("toy.speed.control")) is not null)
        {
            return "toy";
        }

        return "yokonex";
    }

    private ActiveDeviceView? ResolveActiveDevice(string deviceId)
    {
        if (Guid.TryParse(deviceId, out var guid))
        {
            var active = _queryService.GetActiveDevice(new ChargingPanel.Contracts.Identifiers.DeviceInstanceId(guid));
            if (active is not null)
            {
                return active;
            }
        }

        return _queryService.GetActiveDevices()
            .FirstOrDefault(device =>
                string.Equals(device.InstanceId.Value.ToString("D"), deviceId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(device.InstanceId.Value.ToString("N"), deviceId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 获取设备的传感器状态
    /// </summary>
    public SensorState? GetDeviceState(string deviceId)
    {
        return _deviceStates.TryGetValue(deviceId, out var state) ? state : null;
    }

    /// <summary>
    /// 获取所有设备的传感器状态
    /// </summary>
    public IReadOnlyDictionary<string, SensorState> GetAllDeviceStates()
    {
        return _deviceStates;
    }

    private static int ResolveInt(GameEvent evt, string key)
    {
        if (evt.Data.TryGetValue(key, out var raw))
        {
            return raw switch
            {
                byte b => b,
                short s => s,
                int i => i,
                long l => (int)l,
                float f => (int)f,
                double d => (int)d,
                decimal m => (int)m,
                string text when int.TryParse(text, out var parsed) => parsed,
                _ => 0
            };
        }

        return 0;
    }

    private static bool ToBool(GameEvent evt, string key)
    {
        if (evt.Data.TryGetValue(key, out var raw))
        {
            return raw switch
            {
                bool b => b,
                string s => bool.TryParse(s, out var parsed) && parsed,
                int i => i != 0,
                _ => false
            };
        }

        return false;
    }

    private static string? GetString(GameEvent evt, string key)
    {
        return evt.Data.TryGetValue(key, out var raw) ? raw?.ToString() : null;
    }

    public void Dispose()
    {
        Stop();
        _checkTimer.Dispose();
        _deviceStates.Clear();
    }
}

/// <summary>
/// 传感器状态
/// </summary>
public class SensorState
{
    public string DeviceId { get; set; } = "";

    // 计步器
    public int LastStepCount { get; set; }
    public int CurrentStepCount { get; set; }
    public int StepCountDelta { get; set; }

    // 角度传感器
    public (float X, float Y, float Z) LastAngle { get; set; }
    public (float X, float Y, float Z) CurrentAngle { get; set; }
    public float AngleDelta { get; set; }

    // 通道连接状态
    public (bool ChannelA, bool ChannelB) LastChannelConnection { get; set; }

    // 压力
    public int LastPressureA { get; set; }
    public int LastPressureB { get; set; }
    public int CurrentPressureA { get; set; }
    public int CurrentPressureB { get; set; }

    // 灌肠器状态
    public bool HasEnemaStatus { get; set; }
    public bool IsEnemaWorking { get; set; }
    public PeristalticPumpState LastPeristalticPumpState { get; set; }
    public WaterPumpState LastWaterPumpState { get; set; }

    // 设备电量
    public bool HasBattery { get; set; }
    public int LastBattery { get; set; }
    public int CurrentBattery { get; set; }

    // 跳蛋/飞机杯设备信息
    public ToyDeviceInfo? LastToyDeviceInfo { get; set; }

    public DateTime LastUpdateTime { get; set; }
}
