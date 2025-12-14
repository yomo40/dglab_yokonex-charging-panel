using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Data.Entities;
using ChargingPanel.Core.Events;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace ChargingPanel.Core.Logging;

/// <summary>
/// 日志管理器
/// 负责日志配置、轮转、查询和导出
/// 
/// 策略：
/// - SQLite 数据库日志：可手动清理，支持保留 7/10/30 天
/// - 文件日志：永久保留，不自动清理，需手动删除
/// </summary>
public class LogManager : IDisposable
{
    private readonly string _logDirectory;
    private readonly ILogger _logger;
    private readonly ConcurrentQueue<LogEntry> _recentLogs = new();
    private readonly int _maxRecentLogs = 1000;
    private readonly List<IDisposable> _subscriptions = new();
    
    /// <summary>
    /// 可选的保留天数选项
    /// </summary>
    public static readonly int[] RetentionDaysOptions = { 7, 10, 30 };
    
    private static LogManager? _instance;
    public static LogManager Instance => _instance ?? throw new InvalidOperationException("LogManager not initialized");
    
    /// <summary>
    /// 日志目录路径
    /// </summary>
    public string LogDirectory => _logDirectory;
    
    /// <summary>
    /// 新日志事件
    /// </summary>
    public event EventHandler<LogEntry>? LogAdded;
    
    private LogManager(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(logDirectory);
        
        // 配置 Serilog
        _logger = ConfigureLogger();
        
        // 订阅事件总线中的设备控制事件
        _subscriptions.Add(
            EventBus.Instance.DeviceControlEvents.Subscribe(OnDeviceControl)
        );
        
        // 注意：不再自动轮转日志，需要手动调用 ClearDatabaseLogs
        
        Serilog.Log.Information("LogManager initialized, log directory: {LogDirectory}", logDirectory);
    }
    
    /// <summary>
    /// 初始化日志管理器
    /// </summary>
    public static void Initialize(string logDirectory)
    {
        _instance?.Dispose();
        _instance = new LogManager(logDirectory);
    }
    
