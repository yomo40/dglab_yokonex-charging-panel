using System.Text.Json;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.DGLab.Common;
using ChargingPanel.Vendor.DGLab.Protocol;

namespace ChargingPanel.Vendor.DGLab.Drivers;

/// <summary>
/// 爪印无线按钮传感器 V1.1 蓝牙驱动。
/// 强制握手时序：连接后立即写入安全的 0x50 无触发模式，完成后才进入 Ready；
/// 写入失败必须关闭 Session，绝不伪报已连接。
/// </summary>
public sealed class PawPrintsV11Driver : DGLabDriverBase
{
    private readonly object _sync = new();
    private byte[] _lastTriggerFrame;
    private byte _lastD0Sequence;
    private bool _hasD0Sequence;

    public PawPrintsV11Driver(IPluginContext context)
        : base(new ProtocolProfileId(DGLabStableIds.ProfilePawPrintsV11Ble))
    {
        ArgumentNullException.ThrowIfNull(context);
        _lastTriggerFrame = PawPrintsV11Codec.EncodeNoTriggerMode();
    }

    protected override async ValueTask OnOpenAsync(IEndpointLease lease, CancellationToken cancellationToken)
    {
        // 连接后立即写入安全的 0x50 无触发模式；失败则握手失败，Session 不得进入 Ready。
        var result = await SendBinaryAsync(
            DGLabUuids.WriteRoute,
            PawPrintsV11Codec.EncodeNoTriggerMode(),
            EndpointResponseMode.WithoutResponse,
            CommandId.New(),
            cancellationToken);
        if (result.Status is not DeviceCommandStatus.AcceptedByTransport and not DeviceCommandStatus.Executed)
        {
            throw new IOException("爪印握手安全模式写入失败。");
        }
    }

