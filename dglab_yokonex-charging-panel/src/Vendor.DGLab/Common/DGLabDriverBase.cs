using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Telemetry;
using ChargingPanel.Contracts.Transport;

namespace ChargingPanel.Vendor.DGLab.Common;

/// <summary>
/// DG-LAB 驱动基类：统一持有租约、遥测通道、序列号与并发安全停止协调。
/// 具体协议字节编码由派生类负责，本类不构造任何原始协议数据。
/// </summary>
public abstract class DGLabDriverBase : IProtocolDriver
{
    private readonly object _gate = new();
    private readonly Channel<DeviceTelemetryEnvelope> _telemetry = Channel.CreateUnbounded<DeviceTelemetryEnvelope>();
    private IEndpointLease? _lease;
    private int _sequence;
    private bool _safeStopInProgress;
    private DeviceInstanceId? _instanceId;
    private long _sessionEpoch = -1;

    protected DGLabDriverBase(ProtocolProfileId profileId)
    {
        ProfileId = profileId;
    }

    public ProtocolProfileId ProfileId { get; }

    protected IEndpointLease? Lease
    {
        get
        {
            lock (_gate)
            {
                return _lease;
            }
        }
    }

    /// <summary>
    /// 登记遥测所需的实例与会话纪元。宿主在建立会话并分配纪元后调用；
    /// 未登记前驱动不会产生遥测，避免出现实例/纪元不完整的遥测帧。
    /// </summary>
    public void Configure(DeviceInstanceId instanceId, long sessionEpoch)
    {
        lock (_gate)
        {
            _instanceId = instanceId;
            _sessionEpoch = sessionEpoch;
        }
    }

    public virtual async ValueTask OpenAsync(IEndpointLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            _lease = lease;
        }

