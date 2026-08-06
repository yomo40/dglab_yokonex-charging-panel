using System.Collections.ObjectModel;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Core.Devices.Catalog;
using ChargingPanel.Core.Devices.Registry;

namespace ChargingPanel.Core.Devices.Discovery;

public sealed record DiscoveryAdvertisement
{
    public DiscoveryAdvertisement(
        TransportKind transportKind,
        string displayName,
        IReadOnlyDictionary<string, string> properties,
        string? manufacturerStableUid = null)
    {
        if (!Enum.IsDefined(transportKind))
        {
            throw new ArgumentOutOfRangeException(nameof(transportKind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(properties);
        if (properties.Any(property => string.IsNullOrWhiteSpace(property.Key) || property.Value is null))
        {
            throw new ArgumentException("发现属性必须包含非空键和值。", nameof(properties));
        }

        TransportKind = transportKind;
        DisplayName = displayName.Trim();
        Properties = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(properties, StringComparer.Ordinal));
        ManufacturerStableUid = string.IsNullOrWhiteSpace(manufacturerStableUid)
            ? null
            : manufacturerStableUid.Trim();
    }

    public TransportKind TransportKind { get; }

    public string DisplayName { get; }

    public IReadOnlyDictionary<string, string> Properties { get; }

    public string? ManufacturerStableUid { get; }
}

public sealed record DiscoveryResult
{
    public DiscoveryResult(CatalogMatchResult match, DeviceInstance? deviceInstance)
    {
        Match = match ?? throw new ArgumentNullException(nameof(match));
        DeviceInstance = deviceInstance;
    }

    public CatalogMatchResult Match { get; }

    public DeviceInstance? DeviceInstance { get; }
}

public sealed class DiscoveryCoordinator
{
    private readonly IDeviceCatalog _catalog;
    private readonly IDeviceRegistry _registry;

    public DiscoveryCoordinator(IDeviceCatalog catalog, IDeviceRegistry registry)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public DiscoveryResult Observe(DiscoveryAdvertisement advertisement)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        var match = _catalog.Match(advertisement);
        if (!match.CanCreateSession)
        {
            return new DiscoveryResult(match, null);
        }

        var pluginId = match.PluginId ?? throw new InvalidOperationException("可创建会话的发现结果缺少插件 ID。");
        var modelId = match.ModelId ?? throw new InvalidOperationException("可创建会话的发现结果缺少模型 ID。");
        var profileId = match.ProfileId ?? throw new InvalidOperationException("可创建会话的发现结果缺少 Profile ID。");
        var endpoint = ConnectionEndpoint.FromAdvertisement(advertisement, profileId);
        var instance = _registry.Register(new DeviceRegistration(pluginId, modelId, profileId, endpoint));
        return new DiscoveryResult(match, instance);
    }
}
