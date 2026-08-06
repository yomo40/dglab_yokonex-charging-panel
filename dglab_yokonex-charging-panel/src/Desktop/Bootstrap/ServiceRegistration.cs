using System.Text;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Core.Application.Devices;
using ChargingPanel.Core.Automation;
using ChargingPanel.Core.Devices.Catalog;
using ChargingPanel.Core.Devices.Persistence;
using ChargingPanel.Core.Devices.Registry;
using ChargingPanel.Core.Devices.Runtime;
using ChargingPanel.Core.Devices.Safety;
using ChargingPanel.Core.Devices.Sessions;
using ChargingPanel.Core.Devices.Transport;
using ChargingPanel.Core.Events;
using ChargingPanel.Core.Network;
using ChargingPanel.Core.OCR;
using ChargingPanel.Core.Scripting;
using ChargingPanel.Core.Security;
using ChargingPanel.Core.Services;
using ChargingPanel.Infrastructure.Windows.ApiBridge;
using ChargingPanel.Infrastructure.Windows.Bluetooth;
using ChargingPanel.Infrastructure.Windows.IM;
using ChargingPanel.Infrastructure.Windows.Persistence;
using ChargingPanel.Infrastructure.Windows.Security;
using ChargingPanel.Infrastructure.Windows.WebSockets;
using ChargingPanel.Vendor.DGLab;
using ChargingPanel.Vendor.Yokonex;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace ChargingPanel.Desktop.Bootstrap;