        await OnOpenAsync(lease, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>子类握手钩子。握手阶段应写入安全配置（如 V3 的 BF 软上限）。</summary>
    protected virtual ValueTask OnOpenAsync(IEndpointLease lease, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public async ValueTask<DeviceCommandResult> ExecuteAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        IEndpointLease? lease;
        lock (_gate)
        {
            if (_safeStopInProgress)
            {
                return new DeviceCommandResult(
                    command.CommandId,
                    DeviceCommandStatus.Rejected,
                    "driver.safe_stopping",
                    "安全停止进行中，拒绝新命令。");
            }

            lease = _lease;
        }

        if (lease is null)
        {
            return new DeviceCommandResult(
                command.CommandId,
                DeviceCommandStatus.Rejected,
                "driver.not_open",
                "驱动尚未打开。");
        }

        return await ExecuteCoreAsync(command, lease, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 子类命令执行钩子。宿主保证同一实例的 ExecuteAsync 串行调用，
    /// 但 SafeStopAsync 可能并发调用，实现必须自行加锁。
    /// </summary>
    protected abstract ValueTask<DeviceCommandResult> ExecuteCoreAsync(
        DeviceCommandEnvelope command,
        IEndpointLease lease,
        CancellationToken cancellationToken);

    public async ValueTask<SafeStopResult> SafeStopAsync(
        SafeStopContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IEndpointLease? lease;
        lock (_gate)
        {
            if (_safeStopInProgress)
            {
                return new SafeStopResult(SafeStopStatus.OutcomeUnknown, "safety.concurrent_stop", "存在并发安全停止，结果未知。");
            }

            _safeStopInProgress = true;
            lease = _lease;
        }

        try
        {
            if (lease is null)
            {
                return new SafeStopResult(SafeStopStatus.Failed, "driver.not_open", "驱动尚未打开。");
            }

            return await SafeStopCoreAsync(context, lease, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _safeStopInProgress = false;
            }
        }
    }

    /// <summary>子类安全停止钩子。必须归零全部输出并停止后台调度。</summary>
    protected abstract ValueTask<SafeStopResult> SafeStopCoreAsync(
        SafeStopContext context,
        IEndpointLease lease,
        CancellationToken cancellationToken);

    public async IAsyncEnumerable<DeviceTelemetryEnvelope> ReadTelemetryAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var envelope in _telemetry.Reader.ReadAllAsync(cancellationToken))
        {
            yield return envelope;
        }
    }

    protected void EnqueueTelemetry(DeviceTelemetryEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        _telemetry.Writer.TryWrite(envelope);
    }

    /// <summary>
    /// 生成遥测信封；未登记实例或纪元时返回 false，不产生不完整遥测。
    /// </summary>
    protected bool TryCreateTelemetry(
        CapabilityId capabilityId,
        string payloadJson,
        long? sequenceNumber,
        out DeviceTelemetryEnvelope envelope)
    {
        DeviceInstanceId? instanceId;
        long sessionEpoch;
        lock (_gate)
        {
            instanceId = _instanceId;
            sessionEpoch = _sessionEpoch;
        }

        if (instanceId is null || sessionEpoch < 0)
        {
            envelope = null!;
            return false;
        }

        using var document = JsonDocument.Parse(payloadJson);
        envelope = new DeviceTelemetryEnvelope(
            instanceId.Value,
            ProfileId,
            capabilityId,
            "1.0",
            document.RootElement.Clone(),
            DateTimeOffset.UtcNow,
            sessionEpoch,
            sequenceNumber,
            TelemetryQuality.Good);
        return true;
    }

    /// <summary>取下一个 0-15 循环序列号（包含 0，用于不需要回执的写入）。</summary>
    protected int NextSequenceNumber()
    {
        lock (_gate)
        {
            var value = _sequence;
            _sequence = (_sequence + 1) & 0x0F;
            return value;
        }
    }

    /// <summary>取下一个 1-15 循环序列号（用于需要 B1 回执的强度修改）。</summary>
    protected int NextAckSequenceNumber()
    {
        lock (_gate)
        {
            _sequence = (_sequence % 15) + 1;
            return _sequence;
        }
    }

    /// <summary>向租约发送二进制载荷并映射传输结果为命令结果。</summary>
    protected async ValueTask<DeviceCommandResult> SendBinaryAsync(
        string route,
        ReadOnlyMemory<byte> payload,
        EndpointResponseMode responseMode,
        CommandId commandId,
        CancellationToken cancellationToken)
    {
        IEndpointLease? lease;
        lock (_gate)
        {
            lease = _lease;
        }

        if (lease is null)
        {
            return new DeviceCommandResult(commandId, DeviceCommandStatus.Rejected, "driver.not_open", "驱动尚未打开。");
        }

        EndpointSendResult result;
        try
        {
            result = await lease.SendAsync(
                new EndpointRequest(route, EndpointPayload.FromBinary(payload), responseMode),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new DeviceCommandResult(commandId, DeviceCommandStatus.OutcomeUnknown, "command.cancelled_in_flight", "写入途中被取消，结果未知。");
        }
        catch (Exception ex)
        {
            return new DeviceCommandResult(commandId, DeviceCommandStatus.TransportFailed, "transport.threw", ex.Message);
        }

        return ToCommandResult(commandId, result);
    }

    /// <summary>映射 Endpoint 发送结果为命令结果。WithoutResponse 失败不得静默升级为 WithResponse。</summary>
    protected static DeviceCommandResult ToCommandResult(CommandId commandId, EndpointSendResult result) =>
        result.Status switch
        {
            EndpointOperationStatus.Completed => new DeviceCommandResult(commandId, DeviceCommandStatus.AcceptedByTransport),
            EndpointOperationStatus.Rejected => new DeviceCommandResult(commandId, DeviceCommandStatus.Rejected, "transport.rejected", "传输层拒绝写入。"),
            EndpointOperationStatus.Failed => new DeviceCommandResult(commandId, DeviceCommandStatus.TransportFailed, "transport.failed", "传输层写入失败。"),
            EndpointOperationStatus.OutcomeUnknown => new DeviceCommandResult(commandId, DeviceCommandStatus.OutcomeUnknown, "transport.outcome_unknown", "写入结果未知。"),
            _ => new DeviceCommandResult(commandId, DeviceCommandStatus.TransportFailed, "transport.invalid", "传输层返回未知状态。")
        };

    public virtual ValueTask DisposeAsync()
    {
        _telemetry.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}