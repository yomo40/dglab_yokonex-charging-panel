using System.Text.Json;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.Core.Network.RemoteControl;

/// <summary>
/// 远端命令最终状态。与 Dispatcher 的最终结果对齐：
/// 传输层接收、IM 发送或 WS 写入成功都不是物理执行成功。
/// </summary>
public enum RemoteCommandFinalStatus
{
    Executed,
    Rejected,
    OutcomeUnknown,
    Superseded,
    Expired,
    TransportFailed
}

/// <summary>
/// 远端命令最终结果。ACK/最终结果必须来自 Dispatcher 的
/// Executed/Rejected/OutcomeUnknown，业务代码不得把传输成功谎报为执行成功。
/// </summary>
public sealed record RemoteCommandResult
{
    public RemoteCommandResult(
        CommandId commandId,
        RemoteCommandFinalStatus status,
        string? reasonCode = null,
        string? message = null,
        JsonElement? details = null)
    {
        _ = commandId.Value;
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        var normalizedReasonCode = NormalizeOptional(reasonCode);
        var normalizedMessage = NormalizeOptional(message);

        if (status == RemoteCommandFinalStatus.Executed &&
            (normalizedReasonCode is not null || normalizedMessage is not null))
        {
            throw new ArgumentException("已执行结果不能携带错误诊断。");
        }

        if (status is RemoteCommandFinalStatus.Rejected
            or RemoteCommandFinalStatus.Superseded
            or RemoteCommandFinalStatus.Expired
            or RemoteCommandFinalStatus.TransportFailed
            && normalizedReasonCode is null && normalizedMessage is null)
        {
            throw new ArgumentException("明确失败必须携带 reason code 或消息。");
        }

        CommandId = commandId;
        Status = status;
        ReasonCode = normalizedReasonCode;
        Message = normalizedMessage;
        Details = details?.Clone();
    }

    public CommandId CommandId { get; }

    public RemoteCommandFinalStatus Status { get; }

    public string? ReasonCode { get; }

    public string? Message { get; }

    public JsonElement? Details { get; }

    /// <summary>仅当 Dispatcher 最终确认为 Executed 时才为真。</summary>
    public bool IsExecuted => Status == RemoteCommandFinalStatus.Executed;

    public static RemoteCommandResult Rejected(CommandId commandId, string reasonCode, string message) =>
        new(commandId, RemoteCommandFinalStatus.Rejected, reasonCode, message);

    /// <summary>
    /// 把 Dispatcher 的最终结果映射为远端结果。
    /// <see cref="DeviceCommandStatus.AcceptedByTransport"/> 不是物理执行成功，映射为
    /// <see cref="RemoteCommandFinalStatus.OutcomeUnknown"/>。
    /// </summary>
    public static RemoteCommandResult FromDispatcher(DeviceCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var status = result.Status switch
        {
            DeviceCommandStatus.Executed => RemoteCommandFinalStatus.Executed,
            DeviceCommandStatus.Rejected => RemoteCommandFinalStatus.Rejected,
            DeviceCommandStatus.Expired => RemoteCommandFinalStatus.Expired,
            DeviceCommandStatus.Superseded => RemoteCommandFinalStatus.Superseded,
            DeviceCommandStatus.TransportFailed => RemoteCommandFinalStatus.TransportFailed,
            DeviceCommandStatus.AcceptedByTransport => RemoteCommandFinalStatus.OutcomeUnknown,
            DeviceCommandStatus.OutcomeUnknown => RemoteCommandFinalStatus.OutcomeUnknown,
            _ => RemoteCommandFinalStatus.OutcomeUnknown
        };

        return new RemoteCommandResult(
            result.CommandId,
            status,
            result.ReasonCode,
            result.Message,
            result.Details);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
