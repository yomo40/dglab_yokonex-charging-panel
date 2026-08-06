namespace ChargingPanel.Core.Devices.Safety;

/// <summary>
/// 锁闭拒绝的唯一权威定义。Dispatcher 与应用层都必须复用同一常量，
/// 否则两处独立字面量会随时间漂移，调用方难以稳定识别锁闭原因。
/// </summary>
public static class SafetyRejection
{
    public const string LatchedReasonCode = "safety.latched";

    public const string LatchedMessage = "安全锁闭生效，已拒绝普通控制命令。";
}

/// <summary>
/// 不可变安全状态快照。锁闭与 SafetyEpoch 必须原子一起推进，
/// 否则命令可能带着旧 Epoch 通过校验。
/// </summary>
public sealed record SafetyState
{
    public SafetyState(bool isLatched, long safetyEpoch, string? latchReason, DateTimeOffset? latchedAt)
    {
        if (safetyEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(safetyEpoch));
        }

        IsLatched = isLatched;
        SafetyEpoch = safetyEpoch;
        LatchReason = string.IsNullOrWhiteSpace(latchReason) ? null : latchReason.Trim();
        LatchedAt = latchedAt;
    }

    /// <summary>是否处于安全锁闭状态；锁闭期间必须拒绝一切普通控制命令。</summary>
    public bool IsLatched { get; }

    /// <summary>安全纪元；每次急停原子递增，用于丢弃旧命令与旧回调。</summary>
    public long SafetyEpoch { get; }

    public string? LatchReason { get; }

    public DateTimeOffset? LatchedAt { get; }

    public static SafetyState Unlatched(long safetyEpoch) => new(false, safetyEpoch, null, null);
}

/// <summary>
/// 复位请求。复位必须由本地人工显式授权，不能由自动流程隐式解除锁闭。
/// </summary>
public sealed record SafetyResetRequest
{
    public SafetyResetRequest(bool explicitlyAuthorized, string principal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        ExplicitlyAuthorized = explicitlyAuthorized;
        Principal = principal.Trim();
    }

    public bool ExplicitlyAuthorized { get; }

    public string Principal { get; }
}
