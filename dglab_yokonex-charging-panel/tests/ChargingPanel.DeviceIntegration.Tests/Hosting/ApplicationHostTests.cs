using System.Reflection;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Core.Application.Devices;
using ChargingPanel.Core.Data;
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
using ChargingPanel.Desktop.Bootstrap;
using ChargingPanel.Infrastructure.Windows.Bluetooth;
using ChargingPanel.Vendor.DGLab;
using ChargingPanel.Vendor.Yokonex;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Hosting;

public sealed class ApplicationHostTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(
        Path.GetTempPath(),
        "ChargingPanelApplicationHostTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void CompositionRoot_DesktopAssembly_HasSingleRootProviderOwner()
    {
        // 只检查本仓库编写的 Desktop 类型：Avalonia XAML 编译器会生成
        // 自带 IServiceProvider 字段的上下文类型（CompiledAvaloniaXaml.*），
        // 那些不是组合根，也不受本约束管辖。
        var owners = typeof(ApplicationHost).Assembly
            .GetTypes()
            .Where(type => type.Namespace?.StartsWith("ChargingPanel.Desktop", StringComparison.Ordinal) == true)
            .Where(type => type
                .GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(field => typeof(IServiceProvider).IsAssignableFrom(field.FieldType)))
            .ToArray();

        Assert.Equal([typeof(ApplicationHost)], owners);
    }

    [Fact]
    public async Task Create_RuntimeV2Enabled_ResolvesRequiredServices()
    {
        await using var host = CreateRuntimeV2Host();

        Assert.NotNull(host.GetRequiredService<IDeviceCatalog>());
        Assert.NotNull(host.GetRequiredService<IDeviceRegistry>());
        Assert.NotNull(host.GetRequiredService<IDeviceInstanceRepository>());
        Assert.NotNull(host.GetRequiredService<CommandDispatcher>());
        Assert.NotNull(host.GetRequiredService<SafetySupervisor>());
        Assert.NotNull(host.GetRequiredService<SessionCoordinator>());
        Assert.NotNull(host.GetRequiredService<IDeviceQueryService>());
        Assert.NotNull(host.GetRequiredService<IDeviceApplicationService>());
        Assert.Same(TimeProvider.System, host.GetRequiredService<TimeProvider>());
        Assert.NotNull(host.GetRequiredService<ILogger>());
        Assert.NotNull(host.GetRequiredService<ApplicationLifetimeCoordinator>());

        var runtimeRegistry = host.GetRequiredService<IDeviceRuntimeRegistry>();
        Assert.IsType<DeviceRuntimeRegistry>(runtimeRegistry);
        Assert.Empty(runtimeRegistry.ActiveInstanceIds);
    }

    [Fact]
    public async Task Create_RuntimeV2Enabled_ResolvesRealBluetoothBrokerAndDisposesOnShutdown()
    {
        // Task 9 组合根交付：真实 BluetoothConnectionBroker 可解析，
        // 且关闭时在 ConnectionPools 阶段被释放（不依赖调用方逐个归还句柄）。
        await using var host = CreateRuntimeV2Host();

        Assert.NotNull(host.GetRequiredService<BluetoothConnectionBroker>());

        var result = await host.StopAsync();

        Assert.True(result.IsSuccessful);
        var poolsStage = Assert.Single(
            result.StageResults,
            stage => stage.Stage == ApplicationShutdownStage.ConnectionPools);
        Assert.True(poolsStage.IsSuccessful);
    }

    [Fact]
    public void PluginContext_PublicContract_DoesNotExposeRootContainer()
    {
        var exposedTypes = typeof(IPluginContext)
            .GetProperties()
            .Select(property => property.PropertyType)
            .Concat(typeof(IPluginContext).GetMethods().Select(method => method.ReturnType))
            .ToArray();

        Assert.DoesNotContain(exposedTypes, type => typeof(IServiceProvider).IsAssignableFrom(type));
        Assert.DoesNotContain(exposedTypes, type => typeof(IServiceCollection).IsAssignableFrom(type));
    }

    [Fact]
    public async Task StopAsync_AllStagesSucceed_UsesStrictOrder()
    {
        var order = new List<ApplicationShutdownStage>();
        var coordinator = CreateCoordinator(
            Enum.GetValues<ApplicationShutdownStage>()
                .Select(stage => new RecordingParticipant(stage, order)));

        var result = await coordinator.StopAsync();

        Assert.Equal(
            [
                ApplicationShutdownStage.ExternalIntake,
                ApplicationShutdownStage.SafetyLatch,
                ApplicationShutdownStage.Sessions,
                ApplicationShutdownStage.ConnectionPools,
                ApplicationShutdownStage.StorageAndLogging
            ],
            order);
        Assert.True(result.IsSuccessful);
        Assert.All(result.StageResults, stage => Assert.True(stage.IsSuccessful));
    }

    [Fact]
    public async Task StopAsync_StageFails_ContinuesAndRetainsException()
    {
        var order = new List<ApplicationShutdownStage>();
        var expected = new InvalidOperationException("session stop failed");
        var participants = Enum.GetValues<ApplicationShutdownStage>()
            .Select(stage => new RecordingParticipant(
                stage,
                order,
                stage == ApplicationShutdownStage.Sessions ? expected : null));
        var coordinator = CreateCoordinator(participants);

        var result = await coordinator.StopAsync();

        Assert.Equal(Enum.GetValues<ApplicationShutdownStage>(), order);
        Assert.False(result.IsSuccessful);
        var failed = Assert.Single(result.StageResults, stage => !stage.IsSuccessful);
        Assert.Equal(ApplicationShutdownStage.Sessions, failed.Stage);
        Assert.Same(expected, failed.Exception);
        Assert.Contains(
            result.StageResults,
            stage => stage.Stage == ApplicationShutdownStage.StorageAndLogging && stage.IsSuccessful);
    }

    [Fact]
    public async Task StopAsync_CalledTwice_IsIdempotent()
    {
        var order = new List<ApplicationShutdownStage>();
        var coordinator = CreateCoordinator(
            Enum.GetValues<ApplicationShutdownStage>()
                .Select(stage => new RecordingParticipant(stage, order)));

        var first = await coordinator.StopAsync();
        var second = await coordinator.StopAsync();

        Assert.Same(first, second);
        Assert.Equal(Enum.GetValues<ApplicationShutdownStage>(), order);
    }

    [Fact]
    public async Task AcquireAsync_BleEndpointAddressBridgeUnavailable_RejectsConnection()
    {
        // Task 9 后真实 BluetoothConnectionBroker 已注册，但 ITransportBroker
        // 桥接仍需厂商 Driver 提供带蓝牙地址的 EndpointCandidate（Task 11），
        // 因此当前任何连接请求都必须明确失败，绝不返回伪造租约。
        await using var host = CreateRuntimeV2Host();
        var broker = host.GetRequiredService<ITransportBroker>();
        var candidate = new EndpointCandidate(
            EndpointId.New(),
            ProtocolProfileId.Parse("dglab.coyote.v3"),
            TransportKind.BluetoothLowEnergy,
            0,
            true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.AcquireAsync(candidate, CancellationToken.None).AsTask());

        Assert.Contains("Task 11", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RoomService_PublicConstructor_RequiresNarrowDevicePresenceQuery()
    {
        var constructor = Assert.Single(typeof(ChargingPanel.Core.Network.RoomService).GetConstructors());
        var parameter = Assert.Single(constructor.GetParameters());

        Assert.Equal(typeof(IDevicePresenceQuery), parameter.ParameterType);
        Assert.True(typeof(IDevicePresenceQuery).IsAssignableFrom(typeof(IDeviceQueryService)));
    }

    [Fact]
    public async Task Create_RuntimeV2Enabled_ResolvesBuiltInVendorPlugins()
    {
        // T11-T17 后组合根必须解析内置 DG-LAB 与 YOKONEX 插件，
        // 目录才能暴露两家厂商的正式模型。
        await using var host = CreateRuntimeV2Host();

        var plugins = host.GetRequiredService<IEnumerable<IDevicePlugin>>().ToArray();
        Assert.Contains(plugins, plugin => plugin is DGLabPlugin);
        Assert.Contains(plugins, plugin => plugin is YokonexPlugin);
    }

    [Fact]
    public async Task DesktopApplicationLifecycle_RuntimeV2Enabled_StartupRunsMigrationPluginsAndBusinessServices()
    {
        // Step 4 生产切换：v2 启用时生命周期先做数据库初始化与 schema v2 迁移、
        // 确认插件目录，然后才进入 Runtime v2；legacy 路径已删除。
        // 本测试关闭业务服务自动启动（不绑定固定端口，避免与并行测试项目的
        // ModBridge 固定端口 39001/39002 冲突）；业务服务解析见单独测试。
        var lifecycle = DesktopApplicationLifecycle.Create(new ApplicationHostOptions
        {
            DataPath = _dataPath,
            DeviceRuntimeV2 = new DeviceRuntimeV2Options
            {
                Enabled = true,
                AutoConnectEnabled = true,
                StartBusinessServices = false
            }
        });

        await lifecycle.StartAsync();
        try
        {
            Assert.False(lifecycle.V2StartupFailed);
            Assert.Contains(lifecycle.V2StartupDiagnostics, diagnostic => diagnostic.Contains("插件目录"));
            Assert.Contains(lifecycle.V2StartupDiagnostics, diagnostic => diagnostic.Contains("schema v2 迁移"));
            Assert.Contains(lifecycle.V2StartupDiagnostics, diagnostic => diagnostic.Contains("业务服务"));
        }
        finally
        {
            await lifecycle.StopAsync();
        }
    }

    [Fact]
    public async Task Create_RuntimeV2Enabled_ResolvesMigratedBusinessServices()
    {
        // Task 22：旧 AppServices 的业务服务迁移到组合根，容器必须能解析全部单例。
        await using var host = CreateRuntimeV2Host();
        Database.Initialize(Path.Combine(host.DataPath, "device_adapter.db"));

        Assert.NotNull(host.GetRequiredService<EventService>());
        Assert.NotNull(host.GetRequiredService<EventProcessor>());
        Assert.NotNull(host.GetRequiredService<OCRService>());
        Assert.NotNull(host.GetRequiredService<ModBridgeService>());
        Assert.NotNull(host.GetRequiredService<ScriptEngine>());
        Assert.NotNull(host.GetRequiredService<BusinessServicesHost>());
        Assert.NotNull(host.GetRequiredService<ChargingPanel.Core.Services.SensorRuleService>());
        Assert.NotNull(host.GetRequiredService<ChargingPanel.Core.Services.YokonexSensorService>());
        Assert.NotNull(host.GetRequiredService<ChargingPanel.Core.Services.BattleModeService>());
        Assert.NotNull(host.GetRequiredService<RoomService>());

        // T21 远控服务注入：Room 与 MOD 桥接必须持有唯一的设备命令入口。
        Assert.NotNull(RoomService.Instance.CreateRemoteSession(
            "composition-test",
            ChargingPanel.Core.Network.RemoteControl.RemotePermissionScope.All));
        Assert.True(host.GetRequiredService<ModBridgeService>().IsRemoteCommandIngressEnabled);
    }

    [Fact]
    public async Task BusinessServicesHost_StartAsync_WithNetworkDisabled_StartsAndStopsIdempotently()
    {
        // 生产默认 StartBusinessServices=true：验证业务服务启动/停止路径。
        // 关闭网络服务（coyote WS 9000 与 MOD 固定端口 39001/39002），
        // 避免与并行测试项目的固定端口冲突；脚本引擎启动是核心可观测信号。
        await using var host = CreateRuntimeV2Host();
        Database.Initialize(Path.Combine(host.DataPath, "device_adapter.db"));
        Database.Instance.SetSetting("server.coyote.enabled", "false");
        Database.Instance.SetSetting("modbridge.enabled", "false");

        var business = host.GetRequiredService<BusinessServicesHost>();
        await business.StartAsync();
        try
        {
            Assert.Same(business, BusinessServicesHost.StartedInstance);
            Assert.NotNull(ScriptEngine.Instance);
        }
        finally
        {
            await business.StopAsync();
        }

        Assert.Null(BusinessServicesHost.StartedInstance);
        await business.StopAsync(); // 幂等：再次停止不应抛异常
    }

    private ApplicationHost CreateRuntimeV2Host() =>
        ApplicationHost.Create(new ApplicationHostOptions
        {
            DataPath = _dataPath,
            DeviceRuntimeV2 = new DeviceRuntimeV2Options
            {
                Enabled = true,
                AutoConnectEnabled = true
            }
        });

    private static ApplicationLifetimeCoordinator CreateCoordinator(
        IEnumerable<IApplicationShutdownParticipant> participants) =>
        new(participants, TimeProvider.System);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataPath))
            {
                Directory.Delete(_dataPath, recursive: true);
            }
        }
        catch
        {
            // 测试清理失败不覆盖断言结果。
        }
    }

    private sealed class RecordingParticipant : IApplicationShutdownParticipant
    {
        private readonly List<ApplicationShutdownStage> _order;
        private readonly Exception? _exception;

        public RecordingParticipant(
            ApplicationShutdownStage stage,
            List<ApplicationShutdownStage> order,
            Exception? exception = null)
        {
            Stage = stage;
            _order = order;
            _exception = exception;
        }

        public ApplicationShutdownStage Stage { get; }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _order.Add(Stage);
            return _exception is null ? Task.CompletedTask : Task.FromException(_exception);
        }
    }
}
