using System.Text.Json;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.DGLab.Common;
using ChargingPanel.Vendor.DGLab.Protocol;

namespace ChargingPanel.Vendor.DGLab.Drivers;

/// <summary>
/// 郊狼脉冲主机 V3 WebSocket 驱动。
/// WS 传输以 JSON 消息承载与 BLE 相同的语义：绑定、强度、波形队列、清队列、错误、断线。
/// 该协议为公开桥接约定，具体字节由 JSON 序列化负责，本驱动不构造协议二进制。
/// </summary>
public sealed class CoyoteV3WebSocketDriver : DGLabDriverBase
{
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(2);
    private readonly object _sync = new();
    private readonly Dictionary<int, TaskCompletionSource<WSAck>> _pendingAcks = [];
    private readonly WaveformPump _pump;

    public CoyoteV3WebSocketDriver(IPluginContext context)
        : base(new ProtocolProfileId(DGLabStableIds.ProfileCoyoteV3WebSocket))
    {
        ArgumentNullException.ThrowIfNull(context);
        _pump = new WaveformPump(SendFrameAsync, context.TimeProvider);
    }

    protected override async ValueTask OnOpenAsync(IEndpointLease lease, CancellationToken cancellationToken)
    {
        // WS 桥接以绑定消息建立目标，等价于 BLE 的握手安全配置。
        var result = await SendJsonAsync("bind", "{\"target\":\"47L121000\"}", commandId: null, cancellationToken);
        if (result.Status is not DeviceCommandStatus.AcceptedByTransport and not DeviceCommandStatus.Executed)
        {
            throw new IOException("V3 WS 绑定失败。");
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
                return await ExecuteWaveform(command);
            case DGLabStableIds.CapWaveformClear:
                return await ExecuteClearQueue(command, cancellationToken);
            default:
                return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "capability.unsupported", "V3 WS 驱动不支持该能力。");
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

        var sequence = NextAckSequenceNumber();
        var json = BuildStrengthJson(hasA, valueA, hasB, valueB, sequence, absolute);
        var ack = new TaskCompletionSource<WSAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _pendingAcks[sequence] = ack;
        }

