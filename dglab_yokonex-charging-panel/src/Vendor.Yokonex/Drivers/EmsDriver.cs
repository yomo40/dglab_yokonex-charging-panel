using System.Text.Json;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.Yokonex.Protocol;

namespace ChargingPanel.Vendor.Yokonex.Drivers;

/// <summary>电击器 BLE 特征路由。写通道 FF31，通知通道 FF32。</summary>
internal static class EmsBleRoutes
{
    public const string Write = "0000ff31-0000-1000-8000-00805f9b34fb";
    public const string Notify = "0000ff32-0000-1000-8000-00805f9b34fb";
}

/// <summary>
/// 电击器共享驱动。一代（V1.6）与二代（V2.0）共用本驱动，
/// 代际差异全部收敛到 <see cref="IEmsProtocolStrategy"/>。
/// 所有 GATT 写均为 WRITE_WITHOUT_RESPONSE；传输成功只代表已交给 GATT，
/// 绝不伪造成设备已执行。GATT 结果未知时绝不重发 AtMostOnce 帧。
/// </summary>
public sealed class EmsDriver : YokonexDriverBase
{
    private readonly object _sync = new();
    private readonly IEmsProtocolStrategy _strategy;
    private readonly IPluginLogger _logger;
    private EmsChannelOutputState _channelA = EmsChannelOutputState.Off();
    private EmsChannelOutputState _channelB = EmsChannelOutputState.Off();

    public EmsDriver(ProtocolProfileId profileId, IEmsProtocolStrategy strategy, IPluginLogger logger)
        : base(profileId)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async ValueTask OnOpenAsync(IEndpointLease lease, CancellationToken cancellationToken)
    {
        // 握手：查询 A/B、马达、电池。查询为尽力而为，任一失败不阻止打开。
        foreach (var type in new[]
                 {
                     EmsQueryType.ChannelA,
                     EmsQueryType.ChannelB,
                     EmsQueryType.Motor,
                     EmsQueryType.Battery
                 })
        {
            byte[] frame;
            lock (_sync)
            {
                frame = _strategy.BuildQueryFrame(type);
            }

            var result = await SendBinaryAsync(
                EmsBleRoutes.Write,
                frame,
                EndpointResponseMode.WithoutResponse,
                CommandId.New(),
                cancellationToken);
            if (result.Status is not DeviceCommandStatus.AcceptedByTransport and not DeviceCommandStatus.Executed)
            {
                _logger.Log(PluginLogLevel.Debug, "ems.handshake.query_failed", $"握手查询失败：{type}", null);
            }
        }
    }

