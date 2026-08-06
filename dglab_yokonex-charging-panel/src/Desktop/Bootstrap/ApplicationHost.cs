using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices.Runtime;
using ChargingPanel.Core.Devices.Safety;
using ChargingPanel.Core.Logging;
using ChargingPanel.Infrastructure.Windows.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace ChargingPanel.Desktop.Bootstrap;

/// <summary>
/// 组合根选项。数据目录与运行时开关都由宿主显式传入，便于测试隔离。
/// </summary>
public sealed record ApplicationHostOptions
{
    /// <summary>数据目录；为空时使用当前用户的 ApplicationData。</summary>
    public string? DataPath { get; init; }

    public DeviceRuntimeV2Options DeviceRuntimeV2 { get; init; } = new();

    public string ResolveDataPath() =>
        string.IsNullOrWhiteSpace(DataPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ChargingPanel",
                "data")
            : Path.GetFullPath(DataPath);
}

/// <summary>Runtime v2 启动结果：诊断信息 + 是否失败（失败时安全闩已锁闭）。</summary>
public sealed record V2StartupResult
{
    public bool Failed { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 应用组合根。整个进程中只有此类型持有根容器；
/// Core 与 Vendor 不得依赖 <see cref="IServiceProvider"/> 或 <see cref="IServiceCollection"/>。
///
/// Task 22 起旧 <c>AppServices</c> 已删除，本类型是业务服务与 Runtime v2 的唯一组合根；
/// UI 通过 <see cref="Instance"/> 与 <see cref="TryGetService{T}"/> 解析服务。
/// </summary>
public sealed class ApplicationHost : IAsyncDisposable
{
    private readonly ServiceProvider _rootProvider;

    private ApplicationHost(ServiceProvider rootProvider, string dataPath)
    {
        _rootProvider = rootProvider;
        DataPath = dataPath;
        Instance = this;
    }

    /// <summary>当前进程组合根。由 <see cref="Create"/> 在进程启动时设置。</summary>
    public static ApplicationHost? Instance { get; private set; }

    /// <summary>已解析的数据目录。</summary>
    public string DataPath { get; }

    /// <summary>
    /// 从组合根解析服务；未初始化或未注册时返回 null（与旧 AppServices.GetService 语义一致）。
    /// </summary>
    public static T? TryGetService<T>() where T : class
    {
        var instance = Instance;
        return instance is null ? null : instance._rootProvider.GetService<T>();
    }

    public static ApplicationHost Create(ApplicationHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var services = new ServiceCollection();
        services.AddDeviceRuntimeV2(options);
        var provider = services.BuildServiceProvider();
        return new ApplicationHost(provider, options.ResolveDataPath());
    }

    public T GetRequiredService<T>() where T : notnull => _rootProvider.GetRequiredService<T>();

    /// <summary>
    /// Runtime v2 启动编排：
    /// 1) 数据库/日志初始化（旧 AppServices 职责迁移）→ 2) schema v2 迁移
    /// → 3) 插件目录加载 → 4) 业务服务启动。
    /// legacy 路径已删除，失败时锁闭安全闩并保留诊断，绝不"两个运行时一起跑作为回退"。
    /// </summary>
    public async Task<V2StartupResult> StartRuntimeV2Async(CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<string>();
        try
        {
            // 1. 初始化数据库、日志与默认脚本（原 AppServices 职责）。
            Directory.CreateDirectory(DataPath);
            var dbPath = Path.Combine(DataPath, "device_adapter.db");
            Database.Initialize(dbPath);
            LogManager.Initialize(Path.Combine(DataPath, "logs"));
            var scriptsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts");
            Database.Instance.ImportDefaultScripts(scriptsPath);
            diagnostics.Add("数据库与默认脚本已初始化。");

            // 2. schema v2 迁移：把 legacy device 表迁移为 v2 持久化实例。
            var secretStore = _rootProvider.GetRequiredService<Core.Security.ISecretStore>();
            var migrator = new DeviceSchemaMigrator(dbPath, secretStore);
            var migration = await migrator.MigrateAsync(cancellationToken);
            diagnostics.Add(
                $"schema v2 迁移: {(migration.Applied ? "已应用" : "已是 v2")}, " +
                $"迁移实例 {migration.MigratedInstanceCount}, 拒绝 {migration.Rejected.Count} 条");

            // 3. 插件目录：内置厂商插件必须已加载，否则没有可连接目录。
            var plugins = _rootProvider.GetRequiredService<IEnumerable<IDevicePlugin>>().ToArray();
            diagnostics.Add($"插件目录: 已加载 {plugins.Length} 个内置插件");
            if (plugins.Length == 0)
            {
                throw new InvalidOperationException("Runtime v2 插件目录为空，无法创建设备会话。");
            }

            // 4. 业务服务（OCR/事件/脚本/传感器/对战/房间/MOD 桥接）。
            var businessServices = _rootProvider.GetRequiredService<BusinessServicesHost>();
            var runtimeOptions = _rootProvider.GetRequiredService<DeviceRuntimeV2Options>();
            if (runtimeOptions.StartBusinessServices)
            {
                await businessServices.StartAsync(cancellationToken);
                diagnostics.Add("业务服务已启动（OCR/事件/脚本/传感器/对战/房间/MOD 桥接）。");
            }
            else
            {
                diagnostics.Add("业务服务已就绪（未自动启动）。");
            }

            return new V2StartupResult { Failed = false, Diagnostics = diagnostics };
        }
        catch (Exception ex)
        {
            // 迁移/插件/业务服务失败：保留诊断并锁闭安全闩，绝不回退到 legacy 双运行时。
            diagnostics.Add($"Runtime v2 启动失败: {ex.Message}");
            try
            {
                var safety = _rootProvider.GetRequiredService<SafetySupervisor>();
                await safety.EmergencyStopAllAsync("Runtime v2 启动失败", cancellationToken);
            }
            catch (Exception latchEx)
            {
                Serilog.Log.Logger.Error(latchEx, "安全闩锁闭失败");
            }

            return new V2StartupResult { Failed = true, Diagnostics = diagnostics };
        }
    }

    /// <summary>
    /// 按阶段顺序关闭后再释放容器。关闭结果由调用方记录，
    /// 不在此处吞掉失败。
    /// </summary>
    public async Task<ApplicationShutdownResult> StopAsync(
        CancellationToken cancellationToken = default) =>
        await _rootProvider
            .GetRequiredService<ApplicationLifetimeCoordinator>()
            .StopAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        // 先走有序关闭，再释放容器：反序会让参与者在依赖已释放后才被调用。
        try
        {
            await StopAsync();
        }
        catch
        {
            // 关闭失败不得阻止容器释放，否则句柄会一直泄漏。
        }

        await _rootProvider.DisposeAsync();
    }
}
