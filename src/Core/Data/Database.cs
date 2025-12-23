using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Serilog;

namespace ChargingPanel.Core.Data;

/// <summary>
/// SQLite 数据库管理器 - 优化版
/// 使用连接池、预编译语句和批量操作提升性能
/// </summary>
public class Database : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger _logger = Log.ForContext<Database>();
    private static Database? _instance;
    private static readonly object _lock = new();
    
    // 预编译语句缓存
    private readonly ConcurrentDictionary<string, SqliteCommand> _preparedCommands = new();
    
    // 批量日志缓冲
    private readonly ConcurrentQueue<(string level, string module, string message, string? data)> _logBuffer = new();
    private readonly System.Threading.Timer _logFlushTimer;
    private const int LogFlushIntervalMs = 1000;
    private const int MaxLogBufferSize = 50;

    public static Database Instance
    {
        get
        {
            lock (_lock)
            {
                return _instance ?? throw new InvalidOperationException("Database not initialized. Call Initialize() first.");
            }
        }
    }

    private Database(string dbPath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        // 优化 SQLite 性能
        ExecuteNonQuery("PRAGMA journal_mode=WAL;");
        ExecuteNonQuery("PRAGMA synchronous=NORMAL;");
        ExecuteNonQuery("PRAGMA foreign_keys=ON;");
        ExecuteNonQuery("PRAGMA cache_size=-8000;");  // 8MB 缓存
        ExecuteNonQuery("PRAGMA temp_store=MEMORY;");
        ExecuteNonQuery("PRAGMA mmap_size=268435456;");  // 256MB 内存映射

        // 先创建基础表结构
        InitializeBaseTables();
        // 然后迁移旧表（添加缺失的列）
        MigrateTables();
        // 最后创建索引和初始化数据
        InitializeIndexesAndData();
        
        // 启动日志刷新定时器
        _logFlushTimer = new System.Threading.Timer(_ => FlushLogBuffer(), null, LogFlushIntervalMs, LogFlushIntervalMs);
    }

    /// <summary>
    /// 数据库迁移 - 处理旧表缺少新列的情况
    /// </summary>
    private void MigrateTables()
    {
        try
        {
            // 检查 devices 表是否有 macAddress 列
            if (!ColumnExists("devices", "macAddress"))
            {
                _logger.Information("Migrating devices table: adding macAddress column");
                ExecuteNonQuery("ALTER TABLE devices ADD COLUMN macAddress TEXT");
            }

            // 检查 devices 表是否有 lastStrengthA 列
            if (!ColumnExists("devices", "lastStrengthA"))
            {
                _logger.Information("Migrating devices table: adding lastStrengthA column");
                ExecuteNonQuery("ALTER TABLE devices ADD COLUMN lastStrengthA INTEGER DEFAULT 0");
            }

            // 检查 devices 表是否有 lastStrengthB 列
            if (!ColumnExists("devices", "lastStrengthB"))
            {
                _logger.Information("Migrating devices table: adding lastStrengthB column");
                ExecuteNonQuery("ALTER TABLE devices ADD COLUMN lastStrengthB INTEGER DEFAULT 0");
            }

            // 检查 devices 表是否有 lastWaveformA 列
            if (!ColumnExists("devices", "lastWaveformA"))
            {
                _logger.Information("Migrating devices table: adding lastWaveformA column");
                ExecuteNonQuery("ALTER TABLE devices ADD COLUMN lastWaveformA TEXT");
            }

            // 检查 devices 表是否有 lastWaveformB 列
            if (!ColumnExists("devices", "lastWaveformB"))
            {
                _logger.Information("Migrating devices table: adding lastWaveformB column");
                ExecuteNonQuery("ALTER TABLE devices ADD COLUMN lastWaveformB TEXT");
            }

            // 检查 events 表是否有 targetDeviceType 列
            if (!ColumnExists("events", "targetDeviceType"))
            {
                _logger.Information("Migrating events table: adding targetDeviceType column");
                ExecuteNonQuery("ALTER TABLE events ADD COLUMN targetDeviceType TEXT CHECK(targetDeviceType IN ('dglab', 'yokonex', 'custom', NULL))");
            }

            // 检查 events 表是否有 waveformData 列
            if (!ColumnExists("events", "waveformData"))
            {
                _logger.Information("Migrating events table: adding waveformData column");
                ExecuteNonQuery("ALTER TABLE events ADD COLUMN waveformData TEXT");
            }

            // 检查 events 表是否有 priority 列
            if (!ColumnExists("events", "priority"))
            {
                _logger.Information("Migrating events table: adding priority column");
                ExecuteNonQuery("ALTER TABLE events ADD COLUMN priority INTEGER DEFAULT 10");
            }

            // 检查 events 表是否有 cooldown 列
            if (!ColumnExists("events", "cooldown"))
            {
                _logger.Information("Migrating events table: adding cooldown column");
                ExecuteNonQuery("ALTER TABLE events ADD COLUMN cooldown INTEGER DEFAULT 0");
            }

            // 检查 events 表是否有 lastTriggeredAt 列
            if (!ColumnExists("events", "lastTriggeredAt"))
            {
                _logger.Information("Migrating events table: adding lastTriggeredAt column");
                ExecuteNonQuery("ALTER TABLE events ADD COLUMN lastTriggeredAt TEXT");
            }

            // 检查 logs 表是否有 deviceId 列
            if (!ColumnExists("logs", "deviceId"))
            {
                _logger.Information("Migrating logs table: adding deviceId column");
                ExecuteNonQuery("ALTER TABLE logs ADD COLUMN deviceId TEXT");
            }

            // 检查 logs 表是否有 deviceName 列
            if (!ColumnExists("logs", "deviceName"))
            {
                _logger.Information("Migrating logs table: adding deviceName column");
                ExecuteNonQuery("ALTER TABLE logs ADD COLUMN deviceName TEXT");
            }

            // 检查 logs 表是否有 eventId 列
            if (!ColumnExists("logs", "eventId"))
            {
                _logger.Information("Migrating logs table: adding eventId column");
                ExecuteNonQuery("ALTER TABLE logs ADD COLUMN eventId TEXT");
            }

            // 检查 logs 表是否有 source 列
            if (!ColumnExists("logs", "source"))
            {
                _logger.Information("Migrating logs table: adding source column");
                ExecuteNonQuery("ALTER TABLE logs ADD COLUMN source TEXT");
            }

            _logger.Information("Database migration completed");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Database migration failed");
            throw;
        }
    }

    /// <summary>
    /// 检查表中是否存在指定列
    /// </summary>
    private bool ColumnExists(string tableName, string columnName)
    {
        var columns = ExecuteQuery(
            $"PRAGMA table_info({tableName})",
            reader => reader.GetString(reader.GetOrdinal("name"))
        );
        return columns.Contains(columnName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 初始化数据库
    /// </summary>
    public static void Initialize(string dbPath)
    {
        lock (_lock)
        {
            _instance?.Dispose();
            _instance = new Database(dbPath);
        }
    }

    /// <summary>
    /// 创建基础表结构（不包含可能依赖新列的索引）
    /// </summary>
    private void InitializeBaseTables()
    {
        // 设备表 - 基础结构（新列通过迁移添加）
        ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS devices (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                type TEXT NOT NULL CHECK(type IN ('dglab', 'yokonex', 'custom')),
                config TEXT,
                autoConnect INTEGER DEFAULT 0,
                createdAt TEXT NOT NULL,
                updatedAt TEXT NOT NULL
            )
        ");

        // 事件表 - 基础结构（新列通过迁移添加）
        ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS events (
                id TEXT PRIMARY KEY,
                eventId TEXT UNIQUE NOT NULL,
                name TEXT NOT NULL,
                description TEXT,
                category TEXT NOT NULL CHECK(category IN ('system', 'game', 'custom')),
                channel TEXT NOT NULL CHECK(channel IN ('A', 'B', 'AB')),
                action TEXT NOT NULL CHECK(action IN ('set', 'increase', 'decrease', 'wave', 'pulse', 'clear')),
                value INTEGER DEFAULT 0,
                duration INTEGER DEFAULT 0,
                enabled INTEGER DEFAULT 1,
                createdAt TEXT NOT NULL,
                updatedAt TEXT NOT NULL
            )
        ");

        // 脚本表
        ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS scripts (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                game TEXT NOT NULL,
                description TEXT,
                version TEXT DEFAULT '1.0.0',
                author TEXT DEFAULT 'Anonymous',
                code TEXT NOT NULL,
                variables TEXT,
                enabled INTEGER DEFAULT 1,
                createdAt TEXT NOT NULL,
                updatedAt TEXT NOT NULL
            )
        ");

        // 设置表
        ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT,
                category TEXT,
                updatedAt TEXT NOT NULL
            )
        ");

        // 日志表 - 基础结构（新列通过迁移添加）
        ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                level TEXT NOT NULL CHECK(level IN ('debug', 'info', 'warning', 'error', 'fatal')),
                module TEXT,
                message TEXT NOT NULL,
                data TEXT,
                createdAt TEXT NOT NULL
            )
        ");

        // 房间表
        ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS rooms (
                id TEXT PRIMARY KEY,
                code TEXT UNIQUE NOT NULL,
                name TEXT NOT NULL,
                ownerId TEXT NOT NULL,
                password TEXT,
                maxMembers INTEGER DEFAULT 10,
                mode TEXT NOT NULL CHECK(mode IN ('freecontrol', 'gamebattle', 'viewonly')),
                isPublic INTEGER DEFAULT 1,
                settings TEXT,
                createdAt TEXT NOT NULL,
                updatedAt TEXT NOT NULL
            )
        ");

        // 房间成员表
        ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS room_members (
                id TEXT PRIMARY KEY,
                roomId TEXT NOT NULL,
                userId TEXT NOT NULL,
                nickname TEXT NOT NULL,
                role TEXT NOT NULL CHECK(role IN ('owner', 'admin', 'member', 'observer')),
                hasDevice INTEGER DEFAULT 0,
                deviceType TEXT,
                isOnline INTEGER DEFAULT 0,
                lastSeenAt TEXT,
                permissions TEXT,
                createdAt TEXT NOT NULL,
                updatedAt TEXT NOT NULL,
                FOREIGN KEY (roomId) REFERENCES rooms(id) ON DELETE CASCADE
            )
        ");

        // 波形预设表 - 存储自定义波形队列
        ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS waveform_presets (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                description TEXT,
                icon TEXT DEFAULT '🌊',
                channel TEXT NOT NULL CHECK(channel IN ('A', 'B', 'AB')),
                waveformData TEXT NOT NULL,
                duration INTEGER DEFAULT 1000,
                intensity INTEGER DEFAULT 50,
                isBuiltIn INTEGER DEFAULT 0,
                sortOrder INTEGER DEFAULT 0,
                createdAt TEXT NOT NULL,
                updatedAt TEXT NOT NULL
            )
        ");

        // 传感器规则表 - 存储役次元传感器触发规则
        ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS sensor_rules (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                deviceId TEXT,
                sensorType TEXT NOT NULL CHECK(sensorType IN ('step', 'angle', 'channel')),
                triggerType TEXT NOT NULL CHECK(triggerType IN ('threshold', 'change', 'connect', 'disconnect')),
                threshold REAL DEFAULT 0,
                targetDeviceId TEXT,
                targetChannel TEXT DEFAULT 'A' CHECK(targetChannel IN ('A', 'B', 'AB')),
                action TEXT NOT NULL CHECK(action IN ('set', 'increase', 'decrease', 'pulse', 'wave')),
                value INTEGER DEFAULT 10,
                duration INTEGER DEFAULT 500,
                cooldownMs INTEGER DEFAULT 1000,
                enabled INTEGER DEFAULT 1,
                createdAt TEXT NOT NULL,
                updatedAt TEXT NOT NULL
            )
        ");

        _logger.Information("Database base tables created");
    }

    /// <summary>
    /// 创建索引和初始化默认数据（在迁移完成后执行）
    /// </summary>
    private void InitializeIndexesAndData()
    {
        // 创建索引（依赖迁移后的列）
        ExecuteNonQuery(@"
            CREATE INDEX IF NOT EXISTS idx_devices_type ON devices(type);
            CREATE INDEX IF NOT EXISTS idx_devices_macAddress ON devices(macAddress);
            CREATE INDEX IF NOT EXISTS idx_events_eventId ON events(eventId);
            CREATE INDEX IF NOT EXISTS idx_events_category ON events(category);
            CREATE INDEX IF NOT EXISTS idx_events_enabled ON events(enabled);
            CREATE INDEX IF NOT EXISTS idx_scripts_game ON scripts(game);
            CREATE INDEX IF NOT EXISTS idx_settings_category ON settings(category);
            CREATE INDEX IF NOT EXISTS idx_logs_level ON logs(level);
            CREATE INDEX IF NOT EXISTS idx_logs_module ON logs(module);
            CREATE INDEX IF NOT EXISTS idx_logs_deviceId ON logs(deviceId);
            CREATE INDEX IF NOT EXISTS idx_logs_createdAt ON logs(createdAt);
            CREATE INDEX IF NOT EXISTS idx_rooms_code ON rooms(code);
            CREATE INDEX IF NOT EXISTS idx_rooms_ownerId ON rooms(ownerId);
            CREATE INDEX IF NOT EXISTS idx_room_members_roomId ON room_members(roomId);
            CREATE INDEX IF NOT EXISTS idx_room_members_userId ON room_members(userId);
            CREATE INDEX IF NOT EXISTS idx_waveform_presets_channel ON waveform_presets(channel);
            CREATE INDEX IF NOT EXISTS idx_waveform_presets_sortOrder ON waveform_presets(sortOrder);
            CREATE INDEX IF NOT EXISTS idx_sensor_rules_deviceId ON sensor_rules(deviceId);
            CREATE INDEX IF NOT EXISTS idx_sensor_rules_sensorType ON sensor_rules(sensorType);
            CREATE INDEX IF NOT EXISTS idx_sensor_rules_enabled ON sensor_rules(enabled);
        ");

        // 初始化默认数据
        InitializeDefaultData();

        _logger.Information("Database indexes and default data initialized");
    }

    private void InitializeDefaultData()
    {
        var now = DateTime.UtcNow.ToString("o");

        // 检查是否已有事件数据
        var eventCount = ExecuteScalar<long>("SELECT COUNT(*) FROM events");
        if (eventCount == 0)
        {
            var defaultEvents = new[]
            {
                ("lost-ahp", "护甲损失", "护甲损失时触发电击反馈", "system", "B", "increase", 10, 500),
                ("lost-hp", "血量损失", "血量损失时触发电击反馈", "system", "A", "increase", 15, 500),
                ("add-ahp", "护甲恢复", "护甲恢复时轻微反馈", "system", "B", "set", 5, 300),
                ("add-hp", "血量恢复", "血量恢复时轻微反馈", "system", "A", "set", 5, 300),
                ("character-debuff", "角色受负面效果", "中毒、流血等持续伤害", "system", "AB", "wave", 8, 1000),
                ("query", "查询", "查询当前血量状态", "system", "A", "set", 0, 0),
                ("dead", "死亡", "角色死亡时强烈反馈", "system", "AB", "set", 100, 2000),
                ("knocked", "倒地/击倒", "被击倒可救起时的反馈", "system", "AB", "set", 80, 1500),
                ("respawn", "重生", "角色重生时的反馈", "system", "A", "pulse", 30, 500),
                ("new-round", "新回合", "新回合/关卡开始时的反馈", "system", "AB", "pulse", 20, 300),
                ("game-over", "游戏结束", "游戏结束时的反馈", "system", "AB", "set", 50, 1000),
                ("new-credit", "获得积分", "完成任务获得积分时反馈", "system", "A", "pulse", 20, 500),
                ("step-count-changed", "步数变化", "役次元设备计步器步数变化触发", "system", "A", "pulse", 15, 300),
                ("angle-changed", "角度变化", "役次元设备角度传感器变化触发", "system", "B", "pulse", 20, 400),
                ("channel-disconnected", "通道断开", "役次元设备电极片脱落时触发", "system", "AB", "set", 0, 0),
                ("channel-connected", "通道连接", "役次元设备电极片接入时触发", "system", "A", "pulse", 10, 200)
            };

            using var transaction = _connection.BeginTransaction();
            try
            {
                foreach (var (eventId, name, description, category, channel, action, value, duration) in defaultEvents)
                {
                    ExecuteNonQuery(@"
                        INSERT INTO events (id, eventId, name, description, category, channel, action, value, duration, enabled, createdAt, updatedAt)
                        VALUES (@id, @eventId, @name, @description, @category, @channel, @action, @value, @duration, 1, @now, @now)
                    ",
                    ("@id", $"evt_{eventId}"),
                    ("@eventId", eventId),
                    ("@name", name),
                    ("@description", description),
                    ("@category", category),
                    ("@channel", channel),
                    ("@action", action),
                    ("@value", value),
                    ("@duration", duration),
                    ("@now", now));
                }
                transaction.Commit();
                _logger.Information("Initialized {Count} default system events", defaultEvents.Length);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        // 检查是否已有设置数据
        var settingCount = ExecuteScalar<long>("SELECT COUNT(*) FROM settings");
        if (settingCount == 0)
        {
            var defaultSettings = new[]
            {
                ("server.port", "3000", "server"),
                ("server.host", "\"0.0.0.0\"", "server"),
                ("safety.autoStop", "true", "safety"),
                ("safety.defaultLimit", "100", "safety"),
                ("safety.maxStrength", "200", "safety"),
                ("ocr.enabled", "false", "ocr"),
                ("ocr.interval", "100", "ocr")
            };

            foreach (var (key, value, category) in defaultSettings)
            {
                ExecuteNonQuery(@"
                    INSERT INTO settings (key, value, category, updatedAt) VALUES (@key, @value, @category, @now)
                ", ("@key", key), ("@value", value), ("@category", category), ("@now", now));
            }
            _logger.Information("Initialized default settings");
        }

        // 检查是否已有波形预设数据
        var presetCount = ExecuteScalar<long>("SELECT COUNT(*) FROM waveform_presets");
        if (presetCount == 0)
        {
            // 内置波形预设 - DG-LAB 常用波形
            var defaultPresets = new[]
            {
                ("💨", "呼吸灯", "渐强渐弱的呼吸效果", "AB", "0A0A0A0A0A0A0A0A", 1000, 50, 1),
                ("❤️", "心跳", "模拟心跳节奏", "AB", "0F0F0F0F00000000", 800, 60, 2),
                ("📳", "震动", "持续震动效果", "AB", "0F0F0F0F0F0F0F0F", 500, 40, 3),
                ("📈", "爬升", "强度逐渐增加", "AB", "01020304050607080910", 1500, 70, 4),
                ("🎲", "随机", "随机强度变化", "AB", "0305080206040901070A", 600, 50, 5),
                ("⚡", "脉冲", "短促脉冲刺激", "AB", "0F000F000F00", 400, 80, 6),
                ("🌊", "波浪", "波浪起伏效果", "AB", "0103050709070503010305070907050301", 2000, 55, 7),
                ("🔥", "火焰", "快速闪烁效果", "AB", "0F050F050F050F05", 300, 75, 8)
            };

            foreach (var (icon, name, description, channel, waveformData, duration, intensity, sortOrder) in defaultPresets)
            {
                var id = $"wave_builtin_{name.GetHashCode():X8}";
                ExecuteNonQuery(@"
                    INSERT INTO waveform_presets (id, name, description, icon, channel, waveformData, duration, intensity, isBuiltIn, sortOrder, createdAt, updatedAt)
                    VALUES (@id, @name, @description, @icon, @channel, @waveformData, @duration, @intensity, 1, @sortOrder, @now, @now)
                ",
                ("@id", id),
                ("@name", name),
                ("@description", description),
                ("@icon", icon),
                ("@channel", channel),
                ("@waveformData", waveformData),
                ("@duration", duration),
                ("@intensity", intensity),
                ("@sortOrder", sortOrder),
                ("@now", now));
            }
            _logger.Information("Initialized {Count} default waveform presets", defaultPresets.Length);
        }
    }

    #region Helper Methods

    private void ExecuteNonQuery(string sql, params (string name, object? value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        cmd.ExecuteNonQuery();
    }

    private T ExecuteScalar<T>(string sql, params (string name, object? value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? default! : (T)Convert.ChangeType(result, typeof(T));
    }

    private List<T> ExecuteQuery<T>(string sql, Func<SqliteDataReader, T> mapper, params (string name, object? value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        using var reader = cmd.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
        {
            results.Add(mapper(reader));
        }
        return results;
    }

    private T? ExecuteQuerySingle<T>(string sql, Func<SqliteDataReader, T> mapper, params (string name, object? value)[] parameters) where T : class
    {
        var results = ExecuteQuery(sql, mapper, parameters);
        return results.Count > 0 ? results[0] : null;
    }

    #endregion

    #region Device Operations

    public List<DeviceRecord> GetAllDevices()
    {
        return ExecuteQuery("SELECT * FROM devices ORDER BY createdAt DESC", MapDeviceRecord);
    }

    public DeviceRecord? GetDevice(string id)
    {
        return ExecuteQuerySingle("SELECT * FROM devices WHERE id = @id", MapDeviceRecord, ("@id", id));
    }

    public void AddDevice(DeviceRecord device)
    {
        var now = DateTime.UtcNow.ToString("o");
        ExecuteNonQuery(@"
            INSERT INTO devices (id, name, type, config, autoConnect, createdAt, updatedAt)
            VALUES (@id, @name, @type, @config, @autoConnect, @now, @now)
        ",
        ("@id", device.Id),
        ("@name", device.Name),
        ("@type", device.Type),
        ("@config", device.Config),
        ("@autoConnect", device.AutoConnect ? 1 : 0),
        ("@now", now));
    }

    public bool UpdateDevice(string id, DeviceRecord updates)
    {
        var now = DateTime.UtcNow.ToString("o");
        ExecuteNonQuery(@"
            UPDATE devices SET name = @name, type = @type, config = @config, autoConnect = @autoConnect, updatedAt = @now
            WHERE id = @id
        ",
        ("@id", id),
        ("@name", updates.Name),
        ("@type", updates.Type),
        ("@config", updates.Config),
        ("@autoConnect", updates.AutoConnect ? 1 : 0),
        ("@now", now));
        return true;
    }

    public bool DeleteDevice(string id)
    {
        ExecuteNonQuery("DELETE FROM devices WHERE id = @id", ("@id", id));
        return true;
    }

    private static DeviceRecord MapDeviceRecord(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        Type = reader.GetString(2),
        Config = reader.IsDBNull(3) ? null : reader.GetString(3),
        AutoConnect = reader.GetInt32(4) == 1,
        CreatedAt = reader.GetString(5),
        UpdatedAt = reader.GetString(6)
    };

    #endregion

    #region Event Operations

    public List<EventRecord> GetAllEvents()
    {
        return ExecuteQuery("SELECT * FROM events ORDER BY category, eventId", MapEventRecord);
    }

    public List<EventRecord> GetEventsByCategory(string category)
    {
        return ExecuteQuery("SELECT * FROM events WHERE category = @category ORDER BY eventId", MapEventRecord, ("@category", category));
    }

    public EventRecord? GetEventByEventId(string eventId)
    {
        return ExecuteQuerySingle("SELECT * FROM events WHERE eventId = @eventId", MapEventRecord, ("@eventId", eventId));
    }

    public EventRecord? GetEvent(string id)
    {
        return ExecuteQuerySingle("SELECT * FROM events WHERE id = @id", MapEventRecord, ("@id", id));
    }

    public void AddEvent(EventRecord eventRecord)
    {
        var now = DateTime.UtcNow.ToString("o");
        var id = $"evt_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}".Substring(0, 30);
        ExecuteNonQuery(@"
            INSERT INTO events (id, eventId, name, description, category, channel, action, value, duration, waveformData, enabled, createdAt, updatedAt)
            VALUES (@id, @eventId, @name, @description, @category, @channel, @action, @value, @duration, @waveformData, @enabled, @now, @now)
        ",
        ("@id", id),
        ("@eventId", eventRecord.EventId),
        ("@name", eventRecord.Name),
        ("@description", eventRecord.Description),
        ("@category", eventRecord.Category),
        ("@channel", eventRecord.Channel),
        ("@action", eventRecord.Action),
        ("@value", eventRecord.Value),
        ("@duration", eventRecord.Duration),
        ("@waveformData", eventRecord.WaveformData),
        ("@enabled", eventRecord.Enabled ? 1 : 0),
        ("@now", now));
    }

    public bool UpdateEvent(string id, EventRecord updates)
    {
        var now = DateTime.UtcNow.ToString("o");
        ExecuteNonQuery(@"
            UPDATE events SET eventId = @eventId, name = @name, description = @description, category = @category, 
            channel = @channel, action = @action, value = @value, duration = @duration, waveformData = @waveformData, 
            enabled = @enabled, updatedAt = @now WHERE id = @id
        ",
        ("@id", id),
        ("@eventId", updates.EventId),
        ("@name", updates.Name),
        ("@description", updates.Description),
        ("@category", updates.Category),
        ("@channel", updates.Channel),
        ("@action", updates.Action),
        ("@value", updates.Value),
        ("@duration", updates.Duration),
        ("@waveformData", updates.WaveformData),
        ("@enabled", updates.Enabled ? 1 : 0),
        ("@now", now));
        return true;
    }

    public bool DeleteEvent(string id)
    {
        ExecuteNonQuery("DELETE FROM events WHERE id = @id OR eventId = @id", ("@id", id));
        return true;
    }

    public void SaveEvent(EventRecord eventRecord)
    {
        var existing = GetEventByEventId(eventRecord.EventId) ?? GetEvent(eventRecord.Id);
        if (existing != null)
        {
            UpdateEvent(existing.Id, eventRecord);
        }
        else
        {
            AddEvent(eventRecord);
        }
    }

    private static EventRecord MapEventRecord(SqliteDataReader reader)
    {
        // 安全获取列索引，如果列不存在返回 -1
        int GetOrdinalSafe(string name)
        {
            try { return reader.GetOrdinal(name); }
            catch { return -1; }
        }
        
        var waveformOrdinal = GetOrdinalSafe("waveformData");
        var priorityOrdinal = GetOrdinalSafe("priority");
        
        return new EventRecord
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            EventId = reader.GetString(reader.GetOrdinal("eventId")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            Description = reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
            Category = reader.GetString(reader.GetOrdinal("category")),
            Channel = reader.GetString(reader.GetOrdinal("channel")),
            Action = reader.GetString(reader.GetOrdinal("action")),
            Value = reader.GetInt32(reader.GetOrdinal("value")),
            Duration = reader.GetInt32(reader.GetOrdinal("duration")),
            WaveformData = waveformOrdinal >= 0 && !reader.IsDBNull(waveformOrdinal) ? reader.GetString(waveformOrdinal) : null,
            Enabled = reader.GetInt32(reader.GetOrdinal("enabled")) == 1,
            CreatedAt = reader.GetString(reader.GetOrdinal("createdAt")),
            UpdatedAt = reader.GetString(reader.GetOrdinal("updatedAt")),
            // 映射额外的数据库列到 UI 属性
            Priority = priorityOrdinal >= 0 && !reader.IsDBNull(priorityOrdinal) ? reader.GetInt32(priorityOrdinal) : 10,
            // 从 Value 映射到 Strength
            Strength = reader.GetInt32(reader.GetOrdinal("value")),
            // 默认触发类型和范围
            TriggerType = "decrease",
            MinChange = 1,
            MaxChange = 100,
            ActionType = reader.GetString(reader.GetOrdinal("action"))
        };
    }

    #endregion

    #region Script Operations

    public List<ScriptRecord> GetAllScripts()
    {
        return ExecuteQuery("SELECT * FROM scripts ORDER BY name", MapScriptRecord);
    }

    public List<ScriptRecord> GetScriptsByGame(string game)
    {
        return ExecuteQuery("SELECT * FROM scripts WHERE game = @game ORDER BY name", MapScriptRecord, ("@game", game));
    }

    public ScriptRecord? GetScript(string id)
    {
        return ExecuteQuerySingle("SELECT * FROM scripts WHERE id = @id", MapScriptRecord, ("@id", id));
    }

    public void AddScript(ScriptRecord script)
    {
        var now = DateTime.UtcNow.ToString("o");
        var id = $"scr_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}".Substring(0, 30);
        ExecuteNonQuery(@"
            INSERT INTO scripts (id, name, game, description, version, author, code, enabled, createdAt, updatedAt)
            VALUES (@id, @name, @game, @description, @version, @author, @code, @enabled, @now, @now)
        ",
        ("@id", id),
        ("@name", script.Name),
        ("@game", script.Game),
        ("@description", script.Description),
        ("@version", script.Version),
        ("@author", script.Author),
        ("@code", script.Code),
        ("@enabled", script.Enabled ? 1 : 0),
        ("@now", now));
    }

    public bool UpdateScript(string id, ScriptRecord updates)
    {
        var now = DateTime.UtcNow.ToString("o");
        ExecuteNonQuery(@"
            UPDATE scripts SET name = @name, game = @game, description = @description, version = @version,
            author = @author, code = @code, enabled = @enabled, updatedAt = @now WHERE id = @id
        ",
        ("@id", id),
        ("@name", updates.Name),
        ("@game", updates.Game),
        ("@description", updates.Description),
        ("@version", updates.Version),
        ("@author", updates.Author),
        ("@code", updates.Code),
        ("@enabled", updates.Enabled ? 1 : 0),
        ("@now", now));
        return true;
    }

    public bool DeleteScript(string id)
    {
        ExecuteNonQuery("DELETE FROM scripts WHERE id = @id", ("@id", id));
        return true;
    }

    public void SaveScript(ScriptRecord script)
    {
        var existing = GetScript(script.Id);
        if (existing != null)
        {
            UpdateScript(script.Id, script);
        }
        else
        {
            AddScript(script);
        }
    }

    public Dictionary<string, string> GetAllSettings()
    {
        var results = ExecuteQuery("SELECT key, value FROM settings", 
            r => (r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
        return results.ToDictionary(x => x.Item1, x => x.Item2);
    }

    private static ScriptRecord MapScriptRecord(SqliteDataReader reader)
    {
        // 列顺序: id(0), name(1), game(2), description(3), version(4), author(5), code(6), variables(7), enabled(8), createdAt(9), updatedAt(10)
        return new ScriptRecord
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Game = reader.GetString(2),
            Description = reader.IsDBNull(3) ? null : reader.GetString(3),
            Version = reader.IsDBNull(4) ? "1.0.0" : reader.GetString(4),
            Author = reader.IsDBNull(5) ? "Anonymous" : reader.GetString(5),
            Code = reader.IsDBNull(6) ? "" : reader.GetString(6),
            // variables 在索引 7，跳过
            Enabled = reader.IsDBNull(8) ? true : reader.GetInt32(8) == 1,
            CreatedAt = reader.IsDBNull(9) ? "" : reader.GetString(9),
            UpdatedAt = reader.IsDBNull(10) ? "" : reader.GetString(10)
        };
    }

    /// <summary>
    /// 导入默认脚本（如果尚未导入）
    /// </summary>
    public void ImportDefaultScripts(string scriptsPath)
    {
        if (!Directory.Exists(scriptsPath))
        {
            _logger.Debug("Default scripts path not found: {Path}", scriptsPath);
            return;
        }

        var scriptFiles = Directory.GetFiles(scriptsPath, "*.js");
        foreach (var filePath in scriptFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var scriptId = $"default_{fileName}";
            
            // 检查是否已存在
            var existing = GetScript(scriptId);
            if (existing != null)
            {
                _logger.Debug("Default script already exists: {Name}", fileName);
                continue;
            }
            
            try
            {
                var code = File.ReadAllText(filePath);
                var script = new ScriptRecord
                {
                    Id = scriptId,
                    Name = fileName.Replace("_", " "),
                    Game = ExtractGameFromScript(code) ?? "通用",
                    Description = "默认脚本",
                    Version = "1.0.0",
                    Author = "System",
                    Code = code,
                    Enabled = false
                };
                
                AddScript(script);
                _logger.Information("Imported default script: {Name}", fileName);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to import default script: {Path}", filePath);
            }
        }
    }
    
    private static string? ExtractGameFromScript(string code)
    {
        // 尝试从脚本的 return 语句中提取 game 字段
        var match = System.Text.RegularExpressions.Regex.Match(code, @"game:\s*['""]([^'""]+)['""]");
        return match.Success ? match.Groups[1].Value : null;
    }

    #endregion

    #region Settings Operations

    public string? GetSetting(string key)
    {
        return ExecuteScalar<string?>("SELECT value FROM settings WHERE key = @key", ("@key", key));
    }

    public T? GetSetting<T>(string key, T? defaultValue = default)
    {
        var value = GetSetting(key);
        if (string.IsNullOrEmpty(value)) return defaultValue;
        
        try
        {
            if (typeof(T) == typeof(bool))
                return (T)(object)(value.ToLower() == "true" || value == "1");
            if (typeof(T) == typeof(int))
                return (T)(object)int.Parse(value);
            if (typeof(T) == typeof(double))
                return (T)(object)double.Parse(value);
            return System.Text.Json.JsonSerializer.Deserialize<T>(value);
        }
        catch
        {
            return defaultValue;
        }
    }

    public void SetSetting(string key, object? value, string? category = null)
    {
        var now = DateTime.UtcNow.ToString("o");
        var valueStr = value is string s ? s : System.Text.Json.JsonSerializer.Serialize(value);
        
        ExecuteNonQuery(@"
            INSERT INTO settings (key, value, category, updatedAt) VALUES (@key, @value, @category, @now)
            ON CONFLICT(key) DO UPDATE SET value = @value, category = COALESCE(@category, category), updatedAt = @now
        ",
        ("@key", key),
        ("@value", valueStr),
        ("@category", category),
        ("@now", now));
    }

    public Dictionary<string, string?> GetSettingsByCategory(string category)
    {
        var results = ExecuteQuery("SELECT key, value FROM settings WHERE category = @category", 
            r => (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)),
            ("@category", category));
        return results.ToDictionary(x => x.Item1, x => x.Item2);
    }

    #endregion

    #region Log Operations

    /// <summary>
    /// 添加日志（异步批量写入）
    /// </summary>
    public void AddLog(string level, string module, string message, string? data = null)
    {
        _logBuffer.Enqueue((level, module, message, data));
        
        // 如果缓冲区满了，立即刷新
        if (_logBuffer.Count >= MaxLogBufferSize)
        {
            FlushLogBuffer();
        }
    }
    
    /// <summary>
    /// 刷新日志缓冲区到数据库
    /// </summary>
    private void FlushLogBuffer()
    {
        if (_logBuffer.IsEmpty) return;
        
        var logs = new List<(string level, string module, string message, string? data)>();
        while (_logBuffer.TryDequeue(out var log) && logs.Count < MaxLogBufferSize * 2)
        {
            logs.Add(log);
        }
        
        if (logs.Count == 0) return;
        
        try
        {
            using var transaction = _connection.BeginTransaction();
            var now = DateTime.UtcNow.ToString("o");
            
            foreach (var (level, module, message, data) in logs)
            {
                ExecuteNonQuery(@"INSERT INTO logs (level, module, message, data, createdAt) VALUES (@level, @module, @message, @data, @now)",
                    ("@level", level), ("@module", module), ("@message", message), ("@data", data), ("@now", now));
            }
            
            transaction.Commit();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to flush log buffer");
        }
    }

    public List<LogRecord> GetLogs(int limit = 100, string? level = null, string? module = null)
    {
        var sql = "SELECT * FROM logs";
        var conditions = new List<string>();
        var parameters = new List<(string, object?)>();

        if (level != null)
        {
            conditions.Add("level = @level");
            parameters.Add(("@level", level));
        }
        if (module != null)
        {
            conditions.Add("module = @module");
            parameters.Add(("@module", module));
        }

        if (conditions.Count > 0)
            sql += " WHERE " + string.Join(" AND ", conditions);

        sql += " ORDER BY createdAt DESC LIMIT @limit";
        parameters.Add(("@limit", limit));

        return ExecuteQuery(sql, MapLogRecord, parameters.ToArray());
    }

    public List<LogRecord> GetRecentLogs(int limit = 100)
    {
        var sql = "SELECT * FROM logs ORDER BY createdAt DESC LIMIT @limit";
        return ExecuteQuery(sql, MapLogRecord, ("@limit", limit));
    }

    public void ClearLogs(int keepDays = 0)
    {
        if (keepDays <= 0)
        {
            ExecuteNonQuery("DELETE FROM logs");
        }
        else
        {
            var cutoff = DateTime.UtcNow.AddDays(-keepDays).ToString("o");
            ExecuteNonQuery("DELETE FROM logs WHERE createdAt < @cutoff", ("@cutoff", cutoff));
        }
    }

    private static LogRecord MapLogRecord(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Level = reader.GetString(1),
        Module = reader.IsDBNull(2) ? null : reader.GetString(2),
        Message = reader.GetString(3),
        Data = reader.IsDBNull(4) ? null : reader.GetString(4),
        CreatedAt = reader.GetString(5),
        // 转换为扩展字段
        Timestamp = DateTime.TryParse(reader.GetString(5), out var ts) ? ts : DateTime.Now,
        Action = reader.GetString(3),
        Source = reader.IsDBNull(2) ? null : reader.GetString(2)
    };

    #endregion

    #region Waveform Preset Operations

    public List<WaveformPresetRecord> GetAllWaveformPresets()
    {
        return ExecuteQuery("SELECT * FROM waveform_presets ORDER BY isBuiltIn DESC, sortOrder ASC, createdAt DESC", MapWaveformPresetRecord);
    }

    public List<WaveformPresetRecord> GetWaveformPresetsByChannel(string channel)
    {
        return ExecuteQuery(
            "SELECT * FROM waveform_presets WHERE channel = @channel OR channel = 'AB' ORDER BY isBuiltIn DESC, sortOrder ASC", 
            MapWaveformPresetRecord, 
            ("@channel", channel));
    }

    public WaveformPresetRecord? GetWaveformPreset(string id)
    {
        return ExecuteQuerySingle("SELECT * FROM waveform_presets WHERE id = @id", MapWaveformPresetRecord, ("@id", id));
    }

    public void AddWaveformPreset(WaveformPresetRecord preset)
    {
        var now = DateTime.UtcNow.ToString("o");
        var id = $"wave_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}"[..35];
        ExecuteNonQuery(@"
            INSERT INTO waveform_presets (id, name, description, icon, channel, waveformData, duration, intensity, isBuiltIn, sortOrder, createdAt, updatedAt)
            VALUES (@id, @name, @description, @icon, @channel, @waveformData, @duration, @intensity, @isBuiltIn, @sortOrder, @now, @now)
        ",
        ("@id", id),
        ("@name", preset.Name),
        ("@description", preset.Description),
        ("@icon", preset.Icon ?? "🌊"),
        ("@channel", preset.Channel),
        ("@waveformData", preset.WaveformData),
        ("@duration", preset.Duration),
        ("@intensity", preset.Intensity),
        ("@isBuiltIn", preset.IsBuiltIn ? 1 : 0),
        ("@sortOrder", preset.SortOrder),
        ("@now", now));
    }

    public bool UpdateWaveformPreset(string id, WaveformPresetRecord updates)
    {
        var now = DateTime.UtcNow.ToString("o");
        ExecuteNonQuery(@"
            UPDATE waveform_presets SET name = @name, description = @description, icon = @icon, 
            channel = @channel, waveformData = @waveformData, duration = @duration, intensity = @intensity,
            sortOrder = @sortOrder, updatedAt = @now
            WHERE id = @id
        ",
        ("@id", id),
        ("@name", updates.Name),
        ("@description", updates.Description),
        ("@icon", updates.Icon ?? "🌊"),
        ("@channel", updates.Channel),
        ("@waveformData", updates.WaveformData),
        ("@duration", updates.Duration),
        ("@intensity", updates.Intensity),
        ("@sortOrder", updates.SortOrder),
        ("@now", now));
        return true;
    }

    public bool DeleteWaveformPreset(string id)
    {
        // 不允许删除内置预设
        var preset = GetWaveformPreset(id);
        if (preset?.IsBuiltIn == true)
        {
            _logger.Warning("Cannot delete built-in waveform preset: {Id}", id);
            return false;
        }
        
        ExecuteNonQuery("DELETE FROM waveform_presets WHERE id = @id", ("@id", id));
        return true;
    }

    private static WaveformPresetRecord MapWaveformPresetRecord(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        Name = reader.GetString(reader.GetOrdinal("name")),
        Description = reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
        Icon = reader.IsDBNull(reader.GetOrdinal("icon")) ? "🌊" : reader.GetString(reader.GetOrdinal("icon")),
        Channel = reader.GetString(reader.GetOrdinal("channel")),
        WaveformData = reader.GetString(reader.GetOrdinal("waveformData")),
        Duration = reader.GetInt32(reader.GetOrdinal("duration")),
        Intensity = reader.GetInt32(reader.GetOrdinal("intensity")),
        IsBuiltIn = reader.GetInt32(reader.GetOrdinal("isBuiltIn")) == 1,
        SortOrder = reader.GetInt32(reader.GetOrdinal("sortOrder")),
        CreatedAt = reader.GetString(reader.GetOrdinal("createdAt")),
        UpdatedAt = reader.GetString(reader.GetOrdinal("updatedAt"))
    };

    #endregion

    #region Sensor Rule Operations

    public List<SensorRuleRecord> GetAllSensorRules()
    {
        return ExecuteQuery("SELECT * FROM sensor_rules ORDER BY name", MapSensorRuleRecord);
    }

    public List<SensorRuleRecord> GetEnabledSensorRules()
    {
        return ExecuteQuery("SELECT * FROM sensor_rules WHERE enabled = 1 ORDER BY name", MapSensorRuleRecord);
    }

    public List<SensorRuleRecord> GetSensorRulesByDevice(string deviceId)
    {
        return ExecuteQuery("SELECT * FROM sensor_rules WHERE deviceId = @deviceId OR deviceId IS NULL ORDER BY name", 
            MapSensorRuleRecord, ("@deviceId", deviceId));
    }

    public List<SensorRuleRecord> GetSensorRulesBySensorType(string sensorType)
    {
        return ExecuteQuery("SELECT * FROM sensor_rules WHERE sensorType = @sensorType ORDER BY name", 
            MapSensorRuleRecord, ("@sensorType", sensorType));
    }

    public SensorRuleRecord? GetSensorRule(string id)
    {
        return ExecuteQuerySingle("SELECT * FROM sensor_rules WHERE id = @id", MapSensorRuleRecord, ("@id", id));
    }

    public void AddSensorRule(SensorRuleRecord rule)
    {
        var now = DateTime.UtcNow.ToString("o");
        var id = string.IsNullOrEmpty(rule.Id) ? $"sr_{Guid.NewGuid():N}"[..20] : rule.Id;
        ExecuteNonQuery(@"
            INSERT INTO sensor_rules (id, name, deviceId, sensorType, triggerType, threshold, targetDeviceId, targetChannel, action, value, duration, cooldownMs, enabled, createdAt, updatedAt)
            VALUES (@id, @name, @deviceId, @sensorType, @triggerType, @threshold, @targetDeviceId, @targetChannel, @action, @value, @duration, @cooldownMs, @enabled, @now, @now)
        ",
        ("@id", id),
        ("@name", rule.Name),
        ("@deviceId", rule.DeviceId),
        ("@sensorType", rule.SensorType),
        ("@triggerType", rule.TriggerType),
        ("@threshold", rule.Threshold),
        ("@targetDeviceId", rule.TargetDeviceId),
        ("@targetChannel", rule.TargetChannel),
        ("@action", rule.Action),
        ("@value", rule.Value),
        ("@duration", rule.Duration),
        ("@cooldownMs", rule.CooldownMs),
        ("@enabled", rule.Enabled ? 1 : 0),
        ("@now", now));
    }

    public bool UpdateSensorRule(string id, SensorRuleRecord updates)
    {
        var now = DateTime.UtcNow.ToString("o");
        ExecuteNonQuery(@"
            UPDATE sensor_rules SET name = @name, deviceId = @deviceId, sensorType = @sensorType, triggerType = @triggerType,
            threshold = @threshold, targetDeviceId = @targetDeviceId, targetChannel = @targetChannel, action = @action,
            value = @value, duration = @duration, cooldownMs = @cooldownMs, enabled = @enabled, updatedAt = @now
            WHERE id = @id
        ",
        ("@id", id),
        ("@name", updates.Name),
        ("@deviceId", updates.DeviceId),
        ("@sensorType", updates.SensorType),
        ("@triggerType", updates.TriggerType),
        ("@threshold", updates.Threshold),
        ("@targetDeviceId", updates.TargetDeviceId),
        ("@targetChannel", updates.TargetChannel),
        ("@action", updates.Action),
        ("@value", updates.Value),
        ("@duration", updates.Duration),
        ("@cooldownMs", updates.CooldownMs),
        ("@enabled", updates.Enabled ? 1 : 0),
        ("@now", now));
        return true;
    }

    public bool DeleteSensorRule(string id)
    {
        ExecuteNonQuery("DELETE FROM sensor_rules WHERE id = @id", ("@id", id));
        return true;
    }

    public void ToggleSensorRule(string id, bool enabled)
    {
        var now = DateTime.UtcNow.ToString("o");
        ExecuteNonQuery("UPDATE sensor_rules SET enabled = @enabled, updatedAt = @now WHERE id = @id",
            ("@id", id), ("@enabled", enabled ? 1 : 0), ("@now", now));
    }

    private static SensorRuleRecord MapSensorRuleRecord(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        Name = reader.GetString(reader.GetOrdinal("name")),
        DeviceId = reader.IsDBNull(reader.GetOrdinal("deviceId")) ? null : reader.GetString(reader.GetOrdinal("deviceId")),
        SensorType = reader.GetString(reader.GetOrdinal("sensorType")),
        TriggerType = reader.GetString(reader.GetOrdinal("triggerType")),
        Threshold = reader.GetDouble(reader.GetOrdinal("threshold")),
        TargetDeviceId = reader.IsDBNull(reader.GetOrdinal("targetDeviceId")) ? null : reader.GetString(reader.GetOrdinal("targetDeviceId")),
        TargetChannel = reader.GetString(reader.GetOrdinal("targetChannel")),
        Action = reader.GetString(reader.GetOrdinal("action")),
        Value = reader.GetInt32(reader.GetOrdinal("value")),
        Duration = reader.GetInt32(reader.GetOrdinal("duration")),
        CooldownMs = reader.GetInt32(reader.GetOrdinal("cooldownMs")),
        Enabled = reader.GetInt32(reader.GetOrdinal("enabled")) == 1,
        CreatedAt = reader.GetString(reader.GetOrdinal("createdAt")),
        UpdatedAt = reader.GetString(reader.GetOrdinal("updatedAt"))
    };

    #endregion

    public void Dispose()
    {
        // 停止定时器
        _logFlushTimer?.Dispose();
        
        // 刷新剩余日志
        FlushLogBuffer();
        
        // 清理预编译语句
        foreach (var cmd in _preparedCommands.Values)
        {
            cmd?.Dispose();
        }
        _preparedCommands.Clear();
        
        // 优化数据库
        try
        {
            ExecuteNonQuery("PRAGMA optimize;");
        }
        catch { }
        
        _connection?.Dispose();
    }
}

#region Records

public class DeviceRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = ""; // "dglab" | "yokonex"
    public string? Config { get; set; }
    public bool AutoConnect { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public class EventRecord
{
    public string Id { get; set; } = "";
    public string EventId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Category { get; set; } = "custom"; // "system" | "game" | "custom"
    public string Channel { get; set; } = "A"; // "A" | "B" | "AB"
    public string Action { get; set; } = "set"; // "set" | "increase" | "decrease" | "wave" | "pulse"
    public int Value { get; set; }
    public int Duration { get; set; }
    public string? WaveformData { get; set; }
    public bool Enabled { get; set; } = true;
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    
    // 用于 UI 绑定的额外属性
    public string TriggerType { get; set; } = "hp-decrease";
    public int MinChange { get; set; } = 10;  // 指定变化量
    public int MaxChange { get; set; } = 10;  // 保持兼容
    public string ActionType { get; set; } = "set";
    public int Strength { get; set; } = 50;
    public int Priority { get; set; } = 10;
    public string TargetDeviceType { get; set; } = "All"; // DGLab | Yokonex_Estim | Yokonex_Enema | Yokonex_Vibrator | Yokonex_Cup | All
}

public class ScriptRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Game { get; set; } = "";
    public string? Description { get; set; }
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = "Anonymous";
    public string Code { get; set; } = "";
    public string Content { get; set; } = "";  // 别名
    public bool Enabled { get; set; } = true;
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public class LogRecord
{
    public int Id { get; set; }
    public string Level { get; set; } = "";
    public string? Module { get; set; }
    public string Message { get; set; } = "";
    public string? Data { get; set; }
    public string CreatedAt { get; set; } = "";
    
    // 扩展字段用于设备动作日志
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string Action { get; set; } = "";
    public string? Source { get; set; }
}

public class WaveformPresetRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Icon { get; set; } = "🌊";
    public string Channel { get; set; } = "AB"; // "A" | "B" | "AB"
    public string WaveformData { get; set; } = ""; // HEX 格式波形数据
    public int Duration { get; set; } = 1000;
    public int Intensity { get; set; } = 50;
    public bool IsBuiltIn { get; set; }
    public int SortOrder { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

/// <summary>
/// 传感器规则记录
/// </summary>
public class SensorRuleRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? DeviceId { get; set; }  // 源设备ID (null=所有设备)
    public string SensorType { get; set; } = "step";  // step, angle, channel
    public string TriggerType { get; set; } = "threshold";  // threshold, change, connect, disconnect
    public double Threshold { get; set; }  // 触发阈值
    public string? TargetDeviceId { get; set; }  // 目标设备ID (null=所有设备)
    public string TargetChannel { get; set; } = "A";  // A, B, AB
    public string Action { get; set; } = "increase";  // set, increase, decrease, pulse, wave
    public int Value { get; set; } = 10;  // 强度值
    public int Duration { get; set; } = 500;  // 持续时间 (ms)
    public int CooldownMs { get; set; } = 1000;  // 冷却时间 (ms)
    public bool Enabled { get; set; } = true;
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

#endregion
