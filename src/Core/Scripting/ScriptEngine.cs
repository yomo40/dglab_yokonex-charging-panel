using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.DGLab;
using ChargingPanel.Core.Events;
using Jint;
using Jint.Native;
using Serilog;

namespace ChargingPanel.Core.Scripting;

/// <summary>
/// 脚本执行引擎
/// 支持 JavaScript 脚本执行，用于游戏适配
/// </summary>
public class ScriptEngine : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<ScriptEngine>();
    private static ScriptEngine? _instance;
    public static ScriptEngine? Instance => _instance;

    private readonly DeviceManager _deviceManager;
    private readonly EventService _eventService;
    private readonly ConcurrentDictionary<string, LoadedScript> _loadedScripts = new();
    private readonly ConcurrentDictionary<string, Engine> _scriptEngines = new();
    private bool _isRunning;

    /// <summary>脚本执行事件</summary>
    public event EventHandler<ScriptExecutedEventArgs>? ScriptExecuted;
    
    /// <summary>脚本错误事件</summary>
    public event EventHandler<ScriptErrorEventArgs>? ScriptError;

    public ScriptEngine(DeviceManager deviceManager, EventService eventService)
    {
        _deviceManager = deviceManager;
        _eventService = eventService;
        _instance = this;
    }

    /// <summary>
    /// 启动脚本引擎
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;
        
        LoadScriptsFromDatabase();
        _isRunning = true;
        Logger.Information("ScriptEngine started, {Count} scripts loaded", _loadedScripts.Count);
    }

    /// <summary>
    /// 停止脚本引擎
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
        _loadedScripts.Clear();
        _scriptEngines.Clear();
        Logger.Information("ScriptEngine stopped");
    }

    /// <summary>
    /// 从数据库加载所有启用的脚本
    /// </summary>
    public void LoadScriptsFromDatabase()
    {
        try
        {
            var scripts = Database.Instance.GetAllScripts();
            foreach (var script in scripts)
            {
                if (script.Enabled)
                {
                    LoadScript(script);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load scripts from database");
        }
    }

    /// <summary>
    /// 加载单个脚本
    /// </summary>
    public bool LoadScript(ScriptRecord record)
    {
        try
        {
            var engine = CreateScriptEngine();
            var code = !string.IsNullOrEmpty(record.Code) ? record.Code : record.Content;
            
            // 执行脚本初始化
            engine.Execute(code);
            
            var loadedScript = new LoadedScript
            {
                Id = record.Id,
                Name = record.Name,
                Game = record.Game,
                Code = code,
                Engine = engine,
                IsEnabled = record.Enabled
            };
            
            _loadedScripts[record.Id] = loadedScript;
            _scriptEngines[record.Id] = engine;
            
            Logger.Information("Script loaded: {Name} ({Game})", record.Name, record.Game);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load script: {Name}", record.Name);
            ScriptError?.Invoke(this, new ScriptErrorEventArgs
            {
                ScriptId = record.Id,
                ScriptName = record.Name,
                Error = ex.Message
            });
            return false;
        }
    }

    /// <summary>
    /// 卸载脚本
    /// </summary>
    public void UnloadScript(string scriptId)
    {
        _loadedScripts.TryRemove(scriptId, out _);
        _scriptEngines.TryRemove(scriptId, out _);
        Logger.Information("Script unloaded: {ScriptId}", scriptId);
    }

    /// <summary>
    /// 重新加载脚本
    /// </summary>
    public bool ReloadScript(string scriptId)
    {
        var record = Database.Instance.GetScript(scriptId);
        if (record == null) return false;
        
        UnloadScript(scriptId);
        return LoadScript(record);
    }

    /// <summary>
    /// 创建脚本引擎实例
    /// </summary>
    private Engine CreateScriptEngine()
    {
        var engine = new Engine(options =>
        {
            options.TimeoutInterval(TimeSpan.FromSeconds(5));
            options.LimitMemory(16_000_000); // 16MB 内存限制
            options.LimitRecursion(100);
            options.CatchClrExceptions();
        });

        // 注入 API
        engine.SetValue("console", new ScriptConsole());
        engine.SetValue("device", new DeviceAPI(_deviceManager));
        engine.SetValue("event", new EventAPI(_eventService));
        engine.SetValue("game", new GameAPI());
        engine.SetValue("utils", new UtilsAPI());

        return engine;
    }

    /// <summary>
    /// 执行脚本函数
    /// </summary>
    public object? ExecuteFunction(string scriptId, string functionName, params object[] args)
    {
        if (!_scriptEngines.TryGetValue(scriptId, out var engine))
        {
            Logger.Warning("Script not found: {ScriptId}", scriptId);
            return null;
        }

        try
        {
            var result = engine.Invoke(functionName, args);
            
            ScriptExecuted?.Invoke(this, new ScriptExecutedEventArgs
            {
                ScriptId = scriptId,
                FunctionName = functionName,
                Success = true
            });
            
            return result?.ToObject();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Script execution failed: {ScriptId}.{Function}", scriptId, functionName);
            ScriptError?.Invoke(this, new ScriptErrorEventArgs
            {
                ScriptId = scriptId,
                ScriptName = _loadedScripts.TryGetValue(scriptId, out var s) ? s.Name : scriptId,
                Error = ex.Message
            });
            return null;
        }
    }

    /// <summary>
    /// 执行代码片段
    /// </summary>
    public object? ExecuteCode(string scriptId, string code)
    {
        if (!_scriptEngines.TryGetValue(scriptId, out var engine))
        {
            Logger.Warning("Script not found: {ScriptId}", scriptId);
            return null;
        }

        try
        {
            engine.Execute(code);
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Code execution failed: {ScriptId}", scriptId);
            ScriptError?.Invoke(this, new ScriptErrorEventArgs
            {
                ScriptId = scriptId,
                Error = ex.Message
            });
            return null;
        }
    }

    /// <summary>
    /// 触发游戏事件到所有脚本
    /// </summary>
    public void TriggerGameEvent(string eventName, Dictionary<string, object>? data = null)
    {
        foreach (var kvp in _loadedScripts)
        {
            if (!kvp.Value.IsEnabled) continue;
            
            try
            {
                if (_scriptEngines.TryGetValue(kvp.Key, out var engine))
                {
                    // 检查脚本是否有 onGameEvent 函数
                    var hasHandler = engine.Evaluate("typeof onGameEvent === 'function'").AsBoolean();
                    if (hasHandler)
                    {
                        engine.Invoke("onGameEvent", eventName, data ?? new Dictionary<string, object>());
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to trigger event {Event} on script {Script}", eventName, kvp.Value.Name);
            }
        }
    }

    /// <summary>
    /// 获取已加载的脚本列表
    /// </summary>
    public IReadOnlyCollection<LoadedScript> GetLoadedScripts()
    {
        return _loadedScripts.Values.ToArray();
    }

    public void Dispose()
    {
        Stop();
    }
}


#region Script APIs

/// <summary>
/// 控制台 API
/// </summary>
public class ScriptConsole
{
    private static readonly ILogger Logger = Log.ForContext("Script", "Console");

    public void log(params object[] args) => Logger.Information("[Script] {Message}", string.Join(" ", args));
    public void info(params object[] args) => Logger.Information("[Script] {Message}", string.Join(" ", args));
    public void warn(params object[] args) => Logger.Warning("[Script] {Message}", string.Join(" ", args));
    public void error(params object[] args) => Logger.Error("[Script] {Message}", string.Join(" ", args));
    public void debug(params object[] args) => Logger.Debug("[Script] {Message}", string.Join(" ", args));
}

/// <summary>
/// 设备控制 API
/// </summary>
public class DeviceAPI
{
    private readonly DeviceManager _deviceManager;

    public DeviceAPI(DeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
    }

    /// <summary>获取所有已连接设备</summary>
    public object[] getConnectedDevices()
    {
        return _deviceManager.GetConnectedDevices()
            .Select(d => new { id = d.Id, name = d.Name, type = d.Type.ToString() })
            .ToArray<object>();
    }

    /// <summary>设置强度</summary>
    public void setStrength(string deviceId, string channel, int value)
    {
        var ch = channel.ToUpper() switch
        {
            "A" => Channel.A,
            "B" => Channel.B,
            "AB" => Channel.AB,
            _ => Channel.A
        };
        _ = _deviceManager.SetStrengthAsync(deviceId, ch, value, StrengthMode.Set, "script");
    }

    /// <summary>增加强度</summary>
    public void increaseStrength(string deviceId, string channel, int value)
    {
        var ch = channel.ToUpper() switch
        {
            "A" => Channel.A,
            "B" => Channel.B,
            "AB" => Channel.AB,
            _ => Channel.A
        };
        _ = _deviceManager.SetStrengthAsync(deviceId, ch, value, StrengthMode.Increase, "script");
    }

    /// <summary>减少强度</summary>
    public void decreaseStrength(string deviceId, string channel, int value)
    {
        var ch = channel.ToUpper() switch
        {
            "A" => Channel.A,
            "B" => Channel.B,
            "AB" => Channel.AB,
            _ => Channel.A
        };
        _ = _deviceManager.SetStrengthAsync(deviceId, ch, value, StrengthMode.Decrease, "script");
    }

    /// <summary>发送波形</summary>
    public void sendWaveform(string deviceId, string channel, int frequency, int strength, int duration)
    {
        var ch = channel.ToUpper() switch
        {
            "A" => Channel.A,
            "B" => Channel.B,
            "AB" => Channel.AB,
            _ => Channel.A
        };
        var waveform = new WaveformData
        {
            Frequency = frequency,
            Strength = strength,
            Duration = duration
        };
        _ = _deviceManager.SendWaveformAsync(deviceId, ch, waveform);
    }

    /// <summary>紧急停止所有设备</summary>
    public void emergencyStop()
    {
        _ = _deviceManager.EmergencyStopAllAsync();
    }
}

/// <summary>
/// 事件 API
/// </summary>
public class EventAPI
{
    private readonly EventService _eventService;

    public EventAPI(EventService eventService)
    {
        _eventService = eventService;
    }

    /// <summary>触发事件</summary>
    public void trigger(string eventId, string? deviceId = null, double multiplier = 1.0)
    {
        _ = _eventService.TriggerEventAsync(eventId, deviceId, multiplier);
    }

    /// <summary>获取所有事件</summary>
    public object[] getEvents()
    {
        return _eventService.GetAllEvents()
            .Select(e => new { id = e.EventId, name = e.Name, enabled = e.Enabled })
            .ToArray<object>();
    }
}

/// <summary>
/// 游戏 API
/// </summary>
public class GameAPI
{
    /// <summary>发布游戏事件到事件总线</summary>
    public void publishEvent(string eventType, int oldValue = 0, int newValue = 0)
    {
        var type = eventType.ToLower() switch
        {
            "health_lost" or "lost-hp" => GameEventType.HealthLost,
            "health_gained" or "add-hp" => GameEventType.HealthGained,
            "death" or "dead" => GameEventType.Death,
            "knocked" => GameEventType.Knocked,
            "respawn" => GameEventType.Respawn,
            "new_round" or "new-round" => GameEventType.NewRound,
            "game_over" or "game-over" => GameEventType.GameOver,
            _ => GameEventType.Custom
        };

        var evt = new GameEvent
        {
            Type = type,
            EventId = eventType,
            Source = "Script",
            OldValue = oldValue,
            NewValue = newValue
        };

        EventBus.Instance.PublishGameEvent(evt);
    }
}

/// <summary>
/// 工具 API
/// </summary>
public class UtilsAPI
{
    private readonly Random _random = new();

    /// <summary>延迟执行</summary>
    public void delay(int ms)
    {
        Task.Delay(ms).Wait();
    }

    /// <summary>随机数</summary>
    public int random(int min, int max)
    {
        return _random.Next(min, max + 1);
    }

    /// <summary>当前时间戳</summary>
    public long timestamp()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>格式化时间</summary>
    public string formatTime(string format = "yyyy-MM-dd HH:mm:ss")
    {
        return DateTime.Now.ToString(format);
    }

    /// <summary>限制值范围</summary>
    public int clamp(int value, int min, int max)
    {
        return Math.Clamp(value, min, max);
    }
}

#endregion

#region Data Classes

/// <summary>
/// 已加载的脚本
/// </summary>
public class LoadedScript
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Game { get; set; } = "";
    public string Code { get; set; } = "";
    public Engine? Engine { get; set; }
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// 脚本执行事件参数
/// </summary>
public class ScriptExecutedEventArgs : EventArgs
{
    public string ScriptId { get; set; } = "";
    public string FunctionName { get; set; } = "";
    public bool Success { get; set; }
}

/// <summary>
/// 脚本错误事件参数
/// </summary>
public class ScriptErrorEventArgs : EventArgs
{
    public string ScriptId { get; set; } = "";
    public string ScriptName { get; set; } = "";
    public string Error { get; set; } = "";
}

#endregion
