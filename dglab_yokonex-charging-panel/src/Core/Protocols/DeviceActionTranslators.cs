using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.DGLab;
using ChargingPanel.Core.Devices.Yokonex;

namespace ChargingPanel.Core.Protocols;

/// <summary>
/// 设备动作请求
/// </summary>
public sealed class DeviceActionRequest
{
    public string EventId { get; init; } = string.Empty;
    public string ActionType { get; init; } = "set";
    public int Value { get; init; }
    public int RawValue { get; init; }
    public int DurationMs { get; init; }
    public string? WaveformData { get; init; }
    public IReadOnlyList<Channel> Channels { get; init; } = Array.Empty<Channel>();
}

/// <summary>
/// 动作翻译器接口（将抽象动作翻译为设备协议行为）
/// </summary>
public interface IDeviceActionTranslator
{
    int Priority { get; }
    bool CanHandle(IDevice device, string actionType);
    Task ExecuteAsync(IDevice device, DeviceActionRequest request, CancellationToken ct = default);
}

/// <summary>
/// 动作翻译器注册中心
/// </summary>
public sealed class DeviceActionTranslatorRegistry
{
    private readonly IReadOnlyList<IDeviceActionTranslator> _translators;

    public DeviceActionTranslatorRegistry()
    {
        _translators = new IDeviceActionTranslator[]
        {
            new YokonexCommandActionTranslator(),
            new StopAllActionTranslator(),
            new YokonexSmartLockActionTranslator(),
            new YokonexEnemaActionTranslator(),
            new YokonexToyMotorActionTranslator(),
            new YokonexToyActionTranslator(),
            new YokonexEmsActionTranslator(),
            new GenericActionTranslator()
        }
        .OrderByDescending(t => t.Priority)
        .ToArray();
    }

    public async Task ExecuteAsync(IDevice device, DeviceActionRequest request, CancellationToken ct = default)
    {
        var actionType = NormalizeAction(request.ActionType);
        var normalizedRequest = new DeviceActionRequest
        {
            EventId = request.EventId,
            ActionType = actionType,
            Value = request.Value,
            RawValue = request.RawValue,
            DurationMs = request.DurationMs,
            WaveformData = request.WaveformData,
            Channels = request.Channels
        };
        foreach (var translator in _translators)
        {
            if (!translator.CanHandle(device, actionType))
            {
                continue;
            }

            await translator.ExecuteAsync(device, normalizedRequest, ct);
            return;
        }

        throw new InvalidOperationException($"未找到可执行动作翻译器: {actionType}");
    }

    private static string NormalizeAction(string? actionType)
    {
        return ActionSemantic.Normalize(actionType);
    }
}

internal sealed class YokonexCommandActionTranslator : IDeviceActionTranslator
{
    public int Priority => 110;

    public bool CanHandle(IDevice device, string actionType)
    {
        return (actionType.Equals("game_cmd", StringComparison.OrdinalIgnoreCase) ||
                actionType.Equals("stop_all", StringComparison.OrdinalIgnoreCase)) &&
               device is IYokonexCommandDevice;
    }

    public async Task ExecuteAsync(IDevice device, DeviceActionRequest request, CancellationToken ct = default)
    {
        if (device is not IYokonexCommandDevice commandDevice)
        {
            throw new InvalidOperationException("设备不支持 IM 指令下发");
        }

        if (request.ActionType.Equals("stop_all", StringComparison.OrdinalIgnoreCase))
        {
            await commandDevice.SendGameCommandAsync("_stop_all");
            return;
        }

        var commandId = string.IsNullOrWhiteSpace(request.WaveformData)
            ? request.EventId
            : request.WaveformData;
        if (string.IsNullOrWhiteSpace(commandId))
        {
            throw new InvalidOperationException("缺少 game_cmd 指令 ID（需提供 WaveformData 或 EventId）");
        }

        await commandDevice.SendGameCommandAsync(commandId.Trim());
    }
}

internal sealed class StopAllActionTranslator : IDeviceActionTranslator
{
    public int Priority => 102;

    public bool CanHandle(IDevice device, string actionType)
    {
        return actionType.Equals("stop_all", StringComparison.OrdinalIgnoreCase);
    }

