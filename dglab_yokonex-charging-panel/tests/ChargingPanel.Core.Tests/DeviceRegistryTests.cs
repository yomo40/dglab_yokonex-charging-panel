using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Core.Application.Devices;
using ChargingPanel.Core.Devices.Catalog;
using ChargingPanel.Core.Devices.Discovery;
using ChargingPanel.Core.Devices.Registry;
using Xunit;

namespace ChargingPanel.Core.Tests;

public sealed class DeviceRegistryTests
{
    [Fact]
    public void Observe_SameEndpointFingerprint_DeduplicatesEndpointAndInstance()
    {
        var catalog = CreatePublicCatalog();
        var registry = new DeviceRegistry(catalog);
        var coordinator = new DiscoveryCoordinator(catalog, registry);

        var first = coordinator.Observe(TestAdvertisement.Named("广播甲"));
        var duplicate = coordinator.Observe(TestAdvertisement.Named("广播乙", "AA-BB-CC-DD-EE-FF"));

        Assert.NotNull(first.DeviceInstance);
        Assert.NotNull(duplicate.DeviceInstance);
        Assert.Equal(first.DeviceInstance.Id, duplicate.DeviceInstance.Id);
        Assert.Single(duplicate.DeviceInstance.Endpoints);
        Assert.Single(registry.GetSnapshot());
    }

    [Fact]
    public void Observe_DifferentBroadcastNamesWithoutStableUid_DoesNotAutomaticallyMergeInstances()
    {
        var catalog = CreatePublicCatalog();
        var registry = new DeviceRegistry(catalog);
        var coordinator = new DiscoveryCoordinator(catalog, registry);

        var first = coordinator.Observe(TestAdvertisement.Named("广播甲", "AA:BB:CC:DD:EE:01"));
        var second = coordinator.Observe(TestAdvertisement.Named("广播乙", "AA:BB:CC:DD:EE:02"));

        Assert.NotNull(first.DeviceInstance);
        Assert.NotNull(second.DeviceInstance);
        Assert.NotEqual(first.DeviceInstance.Id, second.DeviceInstance.Id);
        Assert.Equal(2, registry.GetSnapshot().Count);
    }

    [Fact]
    public void Register_UnknownPlugin_DoesNotFallbackAndRejectsRegistration()
    {
        var catalog = CreatePublicCatalog();
        var registry = new DeviceRegistry(catalog);
        var endpoint = ConnectionEndpoint.FromAdvertisement(
            TestAdvertisement.Named("广播甲"),
            new ProtocolProfileId("test.profile.public"));

        Assert.Throws<InvalidOperationException>(() => registry.Register(new DeviceRegistration(
            new PluginId("test.plugin.unknown"),
            new ModelId("test.model.public"),
            new ProtocolProfileId("test.profile.public"),
            endpoint)));
        Assert.Empty(registry.GetSnapshot());
    }

    [Fact]
    public void Restore_UnknownPlugin_KeepsStableIdsAndMarksInstancePluginUnavailable()
    {
        var catalog = CreatePublicCatalog();
        var registry = new DeviceRegistry(catalog);
        var endpoint = ConnectionEndpoint.FromAdvertisement(
            TestAdvertisement.Named("广播甲"),
            new ProtocolProfileId("test.profile.unknown"));
        var restored = new DeviceInstance(
            new DeviceInstanceId(Guid.NewGuid()),
            new PluginId("test.plugin.unknown"),
            new ModelId("test.model.unknown"),
            new ProtocolProfileId("test.profile.unknown"),
            [endpoint]);

        registry.Restore([restored]);

        var instance = Assert.Single(registry.GetSnapshot());
        Assert.Equal(DeviceInstanceStatus.PluginUnavailable, instance.Status);
        Assert.Equal("test.plugin.unknown", instance.PluginId.Value);
        Assert.Equal("test.model.unknown", instance.ModelId.Value);
        Assert.Equal("test.profile.unknown", instance.ProfileId.Value);
    }

    [Fact]
    public void GetDeviceSnapshot_ReturnsAnImmutableCopyInsteadOfRegistryState()
    {
        var catalog = CreatePublicCatalog();
        var registry = new DeviceRegistry(catalog);
        var coordinator = new DiscoveryCoordinator(catalog, registry);
        coordinator.Observe(TestAdvertisement.Named("广播甲"));
        var query = new DeviceQueryService(catalog, registry);

        var snapshot = query.GetDeviceSnapshot();
        var mutableView = Assert.IsAssignableFrom<IList<DeviceInstance>>(snapshot);

        Assert.Throws<NotSupportedException>(() => mutableView.Clear());
        Assert.Single(registry.GetSnapshot());
    }

