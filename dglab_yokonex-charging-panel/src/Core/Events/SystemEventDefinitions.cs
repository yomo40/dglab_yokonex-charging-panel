using System;
using System.Collections.Generic;

namespace ChargingPanel.Core.Events;

internal sealed record SystemEventDefinition(
    string EventId,
    string Name,
    string Description,
    string Channel,
    string Action,
    int Value,
    int Duration,
    string TriggerType,
    int MinChange,
    int MaxChange);

internal static class SystemEventDefinitions
{
    public static readonly IReadOnlyCollection<string> DeprecatedDefaultEventIds = new[]
    {
        "character-debuff",
        "knocked"
    };

    public static readonly IReadOnlyList<SystemEventDefinition> Defaults = new[]
    {
        Create("lost-ahp", "护甲损失", "护甲损失时触发电击反馈", "B", "increase", 10, 500),
        Create("lost-hp", "血量损失", "血量损失时触发电击反馈", "A", "increase", 15, 500),
        Create("add-ahp", "护甲恢复", "护甲恢复时轻微反馈", "B", "set", 5, 300),
        Create("add-hp", "血量恢复", "血量恢复时轻微反馈", "A", "set", 5, 300),
        Create("dead", "死亡", "角色死亡时强烈反馈", "AB", "set", 100, 2000),
        Create("respawn", "重生", "角色重生时的反馈", "A", "pulse", 30, 500),
        Create("new-round", "新回合", "新回合/关卡开始时的反馈", "AB", "pulse", 20, 300),
        Create("game-over", "游戏结束", "游戏结束时的反馈", "AB", "set", 50, 1000)
    };

    public static (string TriggerType, int MinChange, int MaxChange) ResolveTriggerProfile(string? eventId)
    {
        return eventId?.Trim().ToLowerInvariant() switch
        {
            "lost-hp" => ("hp-decrease", 1, 100),
            "add-hp" => ("hp-increase", 1, 100),
            "lost-ahp" => ("armor-decrease", 1, 100),
            "add-ahp" => ("armor-increase", 1, 100),
            // 倒地事件已并入死亡事件；为兼容旧数据保留映射
            "dead" or "knocked" => ("death", 0, 0),
            "respawn" => ("revive", 0, 0),
            "new-round" => ("new-round", 0, 0),
            "game-over" => ("game-over", 0, 0),
            _ => ("hp-decrease", 1, 100)
        };
    }

    private static SystemEventDefinition Create(
        string eventId,
        string name,
        string description,
        string channel,
        string action,
        int value,
        int duration)
    {
        var trigger = ResolveTriggerProfile(eventId);
        return new SystemEventDefinition(
            eventId,
            name,
            description,
            channel,
            action,
            value,
            duration,
            trigger.TriggerType,
            trigger.MinChange,
            trigger.MaxChange);
    }
}