    public async Task ExecuteAsync(IDevice device, DeviceActionRequest request, CancellationToken ct = default)
    {
        if (device is IYokonexToyDevice toyDevice)
        {
            await toyDevice.StopAllMotorsAsync();
            return;
        }

        if (device is YokonexEnemaBluetoothAdapter enemaBluetoothDevice)
        {
            await enemaBluetoothDevice.PauseAllAsync();
            return;
        }

        if (device is IYokonexEnemaDevice enemaDevice)
        {
            await enemaDevice.StopInjectionAsync();
            return;
        }

        foreach (var channel in new[] { Channel.A, Channel.B })
        {
            ct.ThrowIfCancellationRequested();
            await device.SetStrengthAsync(channel, 0, StrengthMode.Set);
            await device.ClearWaveformQueueAsync(channel);
        }
    }
}

internal sealed class GenericActionTranslator : IDeviceActionTranslator
{
    private static readonly HashSet<string> SupportedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "set",
        "increase",
        "decrease",
        "wave",
        "pulse",
        "waveform",
        "clear"
    };

    public int Priority => 10;

    public bool CanHandle(IDevice device, string actionType) => SupportedActions.Contains(actionType);

    public async Task ExecuteAsync(IDevice device, DeviceActionRequest request, CancellationToken ct = default)
    {
        switch (NormalizeAction(request.ActionType))
        {
            case "set":
            case "increase":
            case "decrease":
            {
                var mode = request.ActionType.ToLowerInvariant() switch
                {
                    "increase" => StrengthMode.Increase,
                    "decrease" => StrengthMode.Decrease,
                    _ => StrengthMode.Set
                };

                foreach (var channel in request.Channels)
                {
                    ct.ThrowIfCancellationRequested();
                    await device.SetStrengthAsync(channel, request.Value, mode);
                }
                return;
            }
            case "wave":
            case "pulse":
            case "waveform":
            {
                var waveform = WaveformFactory.Build(request);
                foreach (var channel in request.Channels)
                {
                    ct.ThrowIfCancellationRequested();
                    await device.SendWaveformAsync(channel, waveform);
                }
                return;
            }
            case "clear":
            {
                foreach (var channel in request.Channels)
                {
                    ct.ThrowIfCancellationRequested();
                    await device.ClearWaveformQueueAsync(channel);
                }
                return;
            }
            default:
                throw new InvalidOperationException($"不支持的通用动作: {request.ActionType}");
        }
    }

    private static string NormalizeAction(string? actionType)
    {
        return ActionSemantic.Normalize(actionType);
    }
}

internal sealed class YokonexEmsActionTranslator : IDeviceActionTranslator
{
    public int Priority => 100;

    public bool CanHandle(IDevice device, string actionType)
    {
        return (actionType.Equals("mode", StringComparison.OrdinalIgnoreCase) ||
                actionType.Equals("custom_waveform", StringComparison.OrdinalIgnoreCase)) &&
               (device is YokonexEmsBluetoothAdapter || device is IYokonexEmsDevice);
    }

    public async Task ExecuteAsync(IDevice device, DeviceActionRequest request, CancellationToken ct = default)
    {
        if (request.ActionType.Equals("custom_waveform", StringComparison.OrdinalIgnoreCase))
        {
            var (frequency, pulseTime) = ParseCustomWaveform(request);
            foreach (var channel in request.Channels)
            {
                ct.ThrowIfCancellationRequested();
                if (device is YokonexEmsBluetoothAdapter emsDevice)
                {
                    await emsDevice.SetCustomWaveformAsync(channel, frequency, pulseTime);
                    continue;
                }

                if (device is IYokonexEmsDevice emsInterface)
                {
                    await emsInterface.SetCustomWaveformAsync(channel, frequency, pulseTime);
                }
            }

            return;
        }

        var mode = ParseMode(request);
        foreach (var channel in request.Channels)
        {
            ct.ThrowIfCancellationRequested();
            if (device is YokonexEmsBluetoothAdapter emsDevice)
            {
                await emsDevice.SetFixedModeAsync(channel, mode);
                continue;
            }

            if (device is IYokonexEmsDevice emsInterface)
            {
                await emsInterface.SetFixedModeAsync(channel, mode);
            }
        }
    }

    private static int ParseMode(DeviceActionRequest request)
    {
        if (int.TryParse(request.WaveformData, out var parsed))
        {
            return Math.Clamp(parsed, 1, 16);
        }

        var fromValue = request.RawValue > 0 ? request.RawValue : request.Value;
        return Math.Clamp(fromValue, 1, 16);
    }

