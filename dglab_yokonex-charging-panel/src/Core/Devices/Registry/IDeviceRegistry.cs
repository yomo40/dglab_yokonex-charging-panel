using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.Core.Devices.Registry;

public sealed record DeviceRegistration
{
    public DeviceRegistration(
        PluginId pluginId,
        ModelId modelId,
        ProtocolProfileId profileId,
        ConnectionEndpoint endpoint)
    {
        _ = pluginId.Value;
        _ = modelId.Value;
        _ = profileId.Value;
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.ProfileId.Value != profileId.Value)
        {
            throw new ArgumentException("注册 Endpoint 的 Profile 必须与设备注册一致。", nameof(endpoint));
        }

        PluginId = pluginId;
        ModelId = modelId;
        ProfileId = profileId;
        Endpoint = endpoint;
    }

    public PluginId PluginId { get; }

    public ModelId ModelId { get; }

    public ProtocolProfileId ProfileId { get; }

    public ConnectionEndpoint Endpoint { get; }
}

public interface IDeviceRegistry
{
    DeviceInstance Register(DeviceRegistration registration);

    DeviceInstance BindEndpoint(DeviceInstanceId instanceId, DeviceRegistration registration);

    void Restore(IEnumerable<DeviceInstance> instances);

    IReadOnlyList<DeviceInstance> GetSnapshot();
}
