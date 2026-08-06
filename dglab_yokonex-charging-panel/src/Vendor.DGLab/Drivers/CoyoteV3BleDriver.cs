using System.Collections.Concurrent;
using System.Text.Json;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.DGLab.Common;
using ChargingPanel.Vendor.DGLab.Protocol;

namespace ChargingPanel.Vendor.DGLab.Drivers;

/// <summary>
/// 郊狼脉冲主机 V3 蓝牙驱动。
/// 握手阶段先写入 BF 安全软上限，之后才允许进入 Ready 接受普通命令；
/// B0 强度修改按序列号等待对应 B1 回执，波形帧遵守截止时间不无限等待。
/// </summary>
public sealed class CoyoteV3BleDriver : DGLabDriverBase
{
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(2);
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<B1Ack>> _pendingAcks = new();
    private readonly WaveformPump _pump;

    public CoyoteV3BleDriver(IPluginContext context)
        : base(new ProtocolProfileId(DGLabStableIds.ProfileCoyoteV3Ble))
    {
        ArgumentNullException.ThrowIfNull(context);
        _pump = new WaveformPump(SendFrameAsync, context.TimeProvider);
    }

    protected override async ValueTask OnOpenAsync(IEndpointLease lease, CancellationToken cancellationToken)
    {
        // 进入 Ready 之前必须重写 BF 软上限：断电保存值不可信，重连后必须刷新。
        var result = await SendBinaryAsync(
            DGLabUuids.WriteRoute,
            CoyoteV3Codec.EncodeDefaultBf(),
            EndpointResponseMode.WithoutResponse,
            CommandId.New(),
            cancellationToken);
        if (result.Status is not DeviceCommandStatus.AcceptedByTransport and not DeviceCommandStatus.Executed)
        {
            throw new IOException("V3 握手 BF 软上限写入失败。");
        }
    }