    private static (int frequency, int pulseTime) ParseCustomWaveform(DeviceActionRequest request)
    {
        var frequency = 50;
        var pulseTime = 20;

        if (!string.IsNullOrWhiteSpace(request.WaveformData))
        {
            var parts = request.WaveformData
                .Split(new[] { ',', ':', ';', '/', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .ToArray();

            if (parts.Length > 0 && int.TryParse(parts[0], out var parsedFreq))
            {
                frequency = parsedFreq;
            }

            if (parts.Length > 1 && int.TryParse(parts[1], out var parsedPulse))
            {
                pulseTime = parsedPulse;
            }
        }
        else
        {
            if (request.Value > 0)
            {
                frequency = request.Value;
            }

            if (request.DurationMs > 0)
            {
                pulseTime = request.DurationMs;
            }
        }

        return (Math.Clamp(frequency, 1, 100), Math.Clamp(pulseTime, 0, 100));
    }
}

internal sealed class YokonexToyActionTranslator : IDeviceActionTranslator
{
    private static readonly HashSet<string> SupportedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "vibrate_mode",
        "query_device_info"
    };

    public int Priority => 100;

    public bool CanHandle(IDevice device, string actionType)
    {
        return SupportedActions.Contains(actionType) &&
               (device is YokonexToyBluetoothAdapter || device is IYokonexToyDevice);
    }

    public async Task ExecuteAsync(IDevice device, DeviceActionRequest request, CancellationToken ct = default)
    {
        if (request.ActionType.Equals("query_device_info", StringComparison.OrdinalIgnoreCase))
        {
            if (device is YokonexToyBluetoothAdapter toyBluetoothDevice)
            {
                await toyBluetoothDevice.QueryDeviceInfoAsync();
                return;
            }

            if (device is IYokonexToyDevice toyQueryDevice)
            {
                await toyQueryDevice.QueryDeviceInfoAsync();
                return;
            }
        }

        var mode = ParseMode(request);
        if (device is YokonexToyBluetoothAdapter toyDevice)
        {
            await toyDevice.SetFixedModeAsync(YokonexToyProtocol.MotorABC, mode);
            return;
        }

        if (device is IYokonexToyDevice toyInterface)
        {
            await toyInterface.SetFixedModeAsync(mode);
        }
    }

    private static int ParseMode(DeviceActionRequest request)
    {
        if (int.TryParse(request.WaveformData, out var parsed))
        {
            return Math.Max(parsed, 0);
        }

        return Math.Max(request.RawValue > 0 ? request.RawValue : request.Value, 0);
    }
}

internal sealed class YokonexToyMotorActionTranslator : IDeviceActionTranslator
{
    private static readonly HashSet<string> SupportedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "toy_motor_1",
        "toy_motor_2",
        "toy_motor_3",
        "toy_motor_all",
        "toy_stop_all"
    };

    public int Priority => 101;

    public bool CanHandle(IDevice device, string actionType)
    {
        return SupportedActions.Contains(actionType) &&
               (device is YokonexToyBluetoothAdapter || device is IYokonexToyDevice);
    }

    public async Task ExecuteAsync(IDevice device, DeviceActionRequest request, CancellationToken ct = default)
    {
        var action = request.ActionType.ToLowerInvariant();
        var strength = ToToyStrength(request.Value);

        if (device is YokonexToyBluetoothAdapter toyDevice)
        {
            switch (action)
            {
                case "toy_motor_1":
                    await toyDevice.SetMotorStrengthAsync(1, strength);
                    return;
                case "toy_motor_2":
                    await toyDevice.SetMotorStrengthAsync(2, strength);
                    return;
                case "toy_motor_3":
                    await toyDevice.SetMotorStrengthAsync(3, strength);
                    return;
                case "toy_motor_all":
                    await toyDevice.SetAllMotorsAsync(strength, strength, strength);
                    return;
                case "toy_stop_all":
                    await toyDevice.StopAllMotorsAsync();
                    return;
            }
        }

        if (device is IYokonexToyDevice toyInterface)
        {
            switch (action)
            {
                case "toy_motor_1":
                    await toyInterface.SetMotorStrengthAsync(1, strength);
                    return;
                case "toy_motor_2":
                    await toyInterface.SetMotorStrengthAsync(2, strength);
                    return;
                case "toy_motor_3":
                    await toyInterface.SetMotorStrengthAsync(3, strength);
                    return;
                case "toy_motor_all":
                    await toyInterface.SetAllMotorsAsync(strength, strength, strength);
                    return;
                case "toy_stop_all":
                    await toyInterface.StopAllMotorsAsync();
                    return;
            }
        }

        throw new InvalidOperationException($"玩具马达动作无法执行: {action}");
    }

    private static int ToToyStrength(int value)
    {
        var clamped = Math.Clamp(value, 0, 100);
        return clamped <= 20 ? clamped : (int)Math.Round(clamped * 0.2, MidpointRounding.AwayFromZero);
    }
}

