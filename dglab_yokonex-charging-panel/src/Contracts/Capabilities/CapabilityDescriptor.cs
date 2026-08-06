using System.Collections.ObjectModel;
using System.Text.Json;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.Contracts.Capabilities;

public enum CapabilityStability
{
    Stable,
    Experimental
}

public enum CommandAcknowledgement
{
    None,
    TransportAcknowledgement,
    DeviceAcknowledgement,
    TelemetryConfirmation
}

public sealed record VersionedJsonSchema
{
    public VersionedJsonSchema(string version, JsonElement schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("JSON Schema 根节点必须是 object。", nameof(schema));
        }

        Version = version.Trim();
        Schema = schema.Clone();
    }

    public string Version { get; }

    public JsonElement Schema { get; }
}

public sealed record NumericRange
{
    public NumericRange(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
        {
            throw new ArgumentException("数值范围必须有限且最小值不能大于最大值。");
        }

        Minimum = minimum;
        Maximum = maximum;
    }

    public double Minimum { get; }

    public double Maximum { get; }

    public bool Contains(NumericRange other) =>
        Minimum <= other.Minimum && Maximum >= other.Maximum;
}

public sealed record CapabilityChannelDescriptor(string ChannelId, string DisplayName)
{
    public string ChannelId { get; } = StableContractToken.Validate(ChannelId, nameof(ChannelId));

    public string DisplayName { get; } = RequireText(DisplayName, nameof(DisplayName));

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}

public sealed record CapabilityActuatorDescriptor
{
    public CapabilityActuatorDescriptor(
        string actuatorId,
        string displayName,
        string unit,
        NumericRange validRange,
        NumericRange defaultSafetyRange,
        IEnumerable<CapabilityChannelDescriptor> channels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actuatorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentNullException.ThrowIfNull(validRange);
        ArgumentNullException.ThrowIfNull(defaultSafetyRange);
        ArgumentNullException.ThrowIfNull(channels);
        if (!validRange.Contains(defaultSafetyRange))
        {
            throw new ArgumentException("默认安全范围必须包含在合法范围内。", nameof(defaultSafetyRange));
        }

        ActuatorId = StableContractToken.Validate(actuatorId, nameof(actuatorId));
        DisplayName = displayName.Trim();
        Unit = unit.Trim();
        ValidRange = validRange;
        DefaultSafetyRange = defaultSafetyRange;
        Channels = CopyUnique(channels, channel => channel.ChannelId, nameof(channels));
        if (Channels.Count == 0)
        {
            throw new ArgumentException("执行器必须至少声明一个通道。", nameof(channels));
        }
    }

    public string ActuatorId { get; }

    public string DisplayName { get; }

    public string Unit { get; }

    public NumericRange ValidRange { get; }

    public NumericRange DefaultSafetyRange { get; }

    public IReadOnlyList<CapabilityChannelDescriptor> Channels { get; }

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

public sealed record CapabilityCommandPolicy
{
    public CapabilityCommandPolicy(
        CommandDelivery delivery,
        CommandAcknowledgement acknowledgement,
        double? maximumRatePerSecond,
        TimeSpan timeout,
        bool canReturnOutcomeUnknown)
    {
        ContractGuards.ThrowIfUndefined(delivery, nameof(delivery));
        ContractGuards.ThrowIfUndefined(acknowledgement, nameof(acknowledgement));
        if (maximumRatePerSecond is <= 0 || maximumRatePerSecond is double.NaN or double.PositiveInfinity)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRatePerSecond));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Delivery = delivery;
        Acknowledgement = acknowledgement;
        MaximumRatePerSecond = maximumRatePerSecond;
        Timeout = timeout;
        CanReturnOutcomeUnknown = canReturnOutcomeUnknown;
    }

    public CommandDelivery Delivery { get; }

    public CommandAcknowledgement Acknowledgement { get; }

    public double? MaximumRatePerSecond { get; }

    public TimeSpan Timeout { get; }

    public bool CanReturnOutcomeUnknown { get; }
}

public sealed record SafeStopPlanDescriptor
{
    public SafeStopPlanDescriptor(
        string operationId,
        JsonElement payload,
        IEnumerable<string> actuatorIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(actuatorIds);
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ArgumentException("安全停止载荷不能为 Undefined 或 null。", nameof(payload));
        }

        var copiedActuatorIds = actuatorIds
            .Select(id => StableContractToken.Validate(id, nameof(actuatorIds)))
            .ToArray();
        if (copiedActuatorIds.Distinct(StringComparer.Ordinal).Count() != copiedActuatorIds.Length)
        {
            throw new ArgumentException("安全停止计划中存在重复执行器。", nameof(actuatorIds));
        }

        OperationId = StableContractToken.Validate(operationId, nameof(operationId));
        Payload = payload.Clone();
        ActuatorIds = Array.AsReadOnly(copiedActuatorIds);
    }

    public string OperationId { get; }

    public JsonElement Payload { get; }

    public IReadOnlyList<string> ActuatorIds { get; }
}

public sealed record CapabilityDescriptor
{
    public CapabilityDescriptor(
        CapabilityId id,
        string displayName,
        CapabilityStability stability,
        IEnumerable<CapabilityActuatorDescriptor> actuators,
        VersionedJsonSchema commandSchema,
        VersionedJsonSchema telemetrySchema,
        CapabilityCommandPolicy commandPolicy,
        SafeStopPlanDescriptor safeStopPlan,
        VersionedJsonSchema uiSchema)
    {
        id.ThrowIfInvalid(nameof(id));
        ContractGuards.ThrowIfUndefined(stability, nameof(stability));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(actuators);
        ArgumentNullException.ThrowIfNull(commandSchema);
        ArgumentNullException.ThrowIfNull(telemetrySchema);
        ArgumentNullException.ThrowIfNull(commandPolicy);
        ArgumentNullException.ThrowIfNull(safeStopPlan);
        ArgumentNullException.ThrowIfNull(uiSchema);

        var copiedActuators = actuators.ToArray();
        if (copiedActuators.Length == 0)
        {
            throw new ArgumentException("能力必须至少声明一个执行器。", nameof(actuators));
        }

        var actuatorIds = copiedActuators.Select(actuator => actuator.ActuatorId).ToArray();
        if (actuatorIds.Distinct(StringComparer.Ordinal).Count() != actuatorIds.Length)
        {
            throw new ArgumentException("能力中存在重复执行器。", nameof(actuators));
        }

        if (!actuatorIds.Order(StringComparer.Ordinal).SequenceEqual(
                safeStopPlan.ActuatorIds.Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw new ArgumentException("安全停止计划必须覆盖能力中的全部执行器。", nameof(safeStopPlan));
        }

        Id = id;
        DisplayName = displayName.Trim();
        Stability = stability;
        Actuators = Array.AsReadOnly(copiedActuators);
        CommandSchema = commandSchema;
        TelemetrySchema = telemetrySchema;
        CommandPolicy = commandPolicy;
        SafeStopPlan = safeStopPlan;
        UiSchema = uiSchema;
    }

    public CapabilityId Id { get; }

    public string DisplayName { get; }

    public CapabilityStability Stability { get; }

    public IReadOnlyList<CapabilityActuatorDescriptor> Actuators { get; }

    public VersionedJsonSchema CommandSchema { get; }

    public VersionedJsonSchema TelemetrySchema { get; }

    public CapabilityCommandPolicy CommandPolicy { get; }

    public SafeStopPlanDescriptor SafeStopPlan { get; }

    public VersionedJsonSchema UiSchema { get; }
}
