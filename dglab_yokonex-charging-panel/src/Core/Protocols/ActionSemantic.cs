using System;
using System.Collections.Generic;

namespace ChargingPanel.Core.Protocols;

/// <summary>
/// 动作语义标准化词表。
/// 将上层规则/脚本的动作别名归一到统一语义，避免设备协议翻译分歧。
/// </summary>
public static class ActionSemantic
{
    private static readonly IReadOnlyDictionary<string, string> AliasMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 强度基础动作
            ["set"] = "set",
            ["assign"] = "set",
            ["fixed"] = "set",

            ["increase"] = "increase",
            ["inc"] = "increase",
            ["plus"] = "increase",
            ["up"] = "increase",
            ["boost"] = "increase",

            ["decrease"] = "decrease",
            ["dec"] = "decrease",
            ["minus"] = "decrease",
            ["down"] = "decrease",
            ["reduce"] = "decrease",

            // 波形与队列
            ["wave"] = "wave",
            ["waveform"] = "wave",
            ["pulse"] = "pulse",
            ["clear"] = "clear",
            ["stop"] = "clear",
            ["clear_queue"] = "clear",

            // 设备特定扩展
            ["mode"] = "mode",
            ["custom_waveform"] = "custom_waveform",
            ["ems_custom_waveform"] = "custom_waveform",
            ["vibrate_mode"] = "vibrate_mode",
            ["toy_motor_1"] = "toy_motor_1",
            ["toy_motor_2"] = "toy_motor_2",
            ["toy_motor_3"] = "toy_motor_3",
            ["toy_motor_all"] = "toy_motor_all",
            ["toy_stop_all"] = "toy_stop_all",
            ["motor_1"] = "toy_motor_1",
            ["motor_2"] = "toy_motor_2",
            ["motor_3"] = "toy_motor_3",
            ["motor_all"] = "toy_motor_all",
            ["motor_stop"] = "toy_stop_all",
            ["game_cmd"] = "game_cmd",
            ["im_cmd"] = "game_cmd",
            ["command"] = "game_cmd",
            ["stop_all"] = "stop_all",
            ["_stop_all"] = "stop_all",
            ["peristaltic_start"] = "peristaltic_start",
            ["peristaltic_stop"] = "peristaltic_stop",
            ["water_start"] = "water_start",
            ["water_stop"] = "water_stop",
            ["pause_all"] = "pause_all",
            ["query_status"] = "query_status",
            ["query_battery"] = "query_battery",
            ["query_device_info"] = "query_device_info",
            ["toy_query_info"] = "query_device_info",
            ["query_info"] = "query_device_info",
            ["smart_lock"] = "smart_lock",
            ["smart_unlock"] = "smart_unlock",
            ["smart_temp_unlock"] = "smart_temp_unlock",
            ["smart_query_state"] = "smart_query_state",
            ["lock"] = "smart_lock",
            ["unlock"] = "smart_unlock",
            ["temporary_unlock"] = "smart_temp_unlock",
            ["query_lock_state"] = "smart_query_state"
        };

    public static string Normalize(string? actionType)
    {
        if (string.IsNullOrWhiteSpace(actionType))
        {
            return "set";
        }

        var key = actionType.Trim();
        return AliasMap.TryGetValue(key, out var canonical)
            ? canonical
            : key.ToLowerInvariant();
    }
}
