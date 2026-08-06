using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;

namespace ChargingPanel.DeviceIntegration.Tests.Fixtures.Yokonex;

/// <summary>
/// 合成 Endpoint 租约。记录写入并支持注入发送结果（Completed/Rejected/Failed/OutcomeUnknown），
/// 用于在无真实 BLE 硬件的环境下验证驱动帧构造与结果映射。
/// 绝不伪造连接成功——发送结果由测试显式控制。
/// </summary>
internal sealed class FakeEndpointLease : IEndpointLease
{
    public FakeEndpointLease(ProtocolProfileId profileId, TransportKind transportKind = TransportKind.BluetoothLowEnergy)
    {
        ProfileId = profileId;
        TransportKind = transportKind;
    }

    public EndpointId EndpointId { get; } = EndpointId.New();

    public ProtocolProfileId ProfileId { get; }

    public TransportKind TransportKind { get; }

    /// <summary>下一次发送返回的状态。</summary>
    public EndpointOperationStatus NextSendStatus { get; set; } = EndpointOperationStatus.Completed;

    /// <summary>下一次读取返回的结果；null 时返回 Failed。</summary>
    public EndpointReadResult? NextReadResult { get; set; }

    /// <summary>发送闸门；设置后发送等待该闸门直到取消（模拟传输挂起）。</summary>
    public TaskCompletionSource? SendGate { get; set; }

    /// <summary>记录全部发送请求（路由 + 载荷）。</summary>
    public List<(string Route, EndpointPayload Payload)> Sends { get; } = [];

    /// <summary>记录二进制载荷写入。</summary>
    public List<(string Route, byte[] Payload)> BinaryWrites { get; } = [];

    public bool Disposed { get; private set; }

    public ValueTask<EndpointSendResult> SendAsync(EndpointRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Sends.Add((request.Route, request.Payload));
        if (request.Payload.Kind == EndpointPayloadKind.Binary)
        {
            BinaryWrites.Add((request.Route, request.Payload.Binary.ToArray()));
        }

        if (SendGate is not null)
        {
            return new ValueTask<EndpointSendResult>(SendBlockedAsync(SendGate, cancellationToken));
        }

        // 非 Completed 状态必须携带 ErrorCode（契约校验）。
        var errorCode = NextSendStatus == EndpointOperationStatus.Completed ? null : "test.injected";
        return ValueTask.FromResult(new EndpointSendResult(NextSendStatus, null, errorCode));
    }

    private static async Task<EndpointSendResult> SendBlockedAsync(TaskCompletionSource gate, CancellationToken cancellationToken)
    {
        await gate.Task.WaitAsync(cancellationToken);
        return new EndpointSendResult(EndpointOperationStatus.Completed);
    }

    public ValueTask<EndpointReadResult> ReadAsync(EndpointReadRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            NextReadResult
            ?? new EndpointReadResult(EndpointOperationStatus.Failed, null, "transport.no_read"));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
