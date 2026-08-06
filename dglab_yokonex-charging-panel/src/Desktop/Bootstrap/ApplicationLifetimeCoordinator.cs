namespace ChargingPanel.Desktop.Bootstrap;

/// <summary>
/// 关闭阶段。顺序即语义：先停止外部接单，再锁闭安全，
/// 然后释放 Session 与连接池，最后关闭存储与日志。
///
/// 该顺序不可调整：若先关连接池再锁闭安全，
/// 已在途的命令可能在无人监管的情况下继续产生物理输出；
/// 若先关存储与日志，后续阶段的失败就无法被记录。
/// </summary>
public enum ApplicationShutdownStage
{
    /// <summary>停止对外接单：本地服务端、MOD 桥接、房间发现等入口。</summary>
    ExternalIntake,

    /// <summary>安全锁闭并尽力安全停止所有设备输出。</summary>
    SafetyLatch,

    /// <summary>释放设备 Session。</summary>
    Sessions,

    /// <summary>释放传输连接池与 Broker 持有的平台句柄。</summary>
    ConnectionPools,

    /// <summary>关闭数据库与日志。</summary>
    StorageAndLogging
}

/// <summary>
/// 关闭参与者。每个组件声明自己属于哪个阶段，由协调器统一排序调用。
/// </summary>
public interface IApplicationShutdownParticipant
{
    ApplicationShutdownStage Stage { get; }

    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 以委托实现的关闭参与者。用于把迁移期 legacy 服务的停止/释放
/// 纳入统一的分阶段顺序，而不必为每个 legacy 步骤新增一个类型。
/// </summary>
public sealed class DelegateShutdownParticipant : IApplicationShutdownParticipant
{
    private readonly Func<CancellationToken, Task> _stopAsync;

    public DelegateShutdownParticipant(
        ApplicationShutdownStage stage,
        Func<CancellationToken, Task> stopAsync)
    {
        ArgumentNullException.ThrowIfNull(stopAsync);
        Stage = stage;
        _stopAsync = stopAsync;
    }

    public ApplicationShutdownStage Stage { get; }

    public Task StopAsync(CancellationToken cancellationToken) => _stopAsync(cancellationToken);
}

/// <summary>单阶段关闭结果。失败时保留原始异常供宿主记录，不吞掉。</summary>
public sealed record ApplicationShutdownStageResult
{
    public ApplicationShutdownStageResult(ApplicationShutdownStage stage, Exception? exception)
    {
        Stage = stage;
        Exception = exception;
    }

    public ApplicationShutdownStage Stage { get; }

    public Exception? Exception { get; }

    public bool IsSuccessful => Exception is null;
}

/// <summary>整体关闭结果。任一阶段失败都不得被汇总成成功。</summary>
public sealed record ApplicationShutdownResult
{
    public ApplicationShutdownResult(IReadOnlyList<ApplicationShutdownStageResult> stageResults)
    {
        ArgumentNullException.ThrowIfNull(stageResults);
        StageResults = stageResults;
    }

    public IReadOnlyList<ApplicationShutdownStageResult> StageResults { get; }

    public bool IsSuccessful => StageResults.All(stage => stage.IsSuccessful);
}

/// <summary>
/// 应用生命周期协调器。关闭是唯一路径、严格有序、幂等。
///
/// 关键约束：单个阶段失败必须继续执行后续阶段——
/// 否则一个卡住的 Session 会让数据库与日志永不关闭；
/// 但失败绝不能被隐藏，全部阶段结果都返回给宿主记录。
/// </summary>
public sealed class ApplicationLifetimeCoordinator
{
    private readonly List<IApplicationShutdownParticipant> _participants;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _stopLock = new(1, 1);
    private readonly object _participantGate = new();
    private ApplicationShutdownResult? _result;

    public ApplicationLifetimeCoordinator(
        IEnumerable<IApplicationShutdownParticipant> participants,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _participants = [.. participants];
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 追加关闭参与者。迁移期的 legacy 服务在启动后才存在，
    /// 因此必须能在构造之后登记，且仍按阶段顺序执行。
    ///
    /// 关闭已完成后拒绝登记：此时该参与者永不会被执行，
    /// 静默忽略等于让它持有的资源永久泄漏。
    /// </summary>
    public void AddParticipant(IApplicationShutdownParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        lock (_participantGate)
        {
            if (_result is not null)
            {
                throw new InvalidOperationException(
                    "关闭已完成，不能再登记关闭参与者。");
            }

            _participants.Add(participant);
        }
    }

    /// <summary>
    /// 按阶段顺序关闭。重复调用返回同一结果，
    /// 避免窗口关闭与进程退出各触发一次导致二次释放。
    /// </summary>
    public async Task<ApplicationShutdownResult> StopAsync(
        CancellationToken cancellationToken = default)
    {
        await _stopLock.WaitAsync(cancellationToken);
        try
        {
            if (_result is not null)
            {
                return _result;
            }

            var results = new List<ApplicationShutdownStageResult>();
            foreach (var stage in Enum.GetValues<ApplicationShutdownStage>())
            {
                results.Add(await StopStageAsync(stage, cancellationToken));
            }

            var result = new ApplicationShutdownResult(results);
            lock (_participantGate)
            {
                // 在同一把锁内发布结果：之后的 AddParticipant 必须能观察到
                // "关闭已完成"，否则新参与者会被静默丢弃。
                _result = result;
            }

            return result;
        }
        finally
        {
            _stopLock.Release();
        }
    }

    private async Task<ApplicationShutdownStageResult> StopStageAsync(
        ApplicationShutdownStage stage,
        CancellationToken cancellationToken)
    {
        IApplicationShutdownParticipant[] stageParticipants;
        lock (_participantGate)
        {
            // 在锁内快照：迁移期 legacy 参与者可能在关闭开始后才登记。
            stageParticipants = _participants.Where(item => item.Stage == stage).ToArray();
        }

        var exceptions = new List<Exception>();
        foreach (var participant in stageParticipants)
        {
            try
            {
                await participant.StopAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // 同阶段内其余参与者仍需被停止，不能因一个失败就跳过。
                exceptions.Add(ex);
            }
        }

        return exceptions.Count switch
        {
            0 => new ApplicationShutdownStageResult(stage, null),
            // 单个异常直接保留原对象，便于调用方按引用断言与排障。
            1 => new ApplicationShutdownStageResult(stage, exceptions[0]),
            _ => new ApplicationShutdownStageResult(
                stage,
                new AggregateException($"关闭阶段 {stage} 有多个参与者失败。", exceptions))
        };
    }
}
