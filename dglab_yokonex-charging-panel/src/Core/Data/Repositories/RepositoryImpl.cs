using System.Text.Json;
using ChargingPanel.Core.Data.Entities;
using Microsoft.Data.Sqlite;
using Serilog;

namespace ChargingPanel.Core.Data.Repositories;

/// 设备仓储实现
public class DeviceRepository : IDeviceRepository
{
    private readonly SqliteConnection _connection;
    private readonly ILogger _logger = Log.ForContext<DeviceRepository>();

    public DeviceRepository(SqliteConnection connection)
    {
        _connection = connection;
    }

    public IEnumerable<DeviceEntity> GetAll()
    {
        return ExecuteQuery("SELECT * FROM devices ORDER BY createdAt DESC", MapDevice);
    }

    public DeviceEntity? GetById(string id)
    {
        return ExecuteQuerySingle("SELECT * FROM devices WHERE id = @id", MapDevice, ("@id", id));
    }

    public void Add(DeviceEntity entity)
    {
        entity.CreatedAt = DateTime.UtcNow;
        entity.UpdatedAt = DateTime.UtcNow;
        
        ExecuteNonQuery(@"
            INSERT INTO devices (id, name, type, macAddress, config, autoConnect, lastStrengthA, lastStrengthB, lastWaveformA, lastWaveformB, createdAt, updatedAt)
            VALUES (@id, @name, @type, @macAddress, @config, @autoConnect, @lastStrengthA, @lastStrengthB, @lastWaveformA, @lastWaveformB, @createdAt, @updatedAt)
        ",
        ("@id", entity.Id),
        ("@name", entity.Name),
        ("@type", entity.Type.ToString().ToLower()),
        ("@macAddress", entity.MacAddress),
        ("@config", entity.Config),
        ("@autoConnect", entity.AutoConnect ? 1 : 0),
        ("@lastStrengthA", entity.LastStrengthA),
        ("@lastStrengthB", entity.LastStrengthB),
        ("@lastWaveformA", entity.LastWaveformA),
        ("@lastWaveformB", entity.LastWaveformB),
        ("@createdAt", entity.CreatedAt.ToString("o")),
        ("@updatedAt", entity.UpdatedAt.ToString("o")));
    }