        var sendResult = await SendJsonAsync("strength", json, command.CommandId, cancellationToken);
        if (sendResult.Status is not DeviceCommandStatus.AcceptedByTransport and not DeviceCommandStatus.Executed)
        {
            lock (_sync)
            {
                _pendingAcks.Remove(sequence);
            }

            return sendResult;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(AckTimeout);
        try
        {
            var wsAck = await ack.Task.WaitAsync(timeoutCts.Token);
            EmitIntensityTelemetry(wsAck.ChannelA, wsAck.ChannelB, sequence);
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Executed);
        }
        catch (TimeoutException)
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.OutcomeUnknown, "strength.ack_timeout", "等待 WS 强度回执超时，结果未知。");
        }
        catch (OperationCanceledException)
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.OutcomeUnknown, "strength.ack_cancelled", "等待 WS 强度回执被取消。");
        }
        finally
        {
            lock (_sync)
            {
                _pendingAcks.Remove(sequence);
            }
        }
    }

    private ValueTask<DeviceCommandResult> ExecuteWaveform(DeviceCommandEnvelope command)
    {
        var json = BuildWaveformJson(command.Payload);
        var frame = System.Text.Encoding.UTF8.GetBytes(json);
        var repeatCount = TryGetInt(command.Payload, "repeatCount", out var repeat) ? Math.Max(1, repeat) : 1;
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < repeatCount; i++)
        {
            _pump.Enqueue(frame, now + TimeSpan.FromMilliseconds(500));
        }

        return ValueTask.FromResult(new DeviceCommandResult(command.CommandId, DeviceCommandStatus.AcceptedByTransport));
    }

    private async ValueTask<DeviceCommandResult> ExecuteClearQueue(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _pump.Clear();
        }

        return await SendJsonAsync("clear", "{}", command.CommandId, cancellationToken);
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

        var zero = await SendJsonAsync("strength", "{\"a\":0,\"b\":0,\"seq\":0}", null, cancellationToken);
        if (zero.Status is not DeviceCommandStatus.AcceptedByTransport and not DeviceCommandStatus.Executed)
        {
            return new SafeStopResult(SafeStopStatus.Failed, "safety.zero_write_failed", "WS 归零写入失败。");
        }

        return new SafeStopResult(SafeStopStatus.ConfirmedStopped);
    }

    /// <summary>接收来自 WS 的 JSON 通知帧。</summary>
    public void OnMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        switch (typeElement.GetString())
        {
            case "strength":
                HandleStrengthMessage(root);
                break;
            case "error":
                EmitErrorTelemetry(root);
                break;
            case "disconnect":
                EmitDisconnectTelemetry();
                break;
        }
    }

    private void HandleStrengthMessage(JsonElement root)
    {
        var sequence = TryGetJsonInt(root, "seq", out var seq) ? seq : 0;
        var a = TryGetJsonInt(root, "a", out var valueA) ? valueA : 0;
        var b = TryGetJsonInt(root, "b", out var valueB) ? valueB : 0;

        WSAck? ack = null;
        lock (_sync)
        {
            if (_pendingAcks.TryGetValue(sequence, out var pending))
            {
                pending.TrySetResult(new WSAck(a, b));
                ack = new WSAck(a, b);
            }
        }

        EmitIntensityTelemetry(a, b, sequence);
    }

    private void EmitIntensityTelemetry(int a, int b, int sequence)
    {
        if (TryCreateTelemetry(
            new CapabilityId(DGLabStableIds.CapIntensityOutput),
            $"{{\"channelA\":{a},\"channelB\":{b},\"sequenceNumber\":{sequence}}}",
            sequence,
            out var envelope))
        {
            EnqueueTelemetry(envelope);
        }
    }

    private void EmitErrorTelemetry(JsonElement root)
    {
        var code = TryGetJsonString(root, "code", out var value) ? value : "unknown";
        if (TryCreateTelemetry(
            new CapabilityId("connection.status"),
            $"{{\"state\":\"error\",\"code\":\"{JsonEncodedText.Encode(code)}\"}}",
            null,
            out var envelope))
        {
            EnqueueTelemetry(envelope);
        }
    }

    private void EmitDisconnectTelemetry()
    {
        if (TryCreateTelemetry(
            new CapabilityId("connection.status"),
            "{\"state\":\"disconnected\"}",
            null,
            out var envelope))
        {
            EnqueueTelemetry(envelope);
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
            new EndpointRequest("waveform", EndpointPayload.FromJson(JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(frame)).RootElement), EndpointResponseMode.WithoutResponse),
            cancellationToken);
    }

    private async ValueTask<DeviceCommandResult> SendJsonAsync(
        string route,
        string json,
        CommandId? commandId,
        CancellationToken cancellationToken)
    {
        var lease = Lease;
        if (lease is null)
        {
            return new DeviceCommandResult(commandId ?? CommandId.New(), DeviceCommandStatus.Rejected, "driver.not_open", "驱动尚未打开。");
        }

        EndpointPayload payload;
        using (var document = JsonDocument.Parse(json))
        {
            payload = EndpointPayload.FromJson(document.RootElement);
        }

        EndpointSendResult result;
        try
        {
            result = await lease.SendAsync(new EndpointRequest(route, payload, EndpointResponseMode.WithoutResponse), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new DeviceCommandResult(commandId ?? CommandId.New(), DeviceCommandStatus.OutcomeUnknown, "command.cancelled_in_flight", "写入途中被取消，结果未知。");
        }
        catch (Exception ex)
        {
            return new DeviceCommandResult(commandId ?? CommandId.New(), DeviceCommandStatus.TransportFailed, "transport.threw", ex.Message);
        }

        return ToCommandResult(commandId ?? CommandId.New(), result);
    }

    private static string BuildStrengthJson(bool hasA, int valueA, bool hasB, int valueB, int sequence, bool absolute)
    {
        var builder = new System.Text.StringBuilder("{\"seq\":");
        builder.Append(sequence);
        if (hasA)
        {
            builder.Append(",\"a\":");
            builder.Append(absolute ? valueA : Math.Abs(valueA));
            if (!absolute)
            {
                builder.Append(",\"modeA\":");
                builder.Append(valueA >= 0 ? "1" : "2");
            }
        }

        if (hasB)
        {
            builder.Append(",\"b\":");
            builder.Append(absolute ? valueB : Math.Abs(valueB));
            if (!absolute)
            {
                builder.Append(",\"modeB\":");
                builder.Append(valueB >= 0 ? "1" : "2");
            }
        }

        builder.Append('}');
        return builder.ToString();
    }

    private static string BuildWaveformJson(JsonElement payload)
    {
        var builder = new System.Text.StringBuilder("{\"a\":");
        AppendChannel(builder, payload, "channelA");
        builder.Append(",\"b\":");
        AppendChannel(builder, payload, "channelB");
        builder.Append('}');
        return builder.ToString();
    }

    private static void AppendChannel(System.Text.StringBuilder builder, JsonElement payload, string property)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(property, out var channel) &&
            channel.ValueKind == JsonValueKind.Object)
        {
            builder.Append(channel.GetRawText());
        }
        else
        {
            builder.Append("{\"freq\":[0,0,0,0],\"strength\":[0,0,0,101]}");
        }
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

    private static bool TryGetJsonInt(JsonElement element, string property, out int value)
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

    private static bool TryGetJsonString(JsonElement element, string property, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var child) &&
            child.ValueKind == JsonValueKind.String)
        {
            value = child.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private readonly record struct WSAck(int ChannelA, int ChannelB);
}