internal sealed class YokonexSmartLockActionTranslator : IDeviceActionTranslator
{
    private static readonly HashSet<string> SupportedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "smart_lock",
        "smart_unlock",
        "smart_temp_unlock",
        "smart_query_state"
    };

    public int Priority => 105;

    public bool CanHandle(IDevice device, string actionType)
    {
        return SupportedActions.Contains(actionType) && device is IYokonexSmartLockDevice;
    }

    public async Task ExecuteAsync(IDevice device, DeviceActionRequest request, CancellationToken ct = default)
    {
        if (device is not IYokonexSmartLockDevice smartLock)
        {
            throw new InvalidOperationException("设备不支持智能锁动作");
        }

        switch (request.ActionType.ToLowerInvariant())
        {
            case "smart_lock":
                await smartLock.LockAsync();
                return;
            case "smart_unlock":
                await smartLock.UnlockAsync();
                return;
            case "smart_query_state":
                await smartLock.QueryStateAsync();
                return;
            case "smart_temp_unlock":
            {
                var seconds = ResolveTemporaryUnlockSeconds(request);
                await smartLock.TemporaryUnlockAsync(seconds);
                return;
            }
            default:
                throw new InvalidOperationException($"不支持的智能锁动作: {request.ActionType}");
        }
    }

    private static int ResolveTemporaryUnlockSeconds(DeviceActionRequest request)
    {
        if (request.Value > 0)
        {
            return Math.Clamp(request.Value, 1, 3600);
        }

        if (request.DurationMs > 0)
        {
            return Math.Clamp((int)Math.Ceiling(request.DurationMs / 1000.0), 1, 3600);
        }

        return 10;
    }
}

internal sealed class YokonexEnemaActionTranslator : IDeviceActionTranslator
{
    private static readonly HashSet<string> SupportedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "peristaltic_start",
        "peristaltic_stop",
        "water_start",
        "water_stop",
        "pause_all",
        "query_status",
        "query_battery"
    };

    public int Priority => 100;

    public bool CanHandle(IDevice device, string actionType)
    {
        return SupportedActions.Contains(actionType) &&
               (device is YokonexEnemaBluetoothAdapter || device is IYokonexEnemaDevice || device is IYokonexCommandDevice);
    }

    public async Task ExecuteAsync(IDevice device, DeviceActionRequest request, CancellationToken ct = default)
    {
        var action = request.ActionType.ToLowerInvariant();
        var durationSeconds = ResolveDurationSeconds(request);

        if (device is YokonexEnemaBluetoothAdapter enemaDevice)
        {
            switch (action)
            {
                case "peristaltic_start":
                    await enemaDevice.SetPeristalticPumpAsync(PeristalticPumpState.Forward, durationSeconds);
                    return;
                case "peristaltic_stop":
                    await enemaDevice.SetPeristalticPumpAsync(PeristalticPumpState.Stop, 0);
                    return;
                case "water_start":
                    await enemaDevice.SetWaterPumpAsync(WaterPumpState.Forward, durationSeconds);
                    return;
                case "water_stop":
                    await enemaDevice.SetWaterPumpAsync(WaterPumpState.Stop, 0);
                    return;
                case "pause_all":
                    await enemaDevice.PauseAllAsync();
                    return;
                case "query_status":
                    await enemaDevice.QueryStatusAsync();
                    return;
                case "query_battery":
                    await enemaDevice.QueryBatteryAsync();
                    return;
            }
        }

        if (device is IYokonexEnemaDevice enemaInterface)
        {
            switch (action)
            {
                case "peristaltic_start":
                    await enemaInterface.StartInjectionAsync();
                    return;
                case "peristaltic_stop":
                case "water_stop":
                case "pause_all":
                    await enemaInterface.StopInjectionAsync();
                    return;
                case "water_start":
                    await enemaInterface.StartInjectionAsync();
                    return;
                case "query_status":
                case "query_battery":
                    return;
            }
        }

        if (device is IYokonexCommandDevice commandDevice)
        {
            var commandId = action switch
            {
                "peristaltic_start" => "enema_peristaltic_start",
                "peristaltic_stop" => "enema_peristaltic_stop",
                "water_start" => "enema_water_start",
                "water_stop" => "enema_water_stop",
                "pause_all" => "enema_pause_all",
                "query_status" => "enema_query_status",
                "query_battery" => "enema_query_battery",
                _ => action
            };
            await commandDevice.SendGameCommandAsync(commandId);
            return;
        }

        throw new InvalidOperationException($"灌注动作无法执行: {action}");
    }

    private static int ResolveDurationSeconds(DeviceActionRequest request)
    {
        if (request.DurationMs > 0)
        {
            return Math.Clamp((int)Math.Ceiling(request.DurationMs / 1000.0), 1, 300);
        }

        if (request.RawValue > 0)
        {
            return Math.Clamp(request.RawValue, 1, 300);
        }

        if (request.Value > 0)
        {
            return Math.Clamp(request.Value, 1, 300);
        }

        return 10;
    }
}

