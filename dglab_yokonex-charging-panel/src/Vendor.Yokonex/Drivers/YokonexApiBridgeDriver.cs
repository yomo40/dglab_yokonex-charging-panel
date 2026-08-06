using System.Text.Json;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.Yokonex.Protocol;

namespace ChargingPanel.Vendor.Yokonex.Drivers;

/// <summary>
/// 官方 YOKONEX API Bridge 驱动。独立于 Native IM 驱动（两者是不同的 Endpoint）。
/// 依据租约传输类型选择 HTTP 或 WS 路由；Bridge 成功只代表已交给 Bridge，绝不等于设备已执行。
/// WS 推送（connected/status/network/message/heartbeat/error）作为遥测上报。
/// </summary>
public sealed class YokonexApiBridgeDriver : YokonexDriverBase
{
    private readonly object _sync = new();
    private readonly IPluginLogger _logger;
    private TransportKind _transportKind = TransportKind.Http;

    public YokonexApiBridgeDriver(ProtocolProfileId profileId, IPluginLogger logger)
        : base(profileId)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async ValueTask OnOpenAsync(IEndpointLease lease, CancellationToken cancellationToken)
    {
        _transportKind = lease.TransportKind;
        var route = _transportKind == TransportKind.Http
            ? YokonexApiBridgeCodec.Http.Health
            : YokonexApiBridgeCodec.Ws.Ping;
        var payload = _transportKind == TransportKind.Http
            ? YokonexApiBridgeCodec.BuildEmptyPayload()
            : YokonexApiBridgeCodec.BuildEmptyPayload();

        var result = await SendJsonAsync(
            route,
            payload,
            EndpointResponseMode.WithoutResponse,
            CommandId.New(),
            cancellationToken);
        if (result.Status is not DeviceCommandStatus.AcceptedByTransport and not DeviceCommandStatus.Executed)
        {
            throw new IOException("API Bridge 握手失败：无法连通 Bridge 服务。");
        }
    }

    protected override async ValueTask<DeviceCommandResult> ExecuteCoreAsync(
        DeviceCommandEnvelope command,
        IEndpointLease lease,
        CancellationToken cancellationToken)
    {
        return command.CapabilityId.Value switch
        {
            "bridge.command.send" => await ExecuteSendCommandAsync(command, cancellationToken),
            "bridge.login" => await ExecuteLoginAsync(command, cancellationToken),
            "bridge.reinit" => await ExecuteReinitAsync(command, cancellationToken),
            "bridge.telemetry" => await ExecuteStatusQueryAsync(command, cancellationToken),
            _ => new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "capability.unsupported", "该 Bridge Profile 不支持此能力。")
        };
    }

    private async ValueTask<DeviceCommandResult> ExecuteSendCommandAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        if (!TryGetString(command.Payload, "commandId", out var commandId, out var error))
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "command.invalid", error);
        }

        var route = RouteFor(YokonexApiBridgeCodec.Http.SendCommand, YokonexApiBridgeCodec.Ws.SendCommand);
        JsonElement payload;
        lock (_sync)
        {
            payload = YokonexApiBridgeCodec.BuildSendCommandPayload(commandId);
        }

        return await SendJsonAsync(route, payload, EndpointResponseMode.WithoutResponse, command.CommandId, cancellationToken);
    }

    private async ValueTask<DeviceCommandResult> ExecuteLoginAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        if (!TryGetString(command.Payload, "uid", out var uid, out var error) ||
            !TryGetString(command.Payload, "token", out var token, out error))
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "command.invalid", error);
        }

        var route = RouteFor(YokonexApiBridgeCodec.Http.Login, YokonexApiBridgeCodec.Ws.Login);
        JsonElement payload;
        lock (_sync)
        {
            payload = YokonexApiBridgeCodec.BuildLoginPayload(uid, token);
        }

        // 登录载荷含凭据：绝不打日志。
        var result = await SendJsonAsync(route, payload, EndpointResponseMode.WithoutResponse, command.CommandId, cancellationToken);
        return result;
    }

    private async ValueTask<DeviceCommandResult> ExecuteReinitAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        var route = RouteFor(YokonexApiBridgeCodec.Http.Reinit, YokonexApiBridgeCodec.Ws.Reinit);
        return await SendJsonAsync(
            route,
            YokonexApiBridgeCodec.BuildEmptyPayload(),
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
    }

    private async ValueTask<DeviceCommandResult> ExecuteStatusQueryAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        var route = RouteFor(YokonexApiBridgeCodec.Http.Status, YokonexApiBridgeCodec.Ws.GetStatus);
        return await SendJsonAsync(
            route,
            YokonexApiBridgeCodec.BuildEmptyPayload(),
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
    }

    protected override async ValueTask<SafeStopResult> SafeStopCoreAsync(
        SafeStopContext context,
        IEndpointLease lease,
        CancellationToken cancellationToken)
    {
        var route = RouteFor(YokonexApiBridgeCodec.Http.SendCommand, YokonexApiBridgeCodec.Ws.SendCommand);
        JsonElement payload;
        lock (_sync)
        {
            payload = YokonexApiBridgeCodec.BuildSendCommandPayload("_stop_all");
        }

        var result = await SendJsonAsync(route, payload, EndpointResponseMode.WithoutResponse, CommandId.New(), cancellationToken);
        return result.Status switch
        {
            DeviceCommandStatus.OutcomeUnknown => new SafeStopResult(
                SafeStopStatus.OutcomeUnknown,
                "stop.outcome_unknown",
                "Bridge 停止指令结果未知。"),
            DeviceCommandStatus.AcceptedByTransport or DeviceCommandStatus.Executed =>
                new SafeStopResult(SafeStopStatus.ConfirmedStopped),
            _ => new SafeStopResult(SafeStopStatus.Failed, "stop.send_failed", "Bridge 停止指令发送失败。")
        };
    }

    /// <summary>
    /// 接收来自 WS 的推送帧。测试与组合根都通过此入口喂入服务器推送。
    /// </summary>
    public void OnMessage(string json)
    {
        if (!YokonexApiBridgeCodec.TryParsePush(json, out var push) || push is null)
        {
            return;
        }

        var payloadJson = push.Kind switch
        {
            BridgePushKind.Connected => $"{{\"kind\":\"connected\",\"data\":{Serialize(push.Data)}}}",
            BridgePushKind.Status => $"{{\"kind\":\"status\",\"data\":{Serialize(push.Data)}}}",
            BridgePushKind.Network => $"{{\"kind\":\"network\",\"data\":{Serialize(push.Data)}}}",
            BridgePushKind.Message => $"{{\"kind\":\"message\",\"data\":{Serialize(push.Data)}}}",
            BridgePushKind.Heartbeat => $"{{\"kind\":\"heartbeat\",\"data\":{Serialize(push.Data)}}}",
            BridgePushKind.Error => $"{{\"kind\":\"error\",\"data\":{Serialize(push.Data)}}}",
            _ => null
        };

        if (payloadJson is null)
        {
            return;
        }

        if (TryCreateTelemetry(new CapabilityId("bridge.telemetry"), payloadJson, null, out var envelope))
        {
            EnqueueTelemetry(envelope);
        }
    }

    private string RouteFor(string httpRoute, string wsRoute) =>
        _transportKind == TransportKind.Http ? httpRoute : wsRoute;

    private static string Serialize(JsonElement element) => element.GetRawText();

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
