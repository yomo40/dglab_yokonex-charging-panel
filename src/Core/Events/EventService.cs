using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.DGLab;
using Serilog;

namespace ChargingPanel.Core.Events;

/// <summary>
/// 事件服务
/// 负责事件触发和设备控制的映射
/// </summary>
public class EventService
{
    private readonly DeviceManager _deviceManager;
    private readonly ILogger _logger = Log.ForContext<EventService>();
    private readonly ConcurrentDictionary<string, EventRecord> _eventCache = new();

    /// <summary>
    /// 事件触发时发出通知
    /// </summary>
    public event EventHandler<EventTriggeredEventArgs>? EventTriggered;

    public EventService(DeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
        RefreshCache();
    }

    /// <summary>
    /// 刷新事件缓存
    /// </summary>
    public void RefreshCache()
    {
        _eventCache.Clear();
        try
        {
            var events = Database.Instance.GetAllEvents();
            foreach (var evt in events)
            {
                _eventCache[evt.EventId] = evt;
            }
            _logger.Information("Event cache refreshed, {Count} events loaded", _eventCache.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to refresh event cache");
        }
    }

    /// <summary>
    /// 获取所有事件
    /// </summary>
    public List<EventRecord> GetAllEvents()
    {
        return Database.Instance.GetAllEvents();
    }

    /// <summary>
    /// 获取事件
    /// </summary>
    public EventRecord? GetEvent(string eventId)
    {
        return _eventCache.TryGetValue(eventId, out var evt) ? evt : null;
    }

    /// <summary>
    /// 获取启用的事件
    /// </summary>
    public List<EventRecord> GetEnabledEvents()
    {
        return _eventCache.Values.Where(e => e.Enabled).ToList();
    }

    /// <summary>
    /// 触发事件
    /// </summary>
    /// <param name="eventId">事件ID</param>
    /// <param name="deviceId">目标设备ID（null表示所有已连接设备）</param>
    /// <param name="multiplier">强度倍数</param>
    public async Task TriggerEventAsync(string eventId, string? deviceId = null, double multiplier = 1.0)
    {
        if (!_eventCache.TryGetValue(eventId, out var eventRecord))
        {
            _logger.Warning("Event not found: {EventId}", eventId);
            return;
        }

        if (!eventRecord.Enabled)
        {
            _logger.Debug("Event {EventId} is disabled", eventId);
            return;
        }

        var devices = deviceId != null
            ? new[] { _deviceManager.GetDevice(deviceId) }
            : _deviceManager.GetConnectedDevices().Select(d => _deviceManager.GetDevice(d.Id)).ToArray();

        // 过滤掉 null 设备
        devices = devices.Where(d => d != null).ToArray()!;

        if (!devices.Any())
        {
            _logger.Warning("No connected devices to trigger event {EventId}", eventId);
            return;
        }

        // 计算实际强度值
        var actualValue = (int)(eventRecord.Value * multiplier);
        actualValue = Math.Clamp(actualValue, 0, 200);

        // 解析通道
        var channels = eventRecord.Channel switch
        {
            "A" => new[] { Channel.A },
            "B" => new[] { Channel.B },
            "AB" => new[] { Channel.A, Channel.B },
            _ => new[] { Channel.A }
        };

        // 解析动作模式
        var mode = eventRecord.Action switch
        {
            "increase" => StrengthMode.Increase,
            "decrease" => StrengthMode.Decrease,
            _ => StrengthMode.Set
        };

        _logger.Information("Triggering event {EventId} ({Name}): action={Action}, value={Value}, channels={Channels}",
            eventId, eventRecord.Name, eventRecord.Action, actualValue, string.Join(",", channels));

        // 对每个设备执行操作
        foreach (var device in devices)
        {
            try
            {
                switch (eventRecord.Action)
                {
                    case "set":
                    case "increase":
                    case "decrease":
                        foreach (var channel in channels)
                        {
                            await device.SetStrengthAsync(channel, actualValue, mode);
                        }
                        break;

                    case "wave":
                    case "pulse":
                        // 生成波形
                        var waveform = new WaveformData
                        {
                            Frequency = 100,
                            Strength = actualValue,
                            Duration = eventRecord.Duration > 0 ? eventRecord.Duration : 1000
                        };
                        foreach (var channel in channels)
                        {
                            await device.SendWaveformAsync(channel, waveform);
                        }
                        break;

                    case "clear":
                        foreach (var channel in channels)
                        {
                            await device.ClearWaveformQueueAsync(channel);
                        }
                        break;
                }

                // 如果有持续时间且是 set/increase 动作，等待后恢复
                if (eventRecord.Duration > 0 && eventRecord.Action is "set" or "increase")
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(eventRecord.Duration);
                        foreach (var channel in channels)
                        {
                            try
                            {
                                await device.SetStrengthAsync(channel, 0, StrengthMode.Set);
                            }
                            catch { }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to trigger event {EventId} on device {DeviceId}", eventId, device.Id);
            }
        }

        // 发出事件触发通知
        EventTriggered?.Invoke(this, new EventTriggeredEventArgs
        {
            EventId = eventId,
            EventName = eventRecord.Name,
            Action = eventRecord.Action,
            Value = actualValue,
            Channel = eventRecord.Channel,
            Devices = devices.Select(d => d.Id).ToList(),
            Timestamp = DateTime.UtcNow
        });

        // 记录到数据库
        Database.Instance.AddLog("info", "EventService",
            $"Event triggered: {eventId} ({eventRecord.Name})",
            System.Text.Json.JsonSerializer.Serialize(new { eventId, action = eventRecord.Action, value = actualValue }));
    }

    /// <summary>
    /// 添加事件
    /// </summary>
    public void AddEvent(EventRecord eventRecord)
    {
        Database.Instance.AddEvent(eventRecord);
        RefreshCache();
    }

    /// <summary>
    /// 更新事件
    /// </summary>
    public void UpdateEvent(string id, EventRecord updates)
    {
        Database.Instance.UpdateEvent(id, updates);
        RefreshCache();
    }

    /// <summary>
    /// 删除事件
    /// </summary>
    public void DeleteEvent(string id)
    {
        Database.Instance.DeleteEvent(id);
        RefreshCache();
    }

    /// <summary>
    /// 切换事件启用状态
    /// </summary>
    public void ToggleEvent(string id, bool enabled)
    {
        var evt = Database.Instance.GetEvent(id);
        if (evt != null)
        {
            evt.Enabled = enabled;
            Database.Instance.UpdateEvent(id, evt);
            RefreshCache();
        }
    }
}

/// <summary>
/// 事件触发事件参数
/// </summary>
public class EventTriggeredEventArgs : EventArgs
{
    public string EventId { get; set; } = "";
    public string EventName { get; set; } = "";
    public string Action { get; set; } = "";
    public int Value { get; set; }
    public string Channel { get; set; } = "";
    public List<string> Devices { get; set; } = new();
    public DateTime Timestamp { get; set; }
}
