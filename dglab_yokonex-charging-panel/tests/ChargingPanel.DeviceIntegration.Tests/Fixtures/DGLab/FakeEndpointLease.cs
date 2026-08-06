using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;

namespace ChargingPanel.DeviceIntegration.Tests.Fixtures.DGLab;

/// <summary>测试用假 Endpoint 租约：记录写入并允许按需注入失败与读取结果。</summary>
public sealed class FakeEndpointLease : IEndpointLease
{
    private readonly object _gate = new();
    private readonly List<SentWrite> _writes = [];
    private readonly List<string> _reads = [];

    public FakeEndpointLease(ProtocolProfileId profileId, TransportKind transportKind)
    {
        ProfileId = profileId;
        TransportKind = transportKind;
        EndpointId = EndpointId.New();
    }

    public EndpointId EndpointId { get; }

    public ProtocolProfileId ProfileId { get; }

    public TransportKind TransportKind { get; }

    /// <summary>可注入发送处理；返回 null 表示走默认成功路径。</summary>
    public Func<EndpointRequest, CancellationToken, ValueTask<EndpointSendResult?>>? SendHandler { get; set; }

    /// <summary>可注入读取处理；返回 null 表示走默认失败路径。</summary>
    public Func<EndpointReadRequest, CancellationToken, ValueTask<EndpointReadResult?>>? ReadHandler { get; set; }

    public bool FailNextSend { get; set; }

    public bool FailAllSends { get; set; }

    public bool Disposed { get; private set; }

    public IReadOnlyList<SentWrite> Writes
    {
        get
        {
            lock (_gate)
            {
                return _writes.ToArray();
            }
        }
    }

    public IReadOnlyList<string> Reads
    {
        get
        {
            lock (_gate)
            {
                return _reads.ToArray();
            }
        }
    }

    public void ClearWrites()
    {
        lock (_gate)
        {
            _writes.Clear();
        }
    }

    public async ValueTask<EndpointSendResult> SendAsync(EndpointRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _writes.Add(new SentWrite(request.Route, request.Payload, request.ResponseMode));
        }

        if (SendHandler is not null)
        {
            var custom = await SendHandler(request, cancellationToken);
            if (custom is not null)
            {
                return custom;
            }
        }

        lock (_gate)
        {
            if (FailAllSends || FailNextSend)
            {
                FailNextSend = false;
                return new EndpointSendResult(EndpointOperationStatus.Failed, errorCode: "fake.send_failed");
            }
        }

        return new EndpointSendResult(EndpointOperationStatus.Completed);
    }

    public async ValueTask<EndpointReadResult> ReadAsync(EndpointReadRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _reads.Add(request.Route);
        }

        if (ReadHandler is not null)
        {
            var custom = await ReadHandler(request, cancellationToken);
            if (custom is not null)
            {
                return custom;
            }
        }

        return new EndpointReadResult(EndpointOperationStatus.Failed, errorCode: "fake.no_read");
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>一次发送记录的不可变快照。</summary>
public sealed record SentWrite(string Route, EndpointPayload Payload, EndpointResponseMode ResponseMode)
{
    public byte[] Binary => Payload.Binary.ToArray();

    public string? JsonText => Payload.Json.HasValue ? Payload.Json.Value.GetRawText() : null;

    public string Hex => Convert.ToHexString(Binary);
}