    [Fact]
    public void Restore_UpgradeDiagnosticInstance_RejectsItWithoutPublishingInstance()
    {
        var catalog = new DeviceCatalog([
            TestPlugin.Create(
                "test.plugin.upgrade.restore",
                "test.vendor.upgrade.restore",
                "test.model.upgrade.restore",
                "test.profile.upgrade.restore",
                DeviceAvailability.UpgradeDiagnostic,
                "升级设备")
        ]);
        var registry = new DeviceRegistry(catalog);
        var endpoint = ConnectionEndpoint.FromAdvertisement(
            TestAdvertisement.Named("升级设备"),
            new ProtocolProfileId("test.profile.upgrade.restore"));
        var restored = new DeviceInstance(
            new DeviceInstanceId(Guid.NewGuid()),
            new PluginId("test.plugin.upgrade.restore"),
            new ModelId("test.model.upgrade.restore"),
            new ProtocolProfileId("test.profile.upgrade.restore"),
            [endpoint]);

        Assert.Throws<InvalidOperationException>(() => registry.Restore([restored]));
        Assert.Empty(registry.GetSnapshot());
    }

    [Fact]
    public void Restore_DuplicateEndpointFingerprintInInput_RejectsAtomically()
    {
        var catalog = CreatePublicCatalog();
        var registry = new DeviceRegistry(catalog);
        var first = CreateRestoredPublicInstance(TestAdvertisement.Named("广播甲"));
        var second = CreateRestoredPublicInstance(TestAdvertisement.Named("广播乙"));

        Assert.Throws<InvalidOperationException>(() => registry.Restore([first, second]));
        Assert.Empty(registry.GetSnapshot());
    }

    [Fact]
    public void Restore_EndpointFingerprintAlreadyInRegistry_RejectsWithoutPartialWrite()
    {
        var catalog = CreatePublicCatalog();
        var registry = new DeviceRegistry(catalog);
        var coordinator = new DiscoveryCoordinator(catalog, registry);
        coordinator.Observe(TestAdvertisement.Named("广播甲"));
        var conflicting = CreateRestoredPublicInstance(TestAdvertisement.Named("广播乙"));

        Assert.Throws<InvalidOperationException>(() => registry.Restore([conflicting]));
        Assert.Single(registry.GetSnapshot());
    }