/// <summary>
/// Runtime v2 服务注册。仅在 Desktop 组合根内使用 DI，
/// Core 与 Vendor 不得依赖根容器。
/// </summary>
internal static class ServiceRegistration
{
    public static IServiceCollection AddDeviceRuntimeV2(
        this IServiceCollection services,
        ApplicationHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        var dataPath = options.ResolveDataPath();
        Directory.CreateDirectory(dataPath);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ILogger>(_ => Log.Logger);
        services.AddSingleton(options.DeviceRuntimeV2);

        // Task 9：Windows BLE 句柄的唯一协调者已真实注册。
        // 真实的 WinRT 后端只做单次可取消 I/O，不含重连与语义重试。
        services.AddSingleton<IWindowsBluetoothBackend>(_ => new WindowsBluetoothBackend());
        services.AddSingleton(sp => new BluetoothConnectionBroker(
            sp.GetRequiredService<IWindowsBluetoothBackend>(),
            sp.GetRequiredService<TimeProvider>()));

        // Task 10：WS / IM / API Bridge 传输基础设施。
        // 池按服务身份与账户主体隔离复用；连接对象由厂商 Driver（T17）按 Endpoint 配置请求。
        services.AddSingleton<WebSocketConnectionPool>(_ => new WebSocketConnectionPool(TimeProvider.System));

        // IM 账户池：原生客户端按账户键惰性创建；userSig 从密钥存储按引用解析。
        services.AddSingleton(sp =>
        {
            var secretStore = sp.GetRequiredService<ISecretStore>();
            return new ImConnectionPool(
                clientFactory: _ => new TencentImNativeClient(),
                userSigResolver: async (key, ct) =>
                {
                    var secret = await secretStore.ReadAsync(key.CredentialReference, ct);
                    return secret is null ? null : Encoding.UTF8.GetString(secret.Value.ToArray());
                });
        });

        // API Bridge：默认官方本地端口 3001；真实地址由 T17 持久化 Endpoint 提供。
        services.AddSingleton<ApiBridgeHttpClient>(_ => new ApiBridgeHttpClient(new Uri("http://127.0.0.1:3001")));
        services.AddSingleton<ApiBridgeWebSocketClient>(_ => new ApiBridgeWebSocketClient(new Uri("ws://127.0.0.1:3001")));

        // Task 22：旧 AppServices 删除后，业务服务统一由组合根创建并注入。
        // 设备输出只经由 IDeviceApplicationService，业务服务绝不触碰 legacy 设备实现。
        RegisterBusinessServices(services);

        services.AddSingleton<ISecretStore>(_ =>
            new DpapiSecretStore(Path.Combine(dataPath, "device-secrets")));
        services.AddSingleton<IDeviceInstanceRepository>(sp =>
            new SqliteDeviceInstanceRepository(
                Path.Combine(dataPath, "device_adapter.db"),
                sp.GetRequiredService<ISecretStore>()));

        // 内置厂商插件：DG-LAB 与 YOKONEX 目录/Profile/Driver 贡献（T11-T17）。
        services.AddSingleton<IDevicePlugin>(_ => new DGLabPlugin());
        services.AddSingleton<IDevicePlugin>(_ => new YokonexPlugin());
        services.AddSingleton<IDeviceCatalog>(sp =>
            new DeviceCatalog(sp.GetServices<IDevicePlugin>()));
        services.AddSingleton<IDeviceRegistry>(sp =>
            new DeviceRegistry(sp.GetRequiredService<IDeviceCatalog>()));

        services.AddSingleton<DeviceRuntimeRegistry>();
        services.AddSingleton<IDeviceRuntimeRegistry>(sp =>
            sp.GetRequiredService<DeviceRuntimeRegistry>());

        services.AddSingleton(sp =>
        {
            var safety = new SafetySupervisor(sp.GetRequiredService<TimeProvider>());
            safety.AttachRuntime(sp.GetRequiredService<IDeviceRuntimeRegistry>());
            return safety;
        });

        services.AddSingleton(sp => new CommandDispatcher(
            sp.GetRequiredService<SafetySupervisor>(),
            sp.GetRequiredService<IDeviceRuntimeRegistry>(),
            sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<ITransportBroker, UnavailableTransportBroker>();
        services.AddSingleton(sp => new EndpointSelector(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new SessionCoordinator(
            sp.GetRequiredService<ITransportBroker>(),
            sp.GetRequiredService<EndpointSelector>(),
            sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<IDeviceQueryService>(sp => new DeviceQueryService(
            sp.GetRequiredService<IDeviceCatalog>(),
            sp.GetRequiredService<IDeviceRegistry>()));
        services.AddSingleton<IDeviceApplicationService>(sp => new DeviceApplicationService(
            sp.GetRequiredService<CommandDispatcher>(),
            sp.GetRequiredService<SafetySupervisor>(),
            sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<IApplicationShutdownParticipant>(sp =>
            new SafetyLatchShutdownParticipant(sp.GetRequiredService<SafetySupervisor>()));
        services.AddSingleton<IApplicationShutdownParticipant>(sp =>
            new SessionShutdownParticipant(sp.GetRequiredService<SessionCoordinator>()));
        services.AddSingleton<IApplicationShutdownParticipant>(sp =>
            new BluetoothBrokerShutdownParticipant(sp.GetRequiredService<BluetoothConnectionBroker>()));
        services.AddSingleton<IApplicationShutdownParticipant>(sp =>
        {
            var pool = sp.GetRequiredService<WebSocketConnectionPool>();
            return new DelegateShutdownParticipant(
                ApplicationShutdownStage.ConnectionPools,
                _ => pool.DisposeAsync().AsTask());
        });
        services.AddSingleton<IApplicationShutdownParticipant>(sp =>
        {
            var pool = sp.GetRequiredService<ImConnectionPool>();
            return new DelegateShutdownParticipant(
                ApplicationShutdownStage.ConnectionPools,
                _ => pool.DisposeAsync().AsTask());
        });
        services.AddSingleton<IApplicationShutdownParticipant>(sp =>
        {
            var client = sp.GetRequiredService<ApiBridgeWebSocketClient>();
            return new DelegateShutdownParticipant(
                ApplicationShutdownStage.ConnectionPools,
                _ => client.DisposeAsync().AsTask());
        });
        services.AddSingleton<IApplicationShutdownParticipant>(sp =>
            new StorageShutdownParticipant(
                sp.GetRequiredService<IDeviceInstanceRepository>(),
                sp.GetRequiredService<ISecretStore>()));

        services.AddSingleton(sp => new ApplicationLifetimeCoordinator(
            sp.GetServices<IApplicationShutdownParticipant>(),
            sp.GetRequiredService<TimeProvider>()));

        return services;
    }

    /// <summary>
    /// 注册业务服务（Task 22）：旧 <c>AppServices</c> 的职责迁移到组合根。
    /// 全部为容器单例，由生命周期启动/停止；设备输出只经由
    /// <see cref="IDeviceApplicationService"/>。
    /// </summary>
    private static void RegisterBusinessServices(IServiceCollection services)
    {
        // 命令调度与动作映射：事件/脚本/传感器规则共享的唯一输出通道。
        services.AddSingleton<DeviceCommandScheduler>(sp =>
            new DeviceCommandScheduler(
                sp.GetRequiredService<CommandDispatcher>(),
                sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<DeviceActionMapperRegistry>();

        // 事件总线为进程级静态单例（EventBus.Instance），不入容器：
        // 容器释放会 Dispose 静态实例，导致后续订阅失败。

        services.AddSingleton(sp => new EventService(
            sp.GetRequiredService<IDeviceQueryService>(),
            sp.GetRequiredService<IDeviceApplicationService>(),
            sp.GetRequiredService<DeviceCommandScheduler>(),
            sp.GetRequiredService<DeviceActionMapperRegistry>()));

        services.AddSingleton(sp => new EventProcessor(
            sp.GetRequiredService<EventService>(),
            sp.GetRequiredService<IDeviceApplicationService>(),
            sp.GetRequiredService<IDeviceQueryService>(),
            sp.GetRequiredService<DeviceCommandScheduler>(),
            EventBus.Instance,
            sp.GetRequiredService<DeviceActionMapperRegistry>()));

        services.AddSingleton(sp => new OCRService(sp.GetRequiredService<EventService>()));

        services.AddSingleton(sp => new BattleModeService(
            sp.GetRequiredService<IDeviceApplicationService>(),
            sp.GetRequiredService<IDeviceQueryService>(),
            sp.GetRequiredService<DeviceCommandScheduler>(),
            EventBus.Instance,
            sp.GetRequiredService<OCRService>(),
            sp.GetRequiredService<DeviceActionMapperRegistry>()));

        services.AddSingleton(sp => new YokonexSensorService(
            sp.GetRequiredService<IDeviceQueryService>(),
            sp.GetRequiredService<IDeviceApplicationService>()));

        services.AddSingleton(sp => new SensorRuleService(
            sp.GetRequiredService<IDeviceApplicationService>(),
            sp.GetRequiredService<IDeviceQueryService>(),
            sp.GetRequiredService<DeviceCommandScheduler>(),
            sp.GetRequiredService<EventService>(),
            sp.GetRequiredService<DeviceActionMapperRegistry>(),
            sp.GetRequiredService<YokonexSensorService>()));

        // MOD 桥接：注入远端命令唯一入口（T21），MOD 直接命令只经由应用服务下发。
        services.AddSingleton(sp =>
        {
            var modBridge = new ModBridgeService(
                EventBus.Instance,
                sp.GetRequiredService<EventService>(),
                sp.GetRequiredService<IDeviceQueryService>());
            modBridge.AttachRemoteControlService(sp.GetRequiredService<IDeviceApplicationService>());
            return modBridge;
        });

        services.AddSingleton(sp => new ScriptEngine(
            sp.GetRequiredService<IDeviceApplicationService>(),
            sp.GetRequiredService<IDeviceQueryService>(),
            sp.GetRequiredService<EventService>(),
            sp.GetRequiredService<ModBridgeService>(),
            sp.GetRequiredService<DeviceActionMapperRegistry>()));

        // Room：静态单例，组合根注入设备存在性查询与远端命令唯一入口。
        services.AddSingleton(sp =>
        {
            RoomService.Configure(sp.GetRequiredService<IDeviceQueryService>());
            RoomService.ConfigureRemoteControl(sp.GetRequiredService<IDeviceApplicationService>());
            return RoomService.Instance;
        });

        // 业务服务编排宿主：启动/停止全部业务服务。
        // 关闭参与者只停止"已实际启动"的宿主，避免在 Database 未初始化时构造业务服务。
        services.AddSingleton<BusinessServicesHost>();
        services.AddSingleton<IApplicationShutdownParticipant>(_ =>
            new DelegateShutdownParticipant(
                ApplicationShutdownStage.ExternalIntake,
                ct => BusinessServicesHost.StartedInstance is { } host
                    ? host.StopAsync(ct)
                    : Task.CompletedTask));
    }
}

/// <summary>
/// 传输门禁：明确拒绝而不是伪造连接成功。
///
/// 真实 BluetoothConnectionBroker 已在 Task 9 注册，但 SessionCoordinator
/// 消费的 ITransportBroker 桥接需要厂商 Driver 提供带蓝牙地址的
/// EndpointCandidate（Task 11），因此当前任何调用仍会明确失败。
/// 这不是空操作 Adapter——它绝不返回租约、绝不上报握手成功，
/// 任何调用都会得到明确失败，因此不可能产生假连接状态。
/// 删除条件：Task 11 建立带地址的 Endpoint 桥接后替换。
/// </summary>
internal sealed class UnavailableTransportBroker : ITransportBroker
{
    private const string Reason = "BLE Endpoint 地址桥接尚未就绪（Task 11 厂商切片），当前无法建立真实连接。";

    public ValueTask<TransportLeaseHandle> AcquireAsync(
        EndpointCandidate candidate,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Reason);

    public ValueTask<bool> HandshakeAsync(
        TransportLeaseHandle lease,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Reason);

    public ValueTask ReleaseAsync(TransportLeaseHandle lease, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

/// <summary>连接池阶段：释放 BluetoothConnectionBroker 持有的全部平台句柄。</summary>
internal sealed class BluetoothBrokerShutdownParticipant : IApplicationShutdownParticipant
{
    private readonly BluetoothConnectionBroker _broker;

    public BluetoothBrokerShutdownParticipant(BluetoothConnectionBroker broker)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
    }

    public ApplicationShutdownStage Stage => ApplicationShutdownStage.ConnectionPools;

    public async Task StopAsync(CancellationToken cancellationToken) => await _broker.DisposeAsync();
}

/// <summary>安全锁闭阶段：先锁闭并尽力停止所有输出。</summary>
internal sealed class SafetyLatchShutdownParticipant : IApplicationShutdownParticipant
{
    private readonly SafetySupervisor _safety;

    public SafetyLatchShutdownParticipant(SafetySupervisor safety)
    {
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
    }

    public ApplicationShutdownStage Stage => ApplicationShutdownStage.SafetyLatch;

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await _safety.EmergencyStopAllAsync("应用关闭", cancellationToken);
}

/// <summary>Session 阶段：释放所有设备会话与其租约。</summary>
internal sealed class SessionShutdownParticipant : IApplicationShutdownParticipant
{
    private readonly SessionCoordinator _sessions;

    public SessionShutdownParticipant(SessionCoordinator sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public ApplicationShutdownStage Stage => ApplicationShutdownStage.Sessions;

    public Task StopAsync(CancellationToken cancellationToken) =>
        _sessions.StopAllAsync(cancellationToken);
}

/// <summary>存储与日志阶段：最后关闭持久化与密钥存储。</summary>
internal sealed class StorageShutdownParticipant : IApplicationShutdownParticipant
{
    private readonly IDeviceInstanceRepository _repository;
    private readonly ISecretStore _secretStore;

    public StorageShutdownParticipant(IDeviceInstanceRepository repository, ISecretStore secretStore)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
    }

    public ApplicationShutdownStage Stage => ApplicationShutdownStage.StorageAndLogging;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        (_repository as IDisposable)?.Dispose();
        (_secretStore as IDisposable)?.Dispose();
        return Task.CompletedTask;
    }
}
