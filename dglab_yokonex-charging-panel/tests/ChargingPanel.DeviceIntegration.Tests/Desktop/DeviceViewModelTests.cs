using System.Text.Json;
using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Telemetry;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Core.Application.Devices;
using ChargingPanel.Core.Devices.Catalog;
using ChargingPanel.Core.Devices.Discovery;
using ChargingPanel.Core.Devices.Registry;
using ChargingPanel.Core.Devices.Runtime;
using ChargingPanel.Core.Devices.Safety;
using ChargingPanel.Desktop.ViewModels.Devices;
using ChargingPanel.DeviceIntegration.Tests.Fixtures.DGLab;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Desktop;

/// <summary>
/// 设备 ViewModel 行为测试：运行在 v2 运行时骨架之上，
/// 覆盖目录可见性、发现升级诊断、能力驱动控制项生成与稳定 DTO 添加流程。
/// </summary>
public sealed class DeviceViewModelTests
{
    // ---------- 目录 ----------

    [Fact]
    public void DeviceCatalogViewModel_Refresh_HiddenReservedModel_NeverAppearsAsFrontendItem()
    {
        var publicPlugin = TestCatalogPlugin.Create(
            "test.plugin.public",
            "test.vendor.public",
            "test.model.public",
            "test.profile.public",
            DeviceAvailability.Public,
            "public-device");
        var hiddenPlugin = TestCatalogPlugin.Create(
            "test.plugin.hidden",
            "test.vendor.hidden",
            "test.model.hidden",
            "test.profile.hidden",
            DeviceAvailability.HiddenReserved,
            "hidden-device");
        var catalog = new DeviceCatalog([publicPlugin, hiddenPlugin]);
        var registry = new DeviceRegistry(catalog);
        var query = new DeviceQueryService(catalog, registry);

        var viewModel = new DeviceCatalogViewModel(query);
        viewModel.Refresh();

        Assert.Single(viewModel.Models);
        Assert.Equal("test.model.public", viewModel.Models[0].Id);
        Assert.DoesNotContain(viewModel.Models, model => model.Id == "test.model.hidden");
    }

    // ---------- 发现：爪印 V1.0 升级诊断 ----------

    [Fact]
    public void DeviceDiscoveryViewModel_PawPrintsV10Hit_RendersOnlyUpgradeDiagnosticCard()
    {
        using var harness = new DGLabRuntimeHarness();
        var viewModel = new DeviceDiscoveryViewModel(harness.Discovery, CreateQuery(harness));

        var item = viewModel.HandleAdvertisement(DGLabAdvertisements.NamedBle("47L120100"));

        Assert.True(item.IsUpgradeDiagnostic);
        Assert.False(item.IsAddable);
        Assert.Null(item.Instance);
        Assert.Empty(harness.Registry.GetSnapshot());
    }

    [Fact]
    public void DeviceDiscoveryViewModel_PawPrintsV11Hit_RendersAddableInstance()
    {
        using var harness = new DGLabRuntimeHarness();
        var viewModel = new DeviceDiscoveryViewModel(harness.Discovery, CreateQuery(harness));

        var item = viewModel.HandleAdvertisement(DGLabAdvertisements.NamedBle("47L120300"));

        Assert.False(item.IsUpgradeDiagnostic);
        Assert.True(item.IsAddable);
        Assert.NotNull(item.Instance);
        Assert.Single(harness.Registry.GetSnapshot());
    }

    // ---------- 控制项严格来自能力描述 ----------

    [Fact]
    public void DeviceControlViewModel_SensorOnlyDevice_ShowsNoIntensityControls()
    {
        var deviceId = DeviceInstanceId.New();
        var (query, appService) = CreateControlHarness(deviceId, BuildSensorOnlyCapability());
        var view = new ActiveDeviceView(deviceId, sessionEpoch: 1, [BuildSensorOnlyCapability()]);

        var viewModel = new DeviceControlViewModel(appService, query);
        viewModel.Load(view);

        Assert.Empty(viewModel.Controls);
    }

