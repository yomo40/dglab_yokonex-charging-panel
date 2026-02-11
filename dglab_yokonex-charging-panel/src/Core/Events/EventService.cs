using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.DGLab;
using ChargingPanel.Core.Protocols;
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
    private readonly ConcurrentDictionary<string, EventRecord> _runtimeModRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _modSessionRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _eventCooldowns = new();
    private readonly ConcurrentDictionary<string, DevicePriorityWindow> _devicePriorityWindows = new();
    private readonly DeviceActionTranslatorRegistry _translatorRegistry = new();

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
                if (!EventTriggerPolicy.CanBeTriggerRule(evt.EventId))
                {
                    continue;
                }

                _eventCache[evt.EventId] = evt;
            }

            // 默认规则兜底：数据库缺失时仍能执行默认行为。
            foreach (var fallback in DefaultEventRules.All)
            {
                _eventCache.TryAdd(fallback.EventId, fallback);
            }

            // 会话级 MOD 规则（运行时，仅内存，不写入数据库）
            foreach (var rule in _runtimeModRules.Values)
            {
                _eventCache[rule.EventId] = rule;
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
        return _eventCache.Values
            .OrderBy(e => e.Category)
            .ThenBy(e => e.EventId)
            .ToList();
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
            : _deviceManager
                .GetConnectedDevices()
                .Where(d => MatchesTargetDeviceType(d, eventRecord.TargetDeviceType))
                .Select(d => _deviceManager.GetDevice(d.Id))
                .ToArray();

        // 过滤掉 null 设备
        devices = devices.Where(d => d != null).ToArray()!;

        if (!devices.Any())
        {
            _logger.Warning("No connected devices to trigger event {EventId}", eventId);
            return;
        }

        // 统一强度：业务层按 0-100 归一化，再按设备能力线性映射。
        var configuredStrengthCap = Database.Instance.GetSetting<int>("safety.maxStrength", 100);
        var normalizedStrengthCap = StrengthScalingPolicy.ResolveNormalizedCap(configuredStrengthCap);
        var normalizedValue = StrengthScalingPolicy.NormalizeInputValue(eventRecord.Value, multiplier, normalizedStrengthCap);

        var channels = ParseChannels(eventRecord.Channel);
        var actionType = NormalizeActionType(eventRecord);

        _logger.Information(
            "Triggering event {EventId} ({Name}): action={Action}, normalizedValue={Value}/{Cap}, channels={Channels}",
            eventId,
            eventRecord.Name,
            actionType,
            normalizedValue,
            normalizedStrengthCap,
            string.Join(",", channels));

        var triggeredDevices = new List<string>();
        var now = DateTime.UtcNow;

        // 对每个设备执行操作
        foreach (var device in devices)
        {
            if (IsOnCooldown(eventRecord, device.Id, now))
            {
                _logger.Debug("Skip event {EventId} on device {DeviceId}: cooldown", eventRecord.EventId, device.Id);
                continue;
            }

            if (!TryAcquirePriorityWindow(device.Id, eventRecord, now, out var activePriority, out var activeEventId))
            {
                _logger.Debug(
                    "Skip event {EventId} on device {DeviceId}: lower priority ({Priority} < {ActivePriority}), activeEvent={ActiveEventId}",
                    eventRecord.EventId,
                    device.Id,
                    eventRecord.Priority,
                    activePriority,
                    activeEventId);
                continue;
            }

            try
            {
                var valueForDevice = normalizedValue;
                var rawValueForDevice = eventRecord.Strength > 0 ? eventRecord.Strength : eventRecord.Value;
                if (StrengthScalingPolicy.RequiresScaling(actionType))
                {
                    var deviceMaxStrength = StrengthScalingPolicy.ResolveDeviceMaxStrength(device);
                    valueForDevice = StrengthScalingPolicy.ScaleNormalizedToDevice(
                        normalizedValue,
                        deviceMaxStrength,
                        normalizedStrengthCap);
                    rawValueForDevice = valueForDevice;

                    _logger.Debug(
                        "Strength scaled for device {DeviceId}: normalized={Normalized}/{Cap} -> device={DeviceValue}/{DeviceMax}",
                        device.Id,
                        normalizedValue,
                        normalizedStrengthCap,
                        valueForDevice,
                        deviceMaxStrength);
                }

                var request = new DeviceActionRequest
                {
                    EventId = eventRecord.EventId,
                    ActionType = actionType,
                    Value = valueForDevice,
                    RawValue = rawValueForDevice,
                    DurationMs = eventRecord.Duration,
                    WaveformData = eventRecord.WaveformData,
                    Channels = channels
                };

                await _translatorRegistry.ExecuteAsync(device, request);
                MarkTriggered(eventRecord, device.Id, now);
                triggeredDevices.Add(device.Id);

                // 如果有持续时间且是 set/increase 动作，等待后恢复
                if (eventRecord.Duration > 0 && actionType is "set" or "increase")
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

        if (triggeredDevices.Count == 0)
        {
            return;
        }

        // 发出事件触发通知
        EventTriggered?.Invoke(this, new EventTriggeredEventArgs
        {
            EventId = eventId,
            EventName = eventRecord.Name,
            Action = actionType,
            Value = normalizedValue,
            Channel = eventRecord.Channel,
            Devices = triggeredDevices,
            Timestamp = DateTime.UtcNow
        });

        // 记录到数据库
        Database.Instance.AddLog("info", "EventService",
            $"Event triggered: {eventId} ({eventRecord.Name})",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                eventId,
                action = actionType,
                normalizedValue,
                normalizedStrengthCap
            }));
    }

    /// <summary>
    /// 添加事件
    /// </summary>
    public void AddEvent(EventRecord eventRecord)
    {
        Database.Instance.SaveEvent(eventRecord);
        RefreshCache();
    }

    /// <summary>
    /// 更新事件
    /// </summary>
    public void UpdateEvent(string id, EventRecord updates)
    {
        var existing = Database.Instance.GetEvent(id) ??
                       Database.Instance.GetEventByEventId(id) ??
                       Database.Instance.GetEventByEventId(updates.EventId);
        if (existing != null)
        {
            Database.Instance.UpdateEvent(existing.Id, updates);
        }
        else
        {
            Database.Instance.AddEvent(updates);
        }

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
        var evt = Database.Instance.GetEvent(id) ??
                  Database.Instance.GetEventByEventId(id);

        if (evt == null && DefaultEventRules.TryGet(id, out var defaultRule) && defaultRule != null)
        {
            evt = defaultRule;
        }

        if (evt != null)
        {
            evt.Enabled = enabled;
            Database.Instance.SaveEvent(evt);
            RefreshCache();
        }
    }

    /// <summary>
    /// 注册会话级 MOD 规则（仅内存，不写入数据库）
    /// </summary>
    public ModRuleRegistrationResult RegisterModRulesForSession(
        string sessionId,
        IEnumerable<EventRecord>? rules,
        int maxRules = 20)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ModRuleRegistrationResult.Empty;
        }

        if (rules == null)
        {
            return ModRuleRegistrationResult.Empty;
        }

        var accepted = 0;
        var rejected = 0;
        var rejectedEventIds = new List<string>();
        var sessionBag = _modSessionRules.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));

        foreach (var inputRule in rules.Take(Math.Max(1, maxRules)))
        {
            var eventId = inputRule.EventId?.Trim();
            if (string.IsNullOrWhiteSpace(eventId))
            {
                rejected++;
                rejectedEventIds.Add("(empty)");
                continue;
            }

            if (!EventTriggerPolicy.CanBeTriggerRule(eventId))
            {
                rejected++;
                rejectedEventIds.Add(eventId);
                continue;
            }

            if (_eventCache.ContainsKey(eventId) || _runtimeModRules.ContainsKey(eventId))
            {
                rejected++;
                rejectedEventIds.Add(eventId);
                continue;
            }

            var normalized = NormalizeModRule(inputRule, eventId);
            _runtimeModRules[eventId] = normalized;
            _eventCache[eventId] = normalized;
            sessionBag[eventId] = 1;
            accepted++;
        }

        if (accepted > 0)
        {
            _logger.Information("Registered {Count} MOD rules for session {SessionId}", accepted, sessionId);
        }

        return new ModRuleRegistrationResult(accepted, rejected, rejectedEventIds);
    }

    /// <summary>
    /// 注销会话级 MOD 规则
    /// </summary>
    public int UnregisterModRulesForSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return 0;
        }

        if (!_modSessionRules.TryRemove(sessionId, out var eventIds))
        {
            return 0;
        }

        var removed = 0;
        foreach (var eventId in eventIds.Keys)
        {
            if (_runtimeModRules.TryRemove(eventId, out _))
            {
                removed++;
            }
            _eventCache.TryRemove(eventId, out _);
        }

        if (removed > 0)
        {
            // 清理以该 eventId 为前缀的冷却缓存
            foreach (var key in _eventCooldowns.Keys.ToArray())
            {
                if (eventIds.Keys.Any(eid => key.StartsWith($"{eid}::", StringComparison.OrdinalIgnoreCase)))
                {
                    _eventCooldowns.TryRemove(key, out _);
                }
            }
            _logger.Information("Unregistered {Count} MOD rules for session {SessionId}", removed, sessionId);
        }

        return removed;
    }

    private static EventRecord NormalizeModRule(EventRecord inputRule, string eventId)
    {
        var now = DateTime.UtcNow.ToString("o");
        var action = string.IsNullOrWhiteSpace(inputRule.ActionType)
            ? inputRule.Action
            : inputRule.ActionType;
        action = ActionSemantic.Normalize(action);
        var triggerType = NormalizeRuleTriggerType(inputRule.TriggerType);

        var channel = inputRule.Channel?.Trim().ToUpperInvariant() switch
        {
            "A" => "A",
            "B" => "B",
            "AB" => "AB",
            _ => "A"
        };

        var value = inputRule.Value > 0 ? inputRule.Value : inputRule.Strength;
        value = Math.Clamp(value <= 0 ? 10 : value, 0, 100);

        return new EventRecord
        {
            Id = $"mod_{Guid.NewGuid():N}"[..20],
            EventId = eventId,
            Name = string.IsNullOrWhiteSpace(inputRule.Name) ? eventId : inputRule.Name,
            Description = inputRule.Description,
            Category = "mod",
            Channel = channel,
            Action = action,
            ActionType = action,
            Value = value,
            Strength = value,
            Duration = Math.Max(0, inputRule.Duration),
            WaveformData = inputRule.WaveformData,
            Enabled = true,
            Priority = inputRule.Priority <= 0 ? 20 : inputRule.Priority,
            TargetDeviceType = string.IsNullOrWhiteSpace(inputRule.TargetDeviceType) ? "All" : inputRule.TargetDeviceType,
            TriggerType = triggerType,
            MinChange = Math.Max(0, inputRule.MinChange),
            MaxChange = Math.Max(0, inputRule.MaxChange),
            ConditionField = string.IsNullOrWhiteSpace(inputRule.ConditionField) ? null : inputRule.ConditionField.Trim(),
            ConditionOperator = string.IsNullOrWhiteSpace(inputRule.ConditionOperator)
                ? null
                : inputRule.ConditionOperator.Trim().ToLowerInvariant(),
            ConditionValue = inputRule.ConditionValue,
            ConditionMaxValue = inputRule.ConditionMaxValue,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static string NormalizeRuleTriggerType(string? triggerType)
    {
        var normalized = triggerType?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "always" : normalized;
    }

    private static string NormalizeActionType(EventRecord eventRecord)
    {
        var action = string.IsNullOrWhiteSpace(eventRecord.ActionType)
            ? eventRecord.Action
            : eventRecord.ActionType;
        return ActionSemantic.Normalize(action);
    }

    private static Channel[] ParseChannels(string channel)
    {
        return channel?.Trim().ToUpperInvariant() switch
        {
            "A" => new[] { Channel.A },
            "B" => new[] { Channel.B },
            "AB" => new[] { Channel.A, Channel.B },
            _ => new[] { Channel.A }
        };
    }

    private bool IsOnCooldown(EventRecord rule, string deviceId, DateTime now)
    {
        var cooldownMs = ResolveCooldownMs(rule);
        if (cooldownMs <= 0)
        {
            return false;
        }

        var key = $"{rule.EventId}::{deviceId}";
        if (_eventCooldowns.TryGetValue(key, out var lastTriggeredAt))
        {
            return (now - lastTriggeredAt).TotalMilliseconds < cooldownMs;
        }

        return false;
    }

    private void MarkTriggered(EventRecord rule, string deviceId, DateTime now)
    {
        var cooldownMs = ResolveCooldownMs(rule);
        if (cooldownMs > 0)
        {
            _eventCooldowns[$"{rule.EventId}::{deviceId}"] = now;
        }
    }

    private int ResolveCooldownMs(EventRecord rule)
    {
        var configured = Database.Instance.GetSetting<int>($"rules.cooldown.{rule.EventId}", -1);
        if (configured >= 0)
        {
            return configured;
        }

        if (rule.Duration > 0)
        {
            return Math.Min(rule.Duration, 5000);
        }

        return Database.Instance.GetSetting<int>("rules.cooldown.defaultMs", 0);
    }

    private bool TryAcquirePriorityWindow(
        string deviceId,
        EventRecord eventRecord,
        DateTime now,
        out int activePriority,
        out string activeEventId)
    {
        activePriority = 0;
        activeEventId = string.Empty;
        var holdMs = ResolvePriorityHoldMs(eventRecord);
        var nextWindow = new DevicePriorityWindow(eventRecord.Priority, now.AddMilliseconds(holdMs), eventRecord.EventId);

        while (true)
        {
            if (!_devicePriorityWindows.TryGetValue(deviceId, out var current))
            {
                if (_devicePriorityWindows.TryAdd(deviceId, nextWindow))
                {
                    return true;
                }

                continue;
            }

            if (now >= current.ExpiresAtUtc)
            {
                _devicePriorityWindows.TryRemove(deviceId, out _);
                continue;
            }

            activePriority = current.Priority;
            activeEventId = current.EventId;
            if (!string.Equals(current.EventId, eventRecord.EventId, StringComparison.OrdinalIgnoreCase) &&
                eventRecord.Priority < current.Priority)
            {
                return false;
            }

            if (_devicePriorityWindows.TryUpdate(deviceId, nextWindow, current))
            {
                return true;
            }
        }
    }

    private int ResolvePriorityHoldMs(EventRecord eventRecord)
    {
        var defaultHoldMs = Database.Instance.GetSetting<int>("rules.priorityHoldMs", 1200);
        if (eventRecord.Duration > 0)
        {
            return Math.Max(defaultHoldMs, Math.Min(eventRecord.Duration, 5000));
        }

        return defaultHoldMs;
    }

    private static bool MatchesTargetDeviceType(DeviceInfo info, string? targetDeviceType)
    {
        if (string.IsNullOrWhiteSpace(targetDeviceType) ||
            targetDeviceType.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return targetDeviceType.Trim() switch
        {
            "DGLab" => info.Type == DeviceType.DGLab,
            "dglab" => info.Type == DeviceType.DGLab,
            "Yokonex" => info.Type == DeviceType.Yokonex,
            "yokonex" => info.Type == DeviceType.Yokonex,
            "Yokonex_Estim" => info.Type == DeviceType.Yokonex && info.YokonexType == Devices.Yokonex.YokonexDeviceType.Estim,
            "Yokonex_Enema" => info.Type == DeviceType.Yokonex && info.YokonexType == Devices.Yokonex.YokonexDeviceType.Enema,
            "Yokonex_Vibrator" => info.Type == DeviceType.Yokonex && info.YokonexType == Devices.Yokonex.YokonexDeviceType.Vibrator,
            "Yokonex_Cup" => info.Type == DeviceType.Yokonex && info.YokonexType == Devices.Yokonex.YokonexDeviceType.Cup,
            "Yokonex_SmartLock" => info.Type == DeviceType.Yokonex && info.YokonexType == Devices.Yokonex.YokonexDeviceType.SmartLock,
            _ => true
        };
    }

    private sealed record DevicePriorityWindow(int Priority, DateTime ExpiresAtUtc, string EventId);
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

public sealed class ModRuleRegistrationResult
{
    public static ModRuleRegistrationResult Empty { get; } = new(0, 0, new List<string>());

    public ModRuleRegistrationResult(int acceptedCount, int rejectedCount, List<string> rejectedEventIds)
    {
        AcceptedCount = acceptedCount;
        RejectedCount = rejectedCount;
        RejectedEventIds = rejectedEventIds;
    }

    public int AcceptedCount { get; }
    public int RejectedCount { get; }
    public List<string> RejectedEventIds { get; }
}
