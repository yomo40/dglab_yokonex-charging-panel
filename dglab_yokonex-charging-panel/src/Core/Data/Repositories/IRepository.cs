using ChargingPanel.Core.Data.Entities;

namespace ChargingPanel.Core.Data.Repositories;

/// 通用仓储接口
/// <typeparam name="T">实体类型</typeparam>
public interface IRepository<T> where T : BaseEntity
{
        /// 获取所有实体
        IEnumerable<T> GetAll();
    
        /// 根据ID获取实体
        T? GetById(string id);
    
        /// 添加实体
        void Add(T entity);
    
        /// 更新实体
        void Update(T entity);
    
        /// 删除实体
        void Delete(string id);
    
        /// 保存实体（存在则更新，不存在则添加）
        void Save(T entity);
    
        /// 计数
        int Count();
}

/// 设备仓储接口
public interface IDeviceRepository : IRepository<DeviceEntity>
{
        /// 根据类型获取设备
        IEnumerable<DeviceEntity> GetByType(DeviceType type);
    
        /// 获取自动连接的设备
        IEnumerable<DeviceEntity> GetAutoConnectDevices();
    
        /// 根据MAC地址获取设备
        DeviceEntity? GetByMacAddress(string macAddress);
}

/// 事件仓储接口
public interface IEventRepository : IRepository<EventEntity>
{
        /// 根据事件ID获取
        EventEntity? GetByEventId(string eventId);
    
        /// 根据类别获取
        IEnumerable<EventEntity> GetByCategory(EventCategory category);
    
        /// 获取启用的事件
        IEnumerable<EventEntity> GetEnabled();
    
        /// 根据设备类型获取事件
        IEnumerable<EventEntity> GetByDeviceType(DeviceType? deviceType);
}

/// 脚本仓储接口
public interface IScriptRepository : IRepository<ScriptEntity>
{
        /// 根据游戏获取脚本
        IEnumerable<ScriptEntity> GetByGame(string game);
    
        /// 获取启用的脚本
        IEnumerable<ScriptEntity> GetEnabled();
}

/// 设置仓储接口
public interface ISettingRepository
{
        /// 获取设置值
        string? Get(string key);
    
        /// 获取设置值（带类型转换）
        T? Get<T>(string key, T? defaultValue = default);
    
        /// 设置值
        void Set(string key, object? value, string? category = null);
    
        /// 获取所有设置
        Dictionary<string, string?> GetAll();
    
        /// 获取某类别的所有设置
        Dictionary<string, string?> GetByCategory(string category);
}

/// 日志仓储接口
public interface ILogRepository
{
        /// 添加日志
        void Add(LogEntity log);
    
        /// 快速添加日志
        void Log(LogLevel level, string module, string message, object? data = null);
    
        /// 获取日志
        IEnumerable<LogEntity> GetLogs(int limit = 100, LogLevel? level = null, string? module = null, DateTime? since = null);
    
        /// 获取最近日志
        IEnumerable<LogEntity> GetRecent(int limit = 100);
    
        /// 获取设备操作日志
        IEnumerable<LogEntity> GetDeviceLogs(string deviceId, int limit = 100);
    
        /// 清理日志
        void Clear(int keepDays = 0);
    
        /// 日志轮转（清理超过指定大小的旧日志）
        void Rotate(int maxRecords = 10000);
}

/// 房间仓储接口
public interface IRoomRepository : IRepository<RoomEntity>
{
        /// 根据房间码获取
        RoomEntity? GetByCode(string code);
    
        /// 获取公开房间
        IEnumerable<RoomEntity> GetPublicRooms();
    
        /// 获取用户拥有的房间
        IEnumerable<RoomEntity> GetByOwner(string ownerId);
}

/// 波形预设仓储接口
public interface IWaveformPresetRepository : IRepository<WaveformPresetEntity>
{
        /// 根据通道获取预设
        IEnumerable<WaveformPresetEntity> GetByChannel(ChannelTarget channel);
}

/// 工作单元接口
public interface IUnitOfWork : IDisposable
{
    IDeviceRepository Devices { get; }
    IEventRepository Events { get; }
    IScriptRepository Scripts { get; }
    ISettingRepository Settings { get; }
    ILogRepository Logs { get; }
    IRoomRepository Rooms { get; }
    IWaveformPresetRepository WaveformPresets { get; }
    
        /// 开始事务
        void BeginTransaction();
    
        /// 提交事务
        void Commit();
    
        /// 回滚事务
        void Rollback();
}
