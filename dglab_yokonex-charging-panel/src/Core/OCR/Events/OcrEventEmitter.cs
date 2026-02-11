using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChargingPanel.Core.Events;
using ChargingPanel.Core.Services;

namespace ChargingPanel.Core.OCR.Events;

/// <summary>
/// OCR 事件发射器：将识别结果转换为规则事件和 EventBus 事件。
/// </summary>
internal sealed class OcrEventEmitter
{
    private readonly EventService _eventService;
    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new(StringComparer.OrdinalIgnoreCase);

    public OcrEventEmitter(EventService eventService)
    {
        _eventService = eventService;
    }

    public async Task EmitBloodChangedAsync(int previous, int current, int cooldownMs)
    {
        var change = current - previous;
        if (change == 0)
        {
            return;
        }

        if (!TryPassCooldown("blood", cooldownMs))
        {
            return;
        }

        if (change < 0)
        {
            var absChange = Math.Abs(change);
            await _eventService.TriggerEventAsync("lost-hp", multiplier: absChange / 10.0);

            EventBus.Instance.PublishGameEvent(new GameEvent
            {
                Type = GameEventType.HealthLost,
                EventId = "lost-hp",
                Source = "OCR",
                OldValue = previous,
                NewValue = current,
                Data = new Dictionary<string, object>
                {
                    ["change"] = absChange,
                    [EventProcessor.SkipProcessorDataKey] = true
                }
            });
        }
        else
        {
            await _eventService.TriggerEventAsync("add-hp", multiplier: change / 10.0);

            EventBus.Instance.PublishGameEvent(new GameEvent
            {
                Type = GameEventType.HealthGained,
                EventId = "add-hp",
                Source = "OCR",
                OldValue = previous,
                NewValue = current,
                Data = new Dictionary<string, object>
                {
                    ["change"] = change,
                    [EventProcessor.SkipProcessorDataKey] = true
                }
            });
        }

        if (current <= 0 && previous > 0)
        {
            await _eventService.TriggerEventAsync("dead");
            EventBus.Instance.PublishGameEvent(new GameEvent
            {
                Type = GameEventType.Death,
                EventId = "dead",
                Source = "OCR",
                Data = new Dictionary<string, object>
                {
                    [EventProcessor.SkipProcessorDataKey] = true
                }
            });
        }
    }

    public async Task EmitArmorChangedAsync(int previous, int current, int cooldownMs)
    {
        var change = current - previous;
        if (change == 0)
        {
            return;
        }

        if (!TryPassCooldown("armor", cooldownMs))
        {
            return;
        }

        if (change < 0)
        {
            var absChange = Math.Abs(change);
            await _eventService.TriggerEventAsync("lost-ahp", multiplier: absChange / 10.0);

            EventBus.Instance.PublishGameEvent(new GameEvent
            {
                Type = GameEventType.ArmorLost,
                EventId = "lost-ahp",
                Source = "OCR",
                OldValue = previous,
                NewValue = current,
                Data = new Dictionary<string, object>
                {
                    ["change"] = absChange,
                    [EventProcessor.SkipProcessorDataKey] = true
                }
            });

            if (current <= 0 && previous > 0)
            {
                EventBus.Instance.PublishGameEvent(new GameEvent
                {
                    Type = GameEventType.ArmorBroken,
                    EventId = "armor-broken",
                    Source = "OCR",
                    Data = new Dictionary<string, object>
                    {
                        [EventProcessor.SkipProcessorDataKey] = true
                    }
                });
            }
        }
        else
        {
            await _eventService.TriggerEventAsync("add-ahp", multiplier: change / 10.0);

            EventBus.Instance.PublishGameEvent(new GameEvent
            {
                Type = GameEventType.ArmorGained,
                EventId = "add-ahp",
                Source = "OCR",
                OldValue = previous,
                NewValue = current,
                Data = new Dictionary<string, object>
                {
                    ["change"] = change,
                    [EventProcessor.SkipProcessorDataKey] = true
                }
            });
        }
    }

    public void EmitRoundState(string roundState)
    {
        if (string.Equals(roundState, "new_round", StringComparison.OrdinalIgnoreCase))
        {
            EventBus.Instance.PublishGameEvent(new GameEvent
            {
                Type = GameEventType.NewRound,
                EventId = "new-round",
                Source = "OCR.Model",
                Data = new Dictionary<string, object>
                {
                    [EventProcessor.SkipProcessorDataKey] = true
                }
            });
            return;
        }

        if (string.Equals(roundState, "game_over", StringComparison.OrdinalIgnoreCase))
        {
            EventBus.Instance.PublishGameEvent(new GameEvent
            {
                Type = GameEventType.GameOver,
                EventId = "game-over",
                Source = "OCR.Model",
                Data = new Dictionary<string, object>
                {
                    [EventProcessor.SkipProcessorDataKey] = true
                }
            });
        }
    }

    private bool TryPassCooldown(string key, int cooldownMs)
    {
        if (cooldownMs <= 0)
        {
            return true;
        }

        var now = DateTime.UtcNow;
        if (_cooldowns.TryGetValue(key, out var last) &&
            (now - last).TotalMilliseconds < cooldownMs)
        {
            return false;
        }

        _cooldowns[key] = now;
        return true;
    }
}

