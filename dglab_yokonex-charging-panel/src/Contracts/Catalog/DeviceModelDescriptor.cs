using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;

namespace ChargingPanel.Contracts.Catalog;

public enum DeviceAvailability
{
    Public,
    HiddenReserved,
    UpgradeDiagnostic
}

public sealed record VendorDescriptor
{
    public VendorDescriptor(VendorId id, string displayName)
    {
        id.ThrowIfInvalid(nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        Id = id;
        DisplayName = displayName.Trim();
    }

    public VendorId Id { get; }

    public string DisplayName { get; }
}

public sealed record DeviceModelDescriptor
{
    public DeviceModelDescriptor(
        ModelId id,
        VendorId vendorId,
        string displayName,
        DeviceAvailability availability,
        IEnumerable<ProtocolProfileId> protocolProfileIds)
    {
        id.ThrowIfInvalid(nameof(id));
        vendorId.ThrowIfInvalid(nameof(vendorId));
        ContractGuards.ThrowIfUndefined(availability, nameof(availability));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(protocolProfileIds);

        var copiedProtocolProfileIds = protocolProfileIds.ToArray();
        foreach (var protocolProfileId in copiedProtocolProfileIds)
        {
            protocolProfileId.ThrowIfInvalid(nameof(protocolProfileIds));
        }

        Id = id;
        VendorId = vendorId;
        DisplayName = displayName.Trim();
        Availability = availability;
        ProtocolProfileIds = CopyUnique(
            copiedProtocolProfileIds,
            profileId => profileId.Value,
            nameof(protocolProfileIds));
    }

    public ModelId Id { get; }

    public VendorId VendorId { get; }

    public string DisplayName { get; }

    public DeviceAvailability Availability { get; }

    public IReadOnlyList<ProtocolProfileId> ProtocolProfileIds { get; }

    private static IReadOnlyList<T> CopyUnique<T>(
        IEnumerable<T> source,
        Func<T, string> keySelector,
        string parameterName)
    {
        var items = source.ToArray();
        if (items.Select(keySelector).Distinct(StringComparer.Ordinal).Count() != items.Length)
        {
            throw new ArgumentException("集合中存在重复 ID。", parameterName);
        }

        return Array.AsReadOnly(items);
    }
}

public sealed record ProtocolProfileDescriptor
{
    public ProtocolProfileDescriptor(
        ProtocolProfileId id,
        ModelId modelId,
        string displayName,
        IEnumerable<TransportKind> supportedTransports,
        IEnumerable<CapabilityDescriptor> capabilities)
    {
        id.ThrowIfInvalid(nameof(id));
        modelId.ThrowIfInvalid(nameof(modelId));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(supportedTransports);
        ArgumentNullException.ThrowIfNull(capabilities);

        var copiedTransports = supportedTransports.Distinct().ToArray();
        foreach (var transport in copiedTransports)
        {
            ContractGuards.ThrowIfUndefined(transport, nameof(supportedTransports));
        }

        var copiedCapabilities = capabilities.ToArray();
        if (copiedCapabilities.Select(capability => capability.Id).Distinct().Count() != copiedCapabilities.Length)
        {
            throw new ArgumentException("单一协议 Profile 内的 CapabilityId 必须唯一。", nameof(capabilities));
        }

        Id = id;
        ModelId = modelId;
        DisplayName = displayName.Trim();
        SupportedTransports = Array.AsReadOnly(copiedTransports);
        Capabilities = Array.AsReadOnly(copiedCapabilities);
    }

    public ProtocolProfileId Id { get; }

    public ModelId ModelId { get; }

    public string DisplayName { get; }

    public IReadOnlyList<TransportKind> SupportedTransports { get; }

    public IReadOnlyList<CapabilityDescriptor> Capabilities { get; }
}

public sealed record DiscoveryRuleDescriptor
{
    public DiscoveryRuleDescriptor(
        string ruleId,
        ProtocolProfileId profileId,
        TransportKind transportKind,
        VersionedJsonSchema matchSchema)
    {
        profileId.ThrowIfInvalid(nameof(profileId));
        ContractGuards.ThrowIfUndefined(transportKind, nameof(transportKind));
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        ArgumentNullException.ThrowIfNull(matchSchema);

        RuleId = StableContractToken.Validate(ruleId, nameof(ruleId));
        ProfileId = profileId;
        TransportKind = transportKind;
        MatchSchema = matchSchema;
    }

    public string RuleId { get; }

    public ProtocolProfileId ProfileId { get; }

    public TransportKind TransportKind { get; }

    public VersionedJsonSchema MatchSchema { get; }
}
