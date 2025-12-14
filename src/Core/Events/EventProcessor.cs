using System.Reactive.Linq;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Data.Entities;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.DGLab;
using Serilog;

namespace ChargingPanel.Core.Events;

/// <summary>
/// 事件处理器
/// 负责将游戏事件转换为设备控制指令
/// </summary>
public class EventProcessor : IDisposable
{
    private readonly DeviceManager _deviceManager;
    private readonly EventBus _eventBus;
    private readonly ILogger _logger = Log.ForContext<EventProcessor>();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly Dictionary<string, DateTime> _eventCooldowns = new();
    
    /// <summary>
    /// 事件处理完成时触发
    /// </summary>
    public event EventHandler<EventProcessedEventArgs>? EventProcessed;
    
    public EventProcessor(DeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
        _eventBus = EventBus.Instance;
        
        // 订阅游戏事件
        _subscriptions.Add(
            _eventBus.GameEvents.Subscribe(OnGameEvent)
        );
        
        // 订阅远程同步事件
        _subscriptions.Add(
            _eventBus.RemoteSyncEvents
                .Where(e => e.Type == RemoteSyncType.ControlCommand || e.Type == RemoteSyncType.EventTrigger)
                .Subscribe(OnRemoteSync)
        );
        
        _logger.Information("EventProcessor initialized");
    }
    
    /// <summary>
    /// 处理游戏事件
    /// </summary>
    private async void OnGameEvent(GameEvent evt)
    {
        try
        {
            _logger.Debug("Processing game event: {Type} ({EventId})", evt.Type, evt.EventId);
            
            // 查找匹配的事件规则
            var eventRecord = Database.Instance.GetEventByEventId(evt.EventId);
            if (eventRecord == null)
            {
                // 尝试根据事件类型查找
                eventRecord = FindEventByType(evt.Type);
            }
            
            if (eventRecord == null)
            {
                _logger.Debug("No matching event rule found for {EventId}", evt.EventId);
                return;
            }
            
            if (!eventRecord.Enabled)
            {
                _logger.Debug("Event {EventId} is disabled", evt.EventId);
                return;
            }
            
            // 检查冷却时间
            if (eventRecord.Duration > 0 && IsOnCooldown(evt.EventId, eventRecord.Duration))
            {
                _logger.Debug("Event {EventId} is on cooldown", evt.EventId);
                return;
            }
            
            // 计算实际强度值
            var actualValue = CalculateStrength(evt, eventRecord);
            
            // 执行设备控制
            await ExecuteDeviceControl(evt, eventRecord, actualValue);
            
            // 更新冷却时间
            _eventCooldowns[evt.EventId] = DateTime.UtcNow;
            
            // 发出处理完成事件
            EventProcessed?.Invoke(this, new EventProcessedEventArgs
            {
                Event = evt,
                EventRecord = eventRecord,
                ActualValue = actualValue,
                Success = true
            });
            
            // 记录日志
            Database.Instance.AddLog("info", "EventProcessor",
                $"Event processed: {evt.EventId} -> {eventRecord.Action} {actualValue}",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    eventId = evt.EventId,
                    eventType = evt.Type.ToString(),
                    action = eventRecord.Action,
                    value = actualValue,
                    delta = evt.Delta
                }));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing game event {EventId}", evt.EventId);
            
