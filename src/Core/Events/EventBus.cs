using System.Reactive.Linq;
using System.Reactive.Subjects;
using ChargingPanel.Core.Data.Entities;
using Serilog;

namespace ChargingPanel.Core.Events;

/// <summary>
/// 事件类型
/// </summary>
public enum GameEventType
{
    // 血量相关
    HealthLost,         // 血量损失
    HealthGained,       // 血量恢复
    HealthCritical,     // 血量危险
    
    // 护甲相关
    ArmorLost,          // 护甲损失
    ArmorGained,        // 护甲恢复
    ArmorBroken,        // 护甲破碎
    
    // 状态相关
    Death,              // 死亡
    Respawn,            // 重生
    Debuff,             // 负面效果
    Buff,               // 增益效果
    
    // 游戏事件
    Kill,               // 击杀
    Assist,             // 助攻
    Victory,            // 胜利
    Defeat,             // 失败
    
    // 自定义
    Custom              // 自定义事件
}

/// <summary>
/// 游戏事件数据
/// </summary>
public class GameEvent
{
    /// <summary>
    /// 事件唯一ID
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    
    /// <summary>
    /// 事件类型
    /// </summary>
    public GameEventType Type { get; set; }
    
    /// <summary>
    /// 事件标识（对应事件规则的 EventId）
    /// </summary>
    public string EventId { get; set; } = "";
    
    /// <summary>
    /// 来源（OCR/API/Manual/Script）
    /// </summary>
    public string Source { get; set; } = "manual";
    
    /// <summary>
    /// 原始值（如血量变化前的值）
    /// </summary>
    public int OldValue { get; set; }
    
    /// <summary>
    /// 新值（如血量变化后的值）
    /// </summary>
    public int NewValue { get; set; }
    
    /// <summary>
    /// 变化量
    /// </summary>
    public int Delta => NewValue - OldValue;
    
    /// <summary>
    /// 附加数据
    /// </summary>
    public Dictionary<string, object> Data { get; set; } = new();
    
    /// <summary>
    /// 时间戳
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// 目标设备ID（null表示所有已连接设备）
    /// </summary>
    public string? TargetDeviceId { get; set; }
    
    /// <summary>
    /// 是否来自远程
    /// </summary>
    public bool IsRemote { get; set; }
    
    /// <summary>
    /// 发送者ID（远程事件）
    /// </summary>
    public string? SenderId { get; set; }
}

/// <summary>
/// 设备控制事件数据
/// </summary>
public class DeviceControlEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public ChannelTarget Channel { get; set; }
    public EventAction Action { get; set; }
    public int Value { get; set; }
    public int Duration { get; set; }
    public string? WaveformData { get; set; }
    public string Source { get; set; } = "local";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// 远程同步事件