    /// <summary>
    /// 配置 Serilog
    /// 文件日志永久保留，不设置 retainedFileCountLimit
    /// </summary>
    private ILogger ConfigureLogger()
    {
        var logPath = Path.Combine(_logDirectory, "app-.log");
        
        var config = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "ChargingPanel")
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            // 文件日志：永久保留，不自动清理
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: null, // 不限制文件数量，永久保留
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                shared: true)
            // JSON 格式日志：永久保留
            .WriteTo.File(
                new CompactJsonFormatter(),
                Path.Combine(_logDirectory, "app-.json"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: null) // 不限制文件数量，永久保留
            .WriteTo.Sink(new DatabaseLogSink(this));
        
        Serilog.Log.Logger = config.CreateLogger();
        return Serilog.Log.Logger;
    }
    
    /// <summary>
    /// 记录日志
    /// </summary>
    public void Log(LogLevel level, string module, string message, object? data = null, 
        string? deviceId = null, string? eventId = null)
    {
        var entry = new LogEntry
        {
            Level = level,
            Module = module,
            Message = message,
            Data = data == null ? null : JsonSerializer.Serialize(data),
            DeviceId = deviceId,
            EventId = eventId,
            Timestamp = DateTime.UtcNow
        };
        
        AddToRecentLogs(entry);
        
        // 写入数据库
        try
        {
            if (Database.Instance != null)
            {
                Database.Instance.AddLog(
                    level.ToString().ToLower(),
                    module,
                    message,
                    entry.Data);
            }
        }
        catch { }
        
        // 触发事件
        LogAdded?.Invoke(this, entry);
    }
    
    /// <summary>
    /// 记录设备操作日志
    /// </summary>
    public void LogDeviceAction(string deviceId, string deviceName, string action, 
        int? value = null, string? channel = null, string? source = null)
    {
        var message = $"Device action: {action}";
        if (value.HasValue)
            message += $", value={value}";
        if (!string.IsNullOrEmpty(channel))
            message += $", channel={channel}";
        
        Log(LogLevel.Info, "Device", message, new
        {
            deviceId,
            deviceName,
            action,
            value,
            channel,
            source
        }, deviceId);
    }
    
    /// <summary>
    /// 记录事件触发日志
    /// </summary>
    public void LogEventTrigger(string eventId, string eventName, int value, 
        string action, string[] devices)
    {
        var message = $"Event triggered: {eventName} ({eventId})";
        
        Log(LogLevel.Info, "Event", message, new
        {
            eventId,
            eventName,
            action,
            value,
            devices
        }, eventId: eventId);
    }
    
    /// <summary>
    /// 获取最近的日志
    /// </summary>
    public IEnumerable<LogEntry> GetRecentLogs(int count = 100)
    {
        return _recentLogs.TakeLast(count);
    }
    
    /// <summary>
    /// 获取日志（从数据库）
    /// </summary>
    public IEnumerable<LogEntry> GetLogs(
        int limit = 100,
        LogLevel? level = null,
        string? module = null,
        DateTime? since = null,
        string? deviceId = null)
    {
        // 简化实现：从数据库获取
        var logs = Database.Instance.GetLogs(
            limit, 
            level?.ToString().ToLower(), 
            module);
        
        return logs.Select(r => new LogEntry
        {
            Id = r.Id,
            Level = Enum.TryParse<LogLevel>(r.Level, true, out var l) ? l : LogLevel.Info,
            Module = r.Module,
            Message = r.Message,
            Data = r.Data,
            Timestamp = DateTime.TryParse(r.CreatedAt, out var ts) ? ts : DateTime.Now
        });
    }
    
    /// <summary>
    /// 导出日志到文件
    /// </summary>
    public async Task<string> ExportLogsAsync(DateTime startDate, DateTime endDate, string format = "json")
    {
        var logs = GetLogs(10000, since: startDate)
            .Where(l => l.Timestamp <= endDate)
            .ToList();
        
        var fileName = $"logs_{startDate:yyyyMMdd}_{endDate:yyyyMMdd}.{format}";
        var filePath = Path.Combine(_logDirectory, "exports", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        
        if (format == "json")
        {
            var json = JsonSerializer.Serialize(logs, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);
        }
        else // csv
        {
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp,Level,Module,Message,Data");
            foreach (var log in logs)
            {
                sb.AppendLine($"\"{log.Timestamp:O}\",\"{log.Level}\",\"{log.Module}\",\"{EscapeCsv(log.Message)}\",\"{EscapeCsv(log.Data ?? "")}\"");
            }
            await File.WriteAllTextAsync(filePath, sb.ToString());
        }
        
        return filePath;
    }
    
    /// <summary>
    /// 清理数据库日志（手动调用）
    /// 只清理 SQLite 中的日志，不影响文件日志
    /// </summary>
    /// <param name="keepDays">保留天数（7/10/30）</param>
    /// <returns>清理的日志数量</returns>
    public int ClearDatabaseLogs(int keepDays = 30)
    {
        if (!RetentionDaysOptions.Contains(keepDays))
        {
            keepDays = 30; // 默认保留30天
        }
        
        try
        {
            var countBefore = GetDatabaseLogCount();
            Database.Instance.ClearLogs(keepDays);
            var countAfter = GetDatabaseLogCount();
            var deleted = countBefore - countAfter;
            
            Serilog.Log.Information("Database logs cleaned: {Deleted} entries removed, keeping {Days} days", deleted, keepDays);
            return deleted;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Error cleaning database logs");
            return 0;
        }
    }
    
    /// <summary>
    /// 清理所有数据库日志
    /// </summary>
    /// <returns>清理的日志数量</returns>
    public int ClearAllDatabaseLogs()
    {
        try
        {
            var countBefore = GetDatabaseLogCount();
            Database.Instance.ClearLogs(0);
            _recentLogs.Clear();
            Serilog.Log.Information("All database logs cleared: {Count} entries removed", countBefore);
            return countBefore;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Error clearing all database logs");
            return 0;
        }
    }
    
    /// <summary>
    /// 获取数据库日志数量
    /// </summary>
    public int GetDatabaseLogCount()
    {
        try
        {
            return Database.Instance.GetLogs(int.MaxValue).Count;
        }
        catch
        {
            return 0;
        }
    }
    
    /// <summary>
    /// 获取数据库日志大小估算（KB）
    /// </summary>
    public long GetDatabaseLogSizeKB()
    {
        // 估算：每条日志约 200 字节
        return GetDatabaseLogCount() * 200 / 1024;
    }
    
    /// <summary>
    /// 获取文件日志信息
    /// </summary>
    public List<LogFileInfo> GetLogFiles()
    {
        var files = new List<LogFileInfo>();
        
        if (!Directory.Exists(_logDirectory))
            return files;
        
        foreach (var file in Directory.GetFiles(_logDirectory, "*.*")
            .Where(f => f.EndsWith(".log") || f.EndsWith(".json") || f.EndsWith(".txt")))
        {
            var info = new FileInfo(file);
            files.Add(new LogFileInfo
            {
                FileName = info.Name,
                FilePath = info.FullName,
                SizeKB = info.Length / 1024,
                CreatedAt = info.CreationTime,
                ModifiedAt = info.LastWriteTime
            });
        }
        
        return files.OrderByDescending(f => f.ModifiedAt).ToList();
    }
    
    /// <summary>
    /// 获取文件日志总大小（KB）
    /// </summary>
    public long GetLogFilesTotalSizeKB()
    {
        return GetLogFiles().Sum(f => f.SizeKB);
    }
    
    /// <summary>
    /// 手动删除指定的日志文件
    /// </summary>
    public bool DeleteLogFile(string fileName)
    {
        try
        {
            var filePath = Path.Combine(_logDirectory, fileName);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                Serilog.Log.Information("Log file deleted: {FileName}", fileName);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Error deleting log file: {FileName}", fileName);
            return false;
        }
    }
    
    /// <summary>
    /// 手动删除所有日志文件（谨慎使用）
    /// </summary>
    public int DeleteAllLogFiles()
    {
        var deleted = 0;
        foreach (var file in GetLogFiles())
        {
            // 跳过当天的日志文件（正在使用）
            if (file.ModifiedAt.Date == DateTime.Today)
                continue;
                
            if (DeleteLogFile(file.FileName))
                deleted++;
        }
        return deleted;
    }
    
    /// <summary>
    /// [已废弃] 轮转日志 - 仅清理数据库，不清理文件
    /// </summary>
    [Obsolete("Use ClearDatabaseLogs instead. File logs are never auto-deleted.")]
    public void RotateLogs(int keepDays = 30)
    {
        ClearDatabaseLogs(keepDays);
    }
    
    /// <summary>
    /// 清理所有日志（兼容旧接口）
    /// </summary>
    [Obsolete("Use ClearAllDatabaseLogs instead")]
    public void ClearAllLogs()
    {
        ClearAllDatabaseLogs();
    }
    
    /// <summary>
    /// 获取日志统计
    /// </summary>
    public LogStatistics GetStatistics(DateTime? since = null)
    {
        var logs = GetLogs(10000, since: since).ToList();
        var logFiles = GetLogFiles();
        
        return new LogStatistics
        {
            TotalCount = logs.Count,
            ErrorCount = logs.Count(l => l.Level >= LogLevel.Error),
            WarningCount = logs.Count(l => l.Level == LogLevel.Warning),
            InfoCount = logs.Count(l => l.Level == LogLevel.Info),
            DebugCount = logs.Count(l => l.Level == LogLevel.Debug),
            ModuleCounts = logs
                .Where(l => !string.IsNullOrEmpty(l.Module))
                .GroupBy(l => l.Module!)
                .ToDictionary(g => g.Key, g => g.Count()),
            Since = since ?? logs.LastOrDefault()?.Timestamp ?? DateTime.UtcNow,
            DatabaseLogCount = GetDatabaseLogCount(),
            DatabaseLogSizeKB = GetDatabaseLogSizeKB(),
            FileLogCount = logFiles.Count,
            FileLogTotalSizeKB = logFiles.Sum(f => f.SizeKB)
        };
    }
    
    private void AddToRecentLogs(LogEntry entry)
    {
        _recentLogs.Enqueue(entry);
        
        // 限制队列大小
        while (_recentLogs.Count > _maxRecentLogs)
        {
            _recentLogs.TryDequeue(out _);
        }
    }
    
    private void OnDeviceControl(DeviceControlEvent evt)
    {
        LogDeviceAction(
            evt.DeviceId,
            evt.DeviceName,
            evt.Action.ToString(),
            evt.Value,
            evt.Channel.ToString(),
            evt.Source);
    }
    
    private static string EscapeCsv(string value)
    {
        return value.Replace("\"", "\"\"").Replace("\n", " ").Replace("\r", "");
    }
    
    public void Dispose()
    {
        foreach (var sub in _subscriptions)
        {
            sub.Dispose();
        }
        Serilog.Log.CloseAndFlush();
    }
}

