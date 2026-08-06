using System.Text.Json;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Telemetry;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.Yokonex.Protocol;

namespace ChargingPanel.Vendor.Yokonex.Drivers;

/// <summary>灌肠机 BLE 特征路由。写通道 FFB1，通知通道 FFB2。</summary>
internal static class EnemaBleRoutes
{
    public const string Write = "0000ffb1-0000-1000-8000-00805f9b34fb";
    public const string Notify = "0000ffb2-0000-1000-8000-00805f9b34fb";
}

/// <summary>灌肠机查询类型。</summary>
internal enum EnemaQueryKind
{
    Status,
    Battery
}

/// <summary>
/// 灌肠机一代驱动。所有控制帧先构造 16 字节明文再 AES-128 ECB 加密下发；
/// SafeStop 使用官方"暂停工作"指令同时停止双泵。
/// 密钥与明/密文控制帧绝不出现在日志中。
/// </summary>
public sealed class EnemaV10Driver : YokonexDriverBase
{
    private readonly object _sync = new();
    private readonly EnemaV10Codec _codec;
    private readonly IPluginLogger _logger;

    public EnemaV10Driver(ProtocolProfileId profileId, IPluginLogger logger, EnemaV10Codec? codec = null)
        : base(profileId)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _codec = codec ?? new EnemaV10Codec();
    }

    protected override async ValueTask OnOpenAsync(IEndpointLease lease, CancellationToken cancellationToken)
    {
        foreach (var kind in new[] { EnemaQueryKind.Status, EnemaQueryKind.Battery })
        {
            var result = await SendQueryAsync(kind, cancellationToken);
            if (result.Status is not DeviceCommandStatus.AcceptedByTransport and not DeviceCommandStatus.Executed)
            {
                _logger.Log(PluginLogLevel.Debug, "enema.handshake.query_failed", $"握手查询失败：{kind}", null);
            }
        }
    }

    protected override async ValueTask<DeviceCommandResult> ExecuteCoreAsync(
        DeviceCommandEnvelope command,
        IEndpointLease lease,
        CancellationToken cancellationToken)
    {
        return command.CapabilityId.Value switch
        {
            "enema.pump.control" => await ExecutePumpAsync(command, cancellationToken),
            "enema.telemetry" => await ExecuteQueryAsync(command, cancellationToken),
            _ => new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "capability.unsupported", "该灌肠机 Profile 不支持此能力。")
        };
    }

    private async ValueTask<DeviceCommandResult> ExecutePumpAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        if (!TryParsePumpCommand(command.Payload, out var pumpCommand, out var error))
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "command.invalid", error);
        }

        byte[] frame;
        lock (_sync)
        {
            frame = pumpCommand.Pump == EnemaPumpKind.Peristaltic
                ? _codec.BuildPeristalticPump(pumpCommand.State, pumpCommand.DurationSeconds)
                : _codec.BuildSuctionPump(pumpCommand.State, pumpCommand.DurationSeconds);
        }

        return await SendBinaryAsync(
            EnemaBleRoutes.Write,
            frame,
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
    }

    private async ValueTask<DeviceCommandResult> ExecuteQueryAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        if (!TryParseQuery(command.Payload, out var kind, out var error))
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "command.invalid", error);
        }

        return await SendQueryAsync(kind, cancellationToken);
    }

    private async ValueTask<DeviceCommandResult> SendQueryAsync(
        EnemaQueryKind kind,
        CancellationToken cancellationToken)
    {
        byte[] frame;
        lock (_sync)
        {
            frame = kind == EnemaQueryKind.Status ? _codec.BuildQueryStatus() : _codec.BuildGetBattery();
        }

        return await SendBinaryAsync(
            EnemaBleRoutes.Write,
            frame,
            EndpointResponseMode.WithoutResponse,
            CommandId.New(),
            cancellationToken);
    }

    protected override async ValueTask<SafeStopResult> SafeStopCoreAsync(
        SafeStopContext context,
        IEndpointLease lease,
        CancellationToken cancellationToken)
    {
        byte[] frame;
        lock (_sync)
        {
            frame = _codec.BuildPause();
        }

        var result = await SendBinaryAsync(
            EnemaBleRoutes.Write,
            frame,
            EndpointResponseMode.WithoutResponse,
            CommandId.New(),
            cancellationToken);

        return result.Status switch
        {
            DeviceCommandStatus.OutcomeUnknown => new SafeStopResult(
                SafeStopStatus.OutcomeUnknown,
                "stop.outcome_unknown",
                "暂停指令结果未知。"),
            DeviceCommandStatus.AcceptedByTransport or DeviceCommandStatus.Executed =>
                new SafeStopResult(SafeStopStatus.ConfirmedStopped),
            _ => new SafeStopResult(SafeStopStatus.Failed, "stop.send_failed", "暂停指令发送失败。")
        };
    }

    /// <summary>
    /// 接收来自通知特性的密文帧。测试与组合根都通过此入口喂入设备回调。
    /// </summary>
    public void OnNotify(ReadOnlyMemory<byte> ciphertext)
    {
        EnemaReport? report;
        lock (_sync)
        {
            if (!_codec.TryParseReport(ciphertext.Span, out report) || report is null)
            {
                return;
            }
        }

        EmitReportTelemetry(report);
    }

    private void EmitReportTelemetry(EnemaReport report)
    {
        var payloadJson = report switch
        {
            EnemaReport.WorkStatus status =>
                $"{{\"kind\":\"work_status\",\"peristaltic\":\"{ToPumpName(status.Peristaltic)}\"," +
                $"\"suction\":\"{ToPumpName(status.Suction)}\"}}",
            EnemaReport.Pressure pressure =>
                $"{{\"kind\":\"pressure\",\"pressureA\":{pressure.PressureA},\"pressureB\":{pressure.PressureB}}}",
            EnemaReport.Battery battery => $"{{\"kind\":\"battery\",\"percent\":{battery.Percent}}}",
            _ => null
        };

        if (payloadJson is null)
        {
            return;
        }

        if (TryCreateTelemetry(new CapabilityId("enema.telemetry"), payloadJson, null, out var envelope))
        {
            EnqueueTelemetry(envelope);
        }
    }

    private static string ToPumpName(EnemaPumpState state) => state switch
    {
        EnemaPumpState.Stop => "stop",
        EnemaPumpState.Forward => "forward",
        _ => "reverse"
    };

    private static bool TryParsePumpCommand(
        JsonElement payload,
        out EnemaPumpCommand command,
        out string error)
    {
        command = null!;
        error = string.Empty;

        if (payload.ValueKind != JsonValueKind.Object ||
            !TryGetString(payload, "pump", out var rawPump, out error))
        {
            error = error.Length == 0 ? "泵控制载荷必须包含 pump 字段。" : error;
            return false;
        }

        var kind = rawPump.ToLowerInvariant() switch
        {
            "peristaltic" => EnemaPumpKind.Peristaltic,
            "suction" => EnemaPumpKind.Suction,
            _ => (EnemaPumpKind)0
        };
        if (kind == (EnemaPumpKind)0)
        {
            error = "pump 必须是 peristaltic 或 suction。";
            return false;
        }

        if (!TryGetString(payload, "state", out var rawState, out error))
        {
            return false;
        }

        var state = rawState.ToLowerInvariant() switch
        {
            "stop" => EnemaPumpState.Stop,
            "forward" => EnemaPumpState.Forward,
            "reverse" => EnemaPumpState.Reverse,
            _ => (EnemaPumpState)0xFF
        };
        if (state == (EnemaPumpState)0xFF)
        {
            error = "state 必须是 stop/forward/reverse。";
            return false;
        }

        // 抽水泵不支持反转。
        if (kind == EnemaPumpKind.Suction && state == EnemaPumpState.Reverse)
        {
            error = "抽水泵不支持反转。";
            return false;
        }

        var duration = GetInt32(payload, "durationSeconds", 0);
        if (duration < 0 || duration > ushort.MaxValue)
        {
            error = "durationSeconds 必须在 0 到 65535 之间。";
            return false;
        }

        command = new EnemaPumpCommand(kind, state, (ushort)duration);
        return true;
    }

    private static bool TryParseQuery(JsonElement payload, out EnemaQueryKind kind, out string error)
    {
        kind = default;
        error = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object || !TryGetString(payload, "type", out var raw, out error))
        {
            error = error.Length == 0 ? "查询载荷必须包含 type 字段。" : error;
            return false;
        }

        kind = raw.ToLowerInvariant() switch
        {
            "status" => EnemaQueryKind.Status,
            "battery" => EnemaQueryKind.Battery,
            _ => (EnemaQueryKind)0xFF
        };
        if (kind == (EnemaQueryKind)0xFF)
        {
            error = "type 必须是 status 或 battery。";
            return false;
        }

        return true;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value, out string error)
    {
        value = string.Empty;
        error = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            error = $"载荷缺少字符串字段 {propertyName}。";
            return false;
        }

        value = property.GetString()!;
        return true;
    }

    private static int GetInt32(JsonElement element, string propertyName, int defaultValue) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : defaultValue;
}
