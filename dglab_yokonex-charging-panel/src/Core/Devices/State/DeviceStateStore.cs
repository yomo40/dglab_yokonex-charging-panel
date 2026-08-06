using System.Collections.Immutable;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Telemetry;

namespace ChargingPanel.Core.Devices.State;

/// <summary>
/// 不可变设备状态快照。每份快照都绑定具体 SessionEpoch，
/// 调用方据此判断状态属于哪一次连接。
/// </summary>
public sealed record DeviceStateSnapshot
{
    public DeviceStateSnapshot(
        DeviceInstanceId instanceId,
        long sessionEpoch,
        bool isSessionOpen,
        ImmutableDictionary<CapabilityId, DeviceTelemetryEnvelope> telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        InstanceId = instanceId;
        SessionEpoch = sessionEpoch;
        IsSessionOpen = isSessionOpen;
        Telemetry = telemetry;
    }

    public DeviceInstanceId InstanceId { get; }

    public long SessionEpoch { get; }

    public bool IsSessionOpen { get; }

    public IReadOnlyDictionary<CapabilityId, DeviceTelemetryEnvelope> Telemetry { get; }
}

/// <summary>
/// 设备状态存储。丢弃旧 Epoch、乱序与已关闭 Session 的遥测更新，
/// 避免把陈旧或不属于当前连接的数据当作设备现状。
/// </summary>
public sealed class DeviceStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<DeviceInstanceId, SessionSlot> _slots = [];

    /// <summary>
    /// 打开或切换 Session。Epoch 前进时清空遥测：
    /// 新连接不得继承旧连接的状态值，否则 UI 会展示不属于当前连接的数据。
    /// </summary>
    public void OpenSession(DeviceInstanceId instanceId, long sessionEpoch)
    {
        if (sessionEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionEpoch));
        }

        lock (_gate)
        {
            if (_slots.TryGetValue(instanceId, out var existing) &&
                existing.SessionEpoch > sessionEpoch)
            {
                // 拒绝回退到更旧的 Epoch。
                return;
            }

            _slots[instanceId] = new SessionSlot(sessionEpoch);
        }
    }

    /// <summary>关闭 Session。之后到达的遥测一律丢弃。</summary>
    public void CloseSession(DeviceInstanceId instanceId)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(instanceId, out var slot))
            {
                slot.IsOpen = false;
            }
        }
    }

    /// <summary>
    /// 应用一条遥测。返回是否被接受；被丢弃时返回 false 而非静默成功，
    /// 便于调用方统计丢弃率而不是误以为状态已更新。
    /// </summary>
    public bool Apply(DeviceTelemetryEnvelope telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        lock (_gate)
        {
            if (!_slots.TryGetValue(telemetry.DeviceInstanceId, out var slot))
            {
                return false;
            }

            if (!slot.IsOpen || telemetry.SessionEpoch != slot.SessionEpoch)
            {
                return false;
            }

            if (telemetry.SequenceNumber is { } sequence)
            {
                if (slot.LastSequence.TryGetValue(telemetry.CapabilityId, out var lastSequence) &&
                    sequence <= lastSequence)
                {
                    return false;
                }

                slot.LastSequence[telemetry.CapabilityId] = sequence;
            }

            slot.Telemetry[telemetry.CapabilityId] = telemetry;
            return true;
        }
    }

    public DeviceStateSnapshot? GetSnapshot(DeviceInstanceId instanceId)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(instanceId, out var slot))
            {
                return null;
            }

            return new DeviceStateSnapshot(
                instanceId,
                slot.SessionEpoch,
                slot.IsOpen,
                slot.Telemetry.ToImmutableDictionary());
        }
    }

    private sealed class SessionSlot(long sessionEpoch)
    {
        public long SessionEpoch { get; } = sessionEpoch;

        public bool IsOpen { get; set; } = true;

        public Dictionary<CapabilityId, DeviceTelemetryEnvelope> Telemetry { get; } = [];

        public Dictionary<CapabilityId, long> LastSequence { get; } = [];
    }
}
