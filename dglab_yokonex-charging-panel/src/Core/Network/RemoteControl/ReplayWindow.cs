using System.Collections.Concurrent;
using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.Core.Network.RemoteControl;

/// <summary>重放检查结果。</summary>
public enum ReplayCheckResult
{
    Ok,
    ExpiredTimestamp,
    ReplayedCommandId,
    OutOfOrderNonce
}

/// <summary>
/// 重放窗口：按主体拒绝重复 CommandId、乱序 nonce 与过期/超前时间戳。
/// CommandId 一次性使用；nonce 必须严格递增。
/// </summary>
public sealed class ReplayWindow
{
    private readonly TimeSpan _maxTimestampSkew;
    private readonly TimeSpan _commandIdRetention;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, DateTimeOffset>> _seenCommandIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _lastNonces = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _nonceGate = new();

    public ReplayWindow(
        TimeSpan? maxTimestampSkew = null,
        TimeSpan? commandIdRetention = null)
    {
        _maxTimestampSkew = maxTimestampSkew ?? TimeSpan.FromSeconds(30);
        _commandIdRetention = commandIdRetention ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>
    /// 检查并消费一个命令的重放窗口状态。
    /// 只有全部检查通过才推进状态；任何失败都不会被误判为成功。
    /// </summary>
    public ReplayCheckResult CheckAndConsume(
        string principal,
        CommandId commandId,
        long nonce,
        DateTimeOffset timestamp,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _ = commandId.Value;

        // 1. 时间戳窗口：过期或超前都拒绝。
        var now = timeProvider.GetUtcNow();
        var age = now - timestamp;
        if (age > _maxTimestampSkew || age < -_maxTimestampSkew)
        {
            return ReplayCheckResult.ExpiredTimestamp;
        }

        // 2. CommandId 一次性：重复使用直接拒绝。
        var seen = _seenCommandIds.GetOrAdd(principal, static _ => new ConcurrentDictionary<Guid, DateTimeOffset>());
        if (!seen.TryAdd(commandId.Value, timestamp))
        {
            return ReplayCheckResult.ReplayedCommandId;
        }

        if (seen.Count > 4096)
        {
            CleanupExpired(seen, now);
        }

        // 3. nonce 严格递增：乱序/重复 nonce 拒绝。
        lock (_nonceGate)
        {
            if (_lastNonces.TryGetValue(principal, out var last) && nonce <= last)
            {
                return ReplayCheckResult.OutOfOrderNonce;
            }

            _lastNonces[principal] = nonce;
        }

        return ReplayCheckResult.Ok;
    }

    private void CleanupExpired(ConcurrentDictionary<Guid, DateTimeOffset> seen, DateTimeOffset now)
    {
        foreach (var kv in seen)
        {
            if (now - kv.Value > _commandIdRetention)
            {
                seen.TryRemove(kv.Key, out _);
            }
        }
    }
}
