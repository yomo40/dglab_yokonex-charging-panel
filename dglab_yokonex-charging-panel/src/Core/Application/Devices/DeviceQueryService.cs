using System.Collections.ObjectModel;
using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Devices.Catalog;
using ChargingPanel.Core.Devices.Registry;
using ChargingPanel.Core.Devices.Runtime;

namespace ChargingPanel.Core.Application.Devices;

public sealed class DeviceQueryService : IDeviceQueryService
{
    private readonly IDeviceCatalog _catalog;
    private readonly IDeviceRegistry _registry;
    private readonly IDeviceRuntimeRegistry? _runtimeRegistry;

    public DeviceQueryService(IDeviceCatalog catalog, IDeviceRegistry registry)
        : this(catalog, registry, runtimeRegistry: null)
    {
    }

    public DeviceQueryService(
        IDeviceCatalog catalog,
        IDeviceRegistry registry,
        IDeviceRuntimeRegistry? runtimeRegistry)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _runtimeRegistry = runtimeRegistry;
    }

    public IReadOnlyList<DeviceModelDescriptor> GetModels(bool includeHiddenReserved = false) =>
        new ReadOnlyCollection<DeviceModelDescriptor>(_catalog.GetModels(includeHiddenReserved).ToArray());

    public IReadOnlyList<CatalogDiagnostic> GetCatalogDiagnostics() =>
        new ReadOnlyCollection<CatalogDiagnostic>(_catalog.GetDiagnostics().ToArray());

    public IReadOnlyList<DeviceInstance> GetDeviceSnapshot() =>
        new ReadOnlyCollection<DeviceInstance>(_registry.GetSnapshot().ToArray());

    public bool HasRegisteredDevices() => _registry.GetSnapshot().Count > 0;

    public IReadOnlyList<ActiveDeviceView> GetActiveDevices()
    {
        if (_runtimeRegistry is null)
        {
            return Array.Empty<ActiveDeviceView>();
        }

        return new ReadOnlyCollection<ActiveDeviceView>(
            _runtimeRegistry.ActiveInstanceIds
                .Select(instanceId => _runtimeRegistry.Resolve(instanceId))
                .Where(binding => binding is not null)
                .Select(binding => ToView(binding!))
                .ToArray());
    }

    public ActiveDeviceView? GetActiveDevice(DeviceInstanceId instanceId)
    {
        if (_runtimeRegistry is null)
        {
            return null;
        }

        var binding = _runtimeRegistry.Resolve(instanceId);
        return binding is null ? null : ToView(binding);
    }

    private static ActiveDeviceView ToView(DeviceRuntimeBinding binding) =>
        new(binding.InstanceId, binding.SessionEpoch, binding.Capabilities);
}
