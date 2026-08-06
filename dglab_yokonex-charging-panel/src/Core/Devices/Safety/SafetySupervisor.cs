using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Devices.Runtime;

namespace ChargingPanel.Core.Devices.Safety;

/// <summary>
/// 单实例安全停止结果。
/// </summary>
public sealed record InstanceSafeStopOutcome(DeviceInstanceId InstanceId, SafeStopStatus Status, string? ReasonCode);

/// <summary>
/// 急停汇总结果。任一实例失败都不得被汇总成整体成功。
/// </summary>
public sealed record EmergencyStopReport
{
    public EmergencyStopReport(bool driverSafeStopAttempted, IReadOnlyList<InstanceSafeStopOutcome> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        DriverSafeStopAttempted = driverSafeStopAttempted;
        Results = results;
    }

    public bool DriverSafeStopAttempted { get; }

    public IReadOnlyList<InstanceSafeStopOutcome> Results { get; }

    /// <summary>仅当所有实例都确认停止时才为真。</summary>
    public bool AllConfirmedStopped =>
        Results.Count > 0 && Results.All(result => result.Status == SafeStopStatus.ConfirmedStopped);
}

/// <summary>
/// 安全督导：先锁闭、后停止。锁闭与 SafetyEpoch 递增必须原子完成，
/// 之后才允许调用 Driver 的安全停止，避免停止期间仍有新输出入队。
/// </summary>
public sealed class SafetySupervisor
{
    private static readonly TimeSpan DefaultSafeStopTimeout = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _safeStopTimeout;
    private readonly object _gate = new();
    private SafetyState _current;
    private IDeviceRuntimeRegistry? _registry;

    public SafetySupervisor(TimeProvider timeProvider, TimeSpan? safeStopTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (safeStopTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(safeStopTimeout));
        }

        _timeProvider = timeProvider;
        _safeStopTimeout = safeStopTimeout ?? DefaultSafeStopTimeout;
        _current = SafetyState.Unlatched(0);
    }

    public SafetyState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// 绑定运行时注册表。急停需要枚举所有活动实例，但 Core 不应反向依赖组合根。
    /// </summary>
    public void AttachRuntime(IDeviceRuntimeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        lock (_gate)
        {
            _registry = registry;
        }
    }

    public async Task<EmergencyStopReport> EmergencyStopAllAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        IDeviceRuntimeRegistry? registry;
        long safetyEpoch;

        // 第一步：原子置闩并递增 Epoch。必须在任何 Driver 调用之前完成。
        lock (_gate)
        {
            _current = new SafetyState(true, _current.SafetyEpoch + 1, reason, _timeProvider.GetUtcNow());
            safetyEpoch = _current.SafetyEpoch;
            registry = _registry;
        }

        if (registry is null)
        {
            return new EmergencyStopReport(false, []);
        }

        // 第二步：并发对每个已知实例执行安全停止。
        // 必须并发且带单实例超时：顺序执行时，一个无响应设备会让其后所有设备永远得不到停止。
        var targets = registry.ActiveInstanceIds
            .Select(instanceId => (InstanceId: instanceId, Binding: registry.Resolve(instanceId)))
            .Where(item => item.Binding is not null)
            .ToArray();

        if (targets.Length == 0)
        {
            return new EmergencyStopReport(false, []);
        }

        var outcomes = await Task.WhenAll(targets.Select(target =>
            StopInstanceAsync(target.InstanceId, target.Binding!, safetyEpoch, cancellationToken)));

        // 第三步：保持锁闭。无论停止结果如何，都不在此处解除。
        return new EmergencyStopReport(true, outcomes);
    }

    /// <summary>
    /// 停止单个实例。超时或异常一律记为 OutcomeUnknown，绝不谎报已停止。
    /// 若 Driver 不响应取消，这里只停止等待，不声称设备已安全。
    /// </summary>
    private async Task<InstanceSafeStopOutcome> StopInstanceAsync(
        DeviceInstanceId instanceId,
        DeviceRuntimeBinding binding,
        long safetyEpoch,
        CancellationToken cancellationToken)
    {
        var context = new SafeStopContext(
            instanceId,
            safetyEpoch,
            binding.SessionEpoch,
            _timeProvider.GetUtcNow());
        try
        {
            var result = await binding.Driver
                .SafeStopAsync(context, cancellationToken)
                .AsTask()
                .WaitAsync(_safeStopTimeout, _timeProvider);
            return new InstanceSafeStopOutcome(instanceId, result.Status, result.ReasonCode);
        }
        catch (TimeoutException)
        {
            return new InstanceSafeStopOutcome(
                instanceId,
                SafeStopStatus.OutcomeUnknown,
                "safety.stop_timeout");
        }
        catch (OperationCanceledException)
        {
            // 取消时结果未知：不得报告为已停止。
            return new InstanceSafeStopOutcome(
                instanceId,
                SafeStopStatus.OutcomeUnknown,
                "safety.stop_cancelled");
        }
        catch (Exception)
        {
            return new InstanceSafeStopOutcome(
                instanceId,
                SafeStopStatus.OutcomeUnknown,
                "safety.stop_threw");
        }
    }

    /// <summary>
    /// 复位。必须显式人工授权；未授权时保持锁闭并返回 false。
    /// </summary>
    public Task<bool> ResetAsync(SafetyResetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!request.ExplicitlyAuthorized)
            {
                return Task.FromResult(false);
            }

            _current = SafetyState.Unlatched(_current.SafetyEpoch);
            return Task.FromResult(true);
        }
    }
}
