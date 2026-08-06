using System.Text.Json;
using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.Contracts.Commands;

public enum DeviceCommandStatus
{
    AcceptedByTransport,
    Executed,
    Rejected,
    Expired,
    Superseded,
    TransportFailed,
    OutcomeUnknown
}

public sealed record DeviceCommandResult
{
    public DeviceCommandResult(
        CommandId commandId,
        DeviceCommandStatus status,
        string? reasonCode = null,
        string? message = null,
        JsonElement? details = null)
    {
        commandId.ThrowIfInvalid(nameof(commandId));
        ContractGuards.ThrowIfUndefined(status, nameof(status));
        var normalizedReasonCode = NormalizeOptional(reasonCode);
        var normalizedMessage = NormalizeOptional(message);
        if (normalizedReasonCode is not null)
        {
            normalizedReasonCode = StableContractToken.Validate(normalizedReasonCode, nameof(reasonCode));
        }

        var isSuccess = status is DeviceCommandStatus.AcceptedByTransport or DeviceCommandStatus.Executed;
        if (isSuccess && (normalizedReasonCode is not null || normalizedMessage is not null))
        {
            throw new ArgumentException("成功结果不能携带错误诊断。");
        }

        if (!isSuccess && normalizedReasonCode is null && normalizedMessage is null)
        {
            throw new ArgumentException("非成功结果必须携带 reason code 或消息。");
        }

        CommandId = commandId;
        Status = status;
        ReasonCode = normalizedReasonCode;
        Message = normalizedMessage;
        Details = details?.Clone();
    }

    public CommandId CommandId { get; }

    public DeviceCommandStatus Status { get; }

    public string? ReasonCode { get; }

    public string? Message { get; }

    public JsonElement? Details { get; }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record SafeStopContext
{
    public SafeStopContext(
        DeviceInstanceId deviceInstanceId,
        long safetyEpoch,
        long sessionEpoch,
        DateTimeOffset requestedAt)
    {
        deviceInstanceId.ThrowIfInvalid(nameof(deviceInstanceId));
        ContractGuards.ThrowIfNegative(safetyEpoch, nameof(safetyEpoch));
        ContractGuards.ThrowIfNegative(sessionEpoch, nameof(sessionEpoch));
        DeviceInstanceId = deviceInstanceId;
        SafetyEpoch = safetyEpoch;
        SessionEpoch = sessionEpoch;
        RequestedAt = requestedAt;
    }

    public DeviceInstanceId DeviceInstanceId { get; }

    public long SafetyEpoch { get; }

    public long SessionEpoch { get; }

    public DateTimeOffset RequestedAt { get; }
}

public enum SafeStopStatus
{
    ConfirmedStopped,
    Failed,
    OutcomeUnknown
}

public sealed record SafeStopResult
{
    public SafeStopResult(
        SafeStopStatus status,
        string? reasonCode = null,
        string? message = null)
    {
        ContractGuards.ThrowIfUndefined(status, nameof(status));
        var normalizedReasonCode = NormalizeOptional(reasonCode);
        var normalizedMessage = NormalizeOptional(message);
        if (normalizedReasonCode is not null)
        {
            normalizedReasonCode = StableContractToken.Validate(normalizedReasonCode, nameof(reasonCode));
        }

        if (status == SafeStopStatus.ConfirmedStopped &&
            (normalizedReasonCode is not null || normalizedMessage is not null))
        {
            throw new ArgumentException("已确认停止不能携带错误诊断。");
        }

        if (status != SafeStopStatus.ConfirmedStopped &&
            normalizedReasonCode is null && normalizedMessage is null)
        {
            throw new ArgumentException("未确认停止必须携带 reason code 或消息。");
        }

        Status = status;
        ReasonCode = normalizedReasonCode;
        Message = normalizedMessage;
    }

    public SafeStopStatus Status { get; }

    public string? ReasonCode { get; }

    public string? Message { get; }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
