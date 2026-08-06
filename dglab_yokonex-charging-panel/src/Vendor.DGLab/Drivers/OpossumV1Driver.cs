using System.Text.Json;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.DGLab.Common;
using ChargingPanel.Vendor.DGLab.Protocol;

namespace ChargingPanel.Vendor.DGLab.Drivers;

/// <summary>
/// 负鼠振动控制器 V1 蓝牙驱动。
/// 收到 B3 强度变化后立即排入 B2 屏幕显示同步，但该同步低于急停优先级；
/// SafeStop 发送 A/B=0 并同步零值显示，取消全部波形帧。
/// </summary>
public sealed class OpossumV1Driver : DGLabDriverBase
{
    private readonly object _sync = new();
    private readonly WaveformPump _pump;
    private bool _safeStopped;
    private bool _buttonReportEnabled;

    public OpossumV1Driver(IPluginContext context)
        : base(new ProtocolProfileId(DGLabStableIds.ProfileOpossumV1Ble))
    {
        ArgumentNullException.ThrowIfNull(context);
        _pump = new WaveformPump(SendFrameAsync, context.TimeProvider);
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
            case DGLabStableIds.CapButtonStatus:
                return await ExecuteButtonReportAsync(command, cancellationToken);
            case DGLabStableIds.CapWaveformStream:
                return await ExecuteWaveform(command);
            case DGLabStableIds.CapWaveformClear:
                return await ExecuteClearQueue(command);
            case DGLabStableIds.CapIntensityOutput:
                return await ExecuteIntensityAsync(command, cancellationToken);
            case DGLabStableIds.CapScreenSync:
                return await ExecuteScreenSyncAsync(command, cancellationToken);
            case DGLabStableIds.CapBatteryStatus:
                return await ExecuteBatteryReadAsync(command, cancellationToken);
            default:
                return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "capability.unsupported", "负鼠驱动不支持该能力。");
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

        return await SendBinaryAsync(
            DGLabUuids.WriteRoute,
            OpossumV1Codec.EncodeLampAndButtonReport((byte)color, _buttonReportEnabled),
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
    }

    private async ValueTask<DeviceCommandResult> ExecuteButtonReportAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        var enabled = string.Equals(command.OperationId, "enable.report", StringComparison.Ordinal);
        var result = await SendBinaryAsync(
            DGLabUuids.WriteRoute,
            OpossumV1Codec.EncodeLampAndButtonReport(0x01, enabled),
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
        if (result.Status is DeviceCommandStatus.AcceptedByTransport or DeviceCommandStatus.Executed)
        {
            _buttonReportEnabled = enabled;
        }

        return result;
    }

    private ValueTask<DeviceCommandResult> ExecuteWaveform(DeviceCommandEnvelope command)
    {
        var payload = command.Payload;
        var frameA = TryGetByteArray(payload, "channelA", out var a) ? a : new byte[4];
        var frameB = TryGetByteArray(payload, "channelB", out var b) ? b : new byte[4];
        var b0 = OpossumV1Codec.EncodeVibrationWaveform(frameA, frameB);

        var repeatCount = TryGetInt(payload, "repeatCount", out var repeat) ? Math.Max(1, repeat) : 1;
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < repeatCount; i++)
        {
            _pump.Enqueue(b0, now + TimeSpan.FromMilliseconds(500));
        }

