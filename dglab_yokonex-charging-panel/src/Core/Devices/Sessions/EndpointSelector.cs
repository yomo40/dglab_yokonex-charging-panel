using System.Collections.Concurrent;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;

namespace ChargingPanel.Core.Devices.Sessions;

/// <summary>
/// Endpoint 连接候选。仅描述"可以尝试连哪里"，
/// 不承载实例归属语义——不同 Endpoint 绝不因被同时选中而合并为同一实例。
/// </summary>
public sealed record EndpointCandidate(
    EndpointId EndpointId,
    ProtocolProfileId ProfileId,
    TransportKind TransportKind,
    int Priority,
    bool Enabled);

/// <summary>
/// Endpoint 选择器。按 enabled、用户优先级、平台可用性和近期健康排序，
/// 只决定连接候选顺序，不做连接、不做合并、不改实例归属。
/// </summary>
public sealed class EndpointSelector
{
    private static readonly TimeSpan FailurePenaltyWindow = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlySet<TransportKind>? _supportedTransports;
    private readonly ConcurrentDictionary<EndpointId, DateTimeOffset> _recentFailures = new();

    public EndpointSelector(
        TimeProvider timeProvider,
        IReadOnlyCollection<TransportKind>? supportedTransports = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _supportedTransports = supportedTransports is null ? null : supportedTransports.ToHashSet();
    }

    /// <summary>
    /// 记录一次连接失败，用于短期降级该 Endpoint。
    /// 降级而非剔除：唯一通路失败时仍必须保留重试机会。
    /// </summary>
    public void RecordFailure(EndpointId endpointId) =>
        _recentFailures[endpointId] = _timeProvider.GetUtcNow();

    /// <summary>记录一次连接成功，清除降级。</summary>
    public void RecordSuccess(EndpointId endpointId) =>
        _recentFailures.TryRemove(endpointId, out _);

    /// <summary>
    /// 按优先级返回候选顺序。禁用与平台不支持的 Endpoint 一律排除；
    /// 近期失败的排到同优先级之后。
    /// </summary>
    public IReadOnlyList<EndpointCandidate> SelectCandidates(
        IEnumerable<EndpointCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var now = _timeProvider.GetUtcNow();

        return candidates
            .Where(candidate => candidate.Enabled)
            .Where(candidate => _supportedTransports is null ||
                                _supportedTransports.Contains(candidate.TransportKind))
            .OrderBy(candidate => IsRecentlyFailed(candidate.EndpointId, now) ? 1 : 0)
            .ThenBy(candidate => candidate.Priority)
            .ToArray();
    }

    private bool IsRecentlyFailed(EndpointId endpointId, DateTimeOffset now) =>
        _recentFailures.TryGetValue(endpointId, out var failedAt) &&
        now - failedAt < FailurePenaltyWindow;
}