internal static class WaveformFactory
{
    private static readonly Regex HexPattern = new("^[0-9A-Fa-f]+$", RegexOptions.Compiled);

    public static WaveformData Build(DeviceActionRequest request)
    {
        var baseDuration = request.DurationMs > 0 ? request.DurationMs : 1000;
        var strength = request.Value;
        var type = string.IsNullOrWhiteSpace(request.WaveformData)
            ? request.ActionType
            : request.WaveformData;

        // 未指定具体波形时，按全局预设池随机抽取（若仅一条则视为单一波形）
        if (string.IsNullOrWhiteSpace(request.WaveformData) &&
            request.ActionType.Equals("waveform", StringComparison.OrdinalIgnoreCase))
        {
            type = "playlist:auto";
        }

        if (TryBuildFromGlobalPresetPool(type, strength, baseDuration, out var fromPresetPool))
        {
            return fromPresetPool;
        }

        if (TryNormalizeHexWaveform(type, out var normalizedHex))
        {
            return new WaveformData
            {
                HexData = normalizedHex,
                Strength = strength,
                Duration = baseDuration
            };
        }

        return type?.Trim().ToLowerInvariant() switch
        {
            "breath" => new WaveformData { Frequency = 50, Strength = strength, Duration = baseDuration },
            "tide" => new WaveformData { Frequency = 30, Strength = strength, Duration = baseDuration },
            "heartbeat" => new WaveformData { Frequency = 80, Strength = strength, Duration = baseDuration },
            "rhythm" => new WaveformData { Frequency = 100, Strength = strength, Duration = baseDuration },
            "crescendo" => new WaveformData { Frequency = 60, Strength = strength, Duration = baseDuration },
            "decrescendo" => new WaveformData { Frequency = 60, Strength = strength, Duration = baseDuration },
            "pulse" => new WaveformData { Frequency = 150, Strength = strength, Duration = baseDuration },
            "random" => new WaveformData
            {
                Frequency = Random.Shared.Next(30, 150),
                Strength = strength,
                Duration = baseDuration
            },
            _ => new WaveformData { Frequency = 100, Strength = strength, Duration = baseDuration }
        };
    }

    private static bool TryBuildFromGlobalPresetPool(
        string? type,
        int fallbackStrength,
        int fallbackDuration,
        out WaveformData waveform)
    {
        waveform = new WaveformData { Frequency = 100, Strength = fallbackStrength, Duration = fallbackDuration };

        var token = type?.Trim().ToLowerInvariant();
        if (token is not ("playlist:auto" or "playlist:random" or "global_random"))
        {
            return false;
        }

        try
        {
            var candidates = Database.Instance.GetAllWaveformPresets();
            if (candidates.Count == 0)
            {
                return false;
            }

            var selected = candidates.Count == 1
                ? candidates[0]
                : candidates[Random.Shared.Next(candidates.Count)];

            waveform = WaveformPresetExchangeService.BuildWaveformData(selected);
            if (fallbackStrength > 0)
            {
                waveform.Strength = fallbackStrength;
            }

            if (selected.Duration <= 0)
            {
                waveform.Duration = fallbackDuration;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryNormalizeHexWaveform(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var compact = raw.Trim();
        var parts = compact
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

        if (parts.Length > 1)
        {
            if (parts.Any(p => p.Length != 16 || !HexPattern.IsMatch(p)))
            {
                return false;
            }

            normalized = string.Join(",", parts.Select(p => p.ToUpperInvariant()));
            return true;
        }

        var single = parts.Length == 1 ? parts[0] : compact;
        if (!HexPattern.IsMatch(single))
        {
            return false;
        }

        if (single.Length == 16)
        {
            normalized = single.ToUpperInvariant();
            return true;
        }

        // 兼容连续 HEX：按 16 字符分段，交给核心波形解析器。
        if (single.Length > 16 && single.Length % 16 == 0)
        {
            var segments = new List<string>();
            for (var i = 0; i < single.Length; i += 16)
            {
                segments.Add(single.Substring(i, 16).ToUpperInvariant());
            }
            normalized = string.Join(",", segments);
            return true;
        }

        return false;
    }
}
