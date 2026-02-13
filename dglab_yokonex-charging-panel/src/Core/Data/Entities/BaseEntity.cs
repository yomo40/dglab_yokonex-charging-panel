namespace ChargingPanel.Core.Data.Entities;

/// <summary>
/// 实体基类
/// </summary>
public abstract class BaseEntity
{
    /// <summary>
    /// 唯一标识符
    /// </summary>
    public string Id { get; set; } = "";
    
    /// <summary>
    /// 创建时间 
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// 更新时间 
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// 生成新ID
    /// </summary>
    protected static string GenerateId(string prefix = "")
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var guid = Guid.NewGuid().ToString("N")[..8];
        return string.IsNullOrEmpty(prefix) 
            ? $"{timestamp}_{guid}" 
            : $"{prefix}_{timestamp}_{guid}";
    }
}

/// <summary>
/// 设备实体
/// </summary>
public class DeviceEntity : BaseEntity
{
    public string Name { get; set; } = "";
    public DeviceType Type { get; set; } = DeviceType.DGLab;
    public string? MacAddress { get; set; }
    public string? Config { get; set; }
    public bool AutoConnect { get; set; }
    public int LastStrengthA { get; set; }
    public int LastStrengthB { get; set; }
    public string? LastWaveformA { get; set; }
    public string? LastWaveformB { get; set; }
    
    public static DeviceEntity Create(string name, DeviceType type)
    {
        return new DeviceEntity
        {
            Id = GenerateId("dev"),
            Name = name,
            Type = type
        };
    }
}

/// <summary>
/// 设备类型
/// </summary>
public enum DeviceType
{
    DGLab,
    Yokonex,
    Custom
}

/// <summary>
/// 事件实体
/// </summary>
public class EventEntity : BaseEntity
{
    /// <summary>
    /// 事件标识符（用于触发）
    /// </summary>
    public string EventId { get; set; } = "";
    
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    
    /// <summary>
    /// 类别: system, game, custom
    /// </summary>
    public EventCategory Category { get; set; } = EventCategory.Custom;
    
    /// <summary>
    /// 适用设备类型
    /// </summary>
    public DeviceType? TargetDeviceType { get; set; }
    
    /// <summary>
    /// 目标通道
    /// </summary>
    public ChannelTarget Channel { get; set; } = ChannelTarget.A;
    
    /// <summary>
    /// 动作类型
    /// </summary>
    public EventAction Action { get; set; } = EventAction.Set;
    
    /// <summary>
    /// 强度值
    /// </summary>
    public int Value { get; set; }
    
    /// <summary>
    /// 持续时间（毫秒）
    /// </summary>
    public int Duration { get; set; }
    
    /// <summary>
    /// 波形数据 (JSON)
    /// </summary>
    public string? WaveformData { get; set; }
    
    /// <summary>
    /// 是否启用
    /// </summary>
    public bool Enabled { get; set; } = true;
    
    /// <summary>
    /// 优先级
    /// </summary>
    public int Priority { get; set; } = 10;
    
    /// <summary>
    /// 冷却时间（毫秒）
    /// </summary>
    public int Cooldown { get; set; }
    
    /// <summary>
    /// 最后触发时间
    /// </summary>
    public DateTime? LastTriggeredAt { get; set; }
    
    public static EventEntity Create(string eventId, string name, EventCategory category)
    {
        return new EventEntity
        {
            Id = GenerateId("evt"),
            EventId = eventId,
            Name = name,
            Category = category
        };
    }
}

public enum EventCategory
{
    System,
    Game,
    Custom
}

public enum ChannelTarget
{
    A,
    B,
    AB
}

public enum EventAction
{
    Set,
    Increase,
    Decrease,
    Wave,
    Pulse,
    Clear
}

/// <summary>
/// 脚本实体
/// </summary>
public class ScriptEntity : BaseEntity
{
    public string Name { get; set; } = "";
    public string Game { get; set; } = "";
    public string? Description { get; set; }
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = "Anonymous";
    public string Code { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string? Variables { get; set; } // JSON 存储脚本变量
    
    public static ScriptEntity Create(string name, string game)
    {
        return new ScriptEntity
        {
            Id = GenerateId("scr"),
            Name = name,
            Game = game
        };
    }
}

/// <summary>
/// 设置实体
/// </summary>
public class SettingEntity
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
    public string? Category { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 日志实体
/// </summary>
public class LogEntity
{
    public long Id { get; set; }
    public LogLevel Level { get; set; } = LogLevel.Info;
    public string? Module { get; set; }
    public string Message { get; set; } = "";
    public string? Data { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // 扩展字段
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? EventId { get; set; }
    public string? Source { get; set; }
}

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
    Fatal
}

/// <summary>
/// 房间实体（用于网络同步）
/// </summary>
public class RoomEntity : BaseEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string? Password { get; set; }
    public int MaxMembers { get; set; } = 10;
    public RoomMode Mode { get; set; } = RoomMode.FreeControl;
    public bool IsPublic { get; set; } = true;
    public string? Settings { get; set; } // JSON
    
    public static RoomEntity Create(string name, string ownerId)
    {
        return new RoomEntity
        {
            Id = GenerateId("room"),
            Code = GenerateRoomCode(),
            Name = name,
            OwnerId = ownerId
        };
    }
    
    private static string GenerateRoomCode()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var random = new Random();
        return new string(Enumerable.Range(0, 6).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }
}

public enum RoomMode
{
    FreeControl,   // 自由控制
    GameBattle,    // 游戏对战
    ViewOnly       // 仅观看
}

/// <summary>
/// 房间成员实体
/// </summary>
public class RoomMemberEntity : BaseEntity
{
    public string RoomId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public MemberRole Role { get; set; } = MemberRole.Member;
    public bool HasDevice { get; set; }
    public string? DeviceType { get; set; }
    public bool IsOnline { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public string? Permissions { get; set; } // JSON
}

public enum MemberRole
{
    Owner,
    Admin,
    Member,
    Observer
}
/// <summary>
/// 波形预设实体 - 用于存储自定义波形队列
/// </summary>
public class WaveformPresetEntity : BaseEntity
{
    /// <summary>
    /// 预设名称
    /// </summary>
    public string Name { get; set; } = "";
    
    /// <summary>
    /// 预设描述
    /// </summary>
    public string? Description { get; set; }
    
    /// <summary>
    /// 图标 (emoji)
    /// </summary>
    public string Icon { get; set; } = "🌊";
    
    /// <summary>
    /// 目标通道
    /// </summary>
    public ChannelTarget Channel { get; set; } = ChannelTarget.A;
    
    /// <summary>
    /// 波形数据 (HEX 格式，逗号分隔的多段波形)
    /// </summary>
    public string WaveformData { get; set; } = "";
    
    /// <summary>
    /// 持续时间（毫秒）
    /// </summary>
    public int Duration { get; set; } = 1000;
    
    /// <summary>
    /// 强度百分比 (0-100)
    /// </summary>
    public int Intensity { get; set; } = 50;
    
    /// <summary>
    /// 是否为内置预设
    /// </summary>
    public bool IsBuiltIn { get; set; }
    
    /// <summary>
    /// 排序顺序
    /// </summary>
    public int SortOrder { get; set; }
    
    public static WaveformPresetEntity Create(string name, string waveformData)
    {
        return new WaveformPresetEntity
        {
            Id = GenerateId("wave"),
            Name = name,
            WaveformData = waveformData
        };
    }
}