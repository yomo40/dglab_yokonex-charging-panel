using System.Text.Json;
using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Application.Devices;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Protocols;

namespace ChargingPanel.Core.Automation;

/// <summary>
/// 纯命令映射器：输入"事件动作 + 目标能力描述"，输出零个或多个
/// <see cref="DeviceCommandEnvelope"/>。不触碰 Driver、不引用设备适配器接口、
/// 不构造协议字节——厂商差异一律表达为 CapabilityId + OperationId + 版本化 JsonElement 载荷。
/// </summary>
public sealed class DeviceActionMapperRegistry
{
    private const string IntensityOutput = "intensity.output";
    private const string EmsChannelOutput = "ems.channel.output";
    private const string ToySpeedControl = "toy.speed.control";
    private const string ToyMotorControl = "toy.motor.control";
    private const string WaveformStream = "waveform.stream";
    private const string WaveformClear = "waveform.clear";
    private const string EnemaPumpControl = "enema.pump.control";
    private const string EnemaTelemetry = "enema.telemetry";
    private const string ImCommandSend = "im.command.send";
    private const string ImGlobalStop = "im.global.stop";
    private const string BridgeCommandSend = "bridge.command.send";

    private static readonly HashSet<string> StrengthActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "set", "increase", "decrease", "wave", "pulse", "waveform"
    };

    /// <summary>
    /// 将抽象动作映射为目标实例的命令信封。设备不声明对应能力时返回空集合（调用方如实拒绝），
    /// 绝不为不支持的动作伪造成功。
    /// </summary>
    public IReadOnlyList<DeviceCommandEnvelope> Map(
        DeviceActionRequest request,
        ActiveDeviceView device,
        CommandSource source,
        long safetyEpoch,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var action = ActionSemantic.Normalize(request.ActionType);
        var results = new List<DeviceCommandEnvelope>();

        switch (action)
        {
            case "set":
            case "increase":
            case "decrease":
                MapIntensity(action, request, device, source, safetyEpoch, timeProvider, results);
                break;

            case "wave":
            case "pulse":
            case "waveform":
                MapWave(action, request, device, source, safetyEpoch, timeProvider, results);
                break;

            case "clear":
                MapClear(request, device, source, safetyEpoch, timeProvider, results);
                break;

            case "mode":
                MapEmsMode(request, device, source, safetyEpoch, timeProvider, results);
                break;

            case "custom_waveform":
                MapEmsCustomWaveform(request, device, source, safetyEpoch, timeProvider, results);
                break;

            case "vibrate_mode":
                MapToyMode(request, device, source, safetyEpoch, timeProvider, results);
                break;

            case "toy_motor_1":
            case "toy_motor_2":
            case "toy_motor_3":
            case "toy_motor_all":
            case "toy_stop_all":
                MapToyMotor(action, request, device, source, safetyEpoch, timeProvider, results);
                break;

            case "peristaltic_start":
            case "peristaltic_stop":
            case "water_start":
            case "water_stop":
            case "pause_all":
                MapEnemaPump(action, request, device, source, safetyEpoch, timeProvider, results);
                break;

            case "query_status":
            case "query_battery":
                MapEnemaQuery(action, request, device, source, safetyEpoch, timeProvider, results);
                break;

            case "query_device_info":
                MapToyQuery(request, device, source, safetyEpoch, timeProvider, results);
                break;

            case "game_cmd":
            case "stop_all":
                MapImCommand(action, request, device, source, safetyEpoch, timeProvider, results);
                break;
        }

        return results;
    }

    /// <summary>
    /// 构造"动作结束后的释放/归零"命令信封。仅在动作带时长且属于 set/increase 时返回，
    /// 否则返回 null（无需归零）。
    /// </summary>
    public DeviceCommandEnvelope? MapRelease(
        DeviceActionRequest request,
        ActiveDeviceView device,
        CommandSource source,
        long safetyEpoch,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(device);

        var action = ActionSemantic.Normalize(request.ActionType);
        if (request.DurationMs <= 0 || action is not ("set" or "increase"))
        {
            return null;
        }

        var capability = SelectFirstCapability(
            device,
            IntensityOutput, EmsChannelOutput, ToySpeedControl, ToyMotorControl);
        if (capability is null)
        {
            return null;
        }

        return capability.Id.Value switch
        {
            IntensityOutput => BuildEnvelope(
                device, capability, "set.level",
                BuildJson($"{{\"channelA\":0,\"channelB\":0}}"),
                source, safetyEpoch, timeProvider),
            EmsChannelOutput => BuildEnvelope(
                device, capability, "set.output",
                BuildJson(JsonSerializer.Serialize(new
                {
                    channel = ResolveChannelToken(request.Channels),
                    on = false,
                    intensity = 0,
                    mode = 0,
                    frequency = 0,
                    pulseTime = 0
                })),
                source, safetyEpoch, timeProvider),
            ToySpeedControl => BuildEnvelope(
                device, capability, "set.speed",
                BuildJson("{\"a\":0,\"b\":0,\"c\":0}"),
                source, safetyEpoch, timeProvider),
            ToyMotorControl => BuildEnvelope(
                device, capability, "set.mode",
                BuildJson("{\"motors\":\"none\",\"mode\":0}"),
                source, safetyEpoch, timeProvider),
            _ => null
        };
    }

    private static void MapIntensity(
        string action,
        DeviceActionRequest request,
        ActiveDeviceView device,
        CommandSource source,
        long safetyEpoch,
        TimeProvider timeProvider,
        ICollection<DeviceCommandEnvelope> results)
    {
        var capability = SelectFirstCapability(device, IntensityOutput, EmsChannelOutput, ToySpeedControl, ToyMotorControl);
        if (capability is null)
        {
            return;
        }

        switch (capability.Id.Value)
        {
            case IntensityOutput:
            {
                var isSet = action == "set";
                var operation = isSet ? "set.level" : "adjust.level";
                var sign = action == "decrease" ? -1 : 1;
                var value = ScaleToActuator(request.Value, capability.Actuators[0]) * sign;
                var (hasA, hasB) = ResolveDglabChannels(request.Channels);
                var channelA = hasA ? value : 0;
                var channelB = hasB ? value : 0;
                results.Add(BuildEnvelope(
                    device,
                    capability,
                    operation,
                    BuildJson($"{{\"channelA\":{channelA},\"channelB\":{channelB}}}"),
                    source,
                    safetyEpoch,
                    timeProvider));
                return;
            }
            case EmsChannelOutput:
            {
                var actuator = capability.Actuators[0];
                var intensity = action == "decrease" ? 0 : ScaleToActuator(request.Value, actuator);
                var channel = ResolveChannelToken(request.Channels);
                var payload = JsonSerializer.Serialize(new
                {
                    channel,
                    on = intensity > 0,
                    intensity,
                    mode = 0,
                    frequency = 0,
                    pulseTime = 0
                });
                results.Add(BuildEnvelope(device, capability, "set.output", BuildJson(payload), source, safetyEpoch, timeProvider));
                return;
            }
            case ToySpeedControl:
            {
                var actuator = capability.Actuators[0];
                var value = action == "decrease" ? 0 : ScaleToActuator(request.Value, actuator);
                var (a, b, c) = ResolveToyChannels(request.Channels);
                var payload = JsonSerializer.Serialize(new
                {
                    a = a ? value : 0,
                    b = b ? value : 0,
                    c = c ? value : 0
                });
                results.Add(BuildEnvelope(device, capability, "set.speed", BuildJson(payload), source, safetyEpoch, timeProvider));
                return;
            }
            case ToyMotorControl:
            {
                var mode = ParseMode(request);
                var motors = ResolveMotorToken(request.Channels);
                var payload = JsonSerializer.Serialize(new { motors, mode });
                results.Add(BuildEnvelope(device, capability, "set.mode", BuildJson(payload), source, safetyEpoch, timeProvider));
                return;
            }
        }
    }

    private static void MapWave(
        string action,
        DeviceActionRequest request,
        ActiveDeviceView device,
        CommandSource source,
        long safetyEpoch,
        TimeProvider timeProvider,
        ICollection<DeviceCommandEnvelope> results)
    {
        var capability = SelectFirstCapability(device, WaveformStream, EmsChannelOutput, ToySpeedControl);
        if (capability is null)
        {
            return;
        }

        switch (capability.Id.Value)
        {
            case WaveformStream:
            {
                var frequency = ResolveWaveFrequency(request);
                var strength = ScaleToActuator(request.Value, capability.Actuators[0]);
                var (hasA, hasB) = ResolveDglabChannels(request.Channels);
                var payload = JsonSerializer.Serialize(new
                {
                    channelA = hasA ? BuildChannelFrame(frequency, strength) : null,
                    channelB = hasB ? BuildChannelFrame(frequency, strength) : null
                });
                results.Add(BuildEnvelope(device, capability, "stream.frame", BuildJson(payload), source, safetyEpoch, timeProvider));
                return;
            }
            case EmsChannelOutput:
            {
                var (frequency, pulseTime) = ResolveEmsWaveform(request);
                var channel = ResolveChannelToken(request.Channels);
                var payload = JsonSerializer.Serialize(new
                {
                    channel,
                    on = true,
                    intensity = 0,
                    mode = 0,
                    frequency,
                    pulseTime
                });
                results.Add(BuildEnvelope(device, capability, "set.output", BuildJson(payload), source, safetyEpoch, timeProvider));
                return;
            }
            case ToySpeedControl:
            {
                var value = ScaleToActuator(request.Value, capability.Actuators[0]);
                var (a, b, c) = ResolveToyChannels(request.Channels);
                var payload = JsonSerializer.Serialize(new
                {
                    a = a ? value : 0,
                    b = b ? value : 0,
                    c = c ? value : 0
                });
                results.Add(BuildEnvelope(device, capability, "set.speed", BuildJson(payload), source, safetyEpoch, timeProvider));
                return;
            }
        }
    }

    private static void MapClear(
        DeviceActionRequest request,
        ActiveDeviceView device,
        CommandSource source,
        long safetyEpoch,
        TimeProvider timeProvider,
        ICollection<DeviceCommandEnvelope> results)
    {
        var capability = SelectFirstCapability(device, WaveformClear, EmsChannelOutput, ToySpeedControl);
        if (capability is null)
        {
            return;
        }

        switch (capability.Id.Value)
        {
            case WaveformClear:
                results.Add(BuildEnvelope(device, capability, "clear.queue", BuildJson("{}"), source, safetyEpoch, timeProvider));
                return;
            case EmsChannelOutput:
            {
                var payload = JsonSerializer.Serialize(new
                {
                    channel = ResolveChannelToken(request.Channels),
                    on = false,
                    intensity = 0,
                    mode = 0,
                    frequency = 0,
                    pulseTime = 0
                });
                results.Add(BuildEnvelope(device, capability, "set.output", BuildJson(payload), source, safetyEpoch, timeProvider));
                return;
            }
            case ToySpeedControl:
                results.Add(BuildEnvelope(device, capability, "set.speed", BuildJson("{\"a\":0,\"b\":0,\"c\":0}"), source, safetyEpoch, timeProvider));
                return;
        }
    }

    private static void MapEmsMode(
        DeviceActionRequest request,
        ActiveDeviceView device,
        CommandSource source,
        long safetyEpoch,
        TimeProvider timeProvider,
        ICollection<DeviceCommandEnvelope> results)
    {
        var capability = SelectFirstCapability(device, EmsChannelOutput);
        if (capability is null)
        {
            return;
        }

        var mode = ParseMode(request);
        var payload = JsonSerializer.Serialize(new
        {
            channel = ResolveChannelToken(request.Channels),
            on = true,
            intensity = 0,
            mode,
            frequency = 0,
            pulseTime = 0
        });
        results.Add(BuildEnvelope(device, capability, "set.output", BuildJson(payload), source, safetyEpoch, timeProvider));
    }

    private static void MapEmsCustomWaveform(
        DeviceActionRequest request,
        ActiveDeviceView device,
        CommandSource source,
        long safetyEpoch,
        TimeProvider timeProvider,
        ICollection<DeviceCommandEnvelope> results)
    {
        var capability = SelectFirstCapability(device, EmsChannelOutput);
        if (capability is null)
        {
            return;
        }

        var (frequency, pulseTime) = ResolveEmsWaveform(request);
        var payload = JsonSerializer.Serialize(new
        {
            channel = ResolveChannelToken(request.Channels),
            on = true,
            intensity = 0,
            mode = 0,
            frequency,
            pulseTime
        });
        results.Add(BuildEnvelope(device, capability, "set.output", BuildJson(payload), source, safetyEpoch, timeProvider));
    }

    private static void MapToyMode(
        DeviceActionRequest request,
        ActiveDeviceView device,
        CommandSource source,
        long safetyEpoch,
        TimeProvider timeProvider,
        ICollection<DeviceCommandEnvelope> results)
    {
        var capability = SelectFirstCapability(device, ToyMotorControl, ToySpeedControl);
        if (capability is null)
        {
            return;
        }

        if (capability.Id.Value == ToyMotorControl)
        {
            var mode = ParseMode(request);
            var payload = JsonSerializer.Serialize(new { motors = ResolveMotorToken(request.Channels), mode });
            results.Add(BuildEnvelope(device, capability, "set.mode", BuildJson(payload), source, safetyEpoch, timeProvider));
            return;
        }

        var value = ScaleToActuator(request.Value > 0 ? request.Value : request.RawValue, capability.Actuators[0]);
        var (a, b, c) = ResolveToyChannels(request.Channels);
        var speedPayload = JsonSerializer.Serialize(new { a = a ? value : 0, b = b ? value : 0, c = c ? value : 0 });
        results.Add(BuildEnvelope(device, capability, "set.speed", BuildJson(speedPayload), source, safetyEpoch, timeProvider));
    }

    private static void MapToyMotor(
        string action,
        DeviceActionRequest request,
        ActiveDeviceView device,
        CommandSource source,
        long safetyEpoch,
        TimeProvider timeProvider,
        ICollection<DeviceCommandEnvelope> results)
    {
        var capability = SelectFirstCapability(device, ToySpeedControl, ToyMotorControl);
        if (capability is null)
        {
            return;
        }

        var value = ScaleToActuator(request.Value, capability.Actuators[0]);
        switch (action)
        {
            case "toy_motor_1":
                results.Add(BuildEnvelope(device, capability, "set.speed", BuildJson($"{{\"a\":{value},\"b\":0,\"c\":0}}"), source, safetyEpoch, timeProvider));
                return;
            case "toy_motor_2":
                results.Add(BuildEnvelope(device, capability, "set.speed", BuildJson($"{{\"a\":0,\"b\":{value},\"c\":0}}"), source, safetyEpoch, timeProvider));
                return;
            case "toy_motor_3":
                results.Add(BuildEnvelope(device, capability, "set.speed", BuildJson($"{{\"a\":0,\"b\":0,\"c\":{value}}}"), source, safetyEpoch, timeProvider));
                return;
            case "toy_motor_all":
                results.Add(BuildEnvelope(device, capability, "set.speed", BuildJson($"{{\"a\":{value},\"b\":{value},\"c\":{value}}}"), source, safetyEpoch, timeProvider));
                return;
            case "toy_stop_all":
                results.Add(BuildEnvelope(device, capability, "set.speed", BuildJson("{\"a\":0,\"b\":0,\"c\":0}"), source, safetyEpoch, timeProvider));
                return;
        }
    }

    private static void MapEnemaPump(
        string action,
        DeviceActionRequest request,
        ActiveDeviceView device,
        CommandSource source,
        long safetyEpoch,
        TimeProvider timeProvider,
        ICollection<DeviceCommandEnvelope> results)
    {
        var capability = SelectFirstCapability(device, EnemaPumpControl);
        if (capability is null)
        {
            return;
        }

        var durationSeconds = ResolveDurationSeconds(request);
        var (pump, state) = action switch
        {
            "peristaltic_start" => ("peristaltic", "forward"),
            "peristaltic_stop" => ("peristaltic", "stop"),
            "water_start" => ("suction", "forward"),
            "water_stop" => ("suction", "stop"),
            "pause_all" => ("all", "stop"),
            _ => ("all", "stop")
        };
        var payload = JsonSerializer.Serialize(new { pump, state, durationSeconds });
        results.Add(BuildEnvelope(device, capability, "pump.control", BuildJson(payload), source, safetyEpoch, timeProvider));
    }

    private static void MapEnemaQuery(
        string action,
        DeviceActionRequest request,
        ActiveDeviceView device,
        CommandSource source,
        long safetyEpoch,
        TimeProvider timeProvider,
        ICollection<DeviceCommandEnvelope> results)
    {
        var capability = SelectFirstCapability(device, EnemaTelemetry);
        if (capability is null)
        {
            return;
        }

        var operation = action == "query_battery" ? "query.battery" : "query.status";
        results.Add(BuildEnvelope(device, capability, operation, BuildJson("{}"), source, safetyEpoch, timeProvider));
    }

    private static void MapToyQuery(
        DeviceActionRequest request,
        ActiveDeviceView device,
        CommandSource source,
        long safetyEpoch,
        TimeProvider timeProvider,
        ICollection<DeviceCommandEnvelope> results)
    {
        const string toyTelemetry = "toy.telemetry";
        var capability = SelectFirstCapability(device, toyTelemetry);
        if (capability is null)
        {
            return;
        }

        results.Add(BuildEnvelope(device, capability, "query.info", BuildJson("{}"), source, safetyEpoch, timeProvider));
    }

    private static void MapImCommand(
        string action,
        DeviceActionRequest request,
        ActiveDeviceView device,
        CommandSource source,
        long safetyEpoch,
        TimeProvider timeProvider,
        ICollection<DeviceCommandEnvelope> results)
    {
        if (action == "stop_all")
        {
            var globalStop = SelectFirstCapability(device, ImGlobalStop, ImCommandSend);
            if (globalStop is null)
            {
                return;
            }

            results.Add(BuildEnvelope(
                device,
                globalStop,
                "stop.all",
                BuildJson("{\"commandId\":\"_stop_all\"}"),
                source,
                safetyEpoch,
                timeProvider));
            return;
        }

        var capability = SelectFirstCapability(device, ImCommandSend, BridgeCommandSend);
        if (capability is null)
        {
            return;
        }

        var commandId = string.IsNullOrWhiteSpace(request.WaveformData)
            ? request.EventId
            : request.WaveformData.Trim();
        if (string.IsNullOrWhiteSpace(commandId))
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new { commandId });
        results.Add(BuildEnvelope(device, capability, "send.command", BuildJson(payload), source, safetyEpoch, timeProvider));
    }

    private static DeviceCommandEnvelope BuildEnvelope(
        ActiveDeviceView device,
        CapabilityDescriptor capability,
        string operationId,
        JsonElement payload,
        CommandSource source,
        long safetyEpoch,
        TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();
        return new DeviceCommandEnvelope(
            CommandId.New(),
            device.InstanceId,
            capability.Id,
            operationId,
            payload,
            source,
            capability.CommandPolicy.Delivery,
            now,
            now + capability.CommandPolicy.Timeout,
            safetyEpoch,
            device.SessionEpoch);
    }

    private static CapabilityDescriptor? SelectFirstCapability(
        ActiveDeviceView device,
        params string[] capabilityIds)
    {
        foreach (var capabilityId in capabilityIds)
        {
            var capability = device.Capabilities.FirstOrDefault(item => item.Id.Value == capabilityId);
            if (capability is not null)
            {
                return capability;
            }
        }

        return null;
    }

    private static JsonElement BuildJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static int ScaleToActuator(int normalizedValue, CapabilityActuatorDescriptor actuator)
    {
        var max = (int)Math.Floor(actuator.ValidRange.Maximum);
        if (max <= 0)
        {
            return normalizedValue;
        }

        var scaled = (int)Math.Round((double)normalizedValue / 100.0 * max, MidpointRounding.AwayFromZero);
        return Math.Clamp(scaled, 0, max);
    }

    private static int ParseMode(DeviceActionRequest request)
    {
        if (int.TryParse(request.WaveformData, out var parsed))
        {
            return Math.Clamp(parsed, 0, 16);
        }

        return Math.Clamp(request.RawValue > 0 ? request.RawValue : request.Value, 0, 16);
    }

    private static (int frequency, int pulseTime) ResolveEmsWaveform(DeviceActionRequest request)
    {
        var frequency = 50;
        var pulseTime = 20;

        if (!string.IsNullOrWhiteSpace(request.WaveformData))
        {
            var parts = request.WaveformData
                .Split(new[] { ',', ':', ';', '/', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
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

    private static int ResolveWaveFrequency(DeviceActionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.WaveformData))
        {
            var token = request.WaveformData.Trim().ToLowerInvariant();
            return token switch
            {
                "breath" => 50,
                "tide" => 30,
                "heartbeat" => 80,
                "rhythm" => 100,
                "crescendo" => 60,
                "decrescendo" => 60,
                "pulse" => 150,
                _ when int.TryParse(token, out var parsed) => Math.Clamp(parsed, 1, 100),
                _ => 100
            };
        }

        return 100;
    }

    private static (bool hasA, bool hasB) ResolveDglabChannels(IReadOnlyList<Channel> channels)
    {
        if (channels.Count == 0)
        {
            return (true, false);
        }

        var hasA = false;
        var hasB = false;
        foreach (var channel in channels)
        {
            if (channel is Channel.A or Channel.AB)
            {
                hasA = true;
            }

            if (channel is Channel.B or Channel.AB)
            {
                hasB = true;
            }
        }

        return (hasA, hasB);
    }

    private static (bool a, bool b, bool c) ResolveToyChannels(IReadOnlyList<Channel> channels)
    {
        // 玩具马达语义：A 通道 → A 马达，AB → A+B。
        var (hasA, hasB) = ResolveDglabChannels(channels);
        return (hasA, hasB, false);
    }

    private static string ResolveMotorToken(IReadOnlyList<Channel> channels)
    {
        var (hasA, hasB) = ResolveDglabChannels(channels);
        if (hasA && hasB)
        {
            return "ab";
        }

        return hasA ? "a" : hasB ? "b" : "a";
    }

    private static string ResolveChannelToken(IReadOnlyList<Channel> channels)
    {
        var (hasA, hasB) = ResolveDglabChannels(channels);
        if (hasA && hasB)
        {
            return "AB";
        }

        return hasB ? "B" : "A";
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

    private static object BuildChannelFrame(int frequency, int strength) => new
    {
        frequencies = new[] { frequency, frequency, frequency, frequency },
        strengths = new[] { strength, strength, strength, strength }
    };
}