    [Fact]
    public void DeviceControlViewModel_ToyDevice_MotorControlCount_ComesFromRuntimeDeviceInfo()
    {
        var deviceId = DeviceInstanceId.New();
        var (query, appService) = CreateControlHarness(deviceId, BuildToySpeedCapability());
        var view = new ActiveDeviceView(deviceId, sessionEpoch: 1, [BuildToySpeedCapability()]);

        var viewModel = new DeviceControlViewModel(appService, query);
        viewModel.Load(view);

        // 3 个马达通道来自运行时查询到的能力描述，而不是假设固定为 3。
        Assert.Equal(3, viewModel.Controls.Count);
        Assert.All(viewModel.Controls, control => Assert.Equal("toy.speed.control", control.CapabilityId.Value));
    }

    [Fact]
    public async Task DeviceControlViewModel_RejectedResult_NeverShowsAsExecuted()
    {
        var deviceId = DeviceInstanceId.New();
        var (query, appService) = CreateControlHarness(deviceId, BuildToySpeedCapability(), rejectAll: true);
        var view = new ActiveDeviceView(deviceId, sessionEpoch: 1, [BuildToySpeedCapability()]);
        var viewModel = new DeviceControlViewModel(appService, query);
        viewModel.Load(view);

        var slot = viewModel.Controls[0];
        var result = await viewModel.ExecuteAsync(slot, level: 10);

        Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
        Assert.True(viewModel.LastCommandRejected);
        Assert.False(viewModel.LastCommandOutcomeUnknown);
        Assert.DoesNotContain("已执行", viewModel.LastResultText, StringComparison.Ordinal);
    }

    // ---------- 添加流程：稳定 DTO，不构造 BLE 传输 ----------

    [Fact]
    public void DeviceDiscoveryViewModel_AddFlow_UsesStableModelProfileEndpointDtos()
    {
        using var harness = new DGLabRuntimeHarness();
        var viewModel = new DeviceDiscoveryViewModel(harness.Discovery, CreateQuery(harness));

        var item = viewModel.HandleAdvertisement(DGLabAdvertisements.NamedBle("47L121000"));

        var instance = Assert.Single(harness.Registry.GetSnapshot());
        Assert.Equal("dglab", instance.PluginId.Value);
        Assert.Equal("dglab.coyote.v3", instance.ModelId.Value);
        Assert.Equal("dglab.coyote.v3.ble", instance.ProfileId.Value);
        Assert.Single(instance.Endpoints);
        Assert.Equal(TransportKind.BluetoothLowEnergy, instance.Endpoints[0].TransportKind);
        Assert.True(instance.Endpoints[0].HasCanonicalFingerprint());
        Assert.NotNull(item.Instance);
    }

    // ---------- 工具 ----------

    private static DeviceQueryService CreateQuery(DGLabRuntimeHarness harness) =>
        new(harness.Catalog, harness.Registry, harness.Runtime);

    private static (IDeviceQueryService Query, IDeviceApplicationService AppService) CreateControlHarness(
        DeviceInstanceId instanceId,
        CapabilityDescriptor capability,
        bool rejectAll = false)
    {
        var safety = new SafetySupervisor(TimeProvider.System);
        var runtime = new DeviceRuntimeRegistry();
        safety.AttachRuntime(runtime);
        var dispatcher = new CommandDispatcher(safety, runtime, TimeProvider.System);
        var driver = new FakeDriver(rejectAll);
        runtime.Register(new DeviceRuntimeBinding(instanceId, driver, [capability], sessionEpoch: 1));
        var catalog = new DeviceCatalog(Array.Empty<IDevicePlugin>());
        var registry = new DeviceRegistry(catalog);
        var query = new DeviceQueryService(catalog, registry, runtime);
        var appService = new DeviceApplicationService(dispatcher, safety, TimeProvider.System);
        return (query, appService);
    }