        return ValueTask.FromResult(new DeviceCommandResult(command.CommandId, DeviceCommandStatus.AcceptedByTransport));
    }

    private ValueTask<DeviceCommandResult> ExecuteClearQueue(DeviceCommandEnvelope command)
    {
        lock (_sync)
        {
            _pump.Clear();
        }

        return ValueTask.FromResult(new DeviceCommandResult(command.CommandId, DeviceCommandStatus.AcceptedByTransport));
    }

    private async ValueTask<DeviceCommandResult> ExecuteIntensityAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        var payload = command.Payload;
        byte? a = TryGetInt(payload, "channelA", out var valueA) ? (byte)Math.Clamp(valueA, 0, 200) : null;
        byte? b = TryGetInt(payload, "channelB", out var valueB) ? (byte)Math.Clamp(valueB, 0, 200) : null;
        return await SendBinaryAsync(
            DGLabUuids.WriteRoute,
            OpossumV1Codec.EncodeIntensity(a, b),
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
    }

    private async ValueTask<DeviceCommandResult> ExecuteScreenSyncAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        var payload = command.Payload;
        var a = TryGetInt(payload, "channelA", out var valueA) ? (byte)Math.Clamp(valueA, 0, 200) : (byte)0;
        var b = TryGetInt(payload, "channelB", out var valueB) ? (byte)Math.Clamp(valueB, 0, 200) : (byte)0;
        return await SendBinaryAsync(
            DGLabUuids.WriteRoute,
            OpossumV1Codec.EncodeScreenSync(a, b),
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
    }

    private async ValueTask<DeviceCommandResult> ExecuteBatteryReadAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        var lease = Lease;
        if (lease is null)
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "driver.not_open", "驱动尚未打开。");
        }

        try
        {
            var read = await lease.ReadAsync(new EndpointReadRequest(DGLabUuids.BatteryRoute), cancellationToken);
            if (read.Status != EndpointOperationStatus.Completed ||
                read.Payload is null ||
                read.Payload.Kind != EndpointPayloadKind.Binary)
            {
                return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.TransportFailed, "battery.read_failed", "电量读取失败。");
            }

            if (OpossumV1Codec.TryDecodeBattery(read.Payload.Binary.Span, out var percent))
            {
                EmitBatteryTelemetry(percent);
            }

            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Executed);
        }
        catch (OperationCanceledException)
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.OutcomeUnknown, "battery.read_cancelled", "电量读取被取消。");
        }
    }

    protected override async ValueTask<SafeStopResult> SafeStopCoreAsync(
        SafeStopContext context,
        IEndpointLease lease,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _safeStopped = true;
            _pump.Stop();
        }

        // 发送 A/B=0 强度并同步零值显示。
        var intensity = await SendBinaryAsync(
            DGLabUuids.WriteRoute,
            OpossumV1Codec.EncodeIntensity(0, 0),
            EndpointResponseMode.WithoutResponse,
            CommandId.New(),
            cancellationToken);
        if (intensity.Status is not DeviceCommandStatus.AcceptedByTransport and not DeviceCommandStatus.Executed)
        {
            return new SafeStopResult(SafeStopStatus.Failed, "safety.zero_intensity_failed", "负鼠归零强度写入失败。");
        }

        var display = await SendBinaryAsync(
            DGLabUuids.WriteRoute,
            OpossumV1Codec.EncodeScreenSync(0, 0),
            EndpointResponseMode.WithoutResponse,
            CommandId.New(),
            cancellationToken);
        return display.Status is DeviceCommandStatus.AcceptedByTransport or DeviceCommandStatus.Executed
            ? new SafeStopResult(SafeStopStatus.ConfirmedStopped)
            : new SafeStopResult(SafeStopStatus.Failed, "safety.zero_display_failed", "负鼠零值显示同步失败。");
    }

    /// <summary>接收通知帧并产生遥测。B3 强度变化会立即排入 B2 显示同步。</summary>
    public void OnNotify(ReadOnlyMemory<byte> frame)
    {
        var span = frame.Span;

        if (OpossumV1Codec.TryDecodeB3(span, out var channelA, out var channelB))
        {
            EmitIntensityTelemetry(channelA, channelB);
            QueueDisplaySync(channelA, channelB);
            return;
        }

        if (OpossumV1Codec.TryDecodeD0(span, out var sequence, out var buttonState))
        {
            EmitButtonTelemetry(sequence, buttonState);
            return;
        }

        if (OpossumV1Codec.TryDecodeBattery(span, out var percent))
        {
            EmitBatteryTelemetry(percent);
        }
    }

    private void QueueDisplaySync(byte channelA, byte channelB)
    {
        // 显示同步低于急停优先级：急停会清空调度队列，未发出的同步被取消。
        lock (_sync)
        {
            if (_safeStopped)
            {
                return;
            }

            var b2 = OpossumV1Codec.EncodeScreenSync(channelA, channelB);
            _pump.Enqueue(b2, DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(500));
        }
    }

    private async ValueTask SendFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        var lease = Lease;
        if (lease is null)
        {
            return;
        }

        await lease.SendAsync(
            new EndpointRequest(DGLabUuids.WriteRoute, EndpointPayload.FromBinary(frame), EndpointResponseMode.WithoutResponse),
            cancellationToken);
    }

    private void EmitIntensityTelemetry(byte channelA, byte channelB)
    {
        if (TryCreateTelemetry(
            new CapabilityId(DGLabStableIds.CapIntensityOutput),
            $"{{\"channelA\":{channelA},\"channelB\":{channelB}}}",
            null,
            out var envelope))
        {
            EnqueueTelemetry(envelope);
        }
    }

    private void EmitButtonTelemetry(byte sequence, ushort buttonState)
    {
        if (TryCreateTelemetry(
            new CapabilityId(DGLabStableIds.CapButtonStatus),
            $"{{\"sequence\":{sequence},\"buttonState\":{buttonState}}}",
            sequence,
            out var envelope))
        {
            EnqueueTelemetry(envelope);
        }
    }

    private void EmitBatteryTelemetry(byte percent)
    {
        if (TryCreateTelemetry(
            new CapabilityId(DGLabStableIds.CapBatteryStatus),
            $"{{\"percent\":{percent}}}",
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

    private static bool TryGetByteArray(JsonElement element, string property, out byte[] values)
    {
        values = [];
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var list = new List<byte>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            list.Add((byte)item.GetInt32());
        }

        values = list.ToArray();
        return true;
    }
}