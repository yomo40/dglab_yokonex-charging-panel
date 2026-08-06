using System.Text.Json;
using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Core.Application.Devices;
using ChargingPanel.Core.Devices.Catalog;
using ChargingPanel.Core.Devices.Discovery;
using ChargingPanel.Core.Devices.Registry;
using Xunit;

namespace ChargingPanel.Core.Tests;

public sealed class DeviceCatalogTests
{
    [Fact]
    public void Constructor_DuplicateModelId_RejectsWholeLaterPluginContribution()
    {
        var first = TestPlugin.Create(
            "test.plugin.first",
            "test.vendor.first",
            "test.model.shared",
            "test.profile.first",
            DeviceAvailability.Public,
            "first-device");
        var duplicate = TestPlugin.Create(
            "test.plugin.duplicate",
            "test.vendor.duplicate",
            "test.model.shared",
            "test.profile.duplicate",
            DeviceAvailability.Public,
            "duplicate-device");

        var catalog = new DeviceCatalog([first, duplicate]);

        var model = Assert.Single(catalog.GetModels(includeHiddenReserved: true));
        Assert.Equal("test.model.shared", model.Id.Value);
        Assert.Contains(
            catalog.GetDiagnostics(),
            diagnostic => diagnostic.PluginId.Value == "test.plugin.duplicate" &&
                          diagnostic.Code == CatalogDiagnosticCode.DuplicateModelId);
    }

    [Fact]
    public void GetModels_HiddenReservedModel_ExcludesItFromDefaultUserDirectory()
    {
        var publicPlugin = TestPlugin.Create(
            "test.plugin.public",
            "test.vendor.public",
            "test.model.public",
            "test.profile.public",
            DeviceAvailability.Public,
            "public-device");
        var hiddenPlugin = TestPlugin.Create(
            "test.plugin.hidden",
            "test.vendor.hidden",
            "test.model.hidden",
            "test.profile.hidden",
            DeviceAvailability.HiddenReserved,
            "hidden-device");
        var catalog = new DeviceCatalog([publicPlugin, hiddenPlugin]);
        var registry = new DeviceRegistry(catalog);
        var query = new DeviceQueryService(catalog, registry);

        var defaultModels = query.GetModels();
        var diagnosticModels = query.GetModels(includeHiddenReserved: true);

        Assert.Equal("test.model.public", Assert.Single(defaultModels).Id.Value);
        Assert.Equal(2, diagnosticModels.Count);
        Assert.Contains(diagnosticModels, model => model.Id.Value == "test.model.hidden");
    }

    [Fact]
    public void MatchAdvertisement_PawPrintsV10_ReturnsUpgradeDiagnosticWithoutModelSession()
    {
        var plugin = TestPlugin.Create(
            "test.plugin.upgrade",
            "test.vendor.upgrade",
            "test.model.upgrade",
            "test.profile.upgrade",
            DeviceAvailability.UpgradeDiagnostic,
            "47L120100");
        var catalog = new DeviceCatalog([plugin]);

        var result = catalog.Match(TestAdvertisement.Named("47L120100"));

        Assert.Equal(DeviceAvailability.UpgradeDiagnostic, result.Availability);
        Assert.False(result.CanCreateSession);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == DiscoveryDiagnosticCode.UpgradeRequired);
    }

    [Fact]
    public void Observe_UpgradeDiagnosticMatch_DoesNotRegisterInstance()
    {
        var plugin = TestPlugin.Create(
            "test.plugin.upgrade",
            "test.vendor.upgrade",
            "test.model.upgrade",
            "test.profile.upgrade",
            DeviceAvailability.UpgradeDiagnostic,
            "47L120100");
        var catalog = new DeviceCatalog([plugin]);
        var registry = new DeviceRegistry(catalog);
        var coordinator = new DiscoveryCoordinator(catalog, registry);

        var result = coordinator.Observe(TestAdvertisement.Named("47L120100"));

        Assert.False(result.Match.CanCreateSession);
        Assert.Null(result.DeviceInstance);
        Assert.Empty(registry.GetSnapshot());
    }

    [Fact]
    public void Register_UpgradeDiagnosticModel_RejectsDirectRegistration()
    {
        var plugin = TestPlugin.Create(
            "test.plugin.upgrade",
            "test.vendor.upgrade",
            "test.model.upgrade",
            "test.profile.upgrade",
            DeviceAvailability.UpgradeDiagnostic,
            "47L120100");
        var catalog = new DeviceCatalog([plugin]);
        var registry = new DeviceRegistry(catalog);
        var endpoint = ConnectionEndpoint.FromAdvertisement(
            TestAdvertisement.Named("47L120100"),
            new ProtocolProfileId("test.profile.upgrade"));

        Assert.Throws<InvalidOperationException>(() => registry.Register(new DeviceRegistration(
            new PluginId("test.plugin.upgrade"),
            new ModelId("test.model.upgrade"),
            new ProtocolProfileId("test.profile.upgrade"),
            endpoint)));
        Assert.Empty(registry.GetSnapshot());
    }

    [Theory]
    [InlineData("1.0", "{}")]
    [InlineData("1.0", "{\"nam\":\"测试设备\"}")]
    [InlineData("1.0", "{\"name\":1}")]
    [InlineData("1.0", "{\"properties\":{}}")]
    [InlineData("1.0", "{\"properties\":{\"category\":1}}")]
    [InlineData("2.0", "{\"name\":\"测试设备\"}")]
    public void Constructor_InvalidDiscoveryRuleSchema_RejectsWholeContribution(
        string schemaVersion,
        string schemaJson)
    {
        var plugin = TestPlugin.CreateWithDiscoverySchema(
            "test.plugin.invalid-rule",
            "test.vendor.invalid-rule",
            "test.model.invalid-rule",
            "test.profile.invalid-rule",
            DeviceAvailability.Public,
            TransportKind.BluetoothLowEnergy,
            [TransportKind.BluetoothLowEnergy],
            schemaVersion,
            schemaJson);

        var catalog = new DeviceCatalog([plugin]);
        var result = catalog.Match(TestAdvertisement.Named("测试设备"));

        Assert.Empty(catalog.GetModels(includeHiddenReserved: true));
        Assert.Contains(
            catalog.GetDiagnostics(),
            diagnostic => diagnostic.Code == CatalogDiagnosticCode.InvalidContribution);
        Assert.False(result.CanCreateSession);
    }
}