    private static CapabilityDescriptor BuildSensorOnlyCapability()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        return new CapabilityDescriptor(
            new CapabilityId("pose.acceleration"),
            "姿态与加速度",
            CapabilityStability.Stable,
            [
                new CapabilityActuatorDescriptor(
                    "sensor",
                    "传感器",
                    "value",
                    new NumericRange(0, 255),
                    new NumericRange(0, 255),
                    [new CapabilityChannelDescriptor("accel", "加速度")])
            ],
            new VersionedJsonSchema("1.0", schema.RootElement.Clone()),
            new VersionedJsonSchema("1.0", schema.RootElement.Clone()),
            new CapabilityCommandPolicy(
                CommandDelivery.Idempotent,
                CommandAcknowledgement.None,
                null,
                TimeSpan.FromSeconds(2),
                false),
            new SafeStopPlanDescriptor("none", schema.RootElement.Clone(), ["sensor"]),
            new VersionedJsonSchema("1.0", schema.RootElement.Clone()));
    }

    private static CapabilityDescriptor BuildToySpeedCapability()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        return new CapabilityDescriptor(
            new CapabilityId("toy.speed.control"),
            "玩具实时力度控制",
            CapabilityStability.Stable,
            [
                new CapabilityActuatorDescriptor(
                    "motor",
                    "马达",
                    "level",
                    new NumericRange(0, 20),
                    new NumericRange(0, 20),
                    [
                        new CapabilityChannelDescriptor("motor-a", "A 马达"),
                        new CapabilityChannelDescriptor("motor-b", "B 马达"),
                        new CapabilityChannelDescriptor("motor-c", "C 马达")
                    ])
            ],
            new VersionedJsonSchema("1.0", schema.RootElement.Clone()),
            new VersionedJsonSchema("1.0", schema.RootElement.Clone()),
            new CapabilityCommandPolicy(
                CommandDelivery.AtMostOnce,
                CommandAcknowledgement.TransportAcknowledgement,
                null,
                TimeSpan.FromSeconds(2),
                true),
            new SafeStopPlanDescriptor("speed.zero", schema.RootElement.Clone(), ["motor"]),
            new VersionedJsonSchema("1.0", schema.RootElement.Clone()));
    }

    private sealed class FakeDriver : IProtocolDriver
    {
        private readonly bool _rejectAll;

        public FakeDriver(bool rejectAll = false) => _rejectAll = rejectAll;

        public ProtocolProfileId ProfileId { get; } = new("test.profile.v1");

        public ValueTask OpenAsync(IEndpointLease lease, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<DeviceCommandResult> ExecuteAsync(
            DeviceCommandEnvelope command,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                _rejectAll
                    ? new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "test.rejected", "测试拒绝。")
                    : new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Executed));

        public ValueTask<SafeStopResult> SafeStopAsync(SafeStopContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SafeStopResult(SafeStopStatus.ConfirmedStopped));

        public async IAsyncEnumerable<DeviceTelemetryEnvelope> ReadTelemetryAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>供测试构造带发现规则的最小插件。</summary>
internal sealed class TestCatalogPlugin : IDevicePlugin
{
    private TestCatalogPlugin(PluginManifest manifest, DevicePluginContribution contribution)
    {
        Manifest = manifest;
        _contribution = contribution;
    }

    private readonly DevicePluginContribution _contribution;

    public PluginManifest Manifest { get; }

    public DevicePluginContribution Describe() => _contribution;

    public static TestCatalogPlugin Create(
        string pluginId,
        string vendorId,
        string modelId,
        string profileId,
        DeviceAvailability availability,
        string broadcastName)
    {
        var schemaJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["name"] = broadcastName });
        using var document = JsonDocument.Parse(schemaJson);
        var profile = new ProtocolProfileDescriptor(
            new ProtocolProfileId(profileId),
            new ModelId(modelId),
            "测试协议",
            [TransportKind.BluetoothLowEnergy],
            Array.Empty<CapabilityDescriptor>());
        var contribution = new DevicePluginContribution(
            [new VendorDescriptor(new VendorId(vendorId), "测试厂商")],
            [new DeviceModelDescriptor(
                new ModelId(modelId),
                new VendorId(vendorId),
                "测试设备",
                availability,
                [new ProtocolProfileId(profileId)])],
            [profile],
            [new DiscoveryRuleDescriptor(
                $"{pluginId}.discovery",
                new ProtocolProfileId(profileId),
                TransportKind.BluetoothLowEnergy,
                new VersionedJsonSchema("1.0", document.RootElement))],
            Array.Empty<IProtocolDriverFactory>());

        return new TestCatalogPlugin(
            new PluginManifest(
                new PluginId(pluginId),
                "测试插件",
                new Version(1, 0),
                new HostApiVersionRange(new Version(1, 0), new Version(1, 0))),
            contribution);
    }
}