            EventProcessed?.Invoke(this, new EventProcessedEventArgs
            {
                Event = evt,
                Success = false,
                Error = ex.Message
            });
        }
    }
    
    /// <summary>
    /// 处理远程同步事件
    /// </summary>
    private async void OnRemoteSync(RemoteSyncEvent syncEvt)
    {
        try
        {
            _logger.Debug("Processing remote sync: {Type} from {SenderId}", syncEvt.Type, syncEvt.SenderId);
            
            switch (syncEvt.Type)
            {
                case RemoteSyncType.ControlCommand:
                    if (syncEvt.Payload is DeviceControlEvent controlEvt)
                    {
                        await ExecuteRemoteControl(controlEvt);
                    }
                    break;
                    
                case RemoteSyncType.EventTrigger:
                    if (syncEvt.Payload is GameEvent gameEvt)
                    {
                        gameEvt.IsRemote = true;
                        gameEvt.SenderId = syncEvt.SenderId;
                        _eventBus.PublishGameEvent(gameEvt);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing remote sync event");
        }
    }
    
    /// <summary>
    /// 根据事件类型查找匹配的事件规则
    /// </summary>
    private EventRecord? FindEventByType(GameEventType type)
    {
        var eventId = type switch
        {
            GameEventType.HealthLost => "lost-hp",
            GameEventType.HealthGained => "add-hp",
            GameEventType.ArmorLost => "lost-ahp",
            GameEventType.ArmorGained => "add-ahp",
            GameEventType.Death => "dead",
            GameEventType.Debuff => "character-debuff",
            _ => null
        };
        
        return eventId != null ? Database.Instance.GetEventByEventId(eventId) : null;
    }
    
    /// <summary>
    /// 计算实际强度值
    /// </summary>
    private int CalculateStrength(GameEvent evt, EventRecord eventRecord)
    {
        var baseValue = eventRecord.Value;
        
        // 根据变化量调整强度
        if (evt.Delta != 0 && eventRecord.Action is "increase" or "decrease")
        {
            // 每损失/恢复 1 点血量/护甲，增加一定强度
            var multiplier = Math.Abs(evt.Delta) / 10.0;
            baseValue = (int)(baseValue * Math.Max(1, multiplier));
        }
        
        // 检查附加数据中的倍率
        if (evt.Data.TryGetValue("multiplier", out var mult) && mult is double m)
        {
            baseValue = (int)(baseValue * m);
        }
        
        // 限制在安全范围内
        var maxStrength = Database.Instance.GetSetting<int>("safety.maxStrength", 200);
        return Math.Clamp(baseValue, 0, maxStrength);
    }
    
    /// <summary>
    /// 执行设备控制
    /// </summary>
    private async Task ExecuteDeviceControl(GameEvent evt, EventRecord eventRecord, int value)
    {
        // 确定目标设备
        var targetDevices = GetTargetDevices(evt.TargetDeviceId);
        if (!targetDevices.Any())
        {
            _logger.Warning("No target devices for event {EventId}", evt.EventId);
            return;
        }
        
        // 解析通道
        var channels = ParseChannels(eventRecord.Channel);
        
        // 解析动作模式
        var mode = ParseStrengthMode(eventRecord.Action);
        
        foreach (var deviceId in targetDevices)
        {
            try
            {
                var controlEvt = new DeviceControlEvent
                {
                    DeviceId = deviceId,
                    Channel = Enum.TryParse<ChannelTarget>(eventRecord.Channel, out var ch) ? ch : ChannelTarget.A,
                    Action = Enum.TryParse<EventAction>(eventRecord.Action, true, out var action) ? action : EventAction.Set,
                    Value = value,
                    Duration = eventRecord.Duration,
                    WaveformData = eventRecord.WaveformData,
                    Source = evt.Source
                };
                
                // 执行控制
                foreach (var channel in channels)
                {
                    switch (eventRecord.Action)
                    {
                        case "set":
                        case "increase":
                        case "decrease":
                            await _deviceManager.SetStrengthAsync(deviceId, channel, value, mode);
                            break;
                            
                        case "wave":
                        case "pulse":
                            var waveform = new WaveformData
                            {
                                Frequency = 100,
                                Strength = value,
                                Duration = eventRecord.Duration > 0 ? eventRecord.Duration : 1000
                            };
                            await _deviceManager.SendWaveformAsync(deviceId, channel, waveform);
                            break;
                            
                        case "clear":
                            await _deviceManager.ClearWaveformQueueAsync(deviceId, channel);
                            break;
                    }
                }
                
                controlEvt.Success = true;
                _eventBus.PublishDeviceControl(controlEvt);
                
                // 如果有持续时间，安排恢复
                if (eventRecord.Duration > 0 && eventRecord.Action is "set" or "increase")
                {
                    _ = ScheduleRecovery(deviceId, channels, eventRecord.Duration);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to control device {DeviceId}", deviceId);
                
                _eventBus.PublishDeviceControl(new DeviceControlEvent
                {
                    DeviceId = deviceId,
                    Success = false,
                    Error = ex.Message
                });
            }
        }
    }
    
    /// <summary>
    /// 执行远程控制指令
    /// </summary>
    private async Task ExecuteRemoteControl(DeviceControlEvent controlEvt)
    {
        var channels = ParseChannels(controlEvt.Channel.ToString());
        var mode = ParseStrengthMode(controlEvt.Action.ToString().ToLower());
        
        foreach (var channel in channels)
        {
            await _deviceManager.SetStrengthAsync(controlEvt.DeviceId, channel, controlEvt.Value, mode);
        }
        
        _logger.Information("Remote control executed: {DeviceId} {Action} {Value}", 
            controlEvt.DeviceId, controlEvt.Action, controlEvt.Value);
    }
    
    /// <summary>
    /// 安排强度恢复
    /// </summary>
    private async Task ScheduleRecovery(string deviceId, Channel[] channels, int delayMs)
    {
        await Task.Delay(delayMs);
        
        foreach (var channel in channels)
        {
            try
            {
                await _deviceManager.SetStrengthAsync(deviceId, channel, 0, StrengthMode.Set);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to recover device {DeviceId} strength", deviceId);
            }
        }
    }
    
    /// <summary>
    /// 获取目标设备列表
    /// </summary>
    private IEnumerable<string> GetTargetDevices(string? specificDeviceId)
    {
        if (!string.IsNullOrEmpty(specificDeviceId))
        {
            yield return specificDeviceId;
        }
        else
        {
            foreach (var device in _deviceManager.GetConnectedDevices())
            {
                yield return device.Id;
            }
        }
    }
    
    /// <summary>
    /// 解析通道
    /// </summary>
    private static Channel[] ParseChannels(string channelStr)
    {
        return channelStr switch
        {
            "A" => new[] { Channel.A },
            "B" => new[] { Channel.B },
            "AB" => new[] { Channel.A, Channel.B },
            _ => new[] { Channel.A }
        };
    }
    
    /// <summary>
    /// 解析强度模式
    /// </summary>
    private static StrengthMode ParseStrengthMode(string action)
    {
        return action switch
        {
            "increase" => StrengthMode.Increase,
            "decrease" => StrengthMode.Decrease,
            _ => StrengthMode.Set
        };
    }
    
    /// <summary>
    /// 检查事件是否在冷却中
    /// </summary>
    private bool IsOnCooldown(string eventId, int cooldownMs)
    {
        if (_eventCooldowns.TryGetValue(eventId, out var lastTime))
        {
            return (DateTime.UtcNow - lastTime).TotalMilliseconds < cooldownMs;
        }
        return false;
    }
    
    public void Dispose()
    {
        foreach (var sub in _subscriptions)
        {
            sub.Dispose();
        }
        _subscriptions.Clear();
    }
}

/// <summary>
/// 事件处理完成事件参数
/// </summary>
public class EventProcessedEventArgs : EventArgs
{
    public GameEvent Event { get; set; } = null!;
    public EventRecord? EventRecord { get; set; }
    public int ActualValue { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}
