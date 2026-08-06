using System.Text.Json;
using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.Contracts.Telemetry;

public enum TelemetryQuality
{
    Good,
    Degraded,
    Invalid
}

public sealed record DeviceTelemetryEnvelope
{
    public DeviceTelemetryEnvelope(
        DeviceInstanceId deviceInstanceId,
        ProtocolProfileId profileId,
        CapabilityId capabilityId,
        string schemaVersion,
        JsonElement payload,
        DateTimeOffset timestamp,
        long sessionEpoch,
        long? sequenceNumber,
        TelemetryQuality quality)
    {
        deviceInstanceId.ThrowIfInvalid(nameof(deviceInstanceId));
        profileId.ThrowIfInvalid(nameof(profileId));
        capabilityId.ThrowIfInvalid(nameof(capabilityId));
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaVersion);
        ContractGuards.ThrowIfNegative(sessionEpoch, nameof(sessionEpoch));
        if (sequenceNumber is { } value)
        {
            ContractGuards.ThrowIfNegative(value, nameof(sequenceNumber));
        }

        ContractGuards.ThrowIfUndefined(quality, nameof(quality));
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ArgumentException("遥测载荷不能为 Undefined 或 null。", nameof(payload));
        }

        DeviceInstanceId = deviceInstanceId;
        ProfileId = profileId;
        CapabilityId = capabilityId;
        SchemaVersion = StableContractToken.Validate(schemaVersion, nameof(schemaVersion));
        Payload = payload.Clone();
        Timestamp = timestamp;
        SessionEpoch = sessionEpoch;
        SequenceNumber = sequenceNumber;
        Quality = quality;
    }

    public DeviceInstanceId DeviceInstanceId { get; }

    public ProtocolProfileId ProfileId { get; }

    public CapabilityId CapabilityId { get; }

    public string SchemaVersion { get; }

    public JsonElement Payload { get; }

    public DateTimeOffset Timestamp { get; }

    public long SessionEpoch { get; }

    public long? SequenceNumber { get; }

    public TelemetryQuality Quality { get; }
}
