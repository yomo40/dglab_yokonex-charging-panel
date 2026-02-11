using System;
using System.Collections.Generic;

namespace ChargingPanel.Core.Events;

/// <summary>
/// Rule-trigger visibility policy.
/// Certain event IDs are runtime telemetry and should not be used as trigger rules.
/// </summary>
public static class EventTriggerPolicy
{
    private static readonly HashSet<string> NonRuleTriggerEventIdsSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "query",
        "new-credit",
        "toy-device-info-changed",
        "pressure-changed",
        "step-count-changed",
        "external-voltage-changed",
        "enema-status-changed",
        "dglab-feedback",
        "device-battery-changed",
        "channel-disconnected",
        "channel-connected",
        "angle-changed"
    };

    public static IReadOnlyCollection<string> NonRuleTriggerEventIds => NonRuleTriggerEventIdsSet;

    public static bool CanBeTriggerRule(string? eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return false;
        }

        return !NonRuleTriggerEventIdsSet.Contains(eventId.Trim());
    }
}
