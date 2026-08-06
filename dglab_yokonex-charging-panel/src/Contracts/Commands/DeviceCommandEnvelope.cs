using System.Text.Json;
using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.Contracts.Commands;

public enum CommandSourceKind
{
    UserInterface,
    Automation,
    Lan,
    Plugin,
    System
}

public sealed record CommandSource
{
    public CommandSource(CommandSourceKind kind, string principal)
    {
        ContractGuards.ThrowIfUndefined(kind, nameof(kind));
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        Kind = kind;
        Principal = principal.Trim();
    }

    public CommandSourceKind Kind { get; }

    public string Principal { get; }
}

public enum CommandDelivery
{
    AtMostOnce,
    Idempotent,
    StreamingFrame
}

public static class CommandDeliveryExtensions
{
    public static bool CanRetryAfterOutcomeUnknown(this CommandDelivery delivery) =>
        delivery == CommandDelivery.Idempotent;
}

public sealed record DeviceCommandEnvelope
{
    public DeviceCommandEnvelope(
        CommandId commandId,
        DeviceInstanceId deviceInstanceId,
        CapabilityId capabilityId,
        string operationId,
        JsonElement payload,
        CommandSource source,
        CommandDelivery delivery,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt,
        long safetyEpoch,
        long sessionEpoch)
    {
        commandId.ThrowIfInvalid(nameof(commandId));
        deviceInstanceId.ThrowIfInvalid(nameof(deviceInstanceId));
        capabilityId.ThrowIfInvalid(nameof(capabilityId));
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(source);
        ContractGuards.ThrowIfUndefined(delivery, nameof(delivery));
        ContractGuards.ThrowIfNegative(safetyEpoch, nameof(safetyEpoch));
        ContractGuards.ThrowIfNegative(sessionEpoch, nameof(sessionEpoch));
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ArgumentException("命令载荷不能为 Undefined 或 null。", nameof(payload));
        }

        CommandId = commandId;
        DeviceInstanceId = deviceInstanceId;
        CapabilityId = capabilityId;
        OperationId = StableContractToken.Validate(operationId, nameof(operationId));
        Payload = payload.Clone();
        Source = source;
        Delivery = delivery;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        SafetyEpoch = safetyEpoch;
        SessionEpoch = sessionEpoch;
    }

    public CommandId CommandId { get; }

    public DeviceInstanceId DeviceInstanceId { get; }

    public CapabilityId CapabilityId { get; }

    public string OperationId { get; }

    public JsonElement Payload { get; }

    public CommandSource Source { get; }

    public CommandDelivery Delivery { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public long SafetyEpoch { get; }

    public long SessionEpoch { get; }

    public bool IsExpired(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return ExpiresAt is { } expiresAt && expiresAt <= timeProvider.GetUtcNow();
    }

    public void Deconstruct(
        out CommandId commandId,
        out DeviceInstanceId deviceInstanceId,
        out CapabilityId capabilityId,
        out string operationId,
        out JsonElement payload,
        out CommandSource source,
        out CommandDelivery delivery,
        out DateTimeOffset createdAt,
        out DateTimeOffset? expiresAt,
        out long safetyEpoch,
        out long sessionEpoch)
    {
        commandId = CommandId;
        deviceInstanceId = DeviceInstanceId;
        capabilityId = CapabilityId;
        operationId = OperationId;
        payload = Payload;
        source = Source;
        delivery = Delivery;
        createdAt = CreatedAt;
        expiresAt = ExpiresAt;
        safetyEpoch = SafetyEpoch;
        sessionEpoch = SessionEpoch;
    }
}
