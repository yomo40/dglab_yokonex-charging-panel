using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text.Json;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Data.Entities;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.DGLab;
using Serilog;

namespace ChargingPanel.Core.Events;

/// <summary>
/// 事件处理器（总线桥接层）
/// 负责将 EventBus 游戏事件和远程同步事件桥接到规则执行服务。
/// </summary>
public sealed class EventProcessor : IDisposable
{
    public const string SkipProcessorDataKey = "__skip_event_processor";

    private static readonly IReadOnlyDictionary<GameEventType, string> EventIdMap =
        new Dictionary<GameEventType, string>
        {
            [GameEventType.HealthLost] = "lost-hp",
            [GameEventType.HealthGained] = "add-hp",
            [GameEventType.ArmorLost] = "lost-ahp",
            [GameEventType.ArmorGained] = "add-ahp",
            [GameEventType.Death] = "dead",
            // 倒地事件按“死亡”规则处理，保证“角色倒地/死亡”统一触发
            [GameEventType.Knocked] = "dead",
            [GameEventType.Respawn] = "respawn",
            [GameEventType.NewRound] = "new-round",
            [GameEventType.GameOver] = "game-over",
            [GameEventType.Debuff] = "character-debuff",
            [GameEventType.StepCountChanged] = "step-count-changed",
            [GameEventType.AngleChanged] = "angle-changed",
            [GameEventType.PressureChanged] = "pressure-changed",
            [GameEventType.EnemaStatusChanged] = "enema-status-changed",
            [GameEventType.DeviceBatteryChanged] = "device-battery-changed",
            [GameEventType.ToyDeviceInfoChanged] = "toy-device-info-changed",
            [GameEventType.ChannelDisconnected] = "channel-disconnected",
            [GameEventType.ChannelConnected] = "channel-connected",
            [GameEventType.ExternalVoltageChanged] = "external-voltage-changed"
        };

    private readonly DeviceManager _deviceManager;
    private readonly EventService _eventService;
    private readonly EventBus _eventBus;
    private readonly ILogger _logger = Log.ForContext<EventProcessor>();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly ConcurrentDictionary<string, DateTime> _processedEventIds = new();
    private readonly ConcurrentDictionary<string, DateTime> _throttleWindows = new();

    public event EventHandler<EventProcessedEventArgs>? EventProcessed;

    public EventProcessor(DeviceManager deviceManager, EventService eventService, EventBus? eventBus = null)
    {
        _deviceManager = deviceManager;
        _eventService = eventService;
        _eventBus = eventBus ?? EventBus.Instance;

        _subscriptions.Add(_eventBus.GameEvents.Subscribe(OnGameEvent));
        _subscriptions.Add(
            _eventBus.RemoteSyncEvents
                .Where(e => e.Type == RemoteSyncType.ControlCommand || e.Type == RemoteSyncType.EventTrigger)
                .Subscribe(OnRemoteSync));

        _logger.Information("EventProcessor initialized");
    }

    private async void OnGameEvent(GameEvent evt)
    {
        await HandleGameEventAsync(evt);
    }

