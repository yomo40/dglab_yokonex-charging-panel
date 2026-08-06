namespace ChargingPanel.Core.Devices.Sessions;

/// <summary>
/// Session 状态机。合法路径为
/// Disconnected → AcquiringLease → Connecting → Handshaking → Ready；
/// 任一步失败进入 Recovering 或 Failed。
/// </summary>
public enum SessionState
{
    /// <summary>未连接。用户主动断开后也停在此状态，不自动恢复。</summary>
    Disconnected,

    /// <summary>正在获取 Endpoint 租约。</summary>
    AcquiringLease,

    /// <summary>已获得租约，正在建立连接。</summary>
    Connecting,

    /// <summary>连接已建立，正在执行协议握手与安全配置。</summary>
    Handshaking,

    /// <summary>握手完成，可以接受命令。</summary>
    Ready,

    /// <summary>连接意外中断，已安排退避恢复。</summary>
    Recovering,

    /// <summary>连接失败且不再自动重试。</summary>
    Failed,

    /// <summary>缺少可用插件或候选 Endpoint；不得回退为其他厂商实现。</summary>
    PluginUnavailable
}

/// <summary>
/// 恢复策略：指数退避 + jitter + 上限。
/// 退避必须可取消，用户主动断开时立即停止重试。
/// </summary>
public sealed record SessionRecoveryPolicy
{
    public SessionRecoveryPolicy(
        int maxAttempts,
        TimeSpan initialDelay,
        TimeSpan maxDelay)
    {
        if (maxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        if (initialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay));
        }

        if (maxDelay < initialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDelay));
        }

        MaxAttempts = maxAttempts;
        InitialDelay = initialDelay;
        MaxDelay = maxDelay;
    }

    public int MaxAttempts { get; }

    public TimeSpan InitialDelay { get; }

    public TimeSpan MaxDelay { get; }

    public static SessionRecoveryPolicy Default { get; } =
        new(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

    /// <summary>
    /// 计算第 attempt 次重试的延迟：指数增长并加入 jitter，
    /// 避免多设备同时断链后形成同步重连风暴。
    /// </summary>
    public TimeSpan ComputeDelay(int attempt, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var exponent = Math.Min(attempt, 16);
        var scaled = InitialDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, exponent - 1));
        var capped = Math.Min(scaled, MaxDelay.TotalMilliseconds);
        // jitter 取 50%-100% 区间，既打散又不至于过短。
        var jittered = capped * (0.5 + (random.NextDouble() * 0.5));
        return TimeSpan.FromMilliseconds(jittered);
    }
}

/// <summary>
/// 连接尝试结果。SessionEpoch 用于让调用方丢弃旧连接的回调与命令。
/// </summary>
public sealed record SessionConnectResult
{
    public SessionConnectResult(bool isConnected, long sessionEpoch, string? reasonCode)
    {
        IsConnected = isConnected;
        SessionEpoch = sessionEpoch;
        ReasonCode = string.IsNullOrWhiteSpace(reasonCode) ? null : reasonCode.Trim();
    }

    public bool IsConnected { get; }

    public long SessionEpoch { get; }

    public string? ReasonCode { get; }
}
