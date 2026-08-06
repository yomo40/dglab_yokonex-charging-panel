using System.Collections.ObjectModel;
using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Devices.Catalog;

namespace ChargingPanel.Core.Devices.Registry;

public sealed class DeviceRegistry : IDeviceRegistry
{
    private readonly object _gate = new();
    private readonly IDeviceCatalog _catalog;
    private readonly Dictionary<Guid, DeviceInstance> _instances = [];

    public DeviceRegistry(IDeviceCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public DeviceInstance Register(DeviceRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var profile = ResolveRegistration(registration);

        lock (_gate)
        {
            var existingByFingerprint = _instances.Values.FirstOrDefault(instance =>
                instance.Endpoints.Any(endpoint => endpoint.Fingerprint == registration.Endpoint.Fingerprint));
            if (existingByFingerprint is not null)
            {
                EnsureSameDevice(existingByFingerprint, registration);
                return existingByFingerprint;
            }

            if (registration.Endpoint.ManufacturerStableUid is { } stableUid)
            {
                var existingByStableUid = _instances.Values.FirstOrDefault(instance =>
                    HasSameDevice(instance, registration) &&
                    instance.Endpoints.Any(endpoint => endpoint.ManufacturerStableUid == stableUid));
                if (existingByStableUid is not null)
                {
                    var updated = existingByStableUid.WithEndpoint(registration.Endpoint);
                    _instances[updated.Id.Value] = updated;
                    return updated;
                }
            }

            var created = new DeviceInstance(
                DeviceInstanceId.New(),
                profile.PluginId,
                profile.Model.Id,
                profile.Profile.Id,
                [registration.Endpoint]);
            _instances.Add(created.Id.Value, created);
            return created;
        }
    }

    public DeviceInstance BindEndpoint(DeviceInstanceId instanceId, DeviceRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _ = ResolveRegistration(registration);

        lock (_gate)
        {
            if (!_instances.TryGetValue(instanceId.Value, out var existing))
            {
                throw new KeyNotFoundException("找不到要绑定 Endpoint 的设备实例。");
            }

            EnsureSameDevice(existing, registration);
            var owner = _instances.Values.FirstOrDefault(instance =>
                instance.Endpoints.Any(endpoint => endpoint.Fingerprint == registration.Endpoint.Fingerprint));
            if (owner is not null)
            {
                if (owner.Id.Value != existing.Id.Value)
                {
                    throw new InvalidOperationException("Endpoint 已属于另一个设备实例。用户绑定不能隐式移动 Endpoint。");
                }

                return existing;
            }

            var updated = existing.WithEndpoint(registration.Endpoint);
            _instances[updated.Id.Value] = updated;
            return updated;
        }
    }

    public void Restore(IEnumerable<DeviceInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var restored = instances.ToArray();
        if (restored.Any(instance => instance is null))
        {
            throw new ArgumentException("恢复的设备实例不能为 null。", nameof(instances));
        }

        if (restored.Select(instance => instance.Id.Value).Distinct().Count() != restored.Length)
        {
            throw new ArgumentException("恢复的设备实例包含重复 ID。", nameof(instances));
        }

        var normalized = restored
            .Select(NormalizeRestoredInstance)
            .ToArray();
        var restoredFingerprints = normalized
            .SelectMany(instance => instance.Endpoints)
            .Select(endpoint => endpoint.Fingerprint)
            .ToArray();
        if (restoredFingerprints.Distinct(StringComparer.Ordinal).Count() != restoredFingerprints.Length)
        {
            throw new InvalidOperationException("恢复的设备实例包含重复 Endpoint 指纹。");
        }

        lock (_gate)
        {
            foreach (var instance in normalized)
            {
                if (_instances.ContainsKey(instance.Id.Value))
                {
                    throw new InvalidOperationException("不能恢复已存在的设备实例。");
                }
            }

            var existingFingerprints = _instances.Values
                .SelectMany(instance => instance.Endpoints)
                .Select(endpoint => endpoint.Fingerprint)
                .ToHashSet(StringComparer.Ordinal);
            if (restoredFingerprints.Any(existingFingerprints.Contains))
            {
                throw new InvalidOperationException("恢复的 Endpoint 指纹已存在于 Registry。");
            }

            foreach (var instance in normalized)
            {
                _instances.Add(instance.Id.Value, instance);
            }
        }
    }

    public IReadOnlyList<DeviceInstance> GetSnapshot()
    {
        lock (_gate)
        {
            return new ReadOnlyCollection<DeviceInstance>(
                _instances.Values.OrderBy(instance => instance.Id.Value).ToArray());
        }
    }

    private CatalogDeviceProfile ResolveRegistration(DeviceRegistration registration)
    {
        if (!_catalog.TryGetDeviceProfile(
                registration.PluginId,
                registration.ModelId,
                registration.ProfileId,
                out var profile) ||
            profile is null)
        {
            throw new InvalidOperationException("未知的插件、模型或协议 Profile，不能回退到其他厂商。");
        }

        if (profile.Model.Availability == DeviceAvailability.UpgradeDiagnostic)
        {
            throw new InvalidOperationException("升级诊断设备不能直接注册为可用实例。");
        }

        ValidateEndpoint(profile, registration.Endpoint);

        return profile;
    }

    private DeviceInstance NormalizeRestoredInstance(DeviceInstance instance)
    {
        ValidateEndpointFingerprints(instance.Endpoints);
        if (!_catalog.TryGetDeviceProfile(instance.PluginId, instance.ModelId, instance.ProfileId, out var profile) ||
            profile is null)
        {
            return instance.WithStatus(DeviceInstanceStatus.PluginUnavailable);
        }

        if (profile.Model.Availability == DeviceAvailability.UpgradeDiagnostic)
        {
            throw new InvalidOperationException("升级诊断设备不能恢复为可查询实例。");
        }

        foreach (var endpoint in instance.Endpoints)
        {
            ValidateEndpoint(profile, endpoint);
        }

        return instance.WithStatus(DeviceInstanceStatus.Available);
    }

    private static void ValidateEndpoint(CatalogDeviceProfile profile, ConnectionEndpoint endpoint)
    {
        if (!profile.Profile.SupportedTransports.Contains(endpoint.TransportKind))
        {
            throw new InvalidOperationException("Endpoint 传输不受协议 Profile 支持。");
        }

        ValidateEndpointFingerprints([endpoint]);
    }

    private static void ValidateEndpointFingerprints(IEnumerable<ConnectionEndpoint> endpoints)
    {
        if (endpoints.Any(endpoint => !endpoint.HasCanonicalFingerprint()))
        {
            throw new InvalidOperationException("Endpoint 指纹与其公开传输字段不一致。");
        }
    }

    private static bool HasSameDevice(DeviceInstance instance, DeviceRegistration registration) =>
        instance.PluginId.Value == registration.PluginId.Value &&
        instance.ModelId.Value == registration.ModelId.Value &&
        instance.ProfileId.Value == registration.ProfileId.Value;

    private static void EnsureSameDevice(DeviceInstance instance, DeviceRegistration registration)
    {
        if (!HasSameDevice(instance, registration))
        {
            throw new InvalidOperationException("同一 Endpoint 指纹不能被解释为不同插件、模型或协议 Profile。");
        }
    }
}
