using Serilog;

namespace ChargingPanel.Desktop.Bootstrap;

/// <summary>
/// Desktop 应用生命周期的唯一拥有者。
///
/// 架构要求：核心服务启动完成后才创建主窗口；关闭时统一等待，
/// 禁止窗口构造器 fire-and-forget 初始化与重复 Dispose。
/// Task 22 起旧 <c>AppServices</c> 路径已删除，只走 Runtime v2。
/// </summary>
public sealed class DesktopApplicationLifecycle
{
    private static readonly ILogger Logger = Log.ForContext<DesktopApplicationLifecycle>();

    private readonly ApplicationHost _host;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<string> _v2StartupDiagnostics = [];
    private bool _started;
    private bool _stopped;

    private DesktopApplicationLifecycle(ApplicationHost host)
    {
        _host = host;
    }

    public static DesktopApplicationLifecycle Create(ApplicationHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new DesktopApplicationLifecycle(ApplicationHost.Create(options));
    }

    /// <summary>组合根宿主。</summary>
    public ApplicationHost Host => _host;

    /// <summary>Runtime v2 启动诊断。</summary>
    public IReadOnlyList<string> V2StartupDiagnostics => _v2StartupDiagnostics;

    /// <summary>Runtime v2 启动是否失败（迁移/插件/业务服务阶段）。失败时安全闩已锁闭。</summary>
    public bool V2StartupFailed { get; private set; }

    /// <summary>
    /// 启动核心服务。幂等：重复调用只启动一次，
    /// 避免多个 UI 入口各自触发重复加载与端口二次绑定。
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_started)
            {
                return;
            }

            // 生产切换顺序（Step 4）：
            // 1) 数据库/日志初始化 → 2) schema v2 迁移 → 3) 插件目录加载
            // → 4) 业务服务启动。legacy 路径已删除，失败时锁闭安全闩并保留诊断，
            // 绝不"两个运行时一起跑作为回退"。编排逻辑在 ApplicationHost。
            var result = await _host.StartRuntimeV2Async(cancellationToken);
            _v2StartupDiagnostics.AddRange(result.Diagnostics);
            V2StartupFailed = result.Failed;

            _started = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 关闭。按阶段顺序等待完成，失败逐项记录而不隐藏。
    /// 幂等：窗口关闭与进程退出各触发一次也只执行一次。
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
        }
        finally
        {
            _gate.Release();
        }

        var result = await _host.StopAsync(cancellationToken);
        foreach (var stage in result.StageResults.Where(stage => !stage.IsSuccessful))
        {
            Logger.Error(stage.Exception, "关闭阶段失败: {Stage}", stage.Stage);
        }

        await _host.DisposeAsync();
    }
}