    [Fact]
    public void CreateFingerprint_InstantMessaging_DelimiterValuesRemainDistinct()
    {
        var profile = new ProtocolProfileId("test.profile.public");
        var first = ConnectionEndpoint.CreateFingerprint(
            TestAdvertisement.InstantMessaging("甲|乙", "丙"),
            profile);
        var second = ConnectionEndpoint.CreateFingerprint(
            TestAdvertisement.InstantMessaging("甲", "乙|丙"),
            profile);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CreateFingerprint_InstantMessaging_SameIdentityIgnoresProfile()
    {
        var advertisement = TestAdvertisement.InstantMessaging("账号主体", "目标");

        var first = ConnectionEndpoint.CreateFingerprint(advertisement, new ProtocolProfileId("test.profile.first"));
        var second = ConnectionEndpoint.CreateFingerprint(advertisement, new ProtocolProfileId("test.profile.second"));

        Assert.Equal(first, second);
    }

    [Fact]
    public void CreateFingerprint_Bridge_DelimiterValuesRemainDistinct()
    {
        var profile = new ProtocolProfileId("test.profile.public");
        var first = ConnectionEndpoint.CreateFingerprint(
            TestAdvertisement.Bridge("服务|身份", "目标"),
            profile);
        var second = ConnectionEndpoint.CreateFingerprint(
            TestAdvertisement.Bridge("服务", "身份|目标"),
            profile);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CreateFingerprint_Bridge_SameIdentityIgnoresProfile()
    {
        var advertisement = TestAdvertisement.Bridge("服务身份", "目标");

        var first = ConnectionEndpoint.CreateFingerprint(advertisement, new ProtocolProfileId("test.profile.first"));
        var second = ConnectionEndpoint.CreateFingerprint(advertisement, new ProtocolProfileId("test.profile.second"));

        Assert.Equal(first, second);
    }

    [Fact]
    public void CreateFingerprint_BluetoothDuplicateServiceUuid_DoesNotChangeFingerprint()
    {
        var profile = new ProtocolProfileId("test.profile.public");
        var single = ConnectionEndpoint.CreateFingerprint(
            TestAdvertisement.Named("广播甲", serviceUuids: "0000180f-0000-1000-8000-00805f9b34fb"),
            profile);
        var duplicate = ConnectionEndpoint.CreateFingerprint(
            TestAdvertisement.Named(
                "广播乙",
                serviceUuids: "0000180f-0000-1000-8000-00805f9b34fb,0000180F-0000-1000-8000-00805F9B34FB"),
            profile);

        Assert.Equal(single, duplicate);
    }

    [Fact]
    public void Register_UnsupportedEndpointTransport_IsRejected()
    {
        var catalog = CreatePublicCatalog();
        var registry = new DeviceRegistry(catalog);
        var endpoint = ConnectionEndpoint.FromAdvertisement(
            TestAdvertisement.InstantMessaging("账号主体", "目标"),
            new ProtocolProfileId("test.profile.public"));

        Assert.Throws<InvalidOperationException>(() => registry.Register(CreatePublicRegistration(endpoint)));
        Assert.Empty(registry.GetSnapshot());
    }

    [Fact]
    public void BindEndpoint_UnsupportedEndpointTransport_IsRejected()
    {
        var catalog = CreatePublicCatalog();
        var registry = new DeviceRegistry(catalog);
        var coordinator = new DiscoveryCoordinator(catalog, registry);
        var existing = Assert.IsType<DeviceInstance>(coordinator.Observe(TestAdvertisement.Named("广播甲")).DeviceInstance);
        var endpoint = ConnectionEndpoint.FromAdvertisement(
            TestAdvertisement.InstantMessaging("账号主体", "目标"),
            new ProtocolProfileId("test.profile.public"));

        Assert.Throws<InvalidOperationException>(() => registry.BindEndpoint(existing.Id, CreatePublicRegistration(endpoint)));
        Assert.Single(registry.GetSnapshot());
    }

    [Fact]
    public void Register_ForgedEndpointFingerprint_IsRejected()
    {
        var catalog = CreatePublicCatalog();
        var registry = new DeviceRegistry(catalog);
        var endpoint = CreateForgedPublicEndpoint();

        Assert.Throws<InvalidOperationException>(() => registry.Register(CreatePublicRegistration(endpoint)));
        Assert.Empty(registry.GetSnapshot());
    }

    [Fact]
    public void BindEndpoint_ForgedEndpointFingerprint_IsRejected()
    {
        var catalog = CreatePublicCatalog();
        var registry = new DeviceRegistry(catalog);
        var coordinator = new DiscoveryCoordinator(catalog, registry);
        var existing = Assert.IsType<DeviceInstance>(coordinator.Observe(TestAdvertisement.Named("广播甲")).DeviceInstance);

        Assert.Throws<InvalidOperationException>(() => registry.BindEndpoint(existing.Id, CreatePublicRegistration(CreateForgedPublicEndpoint())));
        Assert.Single(registry.GetSnapshot());
    }

    private static DeviceCatalog CreatePublicCatalog() =>
        new([
            TestPlugin.Create(
                "test.plugin.public",
                "test.vendor.public",
                "test.model.public",
                "test.profile.public",
                DeviceAvailability.Public,
                "")
        ]);

    private static DeviceInstance CreateRestoredPublicInstance(DiscoveryAdvertisement advertisement)
    {
        var endpoint = ConnectionEndpoint.FromAdvertisement(
            advertisement,
            new ProtocolProfileId("test.profile.public"));
        return new DeviceInstance(
            new DeviceInstanceId(Guid.NewGuid()),
            new PluginId("test.plugin.public"),
            new ModelId("test.model.public"),
            new ProtocolProfileId("test.profile.public"),
            [endpoint]);
    }

    private static DeviceRegistration CreatePublicRegistration(ConnectionEndpoint endpoint) =>
        new(
            new PluginId("test.plugin.public"),
            new ModelId("test.model.public"),
            new ProtocolProfileId("test.profile.public"),
            endpoint);

    private static ConnectionEndpoint CreateForgedPublicEndpoint()
    {
        var actual = ConnectionEndpoint.FromAdvertisement(
            TestAdvertisement.Named("广播乙", "AA:BB:CC:DD:EE:02"),
            new ProtocolProfileId("test.profile.public"));
        return new ConnectionEndpoint(
            EndpointId.New(),
            actual.ProfileId,
            actual.TransportKind,
            "forged-endpoint-fingerprint",
            actual.DisplayName,
            actual.Properties,
            actual.ManufacturerStableUid);
    }
}