/// </summary>
public class RemoteSyncEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public RemoteSyncType Type { get; set; }
    public string SenderId { get; set; } = "";
    public string? TargetId { get; set; }
    public string? RoomId { get; set; }
    public object? Payload { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public enum RemoteSyncType
{
    DeviceStatus,       // 设备状态同步
    ControlCommand,     // 控制指令
    GameState,          // 游戏状态
    EventTrigger,       // 事件触发
    RoomState,          // 房间状态
    Heartbeat           // 心跳
}

/// <summary>
/// 响应式事件总线
/// 使用 System.Reactive 实现发布-订阅模式
/// </summary>
public class EventBus : IDisposable
{
    private readonly Subject<GameEvent> _gameEvents = new();
    private readonly Subject<DeviceControlEvent> _deviceControlEvents = new();
    private readonly Subject<RemoteSyncEvent> _remoteSyncEvents = new();
    private readonly ILogger _logger = Log.ForContext<EventBus>();
    
    private static EventBus? _instance;
    public static EventBus Instance => _instance ??= new EventBus();
    
    /// <summary>
    /// 游戏事件流
    /// </summary>
    public IObservable<GameEvent> GameEvents => _gameEvents.AsObservable();
    
    /// <summary>
    /// 设备控制事件流
    /// </summary>
    public IObservable<DeviceControlEvent> DeviceControlEvents => _deviceControlEvents.AsObservable();
    
    /// <summary>
    /// 远程同步事件流
    /// </summary>
    public IObservable<RemoteSyncEvent> RemoteSyncEvents => _remoteSyncEvents.AsObservable();
    
    /// <summary>
    /// 发布游戏事件
    /// </summary>
    public void PublishGameEvent(GameEvent evt)
    {
        _logger.Debug("Publishing game event: {EventType} ({EventId})", evt.Type, evt.EventId);
        _gameEvents.OnNext(evt);
    }
    
    /// <summary>
    /// 发布设备控制事件
    /// </summary>
    public void PublishDeviceControl(DeviceControlEvent evt)
    {
        _logger.Debug("Publishing device control: {DeviceId} {Action} {Value}", evt.DeviceId, evt.Action, evt.Value);
        _deviceControlEvents.OnNext(evt);
    }
    
    /// <summary>
    /// 发布远程同步事件
    /// </summary>
    public void PublishRemoteSync(RemoteSyncEvent evt)
    {
        _logger.Debug("Publishing remote sync: {Type} from {SenderId}", evt.Type, evt.SenderId);
        _remoteSyncEvents.OnNext(evt);
    }
    
    /// <summary>
    /// 订阅特定类型的游戏事件
    /// </summary>
    public IDisposable SubscribeGameEvent(GameEventType type, Action<GameEvent> handler)
    {
        return _gameEvents
            .Where(e => e.Type == type)
            .Subscribe(handler);
    }
    
    /// <summary>
    /// 订阅特定事件ID的游戏事件
    /// </summary>
    public IDisposable SubscribeEventId(string eventId, Action<GameEvent> handler)
    {
        return _gameEvents
            .Where(e => e.EventId == eventId)
            .Subscribe(handler);
    }
    
    /// <summary>
    /// 订阅特定设备的控制事件
    /// </summary>
    public IDisposable SubscribeDeviceControl(string deviceId, Action<DeviceControlEvent> handler)
    {
        return _deviceControlEvents
            .Where(e => e.DeviceId == deviceId)
            .Subscribe(handler);
    }
    
    /// <summary>
    /// 获取事件流的节流版本（防抖动）
    /// </summary>
    public IObservable<GameEvent> GetThrottledGameEvents(TimeSpan throttleTime)
    {
        return _gameEvents.Throttle(throttleTime);
    }
    
    /// <summary>
    /// 获取事件流的采样版本
    /// </summary>
    public IObservable<GameEvent> GetSampledGameEvents(TimeSpan sampleTime)
    {
        return _gameEvents.Sample(sampleTime);
    }
    
    /// <summary>
    /// 获取设备控制事件的缓冲版本（批量处理）
    /// </summary>
    public IObservable<IList<DeviceControlEvent>> GetBufferedDeviceControls(TimeSpan bufferTime)
    {
        return _deviceControlEvents.Buffer(bufferTime);
    }
    
    public void Dispose()
    {
        _gameEvents.Dispose();
        _deviceControlEvents.Dispose();
        _remoteSyncEvents.Dispose();
    }
}

/// <summary>
/// 事件处理器接口
/// </summary>
public interface IEventHandler<T>
{
    Task HandleAsync(T evt, CancellationToken cancellationToken = default);
}

/// <summary>
/// 游戏事件处理器
/// </summary>
public interface IGameEventHandler : IEventHandler<GameEvent>
{
    /// <summary>
    /// 处理优先级（值越大优先级越高）
    /// </summary>
    int Priority { get; }
    
    /// <summary>
    /// 是否处理该事件
    /// </summary>
    bool CanHandle(GameEvent evt);
}