    public void Update(DeviceEntity entity)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        
        ExecuteNonQuery(@"
            UPDATE devices SET name = @name, type = @type, macAddress = @macAddress, config = @config, 
            autoConnect = @autoConnect, lastStrengthA = @lastStrengthA, lastStrengthB = @lastStrengthB,
            lastWaveformA = @lastWaveformA, lastWaveformB = @lastWaveformB, updatedAt = @updatedAt
            WHERE id = @id
        ",
        ("@id", entity.Id),
        ("@name", entity.Name),
        ("@type", entity.Type.ToString().ToLower()),
        ("@macAddress", entity.MacAddress),
        ("@config", entity.Config),
        ("@autoConnect", entity.AutoConnect ? 1 : 0),
        ("@lastStrengthA", entity.LastStrengthA),
        ("@lastStrengthB", entity.LastStrengthB),
        ("@lastWaveformA", entity.LastWaveformA),
        ("@lastWaveformB", entity.LastWaveformB),
        ("@updatedAt", entity.UpdatedAt.ToString("o")));
    }

    public void Delete(string id)
    {
        ExecuteNonQuery("DELETE FROM devices WHERE id = @id", ("@id", id));
    }

    public void Save(DeviceEntity entity)
    {
        var existing = GetById(entity.Id);
        if (existing != null)
            Update(entity);
        else
            Add(entity);
    }

    public int Count()
    {
        return ExecuteScalar<int>("SELECT COUNT(*) FROM devices");
    }

    public IEnumerable<DeviceEntity> GetByType(DeviceType type)
    {
        return ExecuteQuery("SELECT * FROM devices WHERE type = @type ORDER BY name",
            MapDevice, ("@type", type.ToString().ToLower()));
    }

    public IEnumerable<DeviceEntity> GetAutoConnectDevices()
    {
        return ExecuteQuery("SELECT * FROM devices WHERE autoConnect = 1 ORDER BY name", MapDevice);
    }

    public DeviceEntity? GetByMacAddress(string macAddress)
    {
        return ExecuteQuerySingle("SELECT * FROM devices WHERE macAddress = @macAddress",
            MapDevice, ("@macAddress", macAddress));
    }

    private static DeviceEntity MapDevice(SqliteDataReader reader)
    {
        return new DeviceEntity
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            Type = Enum.TryParse<DeviceType>(reader.GetString(reader.GetOrdinal("type")), true, out var t) ? t : DeviceType.DGLab,
            MacAddress = reader.IsDBNull(reader.GetOrdinal("macAddress")) ? null : reader.GetString(reader.GetOrdinal("macAddress")),
            Config = reader.IsDBNull(reader.GetOrdinal("config")) ? null : reader.GetString(reader.GetOrdinal("config")),
            AutoConnect = reader.GetInt32(reader.GetOrdinal("autoConnect")) == 1,
            LastStrengthA = reader.GetInt32(reader.GetOrdinal("lastStrengthA")),
            LastStrengthB = reader.GetInt32(reader.GetOrdinal("lastStrengthB")),
            LastWaveformA = reader.IsDBNull(reader.GetOrdinal("lastWaveformA")) ? null : reader.GetString(reader.GetOrdinal("lastWaveformA")),
            LastWaveformB = reader.IsDBNull(reader.GetOrdinal("lastWaveformB")) ? null : reader.GetString(reader.GetOrdinal("lastWaveformB")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("createdAt"))),
            UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("updatedAt")))
        };
    }

    #region Helper Methods
    private void ExecuteNonQuery(string sql, params (string name, object? value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private T ExecuteScalar<T>(string sql, params (string name, object? value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? default! : (T)Convert.ChangeType(result, typeof(T));
    }

    private List<T> ExecuteQuery<T>(string sql, Func<SqliteDataReader, T> mapper, params (string name, object? value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
            results.Add(mapper(reader));
        return results;
    }

    private T? ExecuteQuerySingle<T>(string sql, Func<SqliteDataReader, T> mapper, params (string name, object? value)[] parameters) where T : class
    {
        var results = ExecuteQuery(sql, mapper, parameters);
        return results.Count > 0 ? results[0] : null;
    }
    #endregion
}

/// 事件仓储实现
public class EventRepository : IEventRepository
{
    private readonly SqliteConnection _connection;

    public EventRepository(SqliteConnection connection)
    {
        _connection = connection;
    }

    public IEnumerable<EventEntity> GetAll()
    {
        return ExecuteQuery("SELECT * FROM events ORDER BY category, eventId", MapEvent);
    }

    public EventEntity? GetById(string id)
    {
        return ExecuteQuerySingle("SELECT * FROM events WHERE id = @id", MapEvent, ("@id", id));
    }

    public EventEntity? GetByEventId(string eventId)
    {
        return ExecuteQuerySingle("SELECT * FROM events WHERE eventId = @eventId", MapEvent, ("@eventId", eventId));
    }

    public IEnumerable<EventEntity> GetByCategory(EventCategory category)
    {
        return ExecuteQuery("SELECT * FROM events WHERE category = @category ORDER BY eventId",
            MapEvent, ("@category", category.ToString().ToLower()));
    }

    public IEnumerable<EventEntity> GetEnabled()
    {
        return ExecuteQuery("SELECT * FROM events WHERE enabled = 1 ORDER BY priority DESC, eventId", MapEvent);
    }

    public IEnumerable<EventEntity> GetByDeviceType(DeviceType? deviceType)
    {
        if (deviceType == null)
            return ExecuteQuery("SELECT * FROM events WHERE targetDeviceType IS NULL ORDER BY eventId", MapEvent);
        
        return ExecuteQuery("SELECT * FROM events WHERE targetDeviceType = @type OR targetDeviceType IS NULL ORDER BY eventId",
            MapEvent, ("@type", deviceType.Value.ToString().ToLower()));
    }

    public void Add(EventEntity entity)
    {
        entity.CreatedAt = DateTime.UtcNow;
        entity.UpdatedAt = DateTime.UtcNow;
        
        ExecuteNonQuery(@"
            INSERT INTO events (id, eventId, name, description, category, targetDeviceType, channel, action, value, duration, waveformData, enabled, priority, cooldown, lastTriggeredAt, createdAt, updatedAt)
            VALUES (@id, @eventId, @name, @description, @category, @targetDeviceType, @channel, @action, @value, @duration, @waveformData, @enabled, @priority, @cooldown, @lastTriggeredAt, @createdAt, @updatedAt)
        ",
        ("@id", entity.Id),
        ("@eventId", entity.EventId),
        ("@name", entity.Name),
        ("@description", entity.Description),
        ("@category", entity.Category.ToString().ToLower()),
        ("@targetDeviceType", entity.TargetDeviceType?.ToString().ToLower()),
        ("@channel", entity.Channel.ToString()),
        ("@action", entity.Action.ToString().ToLower()),
        ("@value", entity.Value),
        ("@duration", entity.Duration),
        ("@waveformData", entity.WaveformData),
        ("@enabled", entity.Enabled ? 1 : 0),
        ("@priority", entity.Priority),
        ("@cooldown", entity.Cooldown),
        ("@lastTriggeredAt", entity.LastTriggeredAt?.ToString("o")),
        ("@createdAt", entity.CreatedAt.ToString("o")),
        ("@updatedAt", entity.UpdatedAt.ToString("o")));
    }

    public void Update(EventEntity entity)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        
        ExecuteNonQuery(@"
            UPDATE events SET eventId = @eventId, name = @name, description = @description, category = @category,
            targetDeviceType = @targetDeviceType, channel = @channel, action = @action, value = @value,
            duration = @duration, waveformData = @waveformData, enabled = @enabled, priority = @priority,
            cooldown = @cooldown, lastTriggeredAt = @lastTriggeredAt, updatedAt = @updatedAt
            WHERE id = @id
        ",
        ("@id", entity.Id),
        ("@eventId", entity.EventId),
        ("@name", entity.Name),
        ("@description", entity.Description),
        ("@category", entity.Category.ToString().ToLower()),
        ("@targetDeviceType", entity.TargetDeviceType?.ToString().ToLower()),
        ("@channel", entity.Channel.ToString()),
        ("@action", entity.Action.ToString().ToLower()),
        ("@value", entity.Value),
        ("@duration", entity.Duration),
        ("@waveformData", entity.WaveformData),
        ("@enabled", entity.Enabled ? 1 : 0),
        ("@priority", entity.Priority),
        ("@cooldown", entity.Cooldown),
        ("@lastTriggeredAt", entity.LastTriggeredAt?.ToString("o")),
        ("@updatedAt", entity.UpdatedAt.ToString("o")));
    }

    public void Delete(string id)
    {
        ExecuteNonQuery("DELETE FROM events WHERE id = @id OR eventId = @id", ("@id", id));
    }

    public void Save(EventEntity entity)
    {
        var existing = GetById(entity.Id) ?? GetByEventId(entity.EventId);
        if (existing != null)
        {
            entity.Id = existing.Id;
            Update(entity);
        }
        else
            Add(entity);
    }

    public int Count()
    {
        return ExecuteScalar<int>("SELECT COUNT(*) FROM events");
    }

    private static EventEntity MapEvent(SqliteDataReader reader)
    {
        return new EventEntity
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            EventId = reader.GetString(reader.GetOrdinal("eventId")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            Description = reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
            Category = Enum.TryParse<EventCategory>(reader.GetString(reader.GetOrdinal("category")), true, out var c) ? c : EventCategory.Custom,
            TargetDeviceType = reader.IsDBNull(reader.GetOrdinal("targetDeviceType")) ? null :
                Enum.TryParse<DeviceType>(reader.GetString(reader.GetOrdinal("targetDeviceType")), true, out var dt) ? dt : null,
            Channel = Enum.TryParse<ChannelTarget>(reader.GetString(reader.GetOrdinal("channel")), true, out var ch) ? ch : ChannelTarget.A,
            Action = Enum.TryParse<EventAction>(reader.GetString(reader.GetOrdinal("action")), true, out var a) ? a : EventAction.Set,
            Value = reader.GetInt32(reader.GetOrdinal("value")),
            Duration = reader.GetInt32(reader.GetOrdinal("duration")),
            WaveformData = reader.IsDBNull(reader.GetOrdinal("waveformData")) ? null : reader.GetString(reader.GetOrdinal("waveformData")),
            Enabled = reader.GetInt32(reader.GetOrdinal("enabled")) == 1,
            Priority = reader.GetInt32(reader.GetOrdinal("priority")),
            Cooldown = reader.GetInt32(reader.GetOrdinal("cooldown")),
            LastTriggeredAt = reader.IsDBNull(reader.GetOrdinal("lastTriggeredAt")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("lastTriggeredAt"))),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("createdAt"))),
            UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("updatedAt")))
        };
    }

    #region Helper Methods
    private void ExecuteNonQuery(string sql, params (string name, object? value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private T ExecuteScalar<T>(string sql, params (string name, object? value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? default! : (T)Convert.ChangeType(result, typeof(T));
    }

    private List<T> ExecuteQuery<T>(string sql, Func<SqliteDataReader, T> mapper, params (string name, object? value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
            results.Add(mapper(reader));
        return results;
    }

    private T? ExecuteQuerySingle<T>(string sql, Func<SqliteDataReader, T> mapper, params (string name, object? value)[] parameters) where T : class
    {
        var results = ExecuteQuery(sql, mapper, parameters);
        return results.Count > 0 ? results[0] : null;
    }
    #endregion
}

/// 日志仓储实现
public class LogRepository : ILogRepository
{
    private readonly SqliteConnection _connection;

    public LogRepository(SqliteConnection connection)
    {
        _connection = connection;
    }

    public void Add(LogEntity log)
    {
        log.CreatedAt = DateTime.UtcNow;
        
        ExecuteNonQuery(@"
            INSERT INTO logs (level, module, message, data, deviceId, deviceName, eventId, source, createdAt)
            VALUES (@level, @module, @message, @data, @deviceId, @deviceName, @eventId, @source, @createdAt)
        ",
        ("@level", log.Level.ToString().ToLower()),
        ("@module", log.Module),
        ("@message", log.Message),
        ("@data", log.Data),
        ("@deviceId", log.DeviceId),
        ("@deviceName", log.DeviceName),
        ("@eventId", log.EventId),
        ("@source", log.Source),
        ("@createdAt", log.CreatedAt.ToString("o")));
    }

    public void Log(LogLevel level, string module, string message, object? data = null)
    {
        Add(new LogEntity
        {
            Level = level,
            Module = module,
            Message = message,
            Data = data == null ? null : JsonSerializer.Serialize(data)
        });
    }

    public IEnumerable<LogEntity> GetLogs(int limit = 100, LogLevel? level = null, string? module = null, DateTime? since = null)
    {
        var sql = "SELECT * FROM logs";
        var conditions = new List<string>();
        var parameters = new List<(string, object?)>();

        if (level.HasValue)
        {
            conditions.Add("level = @level");
            parameters.Add(("@level", level.Value.ToString().ToLower()));
        }
        if (!string.IsNullOrEmpty(module))
        {
            conditions.Add("module = @module");
            parameters.Add(("@module", module));
        }
        if (since.HasValue)
        {
            conditions.Add("createdAt >= @since");
            parameters.Add(("@since", since.Value.ToString("o")));
        }

        if (conditions.Count > 0)
            sql += " WHERE " + string.Join(" AND ", conditions);

        sql += " ORDER BY createdAt DESC LIMIT @limit";
        parameters.Add(("@limit", limit));

        return ExecuteQuery(sql, MapLog, parameters.ToArray());
    }

    public IEnumerable<LogEntity> GetRecent(int limit = 100)
    {
        return ExecuteQuery("SELECT * FROM logs ORDER BY createdAt DESC LIMIT @limit", MapLog, ("@limit", limit));
    }

    public IEnumerable<LogEntity> GetDeviceLogs(string deviceId, int limit = 100)
    {
        return ExecuteQuery(
            "SELECT * FROM logs WHERE deviceId = @deviceId ORDER BY createdAt DESC LIMIT @limit",
            MapLog, ("@deviceId", deviceId), ("@limit", limit));
    }

    public void Clear(int keepDays = 0)
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

    public void Rotate(int maxRecords = 10000)
    {
        var count = ExecuteScalar<long>("SELECT COUNT(*) FROM logs");
        if (count > maxRecords)
        {
            var toDelete = count - maxRecords;
            ExecuteNonQuery(@"
                DELETE FROM logs WHERE id IN (
                    SELECT id FROM logs ORDER BY createdAt ASC LIMIT @toDelete
                )", ("@toDelete", toDelete));
        }
    }

    private static LogEntity MapLog(SqliteDataReader reader)
    {
        return new LogEntity
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            Level = Enum.TryParse<LogLevel>(reader.GetString(reader.GetOrdinal("level")), true, out var l) ? l : LogLevel.Info,
            Module = reader.IsDBNull(reader.GetOrdinal("module")) ? null : reader.GetString(reader.GetOrdinal("module")),
            Message = reader.GetString(reader.GetOrdinal("message")),
            Data = reader.IsDBNull(reader.GetOrdinal("data")) ? null : reader.GetString(reader.GetOrdinal("data")),
            DeviceId = reader.IsDBNull(reader.GetOrdinal("deviceId")) ? null : reader.GetString(reader.GetOrdinal("deviceId")),
            DeviceName = reader.IsDBNull(reader.GetOrdinal("deviceName")) ? null : reader.GetString(reader.GetOrdinal("deviceName")),
            EventId = reader.IsDBNull(reader.GetOrdinal("eventId")) ? null : reader.GetString(reader.GetOrdinal("eventId")),
            Source = reader.IsDBNull(reader.GetOrdinal("source")) ? null : reader.GetString(reader.GetOrdinal("source")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("createdAt")))
        };
    }

    #region Helper Methods
    private void ExecuteNonQuery(string sql, params (string name, object? value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private T ExecuteScalar<T>(string sql, params (string name, object? value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? default! : (T)Convert.ChangeType(result, typeof(T));
    }

    private List<T> ExecuteQuery<T>(string sql, Func<SqliteDataReader, T> mapper, params (string name, object? value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
            results.Add(mapper(reader));
        return results;
    }
    #endregion
}
/// 波形预设仓储实现
public class WaveformPresetRepository : IWaveformPresetRepository
{
    private readonly SqliteConnection _connection;
    private readonly ILogger _logger = Log.ForContext<WaveformPresetRepository>();

    public WaveformPresetRepository(SqliteConnection connection)
    {
        _connection = connection;
    }

    public IEnumerable<WaveformPresetEntity> GetAll()
    {
        return ExecuteQuery("SELECT * FROM waveform_presets ORDER BY isBuiltIn DESC, sortOrder ASC, createdAt DESC", MapPreset);
    }

    public IEnumerable<WaveformPresetEntity> GetByChannel(ChannelTarget channel)
    {
        var channelStr = channel.ToString();
        return ExecuteQuery(
            "SELECT * FROM waveform_presets WHERE channel = @channel OR channel = 'AB' ORDER BY isBuiltIn DESC, sortOrder ASC", 
            MapPreset, 
            ("@channel", channelStr));
    }

    public WaveformPresetEntity? GetById(string id)
    {
        return ExecuteQuerySingle("SELECT * FROM waveform_presets WHERE id = @id", MapPreset, ("@id", id));
    }

    public void Add(WaveformPresetEntity entity)
    {
        entity.CreatedAt = DateTime.UtcNow;
        entity.UpdatedAt = DateTime.UtcNow;
        
        ExecuteNonQuery(@"
            INSERT INTO waveform_presets (id, name, description, icon, channel, waveformData, duration, intensity, isBuiltIn, sortOrder, createdAt, updatedAt)
            VALUES (@id, @name, @description, @icon, @channel, @waveformData, @duration, @intensity, @isBuiltIn, @sortOrder, @createdAt, @updatedAt)
        ",
        ("@id", entity.Id),
        ("@name", entity.Name),
        ("@description", entity.Description),
        ("@icon", entity.Icon),
        ("@channel", entity.Channel.ToString()),
        ("@waveformData", entity.WaveformData),
        ("@duration", entity.Duration),
        ("@intensity", entity.Intensity),
        ("@isBuiltIn", entity.IsBuiltIn ? 1 : 0),
        ("@sortOrder", entity.SortOrder),
        ("@createdAt", entity.CreatedAt.ToString("o")),
        ("@updatedAt", entity.UpdatedAt.ToString("o")));
        
        _logger.Debug("Added waveform preset: {Name}", entity.Name);
    }

    public void Update(WaveformPresetEntity entity)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        
        ExecuteNonQuery(@"
            UPDATE waveform_presets SET name = @name, description = @description, icon = @icon, 
            channel = @channel, waveformData = @waveformData, duration = @duration, intensity = @intensity,
            sortOrder = @sortOrder, updatedAt = @updatedAt
            WHERE id = @id
        ",
        ("@id", entity.Id),
        ("@name", entity.Name),
        ("@description", entity.Description),
        ("@icon", entity.Icon),
        ("@channel", entity.Channel.ToString()),
        ("@waveformData", entity.WaveformData),
        ("@duration", entity.Duration),
        ("@intensity", entity.Intensity),
        ("@sortOrder", entity.SortOrder),
        ("@updatedAt", entity.UpdatedAt.ToString("o")));
        
        _logger.Debug("Updated waveform preset: {Name}", entity.Name);
    }

    public void Delete(string id)
    {
        // 不允许删除内置预设
        var preset = GetById(id);
        if (preset?.IsBuiltIn == true)
        {
            _logger.Warning("Cannot delete built-in waveform preset: {Id}", id);
            return;
        }
        
        ExecuteNonQuery("DELETE FROM waveform_presets WHERE id = @id", ("@id", id));
        _logger.Debug("Deleted waveform preset: {Id}", id);
    }

    public void Save(WaveformPresetEntity entity)
    {
        var existing = GetById(entity.Id);
        if (existing != null)
        {
            Update(entity);
        }
        else
        {
            Add(entity);
        }
    }

    public int Count()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM waveform_presets";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static WaveformPresetEntity MapPreset(SqliteDataReader reader)
    {
        return new WaveformPresetEntity
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            Description = reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
            Icon = reader.IsDBNull(reader.GetOrdinal("icon")) ? "🌊" : reader.GetString(reader.GetOrdinal("icon")),
            Channel = Enum.TryParse<ChannelTarget>(reader.GetString(reader.GetOrdinal("channel")), true, out var c) ? c : ChannelTarget.AB,
            WaveformData = reader.GetString(reader.GetOrdinal("waveformData")),
            Duration = reader.GetInt32(reader.GetOrdinal("duration")),
            Intensity = reader.GetInt32(reader.GetOrdinal("intensity")),
            IsBuiltIn = reader.GetInt32(reader.GetOrdinal("isBuiltIn")) == 1,
            SortOrder = reader.GetInt32(reader.GetOrdinal("sortOrder")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("createdAt"))),
            UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("updatedAt")))
        };
    }

    #region Helper Methods
    private void ExecuteNonQuery(string sql, params (string name, object? value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private List<T> ExecuteQuery<T>(string sql, Func<SqliteDataReader, T> mapper, params (string name, object? value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
            results.Add(mapper(reader));
        return results;
    }

    private T? ExecuteQuerySingle<T>(string sql, Func<SqliteDataReader, T> mapper, params (string name, object? value)[] parameters) where T : class
    {
        var results = ExecuteQuery(sql, mapper, parameters);
        return results.FirstOrDefault();
    }
    #endregion
}