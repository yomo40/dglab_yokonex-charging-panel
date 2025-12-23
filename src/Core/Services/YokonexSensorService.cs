using System;
using System.Collections.Generic;
using System.Timers;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.Yokonex;
using ChargingPanel.Core.Events;
using Serilog;

namespace ChargingPanel.Core.Services;

/// <summary>
/// 役次元传感器服务
/// 跟踪计步器和角度传感器数据变化，触发相应事件
/// </summary>
public class YokonexSensorService : IDisposable
{
    private readonly ILogger _logger = Log.ForContext<YokonexSensorService>();
    private readonly DeviceManager _deviceManager;
    private readonly Dictionary<string, SensorState> _deviceStates = new();
    private readonly System.Timers.Timer _checkTimer;
    
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
    
    public YokonexSensorService(DeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
        _deviceManager.DeviceStatusChanged += OnDeviceStatusChanged;
        
        // 定时检查传感器数据变化
        _checkTimer = new System.Timers.Timer(CheckIntervalMs);
        _checkTimer.Elapsed += OnCheckTimerElapsed;
        _checkTimer.AutoReset = true;
        
        _instance = this;
        _logger.Information("YokonexSensorService initialized");
    }
    
    public void Start()
    {
        _checkTimer.Start();
        _logger.Information("YokonexSensorService started");
    }
    
    public void Stop()
    {
        _checkTimer.Stop();
        _logger.Information("YokonexSensorService stopped");
    }
    
    private void OnDeviceStatusChanged(object? sender, DeviceStatusChangedEventArgs e)
    {
        if (e.Status == DeviceStatus.Connected)
        {
            var device = _deviceManager.GetDevice(e.DeviceId);
            if (device is IYokonexEmsDevice emsDevice)
            {
                // 初始化设备状态
                _deviceStates[e.DeviceId] = new SensorState
                {
                    DeviceId = e.DeviceId,
                    LastStepCount = emsDevice.StepCount,
                    LastAngle = emsDevice.CurrentAngle,
                    LastChannelConnection = emsDevice.ChannelConnectionState,
                    LastUpdateTime = DateTime.UtcNow
                };
                
                // 订阅事件
                emsDevice.StepCountChanged += OnStepCountChanged;
                emsDevice.AngleChanged += OnAngleChanged;
                emsDevice.ChannelConnectionChanged += OnChannelConnectionChanged;
                
                _logger.Information("Subscribed to Yokonex device {DeviceId} sensor events", e.DeviceId);
            }
        }
        else if (e.Status == DeviceStatus.Disconnected)
        {
            _deviceStates.Remove(e.DeviceId);
        }
    }
    
    private void OnStepCountChanged(object? sender, int stepCount)
    {
        if (sender is not IDevice device) return;
        
        if (_deviceStates.TryGetValue(device.Id, out var state))
        {
            var delta = stepCount - state.LastStepCount;
            state.CurrentStepCount = stepCount;
            state.StepCountDelta += delta;
            state.LastStepCount = stepCount;
            
            // 触发UI更新事件
            StepCountChanged?.Invoke(this, (device.Id, stepCount));
            
            _logger.Debug("Step count changed: {DeviceId} delta={Delta}, total={Total}", 
                device.Id, delta, state.StepCountDelta);
        }
    }
    
    private void OnAngleChanged(object? sender, (float X, float Y, float Z) angle)
    {
        if (sender is not IDevice device) return;
        
        if (_deviceStates.TryGetValue(device.Id, out var state))
        {
            // 计算角度变化量 (使用欧几里得距离)
            var deltaX = angle.X - state.LastAngle.X;
            var deltaY = angle.Y - state.LastAngle.Y;
            var deltaZ = angle.Z - state.LastAngle.Z;
            var totalDelta = Math.Sqrt(deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ);
            
            state.CurrentAngle = angle;
            state.AngleDelta += (float)totalDelta;
            state.LastAngle = angle;
            
            // 触发UI更新事件
            AngleChanged?.Invoke(this, (device.Id, angle.X, angle.Y, angle.Z));
            
            _logger.Debug("Angle changed: {DeviceId} delta={Delta:F2}, total={Total:F2}", 
                device.Id, totalDelta, state.AngleDelta);
        }
    }
    
    private void OnChannelConnectionChanged(object? sender, (bool ChannelA, bool ChannelB) connection)
    {
        if (sender is not IDevice device) return;
        
        if (_deviceStates.TryGetValue(device.Id, out var state))
        {
            var lastConn = state.LastChannelConnection;
            
            // 检测通道断开
            if (lastConn.ChannelA && !connection.ChannelA)
            {
                TriggerChannelEvent(device.Id, "A", false);
            }
            if (lastConn.ChannelB && !connection.ChannelB)
            {
                TriggerChannelEvent(device.Id, "B", false);
            }
            
            // 检测通道连接
            if (!lastConn.ChannelA && connection.ChannelA)
            {
                TriggerChannelEvent(device.Id, "A", true);
            }
            if (!lastConn.ChannelB && connection.ChannelB)
            {
                TriggerChannelEvent(device.Id, "B", true);
            }
            
            state.LastChannelConnection = connection;
        }
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
                ["angleDelta"] = delta
            }
        };
        
        EventBus.Instance.PublishGameEvent(evt);
        _logger.Information("Angle changed by {Delta:F2}° on device {DeviceId}", delta, deviceId);
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
    
    public void Dispose()
    {
        _checkTimer.Stop();
        _checkTimer.Dispose();
        _deviceManager.DeviceStatusChanged -= OnDeviceStatusChanged;
        
        // 取消所有设备的事件订阅
        foreach (var deviceId in _deviceStates.Keys)
        {
            var device = _deviceManager.GetDevice(deviceId);
            if (device is IYokonexEmsDevice emsDevice)
            {
                emsDevice.StepCountChanged -= OnStepCountChanged;
                emsDevice.AngleChanged -= OnAngleChanged;
                emsDevice.ChannelConnectionChanged -= OnChannelConnectionChanged;
            }
        }
        
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
    
    public DateTime LastUpdateTime { get; set; }
}
