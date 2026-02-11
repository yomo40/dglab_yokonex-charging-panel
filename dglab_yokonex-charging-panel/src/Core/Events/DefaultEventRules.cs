using System;
using System.Collections.Generic;
using System.Linq;
using ChargingPanel.Core.Data;

namespace ChargingPanel.Core.Events;

/// <summary>
/// 默认规则目录
/// 作为数据库规则缺失时的兜底规则来源
/// </summary>
internal static class DefaultEventRules
{
    private static readonly IReadOnlyDictionary<string, EventRecord> Rules =
        SystemEventDefinitions.Defaults
            .ToDictionary(def => def.EventId, Create, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<EventRecord> All => Rules.Values.Select(Clone);

    public static bool TryGet(string eventId, out EventRecord? rule)
    {
        if (Rules.TryGetValue(eventId, out var existing))
        {
            rule = Clone(existing);
            return true;
        }

        rule = null;
        return false;
    }

    private static EventRecord Create(SystemEventDefinition definition)
    {
        return new EventRecord
        {
            Id = $"evt_{definition.EventId}",
            EventId = definition.EventId,
            Name = definition.Name,
            Description = definition.Description,
            Category = "system",
            Channel = definition.Channel,
            Action = definition.Action,
            ActionType = definition.Action,
            Value = definition.Value,
            Strength = definition.Value,
            Duration = definition.Duration,
            TriggerType = definition.TriggerType,
            MinChange = definition.MinChange,
            MaxChange = definition.MaxChange,
            Enabled = true,
            Priority = 10,
            TargetDeviceType = "All",
            CreatedAt = string.Empty,
            UpdatedAt = string.Empty
        };
    }

    private static EventRecord Clone(EventRecord source)
    {
        return new EventRecord
        {
            Id = source.Id,
            EventId = source.EventId,
            Name = source.Name,
            Description = source.Description,
            Category = source.Category,
            Channel = source.Channel,
            Action = source.Action,
            Value = source.Value,
            Duration = source.Duration,
            WaveformData = source.WaveformData,
            Enabled = source.Enabled,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            TriggerType = source.TriggerType,
            MinChange = source.MinChange,
            MaxChange = source.MaxChange,
            ActionType = source.ActionType,
            Strength = source.Strength,
            Priority = source.Priority,
            TargetDeviceType = source.TargetDeviceType
        };
    }
}