/// <summary>
/// 日志条目
/// </summary>
public class LogEntry
{
    public long Id { get; set; }
    public LogLevel Level { get; set; }
    public string? Module { get; set; }
    public string Message { get; set; } = "";
    public string? Data { get; set; }
    public string? DeviceId { get; set; }
    public string? EventId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    public string LevelIcon => Level switch
    {
        LogLevel.Debug => "🔍",
        LogLevel.Info => "ℹ️",
        LogLevel.Warning => "⚠️",
        LogLevel.Error => "❌",
        LogLevel.Fatal => "💀",
        _ => "📝"
    };
    
    public string LevelColor => Level switch
    {
        LogLevel.Debug => "#6c757d",
        LogLevel.Info => "#0dcaf0",
        LogLevel.Warning => "#ffc107",
        LogLevel.Error => "#dc3545",
        LogLevel.Fatal => "#6f42c1",
        _ => "#ffffff"
    };
}

/// <summary>
/// 日志统计
/// </summary>
public class LogStatistics
{
    public int TotalCount { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public int InfoCount { get; set; }
    public int DebugCount { get; set; }
    public Dictionary<string, int> ModuleCounts { get; set; } = new();
    public DateTime Since { get; set; }
    
    /// <summary>
    /// 数据库日志数量
    /// </summary>
    public int DatabaseLogCount { get; set; }
    
    /// <summary>
    /// 数据库日志大小估算 (KB)
    /// </summary>
    public long DatabaseLogSizeKB { get; set; }
    
    /// <summary>
    /// 文件日志数量
    /// </summary>
    public int FileLogCount { get; set; }
    
    /// <summary>
    /// 文件日志总大小 (KB)
    /// </summary>
    public long FileLogTotalSizeKB { get; set; }
}

/// <summary>
/// 日志文件信息
/// </summary>
public class LogFileInfo
{
    /// <summary>
    /// 文件名
    /// </summary>
    public string FileName { get; set; } = "";
    
