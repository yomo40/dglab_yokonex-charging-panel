using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.DGLab;
using ChargingPanel.Core.Events;
using Serilog;

namespace ChargingPanel.Core.Services;

/// <summary>
/// 传感器规则服务
/// 管理役次元传感器触发规则的持久化和执行
/// </summary>
public class SensorRuleService : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<SensorRuleService>();
    private static SensorRuleService? _instance;
    public static SensorRuleService? Instance => _instance;

    private readonly DeviceManager _deviceManager;
    private readonly EventService _eventService;
    private readonly YokonexSensorService? _sensorService;
    
    private readonly ConcurrentDictionary<string, SensorRule> _rules = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastTriggered = new();
    private readonly string _rulesFilePath;
    private bool _isRunning;

    /// <summary>规则触发事件</summary>
    public event EventHandler<SensorRuleTriggeredEventArgs>? RuleTriggered;

    public SensorRuleService(DeviceManager deviceManager, EventService eventService, YokonexSensorService? sensorService = null)
    {
        _deviceManager = deviceManager;
        _eventService = eventService;
        _sensorService = sensorService;
        
        // 规则文件路径
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChargingPanel");
        Directory.CreateDirectory(appDataPath);
        _rulesFilePath = Path.Combine(appDataPath, "sensor_rules.json");
        
        _instance = this;
    }

    /// <summary>
    /// 启动服务
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;
        
        // 从数据库和文件加载规则
        LoadRulesFromDatabase();
        LoadRulesFromFile();
        
        // 订阅传感器事件
        if (_sensorService != null)
        {
            _sensorService.StepCountChanged += OnStepCountChanged;
            _sensorService.AngleChanged += OnAngleChanged;
        }
        
        // 订阅事件总线
        EventBus.Instance.Subscribe<GameEvent>(OnGameEvent);
        
        _isRunning = true;
        Logger.Information("SensorRuleService started, {Count} rules loaded", _rules.Count);
    }

    /// <summary>
    /// 停止服务
    /// </summary>
    public void Stop()
    {
        if (!_isRunning) return;
        
        if (_sensorService != null)
        {
            _sensorService.StepCountChanged -= OnStepCountChanged;
            _sensorService.AngleChanged -= OnAngleChanged;
        }
        
        _isRunning = false;
        Logger.Information("SensorRuleService stopped");
    }

    #region 规则管理

    /// <summary>
    /// 添加规则
    /// </summary>
    public string AddRule(SensorRule rule)
    {
        if (string.IsNullOrEmpty(rule.Id))
        {
            rule.Id = $"sr_{Guid.NewGuid():N}"[..20];
        }
        
        _rules[rule.Id] = rule;
        
        // 保存到数据库
        SaveRuleToDatabase(rule);
        
        // 保存到文件
        SaveRulesToFile();
        
        Logger.Information("Sensor rule added: {Name} ({Id})", rule.Name, rule.Id);
        return rule.Id;
    }

    /// <summary>
    /// 更新规则
    /// </summary>
    public bool UpdateRule(string id, SensorRule updates)
    {
        if (!_rules.ContainsKey(id)) return false;
        
        updates.Id = id;
        _rules[id] = updates;
        
        // 更新数据库
        var record = RuleToRecord(updates);
        Database.Instance.UpdateSensorRule(id, record);
        
        // 保存到文件
        SaveRulesToFile();
        
        Logger.Information("Sensor rule updated: {Name} ({Id})", updates.Name, id);
        return true;
    }

    /// <summary>
    /// 删除规则
    /// </summary>
    public bool DeleteRule(string id)
    {
        if (!_rules.TryRemove(id, out var rule)) return false;
        
        // 从数据库删除
        Database.Instance.DeleteSensorRule(id);
        
        // 保存到文件
        SaveRulesToFile();
        
        Logger.Information("Sensor rule deleted: {Name} ({Id})", rule.Name, id);
        return true;
    }

    /// <summary>
    /// 切换规则启用状态
    /// </summary>
    public void ToggleRule(string id, bool enabled)
    {
        if (_rules.TryGetValue(id, out var rule))
        {
            rule.Enabled = enabled;
            Database.Instance.ToggleSensorRule(id, enabled);
            SaveRulesToFile();
        }
    }

    /// <summary>
    /// 获取所有规则
    /// </summary>
    public List<SensorRule> GetAllRules()
    {
        return _rules.Values.ToList();
    }

    /// <summary>
    /// 获取启用的规则
    /// </summary>
    public List<SensorRule> GetEnabledRules()
    {
        return _rules.Values.Where(r => r.Enabled).ToList();
    }

    /// <summary>
    /// 获取规则
    /// </summary>
    public SensorRule? GetRule(string id)
    {
        return _rules.TryGetValue(id, out var rule) ? rule : null;
    }

    #endregion

    #region 规则持久化

    private void LoadRulesFromDatabase()
    {
        try
        {
            var records = Database.Instance.GetAllSensorRules();
            foreach (var record in records)
            {
                var rule = RecordToRule(record);
                _rules[rule.Id] = rule;
            }
            Logger.Information("Loaded {Count} sensor rules from database", records.Count);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load sensor rules from database");
        }
    }

    private void LoadRulesFromFile()
    {
        try
        {
            if (!File.Exists(_rulesFilePath)) return;
            
            var json = File.ReadAllText(_rulesFilePath);
            var rules = JsonSerializer.Deserialize<List<SensorRule>>(json);
            
            if (rules != null)
            {
                foreach (var rule in rules)
                {
                    // 只加载文件中有但数据库中没有的规则
                    if (!_rules.ContainsKey(rule.Id))
                    {
                        _rules[rule.Id] = rule;
                        SaveRuleToDatabase(rule);
                    }
                }
            }
            
            Logger.Information("Loaded sensor rules from file: {Path}", _rulesFilePath);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to load sensor rules from file");
        }
    }

    private void SaveRulesToFile()
    {
        try
        {
            var json = JsonSerializer.Serialize(_rules.Values.ToList(), new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_rulesFilePath, json);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to save sensor rules to file");
        }
    }

    private void SaveRuleToDatabase(SensorRule rule)
    {
        try
        {
            var existing = Database.Instance.GetSensorRule(rule.Id);
            var record = RuleToRecord(rule);
            
            if (existing != null)
            {
                Database.Instance.UpdateSensorRule(rule.Id, record);
            }
            else
            {
                Database.Instance.AddSensorRule(record);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to save sensor rule to database");
        }
    }

    private static SensorRuleRecord RuleToRecord(SensorRule rule) => new()
    {
        Id = rule.Id,
        Name = rule.Name,
        DeviceId = rule.DeviceId,
        SensorType = rule.SensorType.ToString().ToLower(),
        TriggerType = rule.TriggerType.ToString().ToLower(),
        Threshold = rule.Threshold,
        TargetDeviceId = rule.TargetDeviceId,
        TargetChannel = rule.TargetChannel.ToString(),
        Action = rule.Action.ToString().ToLower(),
        Value = rule.Value,
        Duration = rule.Duration,
        CooldownMs = rule.CooldownMs,
        Enabled = rule.Enabled
    };

    private static SensorRule RecordToRule(SensorRuleRecord record) => new()
    {
        Id = record.Id,
        Name = record.Name,
        DeviceId = record.DeviceId,
        SensorType = Enum.TryParse<SensorType>(record.SensorType, true, out var st) ? st : SensorType.Step,
        TriggerType = Enum.TryParse<SensorTriggerType>(record.TriggerType, true, out var tt) ? tt : SensorTriggerType.Threshold,
        Threshold = record.Threshold,
        TargetDeviceId = record.TargetDeviceId,
        TargetChannel = Enum.TryParse<Channel>(record.TargetChannel, true, out var ch) ? ch : Channel.A,
        Action = Enum.TryParse<SensorAction>(record.Action, true, out var act) ? act : SensorAction.Increase,
        Value = record.Value,
        Duration = record.Duration,
        CooldownMs = record.CooldownMs,
        Enabled = record.Enabled
    };

    #endregion

    #region 事件处理

    private void OnStepCountChanged(object? sender, (string DeviceId, int StepCount) e)
    {
        ProcessSensorEvent(e.DeviceId, SensorType.Step, e.StepCount);
    }

    private void OnAngleChanged(object? sender, (string DeviceId, float X, float Y, float Z) e)
    {
        // 计算角度变化量
        var magnitude = Math.Sqrt(e.X * e.X + e.Y * e.Y + e.Z * e.Z);
        ProcessSensorEvent(e.DeviceId, SensorType.Angle, magnitude);
    }

    private void OnGameEvent(GameEvent evt)
    {
        // 处理通道连接/断开事件
        if (evt.Type == GameEventType.ChannelConnected)
        {
            ProcessChannelEvent(evt.TargetDeviceId ?? "", true);
        }
        else if (evt.Type == GameEventType.ChannelDisconnected)
        {
            ProcessChannelEvent(evt.TargetDeviceId ?? "", false);
        }
    }

    private void ProcessSensorEvent(string deviceId, SensorType sensorType, double value)
    {
        var matchingRules = _rules.Values
            .Where(r => r.Enabled && r.SensorType == sensorType)
            .Where(r => string.IsNullOrEmpty(r.DeviceId) || r.DeviceId == deviceId)
            .ToList();

        foreach (var rule in matchingRules)
        {
            // 检查阈值
            if (rule.TriggerType == SensorTriggerType.Threshold && value < rule.Threshold)
            {
                continue;
            }

            // 检查冷却时间
            var ruleKey = $"{rule.Id}_{deviceId}";
            if (_lastTriggered.TryGetValue(ruleKey, out var lastTime))
            {
                if ((DateTime.UtcNow - lastTime).TotalMilliseconds < rule.CooldownMs)
                {
                    continue;
                }
            }

            // 执行规则
            _ = ExecuteRuleAsync(rule, deviceId, value);
            _lastTriggered[ruleKey] = DateTime.UtcNow;
        }
    }

    private void ProcessChannelEvent(string deviceId, bool connected)
    {
        var triggerType = connected ? SensorTriggerType.Connect : SensorTriggerType.Disconnect;
        
        var matchingRules = _rules.Values
            .Where(r => r.Enabled && r.SensorType == SensorType.Channel && r.TriggerType == triggerType)
            .Where(r => string.IsNullOrEmpty(r.DeviceId) || r.DeviceId == deviceId)
            .ToList();

        foreach (var rule in matchingRules)
        {
            // 检查冷却时间
            var ruleKey = $"{rule.Id}_{deviceId}";
            if (_lastTriggered.TryGetValue(ruleKey, out var lastTime))
            {
                if ((DateTime.UtcNow - lastTime).TotalMilliseconds < rule.CooldownMs)
                {
                    continue;
                }
            }

            _ = ExecuteRuleAsync(rule, deviceId, connected ? 1 : 0);
            _lastTriggered[ruleKey] = DateTime.UtcNow;
        }
    }

    private async Task ExecuteRuleAsync(SensorRule rule, string sourceDeviceId, double sensorValue)
    {
        try
        {
            // 确定目标设备
            var targetDevices = string.IsNullOrEmpty(rule.TargetDeviceId)
                ? _deviceManager.GetConnectedDevices().Select(d => d.Id).ToList()
                : new List<string> { rule.TargetDeviceId };

            var mode = rule.Action switch
            {
                SensorAction.Set => StrengthMode.Set,
                SensorAction.Increase => StrengthMode.Increase,
                SensorAction.Decrease => StrengthMode.Decrease,
                _ => StrengthMode.Set
            };

            foreach (var targetId in targetDevices)
            {
                try
                {
                    var device = _deviceManager.GetDevice(targetId);
                    if (device == null) continue;

                    if (rule.Action == SensorAction.Pulse || rule.Action == SensorAction.Wave)
                    {
                        // 发送波形
                        var waveform = new WaveformData
                        {
                            Frequency = 100,
                            Strength = rule.Value,
                            Duration = rule.Duration
                        };
                        await device.SendWaveformAsync(rule.TargetChannel, waveform);
                    }
                    else
                    {
                        // 设置强度
                        await device.SetStrengthAsync(rule.TargetChannel, rule.Value, mode);
                        
                        // 如果有持续时间，延迟后恢复
                        if (rule.Duration > 0 && rule.Action == SensorAction.Set)
                        {
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(rule.Duration);
                                await device.SetStrengthAsync(rule.TargetChannel, 0, StrengthMode.Set);
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to execute rule on device {DeviceId}", targetId);
                }
            }

            // 触发事件
            RuleTriggered?.Invoke(this, new SensorRuleTriggeredEventArgs
            {
                RuleId = rule.Id,
                RuleName = rule.Name,
                SourceDeviceId = sourceDeviceId,
                SensorValue = sensorValue
            });

            Logger.Information("Sensor rule triggered: {Name}, value={Value}", rule.Name, sensorValue);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to execute sensor rule: {Name}", rule.Name);
        }
    }

    #endregion

    public void Dispose()
    {
        Stop();
        SaveRulesToFile();
    }
}

#region Data Types

/// <summary>
/// 传感器类型
/// </summary>
public enum SensorType
{
    Step,    // 计步器
    Angle,   // 角度传感器
    Channel  // 通道连接状态
}

/// <summary>
/// 触发类型
/// </summary>
public enum SensorTriggerType
{
    Threshold,   // 超过阈值触发
    Change,      // 变化触发
    Connect,     // 连接触发
    Disconnect   // 断开触发
}

/// <summary>
/// 动作类型
/// </summary>
public enum SensorAction
{
    Set,
    Increase,
    Decrease,
    Pulse,
    Wave
}

/// <summary>
/// 传感器规则
/// </summary>
public class SensorRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? DeviceId { get; set; }  // 源设备ID (null=所有设备)
    public SensorType SensorType { get; set; } = SensorType.Step;
    public SensorTriggerType TriggerType { get; set; } = SensorTriggerType.Threshold;
    public double Threshold { get; set; }  // 触发阈值
    public string? TargetDeviceId { get; set; }  // 目标设备ID (null=所有设备)
    public Channel TargetChannel { get; set; } = Channel.A;
    public SensorAction Action { get; set; } = SensorAction.Increase;
    public int Value { get; set; } = 10;  // 强度值
    public int Duration { get; set; } = 500;  // 持续时间 (ms)
    public int CooldownMs { get; set; } = 1000;  // 冷却时间 (ms)
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// 规则触发事件参数
/// </summary>
public class SensorRuleTriggeredEventArgs : EventArgs
{
    public string RuleId { get; set; } = "";
    public string RuleName { get; set; } = "";
    public string SourceDeviceId { get; set; } = "";
    public double SensorValue { get; set; }
}

#endregion
