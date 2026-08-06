using System.Collections.ObjectModel;
using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Devices.Discovery;

namespace ChargingPanel.Core.Devices.Catalog;

public enum CatalogDiagnosticCode
{
    DuplicatePluginId,
    DuplicateVendorId,
    DuplicateModelId,
    DuplicateProfileId,
    DuplicateDiscoveryRuleId,
    InvalidContribution,
    IncompatibleHostApi
}

public sealed record CatalogDiagnostic
{
    public CatalogDiagnostic(
        PluginId pluginId,
        CatalogDiagnosticCode code,
        string message,
        string? conflictingStableId = null)
    {
        _ = pluginId.Value;
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        PluginId = pluginId;
        Code = code;
        Message = message.Trim();
        ConflictingStableId = string.IsNullOrWhiteSpace(conflictingStableId)
            ? null
            : conflictingStableId.Trim();
    }

    public PluginId PluginId { get; }

    public CatalogDiagnosticCode Code { get; }

    public string Message { get; }

    public string? ConflictingStableId { get; }
}

public enum DiscoveryDiagnosticCode
{
    NoMatch,
    AmbiguousMatch,
    UpgradeRequired
}

public sealed record DiscoveryDiagnostic
{
    public DiscoveryDiagnostic(DiscoveryDiagnosticCode code, string message)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        Message = message.Trim();
    }

    public DiscoveryDiagnosticCode Code { get; }

    public string Message { get; }
}

public sealed record CatalogDeviceProfile
{
    public CatalogDeviceProfile(
        PluginId pluginId,
        DeviceModelDescriptor model,
        ProtocolProfileDescriptor profile)
    {
        _ = pluginId.Value;
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(profile);
        PluginId = pluginId;
        Model = model;
        Profile = profile;
    }

    public PluginId PluginId { get; }

    public DeviceModelDescriptor Model { get; }

    public ProtocolProfileDescriptor Profile { get; }
}

public sealed record CatalogMatchResult
{
    public CatalogMatchResult(
        DeviceAvailability? availability,
        PluginId? pluginId,
        ModelId? modelId,
        ProtocolProfileId? profileId,
        bool canCreateSession,
        IEnumerable<DiscoveryDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (canCreateSession && (availability is null || pluginId is null || modelId is null || profileId is null))
        {
            throw new ArgumentException("可创建会话的目录匹配必须包含完整稳定标识。", nameof(canCreateSession));
        }

        Availability = availability;
        PluginId = pluginId;
        ModelId = modelId;
        ProfileId = profileId;
        CanCreateSession = canCreateSession;
        Diagnostics = new ReadOnlyCollection<DiscoveryDiagnostic>(diagnostics.ToArray());
    }

    public DeviceAvailability? Availability { get; }

    public PluginId? PluginId { get; }

    public ModelId? ModelId { get; }

    public ProtocolProfileId? ProfileId { get; }

    public bool CanCreateSession { get; }

    public IReadOnlyList<DiscoveryDiagnostic> Diagnostics { get; }
}

public interface IDeviceCatalog
{
    IReadOnlyList<DeviceModelDescriptor> GetModels(bool includeHiddenReserved = false);

    IReadOnlyList<CatalogDiagnostic> GetDiagnostics();

    CatalogMatchResult Match(DiscoveryAdvertisement advertisement);

    bool TryGetDeviceProfile(
        PluginId pluginId,
        ModelId modelId,
        ProtocolProfileId profileId,
        out CatalogDeviceProfile? deviceProfile);
}
