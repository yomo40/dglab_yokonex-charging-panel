using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Serilog;

namespace ChargingPanel.Core.Services;

/// <summary>
/// 联机仲裁引擎
/// 统一处理多人房间的控制权租约、命令去重与模式策略。
/// </summary>
public sealed class RoomArbitrationService
{
    private readonly ILogger _logger = Log.ForContext<RoomArbitrationService>();
    private readonly ConcurrentDictionary<string, ControlLease> _leases = new();
    private readonly ConcurrentDictionary<string, DateTime> _commandDedup = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _modeLock = new();

    private RoomControlMode _mode = RoomControlMode.SingleControl;
    private static readonly TimeSpan DedupTtl = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DefaultLeaseTtl = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MinLeaseTtl = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxLeaseTtl = TimeSpan.FromMinutes(3);

    public RoomControlMode Mode
    {
        get
        {
            lock (_modeLock)
            {
                return _mode;
            }
        }
    }

    public void Reset(RoomControlMode mode = RoomControlMode.SingleControl)
    {
        lock (_modeLock)
        {
            _mode = mode;
        }
        _leases.Clear();
        _commandDedup.Clear();
    }

    public void SetMode(RoomControlMode mode)
    {
        lock (_modeLock)
        {
            _mode = mode;
        }
    }

    public RoomArbitrationDecision Evaluate(RoomArbitrationRequest request)
    {
        CleanupExpired();

        if (string.IsNullOrWhiteSpace(request.SenderId))
        {
            return Denied(RoomArbitrationReason.InvalidRequest, "senderId 为空");
        }

        if (string.IsNullOrWhiteSpace(request.TargetUserId))
        {
            return Denied(RoomArbitrationReason.InvalidRequest, "targetId 为空");
        }

        if (!string.IsNullOrWhiteSpace(request.CommandId))
        {
            var now = DateTime.UtcNow;
            if (!_commandDedup.TryAdd(request.CommandId, now))
            {
                return Denied(RoomArbitrationReason.DuplicateCommand, "重复命令");
            }
        }

        var action = request.Action?.Trim().ToLowerInvariant() ?? string.Empty;
        switch (action)
        {
            case "control_release":
                ReleaseControl(request.TargetUserId, request.SenderId);
                return Allowed("控制权已释放");

            case "control_request":
                return TryAcquireLeaseDecision(request, explicitRequest: true);

            case "pvp_start":
                SetMode(RoomControlMode.PvpDuel);
                return TryAcquireLeaseDecision(request, explicitRequest: true);

            case "pvp_stop":
                ReleaseControl(request.TargetUserId, request.SenderId);
                SetMode(RoomControlMode.SingleControl);
                return Allowed("PVP 已停止并释放控制权");
        }

        if (!RequiresLease(action))
        {
            return Allowed("无需租约的命令");
        }

        return TryAcquireLeaseDecision(request, explicitRequest: false);
    }

    private RoomArbitrationDecision TryAcquireLeaseDecision(RoomArbitrationRequest request, bool explicitRequest)
    {
        var now = DateTime.UtcNow;
        var ttl = request.LeaseTtl ?? DefaultLeaseTtl;
        if (ttl < MinLeaseTtl) ttl = MinLeaseTtl;
        if (ttl > MaxLeaseTtl) ttl = MaxLeaseTtl;

        var currentMode = Mode;
        if (currentMode == RoomControlMode.PvpDuel && request.Action?.Equals("pvp_start", StringComparison.OrdinalIgnoreCase) != true)
        {
            // PVP 模式下允许处理正常控制命令，但仍需遵循租约。
        }

        var next = new ControlLease(request.TargetUserId, request.SenderId, request.Priority, now.Add(ttl));
        while (true)
        {
            if (!_leases.TryGetValue(request.TargetUserId, out var current))
            {
                if (_leases.TryAdd(request.TargetUserId, next))
                {
                    _logger.Debug("Acquire control lease: target={Target}, owner={Owner}, priority={Priority}",
                        request.TargetUserId, request.SenderId, request.Priority);
                    return Allowed(explicitRequest ? "控制请求通过" : "获取控制权成功");
                }
                continue;
            }

            if (current.ExpiresAtUtc <= now)
            {
                if (_leases.TryUpdate(request.TargetUserId, next, current))
                {
                    return Allowed("旧租约已过期，控制权已更新");
                }
                continue;
            }

            if (current.OwnerUserId.Equals(request.SenderId, StringComparison.OrdinalIgnoreCase))
            {
                if (_leases.TryUpdate(request.TargetUserId, next, current))
                {
                    return Allowed("续约成功");
                }
                continue;
            }

            if (request.Priority > current.Priority)
            {
                if (_leases.TryUpdate(request.TargetUserId, next, current))
                {
                    return Allowed("高优先级接管控制权");
                }
                continue;
            }

            return Denied(RoomArbitrationReason.LeaseOccupied, $"目标正被 {current.OwnerUserId} 控制");
        }
    }