    private async Task HandleGameEventAsync(GameEvent evt)
    {
        try
        {
            if (ShouldSkip(evt))
            {
                return;
            }

            var eventId = ResolveEventId(evt, _eventService);
            if (string.IsNullOrWhiteSpace(eventId))
            {
                _logger.Debug("Skip game event without routable eventId: {Type}, Source={Source}", evt.Type, evt.Source);
                return;
            }

            var matchedRule = _eventService.GetEvent(eventId);
            if (matchedRule != null && !EventRuleConditionEvaluator.IsMatch(matchedRule, evt))
            {
                _logger.Debug(
                    "Skip game event by rule condition: EventId={EventId}, TriggerType={TriggerType}, Source={Source}",
                    eventId,
                    matchedRule.TriggerType,
                    evt.Source);
                return;
            }

            var multiplier = ResolveMultiplier(evt);
            await _eventService.TriggerEventAsync(eventId, evt.TargetDeviceId, multiplier);

            EventProcessed?.Invoke(this, new EventProcessedEventArgs
            {
                Event = evt,
                Success = true,
                ActualValue = 0
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing game event {EventId}", evt.EventId);
            EventProcessed?.Invoke(this, new EventProcessedEventArgs
            {
                Event = evt,
                Success = false,
                Error = ex.Message
            });
        }
    }

    private async void OnRemoteSync(RemoteSyncEvent syncEvt)
    {
        try
        {
            switch (syncEvt.Type)
            {
                case RemoteSyncType.ControlCommand:
                    if (syncEvt.Payload is DeviceControlEvent controlEvt)
                    {
                        await ExecuteRemoteControlAsync(controlEvt);
                    }
                    break;

                case RemoteSyncType.EventTrigger:
                    if (syncEvt.Payload is GameEvent gameEvt)
                    {
                        gameEvt.IsRemote = true;
                        gameEvt.SenderId = syncEvt.SenderId;
                        await HandleGameEventAsync(gameEvt);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing remote sync event");
        }
    }

    private async Task ExecuteRemoteControlAsync(DeviceControlEvent controlEvt)
    {
        try
        {
            var channels = ParseChannels(controlEvt.Channel);

            switch (controlEvt.Action)
            {
                case EventAction.Set:
                case EventAction.Increase:
                case EventAction.Decrease:
                {
                    var mode = controlEvt.Action switch
                    {
                        EventAction.Increase => StrengthMode.Increase,
                        EventAction.Decrease => StrengthMode.Decrease,
                        _ => StrengthMode.Set
                    };
                    foreach (var channel in channels)
                    {
                        await _deviceManager.SetStrengthAsync(controlEvt.DeviceId, channel, controlEvt.Value, mode, source: "remote_sync");
                    }
                    break;
                }
                case EventAction.Wave:
                case EventAction.Pulse:
                {
                    var waveform = TryParseWaveform(controlEvt.WaveformData) ?? new WaveformData
                    {
                        Frequency = controlEvt.Action == EventAction.Pulse ? 150 : 100,
                        Strength = controlEvt.Value,
                        Duration = controlEvt.Duration > 0 ? controlEvt.Duration : 1000
                    };
                    foreach (var channel in channels)
                    {
                        await _deviceManager.SendWaveformAsync(controlEvt.DeviceId, channel, waveform);
                    }
                    break;
                }
                case EventAction.Clear:
                {
                    foreach (var channel in channels)
                    {
                        await _deviceManager.ClearWaveformQueueAsync(controlEvt.DeviceId, channel);
                    }
                    break;
                }
            }

            controlEvt.Success = true;
            _eventBus.PublishDeviceControl(controlEvt);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Execute remote control failed: {DeviceId}", controlEvt.DeviceId);
            _eventBus.PublishDeviceControl(new DeviceControlEvent
            {
                DeviceId = controlEvt.DeviceId,
                Action = controlEvt.Action,
                Channel = controlEvt.Channel,
                Value = controlEvt.Value,
                Duration = controlEvt.Duration,
                Source = controlEvt.Source,
                Success = false,
                Error = ex.Message
            });
        }
    }

    private static string? ResolveEventId(GameEvent evt, EventService eventService)
    {
        if (!string.IsNullOrWhiteSpace(evt.EventId))
        {
            if (!EventTriggerPolicy.CanBeTriggerRule(evt.EventId))
            {
                return null;
            }

            // 显式 eventId 优先；若规则不存在则回退到类型映射，保持兼容。
            if (eventService.GetEvent(evt.EventId) != null)
            {
                return evt.EventId;
            }
        }

        if (EventIdMap.TryGetValue(evt.Type, out var mapped))
        {
            if (!EventTriggerPolicy.CanBeTriggerRule(mapped))
            {
                return null;
            }
            // 类型映射作为兜底路径，不强依赖缓存命中，避免短暂缓存漂移导致事件丢失。
            return mapped;
        }

        return null;
    }

    private bool ShouldSkip(GameEvent evt)
    {
        if (evt.Data.TryGetValue(SkipProcessorDataKey, out var skipRaw) && ToBoolean(skipRaw))
        {
            return true;
        }

        // 防止同一事件对象在短窗口内被重复桥接（例如多处重复发布）。
        if (string.IsNullOrWhiteSpace(evt.Id))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (_processedEventIds.TryGetValue(evt.Id, out var last) &&
            (now - last).TotalMilliseconds < 1500)
        {
            return true;
        }

        _processedEventIds[evt.Id] = now;

        if (_processedEventIds.Count > 4096)
        {
            CleanupProcessedEvents(now);
        }

        if (IsThrottled(evt, now))
        {
            return true;
        }

        return false;
    }

    private void CleanupProcessedEvents(DateTime now)
    {
        foreach (var kv in _processedEventIds)
        {
            if ((now - kv.Value).TotalMinutes > 2)
            {
                _processedEventIds.TryRemove(kv.Key, out _);
            }
        }
    }

    private bool IsThrottled(GameEvent evt, DateTime now)
    {
        var debounceMs = ResolveDebounceMs(evt);
        if (debounceMs <= 0)
        {
            return false;
        }

        var key = BuildThrottleKey(evt);
        if (_throttleWindows.TryGetValue(key, out var lastTriggeredAt) &&
            (now - lastTriggeredAt).TotalMilliseconds < debounceMs)
        {
            return true;
        }

        _throttleWindows[key] = now;
        if (_throttleWindows.Count > 4096)
        {
            CleanupThrottleWindows(now);
        }

        return false;
    }

    private static int ResolveDebounceMs(GameEvent evt)
    {
        if (evt.Data.TryGetValue("debounceMs", out var explicitDebounce))
        {
            var parsed = ToDouble(explicitDebounce);
            if (parsed >= 0)
            {
                return (int)parsed;
            }
        }

        return 120;
    }

    private static string BuildThrottleKey(GameEvent evt)
    {
        var eventId = string.IsNullOrWhiteSpace(evt.EventId) ? evt.Type.ToString() : evt.EventId;
        var target = string.IsNullOrWhiteSpace(evt.TargetDeviceId) ? "*" : evt.TargetDeviceId;
        return $"{eventId}::{evt.Source}::{target}";
    }

    private void CleanupThrottleWindows(DateTime now)
    {
        foreach (var kv in _throttleWindows)
        {
            if ((now - kv.Value).TotalMinutes > 2)
            {
                _throttleWindows.TryRemove(kv.Key, out _);
            }
        }
    }

    private static double ResolveMultiplier(GameEvent evt)
    {
        if (evt.Data.TryGetValue("multiplier", out var multiplierRaw))
        {
            var explicitMultiplier = ToDouble(multiplierRaw);
            if (explicitMultiplier > 0)
            {
                return explicitMultiplier;
            }
        }

        if (evt.Data.TryGetValue("change", out var changeRaw))
        {
            var change = Math.Abs(ToDouble(changeRaw));
            if (change > 0)
            {
                return Math.Max(1.0, change / 10.0);
            }
        }

        // Custom 事件默认不按 old/new 自动放大，避免 MOD 自定义规则被隐式倍率影响。
        if (evt.Type == GameEventType.Custom)
        {
            return 1.0;
        }

        if (evt.Delta != 0)
        {
            return Math.Max(1.0, Math.Abs(evt.Delta) / 10.0);
        }

        return 1.0;
    }

    private static bool ToBoolean(object? value)
    {
        return value switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var parsed) && parsed,
            JsonElement e when e.ValueKind == JsonValueKind.True => true,
            JsonElement e when e.ValueKind == JsonValueKind.False => false,
            JsonElement e when e.ValueKind == JsonValueKind.String &&
                               bool.TryParse(e.GetString(), out var parsed) => parsed,
            _ => false
        };
    }

    private static double ToDouble(object? value)
    {
        return value switch
        {
            byte b => b,
            short s => s,
            int i => i,
            long l => l,
            float f => f,
            double d => d,
            decimal m => (double)m,
            string text when double.TryParse(text, out var parsed) => parsed,
            JsonElement e when e.ValueKind == JsonValueKind.Number => e.GetDouble(),
            JsonElement e when e.ValueKind == JsonValueKind.String &&
                               double.TryParse(e.GetString(), out var parsed) => parsed,
            _ => 0
        };
    }

    private static Channel[] ParseChannels(ChannelTarget target)
    {
        return target switch
        {
            ChannelTarget.A => new[] { Channel.A },
            ChannelTarget.B => new[] { Channel.B },
            ChannelTarget.AB => new[] { Channel.A, Channel.B },
            _ => new[] { Channel.A }
        };
    }

    private static WaveformData? TryParseWaveform(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WaveformData>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        _processedEventIds.Clear();
        _throttleWindows.Clear();
    }
}

public class EventProcessedEventArgs : EventArgs
{
    public GameEvent Event { get; set; } = null!;
    public EventRecord? EventRecord { get; set; }
    public int ActualValue { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}