    protected override async ValueTask<DeviceCommandResult> ExecuteCoreAsync(
        DeviceCommandEnvelope command,
        IEndpointLease lease,
        CancellationToken cancellationToken)
    {
        switch (command.CapabilityId.Value)
        {
            case DGLabStableIds.CapLampColor:
                return await ExecuteLampColorAsync(command, cancellationToken);
            case DGLabStableIds.CapShoulderLamp:
                return await ExecuteShoulderLampAsync(command, cancellationToken);
            case DGLabStableIds.CapTriggerMode:
                return await ExecuteTriggerModeAsync(command, cancellationToken);
            case DGLabStableIds.CapTriggerParamReset:
                return await SendBinaryAsync(DGLabUuids.WriteRoute, PawPrintsV11Codec.EncodeParamReset(), EndpointResponseMode.WithoutResponse, command.CommandId, cancellationToken);
            case DGLabStableIds.CapTriggerAutoDetect:
                return await SendBinaryAsync(DGLabUuids.WriteRoute, PawPrintsV11Codec.EncodeAutoDetect(), EndpointResponseMode.WithoutResponse, command.CommandId, cancellationToken);
            default:
                return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "capability.unsupported", "爪印驱动不支持该能力。");
        }
    }

    private async ValueTask<DeviceCommandResult> ExecuteLampColorAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        if (!TryGetInt(command.Payload, "color", out var color))
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "command.invalid_payload", "缺少颜色参数。");
        }

        byte[] frame;
        lock (_sync)
        {
            frame = (byte[])_lastTriggerFrame.Clone();
            frame[1] = (byte)Math.Clamp(color, 0, 255);
        }

        return await SendBinaryAsync(DGLabUuids.WriteRoute, frame, EndpointResponseMode.WithoutResponse, command.CommandId, cancellationToken);
    }

    private async ValueTask<DeviceCommandResult> ExecuteShoulderLampAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        var operation = command.OperationId;
        if (string.Equals(operation, "set.blink", StringComparison.Ordinal))
        {
            if (!TryGetInt(command.Payload, "color1", out var color1) ||
                !TryGetInt(command.Payload, "color2", out var color2) ||
                !TryGetInt(command.Payload, "speed", out var speed))
            {
                return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "command.invalid_payload", "闪烁参数缺失。");
            }

            return await SendBinaryAsync(
                DGLabUuids.WriteRoute,
                PawPrintsV11Codec.EncodeShoulderLampBlink((byte)color1, (byte)color2, (byte)speed),
                EndpointResponseMode.WithoutResponse,
                command.CommandId,
                cancellationToken);
        }

        if (!TryGetInt(command.Payload, "color", out var solid))
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "command.invalid_payload", "缺少颜色参数。");
        }

        return await SendBinaryAsync(
            DGLabUuids.WriteRoute,
            PawPrintsV11Codec.EncodeShoulderLampSolid((byte)solid),
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
    }

    private async ValueTask<DeviceCommandResult> ExecuteTriggerModeAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        if (!TryGetHex(command.Payload, "frame", out var frame) || frame.Length != PawPrintsV11Codec.CommandLength)
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "command.invalid_payload", "触发模式必须提供 17 字节指令帧。");
        }

        lock (_sync)
        {
            _lastTriggerFrame = (byte[])frame.Clone();
        }

        return await SendBinaryAsync(DGLabUuids.WriteRoute, frame, EndpointResponseMode.WithoutResponse, command.CommandId, cancellationToken);
    }

    /// <summary>接收来自通知特性的原始帧并产生遥测。重复帧按协议语义去重。</summary>
    public void OnNotify(ReadOnlyMemory<byte> frame)
    {
        var span = frame.Span;

        if (PawPrintsV11Codec.TryDecode51(span, out var lampColor, out var deviceType, out var battery))
        {
            EmitBatteryTelemetry(battery, deviceType);
            return;
        }

        if (PawPrintsV11Codec.TryDecode5A(span, out lampColor, out var eventId, out var paramValue))
        {
            EmitEventTelemetry("triggered", lampColor, eventId, paramValue);
            return;
        }

        if (PawPrintsV11Codec.TryDecode5B(span, out lampColor, out eventId))
        {
            EmitEventTelemetry("cancelled", lampColor, eventId, 0);
            return;
        }

        if (PawPrintsV11Codec.TryDecode5C(span, out lampColor, out eventId, out paramValue))
        {
            EmitEventTelemetry("param_changed", lampColor, eventId, paramValue);
            return;
        }

        if (PawPrintsV11Codec.TryDecodeD0(
                span,
                out lampColor,
                out var sequence,
                out var pressState,
                out var acceleration,
                out var xAngle,
                out var yAngle,
                out var zAngle,
                out var externalVoltage))
        {
            lock (_sync)
            {
                // D0 物理数据按序号去重：重复序号视为同一数据点重传。
                if (_hasD0Sequence && sequence == _lastD0Sequence)
                {
                    return;
                }

                _hasD0Sequence = true;
                _lastD0Sequence = sequence;
            }

            EmitD0Telemetry(lampColor, sequence, pressState, acceleration, xAngle, yAngle, zAngle, externalVoltage);
            return;
        }

        if (PawPrintsV11Codec.TryDecodeF1(span, out var xMin, out var xMax, out var yMin, out var yMax, out var zMin, out var zMax))
        {
            EmitF1Telemetry(xMin, xMax, yMin, yMax, zMin, zMax);
        }
    }

    protected override async ValueTask<SafeStopResult> SafeStopCoreAsync(
        SafeStopContext context,
        IEndpointLease lease,
        CancellationToken cancellationToken)
    {
        // 停止主动上报：恢复无触发模式。
        var result = await SendBinaryAsync(
            DGLabUuids.WriteRoute,
            PawPrintsV11Codec.EncodeNoTriggerMode(),
            EndpointResponseMode.WithoutResponse,
            CommandId.New(),
            cancellationToken);
        return result.Status is DeviceCommandStatus.AcceptedByTransport or DeviceCommandStatus.Executed
            ? new SafeStopResult(SafeStopStatus.ConfirmedStopped)
            : new SafeStopResult(SafeStopStatus.Failed, "safety.safe_mode_write_failed", "爪印安全模式写入失败。");
    }

    private void EmitBatteryTelemetry(byte battery, byte deviceType)
    {
        if (TryCreateTelemetry(
            new CapabilityId(DGLabStableIds.CapBatteryStatus),
            $"{{\"percent\":{battery},\"deviceType\":{deviceType}}}",
            null,
            out var envelope))
        {
            EnqueueTelemetry(envelope);
        }
    }

    private void EmitEventTelemetry(string state, byte lampColor, byte eventId, byte paramValue)
    {
        if (TryCreateTelemetry(
            new CapabilityId(DGLabStableIds.CapButtonStatus),
            $"{{\"state\":\"{JsonEncodedText.Encode(state)}\",\"lampColor\":{lampColor},\"eventId\":{eventId},\"paramValue\":{paramValue}}}",
            eventId,
            out var envelope))
        {
            EnqueueTelemetry(envelope);
        }
    }

    private void EmitD0Telemetry(
        byte lampColor,
        byte sequence,
        byte pressState,
        byte acceleration,
        byte xAngle,
        byte yAngle,
        byte zAngle,
        byte externalVoltage)
    {
        if (TryCreateTelemetry(
            new CapabilityId(DGLabStableIds.CapPoseAcceleration),
            $"{{\"lampColor\":{lampColor},\"pressState\":{pressState},\"acceleration\":{acceleration},\"xAngle\":{xAngle},\"yAngle\":{yAngle},\"zAngle\":{zAngle}}}",
            sequence,
            out var envelope))
        {
            EnqueueTelemetry(envelope);
        }

        if (TryCreateTelemetry(
            new CapabilityId(DGLabStableIds.CapExternalVoltage),
            $"{{\"voltage\":{externalVoltage},\"sequence\":{sequence}}}",
            sequence,
            out var voltageEnvelope))
        {
            EnqueueTelemetry(voltageEnvelope);
        }
    }

    private void EmitF1Telemetry(short xMin, short xMax, short yMin, short yMax, short zMin, short zMax)
    {
        if (TryCreateTelemetry(
            new CapabilityId(DGLabStableIds.CapPoseAcceleration),
            $"{{\"autoDetect\":true,\"xRange\":[{xMin},{xMax}],\"yRange\":[{yMin},{yMax}],\"zRange\":[{zMin},{zMax}]}}",
            null,
            out var envelope))
        {
            EnqueueTelemetry(envelope);
        }
    }

    private static bool TryGetInt(JsonElement element, string property, out int value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var child) &&
            child.ValueKind == JsonValueKind.Number)
        {
            value = child.GetInt32();
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryGetHex(JsonElement element, string property, out byte[] bytes)
    {
        bytes = [];
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var child) ||
            child.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = child.GetString() ?? string.Empty;
        if (text.Length % 2 != 0 || text.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }

        bytes = Convert.FromHexString(text);
        return true;
    }
}