    protected override async ValueTask<DeviceCommandResult> ExecuteCoreAsync(
        DeviceCommandEnvelope command,
        IEndpointLease lease,
        CancellationToken cancellationToken)
    {
        switch (command.CapabilityId.Value)
        {
            case DGLabStableIds.CapIntensityOutput:
                return await ExecuteIntensityAsync(command, cancellationToken);
            case DGLabStableIds.CapWaveformStream:
                return await ExecuteWaveform(command, cancellationToken);
            case DGLabStableIds.CapWaveformClear:
                return await ExecuteClearQueue(command, cancellationToken);
            case DGLabStableIds.CapSafetyLimits:
                return await ExecuteSoftLimitAsync(command, cancellationToken);
            case DGLabStableIds.CapBatteryStatus:
                return await ExecuteBatteryReadAsync(command, cancellationToken);
            default:
                return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "capability.unsupported", "V3 驱动不支持该能力。");
        }
    }

    private async ValueTask<DeviceCommandResult> ExecuteIntensityAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        var payload = command.Payload;
        var absolute = string.Equals(command.OperationId, "set.level", StringComparison.Ordinal);
        var hasA = TryGetInt(payload, "channelA", out var valueA);
        var hasB = TryGetInt(payload, "channelB", out var valueB);

        if (!hasA && !hasB)
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "command.invalid_payload", "强度命令必须至少包含一个通道。");
        }

        var modeA = ResolveMode(hasA, valueA, absolute);
        var modeB = ResolveMode(hasB, valueB, absolute);
        var settingA = hasA ? Math.Abs(valueA) : 0;
        var settingB = hasB ? Math.Abs(valueB) : 0;
        var sequence = hasA || hasB ? NextAckSequenceNumber() : 0;

        var frame = CoyoteV3Codec.EncodeB0(
            sequence,
            modeA,
            modeB,
            settingA,
            settingB,
            [0, 0, 0, 0], [0, 0, 0, 101],
            [0, 0, 0, 0], [0, 0, 0, 101]);

        var ack = new TaskCompletionSource<B1Ack>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAcks[sequence] = ack;

        var sendResult = await SendBinaryAsync(
            DGLabUuids.WriteRoute,
            frame,
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
        if (sendResult.Status is not DeviceCommandStatus.AcceptedByTransport and not DeviceCommandStatus.Executed)
        {
            _pendingAcks.TryRemove(sequence, out _);
            return sendResult;
        }

        // 等待对应序列号的 B1 回执；超时按结果未知处理，绝不盲目重发。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(AckTimeout);
        try
        {
            var b1 = await ack.Task.WaitAsync(timeoutCts.Token);
            EmitIntensityTelemetry(b1.StrengthA, b1.StrengthB, sequence);
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Executed);
        }
        catch (TimeoutException)
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.OutcomeUnknown, "strength.ack_timeout", "等待 B1 强度回执超时，结果未知。");
        }
        catch (OperationCanceledException)
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.OutcomeUnknown, "strength.ack_cancelled", "等待 B1 强度回执被取消。");
        }
        finally
        {
            _pendingAcks.TryRemove(sequence, out _);
        }
    }

    private ValueTask<DeviceCommandResult> ExecuteWaveform(DeviceCommandEnvelope command, CancellationToken cancellationToken)
    {
        var payload = command.Payload;
        var hasA = TryGetChannelFrame(payload, "channelA", out var frameA);
        var hasB = TryGetChannelFrame(payload, "channelB", out var frameB);

        var frequencyA = hasA ? frameA.Frequencies : new[] { 0, 0, 0, 0 };
        var strengthA = hasA ? frameA.Strengths : new[] { 0, 0, 0, 101 };
        var frequencyB = hasB ? frameB.Frequencies : new[] { 0, 0, 0, 0 };
        var strengthB = hasB ? frameB.Strengths : new[] { 0, 0, 0, 101 };

        var b0 = CoyoteV3Codec.EncodeB0(
            0,
            CoyoteV3Codec.StrengthNoChange,
            CoyoteV3Codec.StrengthNoChange,
            0,
            0,
            frequencyA,
            strengthA,
            frequencyB,
            strengthB);

        var repeatCount = TryGetInt(payload, "repeatCount", out var repeat) ? Math.Max(1, repeat) : 1;
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < repeatCount; i++)
        {
            _pump.Enqueue(b0, now + TimeSpan.FromMilliseconds(500));
        }

        return ValueTask.FromResult(new DeviceCommandResult(command.CommandId, DeviceCommandStatus.AcceptedByTransport));
    }

    private ValueTask<DeviceCommandResult> ExecuteClearQueue(DeviceCommandEnvelope command, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _pump.Clear();
        }

        return ValueTask.FromResult(new DeviceCommandResult(command.CommandId, DeviceCommandStatus.AcceptedByTransport));
    }

    private async ValueTask<DeviceCommandResult> ExecuteSoftLimitAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        var payload = command.Payload;
        var limitA = TryGetInt(payload, "limitA", out var a) ? a : 200;
        var limitB = TryGetInt(payload, "limitB", out var b) ? b : 200;
        var freqBalanceA = TryGetInt(payload, "freqBalanceA", out var fa) ? fa : 128;
        var freqBalanceB = TryGetInt(payload, "freqBalanceB", out var fb) ? fb : 128;
        var strengthBalanceA = TryGetInt(payload, "strengthBalanceA", out var sa) ? sa : 128;
        var strengthBalanceB = TryGetInt(payload, "strengthBalanceB", out var sb) ? sb : 128;

        return await SendBinaryAsync(
            DGLabUuids.WriteRoute,
            CoyoteV3Codec.EncodeBf(limitA, limitB, freqBalanceA, freqBalanceB, strengthBalanceA, strengthBalanceB),
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
            var read = await lease.ReadAsync(
                new EndpointReadRequest(DGLabUuids.BatteryRoute),
                cancellationToken);
            if (read.Status != EndpointOperationStatus.Completed || read.Payload is null || read.Payload.Kind != EndpointPayloadKind.Binary)
            {
                return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.TransportFailed, "battery.read_failed", "电量读取失败。");
            }

            if (CoyoteV3Codec.TryDecodeBattery(read.Payload.Binary.Span, out var percent))
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
        }

        var result = await SendBinaryAsync(
            DGLabUuids.WriteRoute,
            CoyoteV3Codec.EncodeZero(),
            EndpointResponseMode.WithoutResponse,
            CommandId.New(),
            cancellationToken);
        return result.Status is DeviceCommandStatus.AcceptedByTransport or DeviceCommandStatus.Executed
            ? new SafeStopResult(SafeStopStatus.ConfirmedStopped)
            : new SafeStopResult(SafeStopStatus.Failed, "safety.zero_write_failed", "V3 归零写入失败。");
    }

    /// <summary>接收来自通知特性的原始帧。测试与组合根都通过此入口喂入设备回调。</summary>
    public void OnNotify(ReadOnlyMemory<byte> frame)
    {
        var span = frame.Span;
        if (CoyoteV3Codec.TryDecodeB1(span, out var sequence, out var strengthA, out var strengthB))
        {
            if (_pendingAcks.TryRemove(sequence, out var ack))
            {
                ack.TrySetResult(new B1Ack(strengthA, strengthB));
            }

            EmitIntensityTelemetry(strengthA, strengthB, sequence);
            return;
        }

        if (span.Length > 0 && span[0] != 0xB1 && CoyoteV3Codec.TryDecodeBattery(span, out var percent))
        {
            EmitBatteryTelemetry(percent);
        }
    }

    /// <summary>测试直接断言待发波形是否已清空。</summary>
    public bool HasPendingWaveformFrames
    {
        get
        {
            lock (_sync)
            {
                return _pump.HasPendingFrames;
            }
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

    private void EmitIntensityTelemetry(byte strengthA, byte strengthB, int sequence)
    {
        if (TryCreateTelemetry(
            new CapabilityId(DGLabStableIds.CapIntensityOutput),
            $"{{\"channelA\":{strengthA},\"channelB\":{strengthB},\"sequenceNumber\":{sequence}}}",
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

    private static int ResolveMode(bool present, int value, bool absolute)
    {
        if (!present)
        {
            return CoyoteV3Codec.StrengthNoChange;
        }

        if (absolute)
        {
            return CoyoteV3Codec.StrengthAbsolute;
        }

        return value >= 0 ? CoyoteV3Codec.StrengthIncrease : CoyoteV3Codec.StrengthDecrease;
    }

    private static bool TryGetInt(JsonElement payload, string property, out int value)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(property, out var element) &&
            element.ValueKind == JsonValueKind.Number)
        {
            value = element.GetInt32();
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryGetChannelFrame(JsonElement payload, string property, out ChannelFrame frame)
    {
        frame = default;
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(property, out var channel) ||
            channel.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryGetIntArray(channel, "frequencies", out var frequencies) ||
            !TryGetIntArray(channel, "strengths", out var strengths) ||
            frequencies.Length != 4 ||
            strengths.Length != 4)
        {
            return false;
        }

        // 用户频率 (10-1000) 转为协议频率 (10-240)。
        var protocolFrequencies = new int[4];
        for (var i = 0; i < 4; i++)
        {
            protocolFrequencies[i] = CoyoteV3Codec.ConvertFrequency(frequencies[i]);
        }

        frame = new ChannelFrame(protocolFrequencies, strengths);
        return true;
    }

    private static bool TryGetIntArray(JsonElement element, string property, out int[] values)
    {
        values = [];
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var list = new List<int>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            list.Add(item.GetInt32());
        }

        values = list.ToArray();
        return true;
    }

    private readonly record struct B1Ack(byte StrengthA, byte StrengthB);

    private readonly record struct ChannelFrame(int[] Frequencies, int[] Strengths);
}
