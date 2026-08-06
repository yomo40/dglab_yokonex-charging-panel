using System.Collections.ObjectModel;
using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.Core.Devices.Registry;

public enum DeviceInstanceStatus
{
    Available,
    PluginUnavailable,
    UpgradeDiagnostic
}

public sealed record DeviceInstance
{
    public DeviceInstance(
        DeviceInstanceId id,
        PluginId pluginId,
        ModelId modelId,
        ProtocolProfileId profileId,
        IEnumerable<ConnectionEndpoint> endpoints,
        DeviceInstanceStatus status = DeviceInstanceStatus.Available)
    {
        _ = id.Value;
        _ = pluginId.Value;
        _ = modelId.Value;
        _ = profileId.Value;
        ArgumentNullException.ThrowIfNull(endpoints);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        var copiedEndpoints = endpoints.ToArray();
        if (copiedEndpoints.Length == 0)
        {
            throw new ArgumentException("设备实例必须至少保留一个 Endpoint。", nameof(endpoints));
        }

        if (copiedEndpoints.Any(endpoint => endpoint is null))
        {
            throw new ArgumentException("设备实例 Endpoint 不能为 null。", nameof(endpoints));
        }

        if (copiedEndpoints.Any(endpoint => endpoint.ProfileId.Value != profileId.Value))
        {
            throw new ArgumentException("设备实例中的 Endpoint 必须使用相同的协议 Profile。", nameof(endpoints));
        }

        if (copiedEndpoints.Select(endpoint => endpoint.Fingerprint).Distinct(StringComparer.Ordinal).Count() != copiedEndpoints.Length)
        {
            throw new ArgumentException("设备实例中存在重复 Endpoint 指纹。", nameof(endpoints));
        }

        Id = id;
        PluginId = pluginId;
        ModelId = modelId;
        ProfileId = profileId;
        Endpoints = new ReadOnlyCollection<ConnectionEndpoint>(copiedEndpoints);
        Status = status;
    }

    public DeviceInstanceId Id { get; }

    public PluginId PluginId { get; }

    public ModelId ModelId { get; }

    public ProtocolProfileId ProfileId { get; }

    public IReadOnlyList<ConnectionEndpoint> Endpoints { get; }

    public DeviceInstanceStatus Status { get; }

    internal DeviceInstance WithEndpoint(ConnectionEndpoint endpoint) =>
        new(Id, PluginId, ModelId, ProfileId, Endpoints.Append(endpoint), Status);

    internal DeviceInstance WithStatus(DeviceInstanceStatus status) =>
        new(Id, PluginId, ModelId, ProfileId, Endpoints, status);
}
