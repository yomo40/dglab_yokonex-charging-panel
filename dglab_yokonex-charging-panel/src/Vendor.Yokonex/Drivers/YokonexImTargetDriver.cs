using System.Text.Json;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.Yokonex.Protocol;

namespace ChargingPanel.Vendor.Yokonex.Drivers;

/// <summary>IM 目标路由。适配层将该路由映射为向目标用户发送 C2C 文本。</summary>
internal static class ImRoutes
{
    public const string Send = "c2c";
}

/// <summary>
/// 役次元 IM 目标驱动。登录由账户池负责，驱动只通过租约发送 C2C 文本。
/// 稳定指令面使用 code=game_cmd + 字符串 id；厂商标记"正在开发"的
/// 自定义参数/全局指令/元数据更新/旧版 game_info 均保持 Experimental，默认禁用。
/// SDK 成功只代表已交给 IM（AcceptedByTransport），绝不伪造成设备已执行。
/// </summary>
public sealed class YokonexImTargetDriver : YokonexDriverBase
{
    private readonly object _sync = new();
    private readonly IPluginLogger _logger;

    public YokonexImTargetDriver(ProtocolProfileId profileId, IPluginLogger logger)
        : base(profileId)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async ValueTask<DeviceCommandResult> ExecuteCoreAsync(
        DeviceCommandEnvelope command,
        IEndpointLease lease,
        CancellationToken cancellationToken)
    {
        return command.CapabilityId.Value switch
        {
            "im.command.send" => await ExecuteCommandSendAsync(command, cancellationToken),
            "im.global.stop" or "im.command.custom" or "im.metadata.update" or "im.game_info.legacy" =>
                new DeviceCommandResult(
                    command.CommandId,
                    DeviceCommandStatus.Rejected,
                    "capability.experimental_disabled",
                    "该指令仍在开发中，默认禁用。"),
            _ => new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "capability.unsupported", "该 IM Profile 不支持此能力。")
        };
    }

    private async ValueTask<DeviceCommandResult> ExecuteCommandSendAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        if (!TryGetString(command.Payload, "commandId", out var commandId, out var error))
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "command.invalid", error);
        }

        JsonElement message;
        lock (_sync)
        {
            message = YokonexImCodec.BuildGameCommand(commandId);
        }

        return await SendJsonAsync(
            ImRoutes.Send,
            message,
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
    }

    protected override async ValueTask<SafeStopResult> SafeStopCoreAsync(
        SafeStopContext context,
        IEndpointLease lease,
        CancellationToken cancellationToken)
    {
        // 尽力向远端设备下发全局停止指令；结果未知时不谎报已停止。
        JsonElement message;
        lock (_sync)
        {
            message = YokonexImCodec.BuildGlobalStop();
        }

        var result = await SendJsonAsync(
            ImRoutes.Send,
            message,
            EndpointResponseMode.WithoutResponse,
            CommandId.New(),
            cancellationToken);

        return result.Status switch
        {
            DeviceCommandStatus.OutcomeUnknown => new SafeStopResult(
                SafeStopStatus.OutcomeUnknown,
                "stop.outcome_unknown",
                "全局停止指令结果未知。"),
            DeviceCommandStatus.AcceptedByTransport or DeviceCommandStatus.Executed =>
                new SafeStopResult(SafeStopStatus.ConfirmedStopped),
            _ => new SafeStopResult(SafeStopStatus.Failed, "stop.send_failed", "全局停止指令发送失败。")
        };
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value, out string error)
    {
        value = string.Empty;
        error = string.Empty;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            error = $"载荷缺少字符串字段 {propertyName}。";
            return false;
        }

        value = property.GetString()!;
        return true;
    }
}