    protected override async ValueTask<DeviceCommandResult> ExecuteCoreAsync(
        DeviceCommandEnvelope command,
        IEndpointLease lease,
        CancellationToken cancellationToken)
    {
        return command.CapabilityId.Value switch
        {
            "ems.channel.output" => await ExecuteChannelOutputAsync(command, cancellationToken),
            "ems.motor.control" => await ExecuteMotorAsync(command, cancellationToken),
            "ems.step.control" => await ExecuteStepAsync(command, cancellationToken),
            "ems.angle.control" => await ExecuteAngleAsync(command, cancellationToken),
            "ems.telemetry" => await ExecuteQueryAsync(command, cancellationToken),
            "ems.frequency_list" => await ExecuteFrequencyListAsync(command, cancellationToken),
            _ => new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "capability.unsupported", "该电击器 Profile 不支持此能力。")
        };
    }

    private async ValueTask<DeviceCommandResult> ExecuteChannelOutputAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        if (!TryParseChannelOutput(command.Payload, out var channel, out var state, out var error))
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "command.invalid", error);
        }

        byte[] frame;
        lock (_sync)
        {
            if (channel == EmsChannel.A)
            {
                _channelA = state;
            }
            else if (channel == EmsChannel.B)
            {
                _channelB = state;
            }
            else
            {
                _channelA = state;
                _channelB = state;
            }

            var other = channel == EmsChannel.A ? _channelB : _channelA;
            frame = _strategy.BuildChannelOutputFrame(new EmsChannelOutputCommand(channel, state), other);
        }

        return await SendBinaryAsync(
            EmsBleRoutes.Write,
            frame,
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
    }

    private async ValueTask<DeviceCommandResult> ExecuteMotorAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        if (!TryParseMotor(command.Payload, out var state, out var error))
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "command.invalid", error);
        }

        byte[] frame;
        lock (_sync)
        {
            frame = _strategy.BuildMotorFrame(state);
        }

        return await SendBinaryAsync(
            EmsBleRoutes.Write,
            frame,
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
    }

    private async ValueTask<DeviceCommandResult> ExecuteStepAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        if (!TryParseStep(command.Payload, out var stepCommand, out var error))
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "command.invalid", error);
        }

        byte[] frame;
        lock (_sync)
        {
            frame = _strategy.BuildStepFrame(stepCommand);
        }

        return await SendBinaryAsync(
            EmsBleRoutes.Write,
            frame,
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
    }

    private async ValueTask<DeviceCommandResult> ExecuteAngleAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        if (!TryParseAngle(command.Payload, out var angleCommand, out var error))
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "command.invalid", error);
        }

        byte[] frame;
        lock (_sync)
        {
            frame = _strategy.BuildAngleFrame(angleCommand);
        }

        return await SendBinaryAsync(
            EmsBleRoutes.Write,
            frame,
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
    }

    private async ValueTask<DeviceCommandResult> ExecuteQueryAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        if (!TryParseQuery(command.Payload, out var queryType, out var error))
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "command.invalid", error);
        }

        byte[] frame;
        lock (_sync)
        {
            frame = _strategy.BuildQueryFrame(queryType);
        }

        return await SendBinaryAsync(
            EmsBleRoutes.Write,
            frame,
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
    }

    private async ValueTask<DeviceCommandResult> ExecuteFrequencyListAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        if (_strategy is not EmsV20Codec v20)
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "capability.unsupported", "一代电击器不支持频率列表模式。");
        }

        if (!TryParseFrequencyList(command.Payload, out var channel, out var intensity, out var groups, out var error))
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "command.invalid", error);
        }

        byte[] frame;
        lock (_sync)
        {
            frame = v20.BuildFrequencyListFrame(channel, intensity, groups);
        }

        return await SendBinaryAsync(
            EmsBleRoutes.Write,
            frame,
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
    }

    protected override async ValueTask<SafeStopResult> SafeStopCoreAsync(
        SafeStopContext context,
        IEndpointLease lease,
        CancellationToken cancellationToken)
    {
        byte[] closeFrame;
        byte[] motorOffFrame;
        lock (_sync)
        {
            // 安全停止不依赖任何缓存的强度/模式值，也不修改计步/角度数据。
            closeFrame = _strategy.BuildCloseFrame();
            motorOffFrame = _strategy.BuildMotorFrame(EmsMotorState.Off);
        }

        var closeResult = await SendBinaryAsync(
            EmsBleRoutes.Write,
            closeFrame,
            EndpointResponseMode.WithoutResponse,
            CommandId.New(),
            cancellationToken);

        var motorResult = await SendBinaryAsync(
            EmsBleRoutes.Write,
            motorOffFrame,
            EndpointResponseMode.WithoutResponse,
            CommandId.New(),
            cancellationToken);

        if (closeResult.Status is DeviceCommandStatus.OutcomeUnknown
            || motorResult.Status is DeviceCommandStatus.OutcomeUnknown)
        {
            // GATT 结果未知：绝不重发 AtMostOnce 帧，也不谎报已停止。
            return new SafeStopResult(SafeStopStatus.OutcomeUnknown, "stop.outcome_unknown", "停止帧的 GATT 结果未知。");
        }

        if (closeResult.Status is DeviceCommandStatus.TransportFailed or DeviceCommandStatus.Rejected
            || motorResult.Status is DeviceCommandStatus.TransportFailed or DeviceCommandStatus.Rejected)
        {
            return new SafeStopResult(SafeStopStatus.Failed, "stop.send_failed", "停止帧发送失败。");
        }

        return new SafeStopResult(SafeStopStatus.ConfirmedStopped);
    }

    /// <summary>
    /// 接收来自通知特性的原始帧。测试与组合根都通过此入口喂入设备回调。
    /// </summary>
    public void OnNotify(ReadOnlyMemory<byte> frame)
    {
        EmsStatusReport? report;
        lock (_sync)
        {
            if (!_strategy.TryParseReport(frame.Span, out report) || report is null)
            {
                return;
            }

            ApplyReport(report);
        }

        EmitReportTelemetry(report);
    }

    private void ApplyReport(EmsStatusReport report)
    {
        if (report is not EmsStatusReport.Channel channel)
        {
            return;
        }

        var status = channel.Status;
        var state = new EmsChannelOutputState(status.On, status.Intensity, status.Mode, 0, 0);
        if (status.Channel == EmsChannel.A)
        {
            _channelA = state;
        }
        else if (status.Channel == EmsChannel.B)
        {
            _channelB = state;
        }
    }

    private void EmitReportTelemetry(EmsStatusReport report)
    {
        var (capabilityId, payloadJson) = report switch
        {
            EmsStatusReport.Battery battery => (
                "ems.telemetry",
                $"{{\"kind\":\"battery\",\"percent\":{battery.Status.Percent}}}"),
            EmsStatusReport.Step step => (
                "ems.telemetry",
                $"{{\"kind\":\"step\",\"stepCount\":{step.Status.StepCount}}}"),
            EmsStatusReport.Angle angle => (
                "ems.telemetry",
                $"{{\"kind\":\"angle\",\"gravityX\":{angle.Status.GravityX},\"gravityY\":{angle.Status.GravityY}," +
                $"\"gravityZ\":{angle.Status.GravityZ},\"gyroX\":{angle.Status.GyroX},\"gyroY\":{angle.Status.GyroY}," +
                $"\"gyroZ\":{angle.Status.GyroZ}}}"),
            EmsStatusReport.Exception exception => (
                "ems.telemetry",
                $"{{\"kind\":\"exception\",\"code\":\"{ToExceptionName(exception.Report.Code)}\"}}"),
            EmsStatusReport.Channel channel => BuildChannelPayload(channel.Status),
            _ => (null, null)
        };

        if (capabilityId is null)
        {
            return;
        }

        if (TryCreateTelemetry(new CapabilityId(capabilityId), payloadJson!, null, out var envelope))
        {
            EnqueueTelemetry(envelope);
        }
    }

    private static (string? CapabilityId, string? Payload) BuildChannelPayload(EmsChannelStatus status)
    {
        var connection = status.Connection switch
        {
            EmsChannelConnection.NotConnected => "not_connected",
            EmsChannelConnection.Discharging => "discharging",
            _ => "connected_no_discharge"
        };
        var channelName = status.Channel == EmsChannel.A ? "A" : "B";
        return (
            "ems.telemetry",
            $"{{\"kind\":\"channel\",\"channel\":\"{channelName}\",\"connection\":\"{connection}\"," +
            $"\"on\":{status.On.ToString().ToLowerInvariant()},\"intensity\":{status.Intensity},\"mode\":{status.Mode}}}");
    }

    private static string ToExceptionName(EmsExceptionCode code) => code switch
    {
        EmsExceptionCode.ChecksumError => "checksum_error",
        EmsExceptionCode.HeaderError => "header_error",
        EmsExceptionCode.CommandError => "command_error",
        EmsExceptionCode.DataError => "data_error",
        _ => "not_implemented"
    };

    private static bool TryParseChannelOutput(
        JsonElement payload,
        out EmsChannel channel,
        out EmsChannelOutputState state,
        out string error)
    {
        channel = default;
        state = EmsChannelOutputState.Off();
        error = string.Empty;

        if (payload.ValueKind != JsonValueKind.Object ||
            !TryGetString(payload, "channel", out var rawChannel, out error))
        {
            error = error.Length == 0 ? "通道输出载荷必须包含 channel 字段。" : error;
            return false;
        }

        channel = rawChannel.ToUpperInvariant() switch
        {
            "A" => EmsChannel.A,
            "B" => EmsChannel.B,
            "AB" => EmsChannel.AB,
            _ => (EmsChannel)0
        };
        if (channel == (EmsChannel)0)
        {
            error = "channel 必须是 A、B 或 AB。";
            return false;
        }

        var on = payload.TryGetProperty("on", out var onElement) && onElement.ValueKind == JsonValueKind.True;
        var intensity = GetInt32(payload, "intensity", 0);
        var mode = GetInt32(payload, "mode", 0);
        var frequency = GetInt32(payload, "frequency", 0);
        var pulseTime = GetInt32(payload, "pulseTime", 0);

        if (intensity < 0 || intensity > 276)
        {
            error = "强度必须在 0 到 276 之间。";
            return false;
        }

        if (mode < 0 || mode > 16)
        {
            error = "模式必须在 0（自定义）到 16 之间。";
            return false;
        }

        if (frequency < 0 || frequency > 100 || pulseTime < 0 || pulseTime > 100)
        {
            error = "频率与脉冲时间必须在 0 到 100 之间。";
            return false;
        }

        state = new EmsChannelOutputState(on, intensity, mode, frequency, pulseTime);
        return true;
    }

    private static bool TryParseMotor(JsonElement payload, out EmsMotorState state, out string error)
    {
        state = default;
        error = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object || !TryGetString(payload, "state", out var raw, out error))
        {
            error = error.Length == 0 ? "马达控制载荷必须包含 state 字段。" : error;
            return false;
        }

        state = raw.ToLowerInvariant() switch
        {
            "off" => EmsMotorState.Off,
            "on" => EmsMotorState.On,
            "preset1" => EmsMotorState.Preset1,
            "preset2" => EmsMotorState.Preset2,
            "preset3" => EmsMotorState.Preset3,
            _ => (EmsMotorState)0xFF
        };
        if (state == (EmsMotorState)0xFF)
        {
            error = "state 必须是 off/on/preset1/preset2/preset3。";
            return false;
        }

        return true;
    }

    private static bool TryParseStep(JsonElement payload, out EmsStepCommand command, out string error)
    {
        command = default;
        error = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object || !TryGetString(payload, "command", out var raw, out error))
        {
            error = error.Length == 0 ? "计步控制载荷必须包含 command 字段。" : error;
            return false;
        }

        command = raw.ToLowerInvariant() switch
        {
            "off" => EmsStepCommand.Off,
            "on" => EmsStepCommand.On,
            "clear" => EmsStepCommand.Clear,
            "pause" => EmsStepCommand.Pause,
            "resume" => EmsStepCommand.Resume,
            _ => (EmsStepCommand)0xFF
        };
        if (command == (EmsStepCommand)0xFF)
        {
            error = "command 必须是 off/on/clear/pause/resume。";
            return false;
        }

        return true;
    }

    private static bool TryParseAngle(JsonElement payload, out EmsAngleCommand command, out string error)
    {
        command = default;
        error = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object || !TryGetString(payload, "command", out var raw, out error))
        {
            error = error.Length == 0 ? "角度控制载荷必须包含 command 字段。" : error;
            return false;
        }

        command = raw.ToLowerInvariant() switch
        {
            "off" => EmsAngleCommand.Off,
            "on" => EmsAngleCommand.On,
            _ => (EmsAngleCommand)0xFF
        };
        if (command == (EmsAngleCommand)0xFF)
        {
            error = "command 必须是 off 或 on。";
            return false;
        }

        return true;
    }

    private static bool TryParseQuery(JsonElement payload, out EmsQueryType type, out string error)
    {
        type = default;
        error = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object || !TryGetString(payload, "type", out var raw, out error))
        {
            error = error.Length == 0 ? "查询载荷必须包含 type 字段。" : error;
            return false;
        }

        type = raw.ToLowerInvariant() switch
        {
            "a" => EmsQueryType.ChannelA,
            "b" => EmsQueryType.ChannelB,
            "motor" => EmsQueryType.Motor,
            "battery" => EmsQueryType.Battery,
            "step" => EmsQueryType.Step,
            "angle" => EmsQueryType.Angle,
            _ => (EmsQueryType)0xFF
        };
        if (type == (EmsQueryType)0xFF)
        {
            error = "type 必须是 a/b/motor/battery/step/angle。";
            return false;
        }

        return true;
    }

    private static bool TryParseFrequencyList(
        JsonElement payload,
        out EmsChannel channel,
        out int intensity,
        out EmsFrequencyGroup[] groups,
        out string error)
    {
        channel = default;
        intensity = 0;
        groups = [];
        error = string.Empty;

        if (payload.ValueKind != JsonValueKind.Object ||
            !TryGetString(payload, "channel", out var rawChannel, out error))
        {
            error = error.Length == 0 ? "频率列表载荷必须包含 channel 字段。" : error;
            return false;
        }

        channel = rawChannel.ToUpperInvariant() switch
        {
            "A" => EmsChannel.A,
            "B" => EmsChannel.B,
            _ => (EmsChannel)0
        };
        if (channel is not EmsChannel.A and not EmsChannel.B)
        {
            error = "频率列表模式 channel 必须是 A 或 B。";
            return false;
        }

        intensity = GetInt32(payload, "intensity", 0);
        if (intensity < 0 || intensity > 276)
        {
            error = "强度必须在 0 到 276 之间。";
            return false;
        }

        if (!payload.TryGetProperty("groups", out var groupsElement) || groupsElement.ValueKind != JsonValueKind.Array)
        {
            error = "频率列表载荷必须包含 groups 数组。";
            return false;
        }

        var list = new List<EmsFrequencyGroup>();
        foreach (var item in groupsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = "groups 每个元素必须是对象。";
                return false;
            }

            var frequency = GetInt32(item, "frequency", 0);
            var pulseTime = GetInt32(item, "pulseTime", 0);
            if (frequency < 1 || frequency > 100 || pulseTime < 0 || pulseTime > 100)
            {
                error = "频率必须在 1 到 100，脉冲时间在 0 到 100。";
                return false;
            }

            list.Add(new EmsFrequencyGroup(frequency, pulseTime));
        }

        if (list.Count is 0 or > 100)
        {
            error = "频率列表必须包含 1 到 100 组。";
            return false;
        }

        groups = list.ToArray();
        return true;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value, out string error)
    {
        value = string.Empty;
        error = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            error = $"载荷缺少字符串字段 {propertyName}。";
            return false;
        }

        value = property.GetString()!;
        return true;
    }

    private static int GetInt32(JsonElement element, string propertyName, int defaultValue) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : defaultValue;
}
