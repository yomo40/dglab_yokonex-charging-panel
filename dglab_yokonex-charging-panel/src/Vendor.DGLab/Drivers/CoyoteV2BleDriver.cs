using System.Text.Json;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.DGLab.Common;
using ChargingPanel.Vendor.DGLab.Protocol;

namespace ChargingPanel.Vendor.DGLab.Drivers;

/// <summary>
/// 郊狼脉冲主机 V2 蓝牙驱动。
/// 能力层暴露标准双通道强度/波形/电量，本驱动负责显示尺度换算与 characteristic 映射。
/// 注意官方命名与物理通道相反：PWM_A34 控制 B 通道波形，PWM_B34 控制 A 通道波形。
/// </summary>
public sealed class CoyoteV2BleDriver : DGLabDriverBase
{
    private readonly object _sync = new();
    private readonly WaveformPump _pump;
    private int _lastRawA;
    private int _lastRawB;

    public CoyoteV2BleDriver(IPluginContext context)
        : base(new ProtocolProfileId(DGLabStableIds.ProfileCoyoteV2Ble))
    {
        ArgumentNullException.ThrowIfNull(context);
        _pump = new WaveformPump(SendFrameAsync, context.TimeProvider);
    }

    protected override ValueTask<DeviceCommandResult> ExecuteCoreAsync(
        DeviceCommandEnvelope command,
        IEndpointLease lease,
        CancellationToken cancellationToken)
    {
        switch (command.CapabilityId.Value)
        {
            case DGLabStableIds.CapIntensityOutput:
                return ExecuteIntensityAsync(command, cancellationToken);
            case DGLabStableIds.CapWaveformStream:
                return ExecuteWaveform(command);
            case DGLabStableIds.CapWaveformClear:
                return ExecuteClearQueue(command);
            case DGLabStableIds.CapBatteryStatus:
                return ExecuteBatteryReadAsync(command, cancellationToken);
            default:
                return ValueTask.FromResult(new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "capability.unsupported", "V2 驱动不支持该能力。"));
        }
    }

    private async ValueTask<DeviceCommandResult> ExecuteIntensityAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        var payload = command.Payload;
        int rawA;
        int rawB;
        lock (_sync)
        {
            if (TryGetInt(payload, "channelA", out var displayA))
            {
                rawA = CoyoteV2Codec.DisplayToRaw(displayA);
                _lastRawA = rawA;
            }
            else
            {
                rawA = _lastRawA;
            }

            if (TryGetInt(payload, "channelB", out var displayB))
            {
                rawB = CoyoteV2Codec.DisplayToRaw(displayB);
                _lastRawB = rawB;
            }
            else
            {
                rawB = _lastRawB;
            }
        }

        return await SendBinaryAsync(
            DGLabUuids.V2PwmAb2,
            CoyoteV2Codec.EncodePwmAb2(rawA, rawB),
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
    }

    private ValueTask<DeviceCommandResult> ExecuteWaveform(DeviceCommandEnvelope command)
    {
        var payload = command.Payload;
        var frameA = BuildChannelFrame(payload, "channelA");
        var frameB = BuildChannelFrame(payload, "channelB");

        // 数据包：前 3 字节 A 通道波形（写 PWM_B34），后 3 字节 B 通道波形（写 PWM_A34）。
        var packet = new byte[6];
        frameA.CopyTo(packet, 0);
        frameB.CopyTo(packet, 3);

        var repeatCount = TryGetInt(payload, "repeatCount", out var repeat) ? Math.Max(1, repeat) : 1;
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < repeatCount; i++)
        {
            _pump.Enqueue(packet, now + TimeSpan.FromMilliseconds(500));
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
            var read = await lease.ReadAsync(
                new EndpointReadRequest(DGLabUuids.V2BatteryCharacteristic),
                cancellationToken);
            if (read.Status != EndpointOperationStatus.Completed ||
                read.Payload is null ||
                read.Payload.Kind != EndpointPayloadKind.Binary)
            {
                return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.TransportFailed, "battery.read_failed", "电量读取失败。");
            }

            if (CoyoteV2Codec.TryDecodeBattery(read.Payload.Binary.Span, out var percent))
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
            _pump.Stop();
            // 停止后清空缓存值，绝不依赖陈旧强度。
            _lastRawA = 0;
            _lastRawB = 0;
        }

        // 原子生成 A/B=0 的 PWM_AB2 写入并停止波形调度。
        var result = await SendBinaryAsync(
            DGLabUuids.V2PwmAb2,
            CoyoteV2Codec.EncodePwmAb2(0, 0),
            EndpointResponseMode.WithoutResponse,
            CommandId.New(),
            cancellationToken);
        return result.Status is DeviceCommandStatus.AcceptedByTransport or DeviceCommandStatus.Executed
            ? new SafeStopResult(SafeStopStatus.ConfirmedStopped)
            : new SafeStopResult(SafeStopStatus.Failed, "safety.zero_write_failed", "V2 归零写入失败。");
    }

    private async ValueTask SendFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        var lease = Lease;
        if (lease is null)
        {
            return;
        }

        var frameA = frame.AsSpan(0, 3).ToArray();
        var frameB = frame.AsSpan(3, 3).ToArray();
        // A 通道波形写到 PWM_B34，B 通道波形写到 PWM_A34。
        await lease.SendAsync(
            new EndpointRequest(DGLabUuids.V2PwmB34, EndpointPayload.FromBinary(frameA), EndpointResponseMode.WithoutResponse),
            cancellationToken);
        await lease.SendAsync(
            new EndpointRequest(DGLabUuids.V2PwmA34, EndpointPayload.FromBinary(frameB), EndpointResponseMode.WithoutResponse),
            cancellationToken);
    }

    private static byte[] BuildChannelFrame(JsonElement payload, string property)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(property, out var channel) ||
            channel.ValueKind != JsonValueKind.Object)
        {
            return CoyoteV2Codec.EncodePwmWaveform(0, 0, 0);
        }

        var frequency = TryGetInt(channel, "frequency", out var freq) ? freq : 10;
        var strength = TryGetInt(channel, "strength", out var displayStrength) ? displayStrength : 0;
        var (x, y) = CoyoteV2Codec.CalculateXYFromFrequency(frequency);
        var z = Math.Clamp(strength / 5, 0, 20);
        return CoyoteV2Codec.EncodePwmWaveform(x, y, z);
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
}