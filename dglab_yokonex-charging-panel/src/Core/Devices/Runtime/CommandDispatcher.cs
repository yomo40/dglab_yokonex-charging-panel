using System.Collections.Concurrent;
using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Devices.Safety;

namespace ChargingPanel.Core.Devices.Runtime;

/// <summary>
/// 命令唯一入口。校验顺序固定为：
/// SafetyLatch → 实例/Session → 能力+operation → Deadline/Epoch → Actor 排队 → Driver。
/// 任何绕过本类的物理输出都属于架构违规。
/// </summary>
public sealed class CommandDispatcher : IDisposable
{
    private readonly SafetySupervisor _safety;
    private readonly IDeviceRuntimeRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<DeviceInstanceId, DeviceCommandActor> _actors = new();
    private bool _disposed;

    public CommandDispatcher(
        SafetySupervisor safety,
        IDeviceRuntimeRegistry registry,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(safety);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _safety = safety;
        _registry = registry;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 解析实例的运行时绑定，供应用层在构造信封前读取能力与 SessionEpoch。
    /// 只读查询，不产生任何物理输出。
    /// </summary>
    public DeviceRuntimeBinding? ResolveBinding(DeviceInstanceId instanceId) =>
        _registry.Resolve(instanceId);

    public async Task<DeviceCommandResult> DispatchAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 1. SafetyLatch：锁闭期间拒绝一切普通命令。
        var safety = _safety.Current;
        if (safety.IsLatched)
        {
            return Reject(command, SafetyRejection.LatchedReasonCode, SafetyRejection.LatchedMessage);
        }

        // 2. 实例与 Session。
        var binding = _registry.Resolve(command.DeviceInstanceId);
        if (binding is null)
        {
            return Reject(command, "instance.unavailable", "设备实例不存在或没有活动 Session。");
        }

        // 3. 能力与 operation 声明。
        var capability = binding.Capabilities
            .FirstOrDefault(item => item.Id == command.CapabilityId);
        if (capability is null)
        {
            return Reject(command, "capability.unsupported", "设备未声明该能力。");
        }

        // 4. Deadline 与 Epoch。过期流式帧必须丢弃而非补发。
        if (command.IsExpired(_timeProvider))
        {
            return new DeviceCommandResult(
                command.CommandId,
                DeviceCommandStatus.Expired,
                "command.expired",
                "命令已超过截止时间。");
        }

        if (command.SafetyEpoch != safety.SafetyEpoch)
        {
            return new DeviceCommandResult(
                command.CommandId,
                DeviceCommandStatus.Superseded,
                "safety.epoch_mismatch",
                "命令携带的安全纪元已过期。");
        }

        if (command.SessionEpoch != binding.SessionEpoch)
        {
            return new DeviceCommandResult(
                command.CommandId,
                DeviceCommandStatus.Superseded,
                "session.epoch_mismatch",
                "命令携带的会话纪元已过期。");
        }

        // 5. Actor 排队并执行。
        var actor = _actors.GetOrAdd(
            command.DeviceInstanceId,
            static id => new DeviceCommandActor(id));

        return await actor.EnqueueAsync(
            command,
            async (envelope, token) =>
            {
                // 执行前复检锁闭：入队等待期间可能发生急停。
                if (_safety.Current.IsLatched)
                {
                    return Reject(envelope, SafetyRejection.LatchedReasonCode, SafetyRejection.LatchedMessage);
                }

                return await binding.Driver.ExecuteAsync(envelope, token);
            },
            cancellationToken);
    }

    private static DeviceCommandResult Reject(
        DeviceCommandEnvelope command,
        string reasonCode,
        string message) =>
        new(command.CommandId, DeviceCommandStatus.Rejected, reasonCode, message);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var actor in _actors.Values)
        {
            actor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _actors.Clear();
    }
}
