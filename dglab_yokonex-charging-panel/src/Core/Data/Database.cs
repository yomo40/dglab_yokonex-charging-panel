using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using ChargingPanel.Core.Events;
using Microsoft.Data.Sqlite;
using Serilog;

namespace ChargingPanel.Core.Data;

/// <summary>
/// SQLite 数据库管理器
/// </summary>
public class Database : IDisposable
{
    private static readonly HashSet<string> AllowedDefaultScriptFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft_hp_output"
    };

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
        // 然后迁移旧表--添加缺失的列
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

            // 规则触发字段（触发类型/变化量）
            if (!ColumnExists("events", "triggerType"))
            {
                _logger.Information("Migrating events table: adding triggerType column");
                ExecuteNonQuery("ALTER TABLE events ADD COLUMN triggerType TEXT");
            }

            if (!ColumnExists("events", "minChange"))
            {
                _logger.Information("Migrating events table: adding minChange column");
                ExecuteNonQuery("ALTER TABLE events ADD COLUMN minChange INTEGER DEFAULT 10");
            }

            if (!ColumnExists("events", "maxChange"))
            {
                _logger.Information("Migrating events table: adding maxChange column");
                ExecuteNonQuery("ALTER TABLE events ADD COLUMN maxChange INTEGER DEFAULT 10");
            }

            // 规则条件字段（ConditionField / ConditionOperator）
            if (!ColumnExists("events", "conditionField"))
            {
                _logger.Information("Migrating events table: adding conditionField column");
                ExecuteNonQuery("ALTER TABLE events ADD COLUMN conditionField TEXT");
            }

            if (!ColumnExists("events", "conditionOperator"))
            {
                _logger.Information("Migrating events table: adding conditionOperator column");
                ExecuteNonQuery("ALTER TABLE events ADD COLUMN conditionOperator TEXT");
            }

            if (!ColumnExists("events", "conditionValue"))
            {
                _logger.Information("Migrating events table: adding conditionValue column");
                ExecuteNonQuery("ALTER TABLE events ADD COLUMN conditionValue REAL");
            }

            if (!ColumnExists("events", "conditionMaxValue"))
            {
                _logger.Information("Migrating events table: adding conditionMaxValue column");
                ExecuteNonQuery("ALTER TABLE events ADD COLUMN conditionMaxValue REAL");
            }

            MigrateEventsTableSchema();

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

            MigrateSensorRulesTable();

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
    /// 迁移 events 表：
    /// 1) 放宽旧版 action/targetDeviceType 的 CHECK 约束，避免新动作无法保存。
    /// 2) 固化 trigger/condition 字段，支持桌面端可视化规则编辑持久化。
    /// </summary>
    private void MigrateEventsTableSchema()
    {
        var tableSql = ExecuteScalar<string>(
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='events'");
        if (string.IsNullOrWhiteSpace(tableSql))
        {
            return;
        }

        var normalizedSql = tableSql.ToLowerInvariant();
        var hasLegacyActionCheck =
            normalizedSql.Contains("check(action in ('set', 'increase', 'decrease', 'wave', 'pulse', 'clear'))");
        var hasLegacyTargetCheck = normalizedSql.Contains("targetdevicetype text check(");
        var hasTriggerColumns = normalizedSql.Contains("triggertype");
        var hasConditionColumns = normalizedSql.Contains("conditionfield") &&
                                  normalizedSql.Contains("conditionoperator") &&
                                  normalizedSql.Contains("conditionvalue") &&
                                  normalizedSql.Contains("conditionmaxvalue");

        if (!hasLegacyActionCheck && !hasLegacyTargetCheck && hasTriggerColumns && hasConditionColumns)
        {
            return;
        }

        _logger.Information("Migrating events table: rebuilding schema for extended rule/action support");

        using var transaction = _connection.BeginTransaction();

        ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS events_new (
                id TEXT PRIMARY KEY,
                eventId TEXT UNIQUE NOT NULL,
                name TEXT NOT NULL,
                description TEXT,
                category TEXT NOT NULL CHECK(category IN ('system', 'game', 'custom', 'mod')),
                channel TEXT NOT NULL CHECK(channel IN ('A', 'B', 'AB')),
                action TEXT NOT NULL,
                value INTEGER DEFAULT 0,
                duration INTEGER DEFAULT 0,
                waveformData TEXT,
                priority INTEGER DEFAULT 10,
                targetDeviceType TEXT,
                triggerType TEXT DEFAULT 'hp-decrease',
                minChange INTEGER DEFAULT 10,
                maxChange INTEGER DEFAULT 10,
                conditionField TEXT,
                conditionOperator TEXT,
                conditionValue REAL,
                conditionMaxValue REAL,
                enabled INTEGER DEFAULT 1,
                createdAt TEXT NOT NULL,
                updatedAt TEXT NOT NULL
            )
        ");

        ExecuteNonQuery(@"
            INSERT INTO events_new (
                id, eventId, name, description, category, channel, action, value, duration,
                waveformData, priority, targetDeviceType, triggerType, minChange, maxChange,
                conditionField, conditionOperator, conditionValue, conditionMaxValue,
                enabled, createdAt, updatedAt
            )
            SELECT
                id,
                eventId,
                name,
                description,
                category,
                channel,
                action,
                value,
                duration,
                waveformData,
                COALESCE(priority, 10),
                targetDeviceType,
                COALESCE(triggerType, 'hp-decrease'),
                COALESCE(minChange, 10),
                COALESCE(maxChange, 10),
                conditionField,
                conditionOperator,
                conditionValue,
                conditionMaxValue,
                enabled,
                createdAt,
                updatedAt
            FROM events
        ");

        ExecuteNonQuery("DROP TABLE events");
        ExecuteNonQuery("ALTER TABLE events_new RENAME TO events");
        transaction.Commit();
    }

    /// <summary>
    /// 迁移 sensor_rules 表约束，扩展 sensorType 到 pressure/external_voltage。
    /// SQLite 不支持直接修改 CHECK，需重建表。
    /// </summary>
    private void MigrateSensorRulesTable()
    {
        var tableSql = ExecuteScalar<string>(
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='sensor_rules'");
        if (string.IsNullOrWhiteSpace(tableSql))
        {
            return;
        }

        var normalizedSql = tableSql.ToLowerInvariant();
        if (normalizedSql.Contains("'pressure'") && normalizedSql.Contains("'external_voltage'"))
        {
            return;
        }

        _logger.Information("Migrating sensor_rules table: extending sensorType values");

        using var transaction = _connection.BeginTransaction();
        ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS sensor_rules_new (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                deviceId TEXT,
                sensorType TEXT NOT NULL CHECK(sensorType IN ('step', 'angle', 'channel', 'pressure', 'external_voltage')),
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
        ExecuteNonQuery(@"
            INSERT INTO sensor_rules_new (
                id, name, deviceId, sensorType, triggerType, threshold, targetDeviceId, targetChannel, action, value, duration, cooldownMs, enabled, createdAt, updatedAt
            )
            SELECT
                id, name, deviceId, sensorType, triggerType, threshold, targetDeviceId, targetChannel, action, value, duration, cooldownMs, enabled, createdAt, updatedAt
            FROM sensor_rules
        ");
        ExecuteNonQuery("DROP TABLE sensor_rules");
        ExecuteNonQuery("ALTER TABLE sensor_rules_new RENAME TO sensor_rules");
        transaction.Commit();
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
    /// 创建基础表结构--不包含可能依赖新列的索引
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
                category TEXT NOT NULL CHECK(category IN ('system', 'game', 'custom', 'mod')),
                channel TEXT NOT NULL CHECK(channel IN ('A', 'B', 'AB')),
                action TEXT NOT NULL,
                value INTEGER DEFAULT 0,
                duration INTEGER DEFAULT 0,
                waveformData TEXT,
                priority INTEGER DEFAULT 10,
                targetDeviceType TEXT,
                triggerType TEXT DEFAULT 'hp-decrease',
                minChange INTEGER DEFAULT 10,
                maxChange INTEGER DEFAULT 10,
                conditionField TEXT,
                conditionOperator TEXT,
                conditionValue REAL,
                conditionMaxValue REAL,
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
                sensorType TEXT NOT NULL CHECK(sensorType IN ('step', 'angle', 'channel', 'pressure', 'external_voltage')),
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

        var defaultEvents = SystemEventDefinitions.Defaults;

        using (var transaction = _connection.BeginTransaction())
        {
            try
            {
                foreach (var rule in defaultEvents)
                {
                    ExecuteNonQuery(@"
                        INSERT INTO events (id, eventId, name, description, category, channel, action, value, duration, enabled, createdAt, updatedAt)
                        VALUES (@id, @eventId, @name, @description, @category, @channel, @action, @value, @duration, 1, @now, @now)
                        ON CONFLICT(eventId) DO NOTHING
                    ",
                    ("@id", $"evt_{rule.EventId}"),
                    ("@eventId", rule.EventId),
                    ("@name", rule.Name),
                    ("@description", rule.Description),
                    ("@category", "system"),
                    ("@channel", rule.Channel),
                    ("@action", rule.Action),
                    ("@value", rule.Value),
                    ("@duration", rule.Duration),
                    ("@now", now));
                }
                transaction.Commit();
                _logger.Information("Ensured {Count} default system events", defaultEvents.Count);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        CleanupLegacyNonRuleTriggerEvents();
        CleanupDeprecatedSystemDefaultEvents();

        // 检查是否已有设置数据
        var settingCount = ExecuteScalar<long>("SELECT COUNT(*) FROM settings");
        if (settingCount == 0)
        {
            var defaultSettings = new[]
            {
                ("server.port", "3000", "server"),
                ("server.host", "\"0.0.0.0\"", "server"),
                ("server.coyote.enabled", "true", "server"),
                ("server.coyote.port", "9000", "server"),
                ("safety.autoStop", "true", "safety"),
                ("safety.defaultLimit", "100", "safety"),
                ("safety.maxStrength", "100", "safety"),
                ("ocr.enabled", "false", "ocr"),
                ("ocr.interval", "100", "ocr"),
                ("modbridge.enabled", "true", "modbridge"),
                ("modbridge.ws.port", "39001", "modbridge"),
                ("modbridge.http.port", "39002", "modbridge")
            };

            foreach (var (key, value, category) in defaultSettings)
            {
                ExecuteNonQuery(@"
                    INSERT INTO settings (key, value, category, updatedAt) VALUES (@key, @value, @category, @now)
                ", ("@key", key), ("@value", value), ("@category", category), ("@now", now));
            }
            _logger.Information("Initialized default settings");
        }

        EnsureDefaultSetting("server.coyote.enabled", "true", "server");
        EnsureDefaultSetting("server.coyote.port", "9000", "server");
        EnsureDefaultSetting("modbridge.enabled", "true", "modbridge");
        EnsureDefaultSetting("modbridge.ws.port", "39001", "modbridge");
        EnsureDefaultSetting("modbridge.http.port", "39002", "modbridge");
        EnsureDefaultSetting("safety.maxStrength", "100", "safety");
        NormalizeSafetyMaxStrengthSetting();

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

    private void CleanupLegacyNonRuleTriggerEvents()
    {
        var nonRuleEventIds = EventTriggerPolicy.NonRuleTriggerEventIds.ToArray();
        if (nonRuleEventIds.Length == 0)
        {
            return;
        }

        var placeholders = string.Join(", ", nonRuleEventIds.Select((_, index) => $"@eventId{index}"));
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM events WHERE category = 'system' AND eventId IN ({placeholders})";

        for (var i = 0; i < nonRuleEventIds.Length; i++)
        {
            cmd.Parameters.AddWithValue($"@eventId{i}", nonRuleEventIds[i]);
        }

        var deleted = cmd.ExecuteNonQuery();
        if (deleted > 0)
        {
            _logger.Information("Removed {Count} non-rule trigger events from event table", deleted);
        }
    }

    private void CleanupDeprecatedSystemDefaultEvents()
    {
        var deprecatedEventIds = SystemEventDefinitions.DeprecatedDefaultEventIds.ToArray();
        if (deprecatedEventIds.Length == 0)
        {
            return;
        }

        var placeholders = string.Join(", ", deprecatedEventIds.Select((_, index) => $"@eventId{index}"));
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM events WHERE category = 'system' AND eventId IN ({placeholders})";

        for (var i = 0; i < deprecatedEventIds.Length; i++)
        {
            cmd.Parameters.AddWithValue($"@eventId{i}", deprecatedEventIds[i]);
        }

        var deleted = cmd.ExecuteNonQuery();
        if (deleted > 0)
        {
            _logger.Information("Removed {Count} deprecated default system events from event table", deleted);
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

    private void EnsureDefaultSetting(string key, string value, string category)
    {
        var now = DateTime.UtcNow.ToString("o");
        ExecuteNonQuery(@"
            INSERT INTO settings (key, value, category, updatedAt) VALUES (@key, @value, @category, @now)
            ON CONFLICT(key) DO NOTHING
        ",
        ("@key", key),
        ("@value", value),
        ("@category", category),
        ("@now", now));
    }

    private void NormalizeSafetyMaxStrengthSetting()
    {
        var current = GetSetting<int>("safety.maxStrength", 100);
        var normalized = Math.Clamp(current, 1, 100);
        if (normalized == current)
        {
            return;
        }

        SetSetting("safety.maxStrength", normalized, "safety");
        _logger.Information("Normalized safety.maxStrength from {Old} to {New}", current, normalized);
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
            INSERT INTO events (
                id, eventId, name, description, category, channel, action, value, duration,
                waveformData, priority, targetDeviceType, triggerType, minChange, maxChange,
                conditionField, conditionOperator, conditionValue, conditionMaxValue,
                enabled, createdAt, updatedAt
            )
            VALUES (
                @id, @eventId, @name, @description, @category, @channel, @action, @value, @duration,
                @waveformData, @priority, @targetDeviceType, @triggerType, @minChange, @maxChange,
                @conditionField, @conditionOperator, @conditionValue, @conditionMaxValue,
                @enabled, @now, @now
            )
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
        ("@priority", eventRecord.Priority),
        ("@targetDeviceType", eventRecord.TargetDeviceType),
        ("@triggerType", eventRecord.TriggerType),
        ("@minChange", eventRecord.MinChange),
        ("@maxChange", eventRecord.MaxChange),
        ("@conditionField", eventRecord.ConditionField),
        ("@conditionOperator", eventRecord.ConditionOperator),
        ("@conditionValue", eventRecord.ConditionValue),
        ("@conditionMaxValue", eventRecord.ConditionMaxValue),
        ("@enabled", eventRecord.Enabled ? 1 : 0),
        ("@now", now));
    }

    public bool UpdateEvent(string id, EventRecord updates)
    {
        var now = DateTime.UtcNow.ToString("o");
        ExecuteNonQuery(@"
            UPDATE events SET eventId = @eventId, name = @name, description = @description, category = @category, 
            channel = @channel, action = @action, value = @value, duration = @duration, waveformData = @waveformData,
            priority = @priority, targetDeviceType = @targetDeviceType,
            triggerType = @triggerType, minChange = @minChange, maxChange = @maxChange,
            conditionField = @conditionField, conditionOperator = @conditionOperator,
            conditionValue = @conditionValue, conditionMaxValue = @conditionMaxValue,
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
        ("@priority", updates.Priority),
        ("@targetDeviceType", updates.TargetDeviceType),
        ("@triggerType", updates.TriggerType),
        ("@minChange", updates.MinChange),
        ("@maxChange", updates.MaxChange),
        ("@conditionField", updates.ConditionField),
        ("@conditionOperator", updates.ConditionOperator),
        ("@conditionValue", updates.ConditionValue),
        ("@conditionMaxValue", updates.ConditionMaxValue),
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
        var targetDeviceTypeOrdinal = GetOrdinalSafe("targetDeviceType");
        var triggerTypeOrdinal = GetOrdinalSafe("triggerType");
        var minChangeOrdinal = GetOrdinalSafe("minChange");
        var maxChangeOrdinal = GetOrdinalSafe("maxChange");
        var conditionFieldOrdinal = GetOrdinalSafe("conditionField");
        var conditionOperatorOrdinal = GetOrdinalSafe("conditionOperator");
        var conditionValueOrdinal = GetOrdinalSafe("conditionValue");
        var conditionMaxValueOrdinal = GetOrdinalSafe("conditionMaxValue");
        
        var eventId = reader.GetString(reader.GetOrdinal("eventId"));
        var triggerProfile = SystemEventDefinitions.ResolveTriggerProfile(eventId);
        var persistedTriggerType = triggerTypeOrdinal >= 0 && !reader.IsDBNull(triggerTypeOrdinal)
            ? reader.GetString(triggerTypeOrdinal)
            : null;
        var persistedMinChange = minChangeOrdinal >= 0 && !reader.IsDBNull(minChangeOrdinal)
            ? reader.GetInt32(minChangeOrdinal)
            : (int?)null;
        var persistedMaxChange = maxChangeOrdinal >= 0 && !reader.IsDBNull(maxChangeOrdinal)
            ? reader.GetInt32(maxChangeOrdinal)
            : (int?)null;

        return new EventRecord
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            EventId = eventId,
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
            TargetDeviceType = targetDeviceTypeOrdinal >= 0 && !reader.IsDBNull(targetDeviceTypeOrdinal)
                ? reader.GetString(targetDeviceTypeOrdinal)
                : "All",
            // 从 Value 映射到 Strength
            Strength = reader.GetInt32(reader.GetOrdinal("value")),
            // 优先使用持久化规则条件；缺省时回落到系统事件默认触发策略
            TriggerType = string.IsNullOrWhiteSpace(persistedTriggerType)
                ? triggerProfile.TriggerType
                : persistedTriggerType,
            MinChange = persistedMinChange ?? triggerProfile.MinChange,
            MaxChange = persistedMaxChange ?? triggerProfile.MaxChange,
            ConditionField = conditionFieldOrdinal >= 0 && !reader.IsDBNull(conditionFieldOrdinal)
                ? reader.GetString(conditionFieldOrdinal)
                : null,
            ConditionOperator = conditionOperatorOrdinal >= 0 && !reader.IsDBNull(conditionOperatorOrdinal)
                ? reader.GetString(conditionOperatorOrdinal)
                : null,
            ConditionValue = conditionValueOrdinal >= 0 && !reader.IsDBNull(conditionValueOrdinal)
                ? reader.GetDouble(conditionValueOrdinal)
                : null,
            ConditionMaxValue = conditionMaxValueOrdinal >= 0 && !reader.IsDBNull(conditionMaxValueOrdinal)
                ? reader.GetDouble(conditionMaxValueOrdinal)
                : null,
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
        var id = string.IsNullOrWhiteSpace(script.Id)
            ? $"scr_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}".Substring(0, 30)
            : script.Id.Trim();
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

        var scriptFiles = Directory.GetFiles(scriptsPath, "*.js")
            .Where(path => AllowedDefaultScriptFileNames.Contains(Path.GetFileNameWithoutExtension(path)))
            .ToArray();

        CleanupDeprecatedDefaultScripts(
            AllowedDefaultScriptFileNames.Select(fileName => $"default_{fileName}"));

        foreach (var filePath in scriptFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var scriptId = $"default_{fileName}";
            try
            {
                var code = File.ReadAllText(filePath);
                var game = ExtractGameFromScript(code) ?? "通用";
                NormalizeAllowedDefaultScript(fileName, code, game);

                // 检查标准 default_ 脚本是否已存在
                var existing = GetScript(scriptId);
                if (existing != null)
                {
                    _logger.Debug("Default script already exists: {Name}", fileName);
                    continue;
                }

                var script = new ScriptRecord
                {
                    Id = scriptId,
                    Name = fileName.Replace("_", " "),
                    Game = game,
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

    private void NormalizeAllowedDefaultScript(string fileName, string code, string game)
    {
        var canonicalId = $"default_{fileName}";
        var canonicalName = fileName.Replace("_", " ");
        var now = DateTime.UtcNow.ToString("o");

        var matches = ExecuteQuery(@"
            SELECT id, createdAt
            FROM scripts
            WHERE LOWER(name) = LOWER(@name)
              AND code = @code
            ORDER BY createdAt ASC
        ",
        r => (Id: r.GetString(0), CreatedAt: r.IsDBNull(1) ? string.Empty : r.GetString(1)),
        ("@name", canonicalName),
        ("@code", code));

        if (matches.Count == 0)
        {
            return;
        }

        var keepId = canonicalId;
        var canonicalExists = matches.Any(m => string.Equals(m.Id, canonicalId, StringComparison.OrdinalIgnoreCase));
        if (!canonicalExists)
        {
            var seed = matches[0].Id;
            // 把首个匹配脚本归一为标准 default_ ID，避免每次启动继续新增重复项
            ExecuteNonQuery(@"
                UPDATE scripts
                SET id = @newId,
                    name = @name,
                    game = @game,
                    author = 'System',
                    updatedAt = @now
                WHERE id = @oldId
                  AND NOT EXISTS (SELECT 1 FROM scripts WHERE id = @newId)
            ",
            ("@newId", canonicalId),
            ("@oldId", seed),
            ("@name", canonicalName),
            ("@game", game),
            ("@now", now));

            keepId = GetScript(canonicalId) != null ? canonicalId : seed;
        }

        var staleIds = matches
            .Select(m => m.Id)
            .Where(id => !string.Equals(id, keepId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (staleIds.Length == 0)
        {
            return;
        }

        var placeholders = string.Join(", ", staleIds.Select((_, index) => $"@dup{index}"));
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM scripts WHERE id IN ({placeholders})";
        for (var i = 0; i < staleIds.Length; i++)
        {
            cmd.Parameters.AddWithValue($"@dup{i}", staleIds[i]);
        }

        var deleted = cmd.ExecuteNonQuery();
        if (deleted > 0)
        {
            _logger.Information("Removed {Count} duplicate default scripts for {Script}", deleted, fileName);
        }
    }

    private void CleanupDeprecatedDefaultScripts(IEnumerable<string> allowedDefaultScriptIds)
    {
        var allowed = new HashSet<string>(allowedDefaultScriptIds, StringComparer.OrdinalIgnoreCase);
        var currentDefaultIds = ExecuteQuery(
            "SELECT id FROM scripts WHERE id LIKE 'default_%'",
            r => r.GetString(0));

        var deprecated = currentDefaultIds
            .Where(id => !allowed.Contains(id))
            .ToArray();

        if (deprecated.Length == 0)
        {
            return;
        }

        var placeholders = string.Join(", ", deprecated.Select((_, index) => $"@id{index}"));
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM scripts WHERE id IN ({placeholders})";
        for (var i = 0; i < deprecated.Length; i++)
        {
            cmd.Parameters.AddWithValue($"@id{i}", deprecated[i]);
        }

        var deleted = cmd.ExecuteNonQuery();
        if (deleted > 0)
        {
            _logger.Information("Removed {Count} deprecated default scripts", deleted);
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
            if (typeof(T) == typeof(string))
                return (T)(object)value;
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

    public int GetLogCount(string? level = null, string? module = null)
    {
        var sql = "SELECT COUNT(*) FROM logs";
        var conditions = new List<string>();
        var parameters = new List<(string, object?)>();

        if (!string.IsNullOrWhiteSpace(level))
        {
            conditions.Add("level = @level");
            parameters.Add(("@level", level));
        }

        if (!string.IsNullOrWhiteSpace(module))
        {
            conditions.Add("module = @module");
            parameters.Add(("@module", module));
        }

        if (conditions.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", conditions);
        }

        return ExecuteScalar<int>(sql, parameters.ToArray());
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
    public int MaxChange { get; set; } = 0;   // 0 表示不限制上限
    // 自定义触发条件（支持持久化）
    public string? ConditionField { get; set; }      // oldValue/newValue/delta/absDelta/data.xxx
    public string? ConditionOperator { get; set; }   // gt/gte/lt/lte/eq/neq/between/outside
    public double? ConditionValue { get; set; }
    public double? ConditionMaxValue { get; set; }
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
    public string SensorType { get; set; } = "step";  // step, angle, channel, pressure, external_voltage
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