internal sealed class TestPlugin : IDevicePlugin
{
    private TestPlugin(PluginManifest manifest, DevicePluginContribution contribution)
    {
        Manifest = manifest;
        _contribution = contribution;
    }

    private readonly DevicePluginContribution _contribution;

    public PluginManifest Manifest { get; }

    public DevicePluginContribution Describe() => _contribution;

    public static TestPlugin Create(
        string pluginId,
        string vendorId,
        string modelId,
        string profileId,
        DeviceAvailability availability,
        string broadcastName)
    {
        var schemaJson = string.IsNullOrEmpty(broadcastName)
            ? "{\"properties\":{\"fixture\":\"generic\"}}"
            : JsonSerializer.Serialize(new Dictionary<string, string> { ["name"] = broadcastName });
        return CreateWithDiscoverySchema(
            pluginId,
            vendorId,
            modelId,
            profileId,
            availability,
            TransportKind.BluetoothLowEnergy,
            [TransportKind.BluetoothLowEnergy],
            "1.0",
            schemaJson);
    }

    public static TestPlugin CreateWithDiscoverySchema(
        string pluginId,
        string vendorId,
        string modelId,
        string profileId,
        DeviceAvailability availability,
        TransportKind ruleTransport,
        IEnumerable<TransportKind> supportedTransports,
        string schemaVersion,
        string schemaJson)
    {
        using var document = JsonDocument.Parse(schemaJson);
        var profile = new ProtocolProfileDescriptor(
            new ProtocolProfileId(profileId),
            new ModelId(modelId),
            "测试协议",
            supportedTransports,
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
                ruleTransport,
                new VersionedJsonSchema(schemaVersion, document.RootElement))],
            Array.Empty<IProtocolDriverFactory>());

        return new TestPlugin(
            new PluginManifest(
                new PluginId(pluginId),
                "测试插件",
                new Version(1, 0),
                new HostApiVersionRange(new Version(1, 0), new Version(1, 0))),
            contribution);
    }
}

internal static class TestAdvertisement
{
    public static DiscoveryAdvertisement Named(
        string name,
        string address = "AA:BB:CC:DD:EE:FF",
        string serviceUuids = "0000180f-0000-1000-8000-00805f9b34fb",
        string? manufacturerStableUid = null) =>
        new(
            TransportKind.BluetoothLowEnergy,
            name,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["address"] = address,
                ["serviceUuids"] = serviceUuids,
                ["fixture"] = "generic"
            },
            manufacturerStableUid);

    public static DiscoveryAdvertisement InstantMessaging(string accountPrincipal, string target) =>
        new(
            TransportKind.InstantMessaging,
            "即时消息端点",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["accountPrincipal"] = accountPrincipal,
                ["target"] = target
            });

    public static DiscoveryAdvertisement Bridge(string serviceIdentity, string target) =>
        new(
            TransportKind.WebSocket,
            "桥接端点",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["serviceIdentity"] = serviceIdentity,
                ["target"] = target
            });
}