    public bool TryAcquireControl(string targetUserId, string requesterId, int priority, TimeSpan ttl)
    {
        var now = DateTime.UtcNow;
        var next = new ControlLease(targetUserId, requesterId, priority, now.Add(ttl));

        while (true)
        {
            if (!_leases.TryGetValue(targetUserId, out var current))
            {
                if (_leases.TryAdd(targetUserId, next))
                {
                    _logger.Debug("Acquire control lease: target={Target}, owner={Owner}, priority={Priority}", targetUserId, requesterId, priority);
                    return true;
                }
                continue;
            }

            if (current.ExpiresAtUtc <= now || priority >= current.Priority)
            {
                if (_leases.TryUpdate(targetUserId, next, current))
                {
                    _logger.Debug("Replace control lease: target={Target}, owner={Owner}, priority={Priority}", targetUserId, requesterId, priority);
                    return true;
                }
                continue;
            }

            return false;
        }
    }

    public void ReleaseControl(string targetUserId, string requesterId)
    {
        if (_leases.TryGetValue(targetUserId, out var current) && current.OwnerUserId == requesterId)
        {
            _leases.TryRemove(targetUserId, out _);
        }
    }

    public string? GetControlOwner(string targetUserId)
    {
        if (!_leases.TryGetValue(targetUserId, out var current))
        {
            return null;
        }

        if (current.ExpiresAtUtc <= DateTime.UtcNow)
        {
            _leases.TryRemove(targetUserId, out _);
            return null;
        }

        return current.OwnerUserId;
    }

    public IReadOnlyCollection<string> GetActiveTargetsByOwner(string ownerUserId)
    {
        var now = DateTime.UtcNow;
        return _leases.Values
            .Where(l => l.ExpiresAtUtc > now && l.OwnerUserId.Equals(ownerUserId, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.TargetUserId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void CleanupExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _leases.ToArray())
        {
            if (kvp.Value.ExpiresAtUtc <= now)
            {
                _leases.TryRemove(kvp.Key, out _);
            }
        }

        foreach (var kvp in _commandDedup.ToArray())
        {
            if (now - kvp.Value > DedupTtl)
            {
                _commandDedup.TryRemove(kvp.Key, out _);
            }
        }
    }

    private static bool RequiresLease(string action)
    {
        return action switch
        {
            "set_strength" => true,
            "increase_strength" => true,
            "decrease_strength" => true,
            "send_waveform" => true,
            "clear_queue" => true,
            "trigger_event" => true,
            "emergency_stop" => true,
            _ => false
        };
    }

    private static RoomArbitrationDecision Allowed(string message)
        => new(true, RoomArbitrationReason.Allowed, message);

    private static RoomArbitrationDecision Denied(RoomArbitrationReason reason, string message)
        => new(false, reason, message);

    private sealed record ControlLease(string TargetUserId, string OwnerUserId, int Priority, DateTime ExpiresAtUtc);
}

public enum RoomControlMode
{
    SingleControl = 1,
    OneToMany = 2,
    PvpDuel = 3
}

public enum RoomArbitrationReason
{
    Allowed,
    InvalidRequest,
    PermissionDenied,
    TargetOffline,
    LeaseOccupied,
    DuplicateCommand,
    ModeConflict
}

public sealed record RoomArbitrationRequest(
    string SenderId,
    string TargetUserId,
    string? Action,
    string? CommandId = null,
    int Priority = 0,
    TimeSpan? LeaseTtl = null);

public sealed record RoomArbitrationDecision(
    bool Allowed,
    RoomArbitrationReason Reason,
    string Message);
