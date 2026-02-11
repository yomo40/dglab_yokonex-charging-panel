using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ChargingPanel.Core.Data;

namespace ChargingPanel.Core.Events;

/// <summary>
/// 统一规则触发条件判断：
/// 1) triggerType/minChange/maxChange
/// 2) conditionField + conditionOperator + conditionValue(+conditionMaxValue)
/// </summary>
internal static class EventRuleConditionEvaluator
{
    public static bool IsMatch(EventRecord rule, GameEvent evt)
    {
        if (!MatchesTriggerType(rule, evt))
        {
            return false;
        }

        return MatchesConditionOperator(rule, evt);
    }

    private static bool MatchesTriggerType(EventRecord rule, GameEvent evt)
    {
        var trigger = Normalize(rule.TriggerType);
        if (string.IsNullOrWhiteSpace(trigger) || trigger == "always")
        {
            return true;
        }

        var min = rule.MinChange > 0 ? rule.MinChange : 1;
        var max = rule.MaxChange > 0 ? rule.MaxChange : double.MaxValue;
        var delta = ResolveDelta(evt);
        var absDelta = Math.Abs(delta);
        var hasNumber = HasComparableNumericPayload(evt);
        var eventId = Normalize(evt.EventId);

        switch (trigger)
        {
            case "hp-decrease":
            case "decrease":
            case "health-decrease":
            case "health-lost":
                return !hasNumber || InRange(Math.Max(0, -delta), min, max);

            case "hp-increase":
            case "increase":
            case "health-increase":
            case "health-gained":
                return !hasNumber || InRange(Math.Max(0, delta), min, max);

            case "armor-decrease":
                return !hasNumber || InRange(Math.Max(0, -delta), min, max);

            case "armor-increase":
                return !hasNumber || InRange(Math.Max(0, delta), min, max);

            case "value-change":
            case "change":
                return !hasNumber || InRange(absDelta, min, max);

            case "value-increase":
                return !hasNumber || InRange(Math.Max(0, delta), min, max);

            case "value-decrease":
                return !hasNumber || InRange(Math.Max(0, -delta), min, max);

            case "death":
                return evt.Type is GameEventType.Death or GameEventType.Knocked
                       || eventId is "dead" or "death" or "knocked";

            case "revive":
                return evt.Type == GameEventType.Respawn
                       || eventId is "respawn" or "revive";

            case "new-round":
                return evt.Type == GameEventType.NewRound
                       || eventId is "new-round" or "new_round";

            case "game-over":
                return evt.Type == GameEventType.GameOver
                       || eventId is "game-over" or "game_over";

            default:
                // 未识别 triggerType 不拦截，保持兼容
                return true;
        }
    }

    private static bool MatchesConditionOperator(EventRecord rule, GameEvent evt)
    {
        var op = Normalize(rule.ConditionOperator);
        if (string.IsNullOrWhiteSpace(op))
        {
            return true;
        }

        var field = Normalize(rule.ConditionField);
        if (string.IsNullOrWhiteSpace(field))
        {
            return true;
        }

        if (!TryResolveFieldValue(evt, field, out var actual))
        {
            return false;
        }

        var expected = rule.ConditionValue ?? rule.MinChange;
        var expectedMax = rule.ConditionMaxValue;
        if (expectedMax == null && rule.MaxChange > 0)
        {
            expectedMax = rule.MaxChange;
        }

        return EvaluateOperator(actual, op, expected, expectedMax);
    }

    private static bool EvaluateOperator(double actual, string op, double expected, double? expectedMax)
    {
        return op switch
        {
            ">" or "gt" => actual > expected,
            ">=" or "gte" => actual >= expected,
            "<" or "lt" => actual < expected,
            "<=" or "lte" => actual <= expected,
            "=" or "==" or "eq" => NearlyEquals(actual, expected),
            "!=" or "<>" or "neq" => !NearlyEquals(actual, expected),
            "between" when expectedMax.HasValue => actual >= expected && actual <= expectedMax.Value,
            "outside" when expectedMax.HasValue => actual < expected || actual > expectedMax.Value,
            _ => true
        };
    }

    private static bool TryResolveFieldValue(GameEvent evt, string field, out double value)
    {
        switch (field)
        {
            case "old":
            case "oldvalue":
                value = ResolveOldValue(evt);
                return true;

            case "new":
            case "newvalue":
            case "value":
                value = ResolveNewValue(evt);
                return true;

            case "delta":
                value = ResolveDelta(evt);
                return true;

            case "absdelta":
            case "change":
                value = Math.Abs(ResolveDelta(evt));
                return true;
        }

        if (field.StartsWith("data.", StringComparison.OrdinalIgnoreCase))
        {
            var key = field["data.".Length..];
            return TryGetDataNumber(evt, key, out value);
        }

        return TryGetDataNumber(evt, field, out value);
    }

    private static double ResolveOldValue(GameEvent evt)
    {
        return TryGetDataNumber(evt, "oldValue", out var oldValue) ? oldValue : evt.OldValue;
    }

    private static double ResolveNewValue(GameEvent evt)
    {
        return TryGetDataNumber(evt, "newValue", out var newValue) ? newValue : evt.NewValue;
    }

    private static double ResolveDelta(GameEvent evt)
    {
        if (TryGetDataNumber(evt, "delta", out var delta))
        {
            return delta;
        }

        if (TryGetDataNumber(evt, "change", out var change))
        {
            return change;
        }

        return ResolveNewValue(evt) - ResolveOldValue(evt);
    }

    private static bool HasComparableNumericPayload(GameEvent evt)
    {
        if (evt.OldValue != 0 || evt.NewValue != 0)
        {
            return true;
        }

        return TryGetDataNumber(evt, "oldValue", out _)
               || TryGetDataNumber(evt, "newValue", out _)
               || TryGetDataNumber(evt, "delta", out _)
               || TryGetDataNumber(evt, "change", out _);
    }

    private static bool TryGetDataNumber(GameEvent evt, string key, out double number)
    {
        number = 0;
        if (evt.Data == null || evt.Data.Count == 0)
        {
            return false;
        }

        if (!TryGetDataValue(evt.Data, key, out var raw))
        {
            return false;
        }

        switch (raw)
        {
            case byte b:
                number = b;
                return true;
            case short s:
                number = s;
                return true;
            case int i:
                number = i;
                return true;
            case long l:
                number = l;
                return true;
            case float f:
                number = f;
                return true;
            case double d:
                number = d;
                return true;
            case decimal m:
                number = (double)m;
                return true;
            case string text when double.TryParse(text, out var parsed):
                number = parsed;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var e):
                number = e;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.String &&
                                          double.TryParse(element.GetString(), out var svalue):
                number = svalue;
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetDataValue(
        IReadOnlyDictionary<string, object> data,
        string key,
        out object? value)
    {
        if (data.TryGetValue(key, out value))
        {
            return true;
        }

        var match = data.FirstOrDefault(pair =>
            string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(match.Key))
        {
            value = match.Value;
            return true;
        }

        value = null;
        return false;
    }

    private static string Normalize(string? value)
    {
        return value?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static bool InRange(double value, double min, double max)
    {
        return value >= min && value <= max;
    }

    private static bool NearlyEquals(double left, double right)
    {
        return Math.Abs(left - right) < 0.0001d;
    }
}
