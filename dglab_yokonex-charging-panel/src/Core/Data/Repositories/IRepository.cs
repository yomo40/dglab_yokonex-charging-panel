using ChargingPanel.Core.Data.Entities;

namespace ChargingPanel.Core.Data.Repositories;

/// <summary>
/// 通用仓储接口
/// </summary>
/// <typeparam name="T">实体类型</typeparam>
public interface IRepository<T> where T : BaseEntity
{
    /// <summary>
    /// 获取所有实体
    /// </summary>
    IEnumerable<T> GetAll();
    
    /// <summary>
    /// 根据ID获取实体
    /// </summary>
    T? GetById(string id);
    
    /// <summary>
    /// 添加实体
    /// </summary>
    void Add(T entity);
    
    /// <summary>
    /// 更新实体
    /// </summary>
    void Update(T entity);
    
    /// <summary>
    /// 删除实体
    /// </summary>
    void Delete(string id);
    
    /// <summary>
    /// 保存实体（存在则更新，不存在则添加）
    /// </summary>
    void Save(T entity);
    
    /// <summary>
    /// 计数
    /// </summary>
    int Count();
}

/// <summary>
/// 设备仓储接口
/// </summary>
public interface IDeviceRepository : IRepository<DeviceEntity>
{
    /// <summary>
    /// 根据类型获取设备
    /// </summary>
    IEnumerable<DeviceEntity> GetByType(DeviceType type);
    
    /// <summary>
    /// 获取自动连接的设备
    /// </summary>
    IEnumerable<DeviceEntity> GetAutoConnectDevices();
    
    /// <summary>
    /// 根据MAC地址获取设备
    /// </summary>
    DeviceEntity? GetByMacAddress(string macAddress);
}

/// <summary>
/// 事件仓储接口
/// </summary>
public interface IEventRepository : IRepository<EventEntity>
{
    /// <summary>
    /// 根据事件ID获取
    /// </summary>
    EventEntity? GetByEventId(string eventId);
    
    /// <summary>
    /// 根据类别获取
    /// </summary>
    IEnumerable<EventEntity> GetByCategory(EventCategory category);
    
    /// <summary>
    /// 获取启用的事件
    /// </summary>
    IEnumerable<EventEntity> GetEnabled();
    
    /// <summary>
    /// 根据设备类型获取事件
    /// </summary>
    IEnumerable<EventEntity> GetByDeviceType(DeviceType? deviceType);
}

/// <summary>
/// 脚本仓储接口
/// </summary>
public interface IScriptRepository : IRepository<ScriptEntity>
{
    /// <summary>
    /// 根据游戏获取脚本
    /// </summary>
    IEnumerable<ScriptEntity> GetByGame(string game);
    
    /// <summary>
    /// 获取启用的脚本
    /// </summary>
    IEnumerable<ScriptEntity> GetEnabled();
}

/// <summary>
/// 设置仓储接口
/// </summary>
public interface ISettingRepository
{
    /// <summary>
    /// 获取设置值
    /// </summary>
    string? Get(string key);
    
    /// <summary>
    /// 获取设置值（带类型转换）
    /// </summary>
    T? Get<T>(string key, T? defaultValue = default);
    
    /// <summary>
    /// 设置值
    /// </summary>
    void Set(string key, object? value, string? category = null);
    
    /// <summary>
    /// 获取所有设置
    /// </summary>
    Dictionary<string, string?> GetAll();
    
    /// <summary>
    /// 获取某类别的所有设置
    /// </summary>
    Dictionary<string, string?> GetByCategory(string category);
}

/// <summary>
/// 日志仓储接口
/// </summary>
public interface ILogRepository
{
    /// <summary>
    /// 添加日志
    /// </summary>
    void Add(LogEntity log);
    
    /// <summary>
    /// 快速添加日志
    /// </summary>
    void Log(LogLevel level, string module, string message, object? data = null);
    
    /// <summary>
    /// 获取日志
    /// </summary>
    IEnumerable<LogEntity> GetLogs(int limit = 100, LogLevel? level = null, string? module = null, DateTime? since = null);
    
    /// <summary>
    /// 获取最近日志
    /// </summary>
    IEnumerable<LogEntity> GetRecent(int limit = 100);
    
    /// <summary>
    /// 获取设备操作日志
    /// </summary>
    IEnumerable<LogEntity> GetDeviceLogs(string deviceId, int limit = 100);
    
    /// <summary>
    /// 清理日志
    /// </summary>
    void Clear(int keepDays = 0);
    
    /// <summary>
    /// 日志轮转（清理超过指定大小的旧日志）
    /// </summary>
    void Rotate(int maxRecords = 10000);
}

/// <summary>
/// 房间仓储接口
/// </summary>
public interface IRoomRepository : IRepository<RoomEntity>
{
    /// <summary>
    /// 根据房间码获取
    /// </summary>
    RoomEntity? GetByCode(string code);
    
    /// <summary>
    /// 获取公开房间
    /// </summary>
    IEnumerable<RoomEntity> GetPublicRooms();
    
    /// <summary>
    /// 获取用户拥有的房间
    /// </summary>
    IEnumerable<RoomEntity> GetByOwner(string ownerId);
}

/// <summary>
/// 波形预设仓储接口
/// </summary>
public interface IWaveformPresetRepository : IRepository<WaveformPresetEntity>
{
    /// <summary>
    /// 根据通道获取预设
    /// </summary>
    IEnumerable<WaveformPresetEntity> GetByChannel(ChannelTarget channel);
}

/// <summary>
/// 工作单元接口
/// </summary>
public interface IUnitOfWork : IDisposable
{
    IDeviceRepository Devices { get; }
    IEventRepository Events { get; }
    IScriptRepository Scripts { get; }
    ISettingRepository Settings { get; }
    ILogRepository Logs { get; }
    IRoomRepository Rooms { get; }
    IWaveformPresetRepository WaveformPresets { get; }
    
    /// <summary>
    /// 开始事务
    /// </summary>
    void BeginTransaction();
    
    /// <summary>
    /// 提交事务
    /// </summary>
    void Commit();
    
    /// <summary>
    /// 回滚事务
    /// </summary>
    void Rollback();
}