    /// <summary>
    /// 完整路径
    /// </summary>
    public string FilePath { get; set; } = "";
    
    /// <summary>
    /// 文件大小 (KB)
    /// </summary>
    public long SizeKB { get; set; }
    
    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// 修改时间
    /// </summary>
    public DateTime ModifiedAt { get; set; }
    
    /// <summary>
    /// 格式化的大小显示
    /// </summary>
    public string FormattedSize => SizeKB < 1024 
        ? $"{SizeKB} KB" 
        : $"{SizeKB / 1024.0:F2} MB";
}

/// <summary>
/// Serilog 数据库接收器
/// </summary>
internal class DatabaseLogSink : ILogEventSink
{
    private readonly LogManager _logManager;
    
    public DatabaseLogSink(LogManager logManager)
    {
        _logManager = logManager;
    }
    
    public void Emit(LogEvent logEvent)
    {
        var level = logEvent.Level switch
        {
            LogEventLevel.Verbose => LogLevel.Debug,
            LogEventLevel.Debug => LogLevel.Debug,
            LogEventLevel.Information => LogLevel.Info,
            LogEventLevel.Warning => LogLevel.Warning,
            LogEventLevel.Error => LogLevel.Error,
            LogEventLevel.Fatal => LogLevel.Fatal,
            _ => LogLevel.Info
        };
        
        var module = logEvent.Properties.TryGetValue("SourceContext", out var sc)
            ? sc.ToString().Trim('"').Split('.').LastOrDefault()
            : null;
        
        var message = logEvent.RenderMessage();
        
        // 不要递归记录
        if (module != "LogManager" && module != "DatabaseLogSink")
        {
            _logManager.Log(level, module ?? "Unknown", message);
        }
    }
}
