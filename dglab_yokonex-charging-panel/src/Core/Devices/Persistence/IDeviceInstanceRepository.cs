using System.Collections.ObjectModel;
using System.Text.Json;
using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.Core.Devices.Persistence;

public enum PersistedTransportKind
{
    BluetoothLowEnergy,
    WebSocket,
    InstantMessaging,
    ApiBridge
}

public sealed record PersistedDeviceEndpoint
{
    public PersistedDeviceEndpoint(
        EndpointId endpointId,
        PersistedTransportKind transportKind,
        string publicConfigJson,
        string? secretReference,
        int priority,
        bool enabled,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        _ = endpointId.Value;
        if (!Enum.IsDefined(transportKind))
        {
            throw new ArgumentOutOfRangeException(nameof(transportKind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(publicConfigJson);
        using var document = JsonDocument.Parse(publicConfigJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Endpoint 公共配置必须是 JSON 对象。", nameof(publicConfigJson));
        }

        if (priority < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }

        EndpointId = endpointId;
        TransportKind = transportKind;
        PublicConfigJson = document.RootElement.GetRawText();
        SecretReference = string.IsNullOrWhiteSpace(secretReference) ? null : secretReference;
        Priority = priority;
        Enabled = enabled;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public EndpointId EndpointId { get; }

    public PersistedTransportKind TransportKind { get; }

    public string PublicConfigJson { get; }

    public string? SecretReference { get; }

    public int Priority { get; }

    public bool Enabled { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }
}

public sealed record PersistedDeviceInstance
{
    public PersistedDeviceInstance(
        DeviceInstanceId instanceId,
        PluginId pluginId,
        ModelId modelId,
        ProtocolProfileId profileId,
        string displayName,
        bool autoConnect,
        int configSchemaVersion,
        IEnumerable<PersistedDeviceEndpoint> endpoints,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        _ = instanceId.Value;
        _ = pluginId.Value;
        _ = modelId.Value;
        _ = profileId.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(endpoints);
        if (configSchemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configSchemaVersion));
        }

        var endpointCopy = endpoints.ToArray();
        if (endpointCopy.Length == 0 || endpointCopy.Any(endpoint => endpoint is null))
        {
            throw new ArgumentException("持久化设备实例必须包含至少一个有效 Endpoint。", nameof(endpoints));
        }

        InstanceId = instanceId;
        PluginId = pluginId;
        ModelId = modelId;
        ProfileId = profileId;
        DisplayName = displayName.Trim();
        AutoConnect = autoConnect;
        ConfigSchemaVersion = configSchemaVersion;
        Endpoints = new ReadOnlyCollection<PersistedDeviceEndpoint>(endpointCopy);
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public DeviceInstanceId InstanceId { get; }

    public PluginId PluginId { get; }

    public ModelId ModelId { get; }

    public ProtocolProfileId ProfileId { get; }

    public string DisplayName { get; }

    public bool AutoConnect { get; }

    public int ConfigSchemaVersion { get; }

    public IReadOnlyList<PersistedDeviceEndpoint> Endpoints { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }
}

public interface IDeviceInstanceRepository
{
    ValueTask<IReadOnlyList<PersistedDeviceInstance>> GetAllAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(PersistedDeviceInstance instance, CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(DeviceInstanceId instanceId, CancellationToken cancellationToken = default);
}
