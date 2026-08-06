using System.Text.Json;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.Yokonex.Protocol;

namespace ChargingPanel.Vendor.Yokonex.Drivers;

/// <summary>玩具 BLE 特征路由。写通道 FF41，通知通道 FF42。</summary>
internal static class ToyBleRoutes
{
    public const string Write = "0000ff41-0000-1000-8000-00805f9b34fb";
    public const string Notify = "0000ff42-0000-1000-8000-00805f9b34fb";
}

/// <summary>
/// 跳蛋/飞机杯 V1.1 驱动。握手时查询设备信息以确定实际马达数量与模式数，
/// 能力验证必须来自查询结果，绝不假设每个设备都有三马达或相同模式数。
/// SafeStop 发送 A/B/C=0 的实时力度帧。
/// </summary>
public sealed class ToyV11Driver : YokonexDriverBase
{
    private readonly object _sync = new();
    private readonly IPluginLogger _logger;
    private ToyDeviceInfo? _deviceInfo;

    public ToyV11Driver(ProtocolProfileId profileId, IPluginLogger logger)
        : base(profileId)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async ValueTask OnOpenAsync(IEndpointLease lease, CancellationToken cancellationToken)
    {
        var result = await SendBinaryAsync(
            ToyBleRoutes.Write,
            ToyV11Codec.BuildDeviceInfoQuery(),
            EndpointResponseMode.WithoutResponse,
            CommandId.New(),
            cancellationToken);
        if (result.Status is not DeviceCommandStatus.AcceptedByTransport and not DeviceCommandStatus.Executed)
        {
            _logger.Log(PluginLogLevel.Debug, "toy.handshake.info_query_failed", "握手设备信息查询失败。", null);
        }
    }

    protected override async ValueTask<DeviceCommandResult> ExecuteCoreAsync(
        DeviceCommandEnvelope command,
        IEndpointLease lease,
        CancellationToken cancellationToken)
    {
        return command.CapabilityId.Value switch
        {
            "toy.motor.control" => await ExecuteFixedModeAsync(command, cancellationToken),
            "toy.speed.control" => await ExecuteSpeedAsync(command, cancellationToken),
            "toy.telemetry" => await ExecuteInfoQueryAsync(command, cancellationToken),
            _ => new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "capability.unsupported", "该玩具 Profile 不支持此能力。")
        };
    }

    private async ValueTask<DeviceCommandResult> ExecuteFixedModeAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        ToyDeviceInfo? info;
        lock (_sync)
        {
            info = _deviceInfo;
        }

        if (info is null)
        {
            return new DeviceCommandResult(
                command.CommandId,
                DeviceCommandStatus.Rejected,
                "toy.info_unknown",
                "尚未取得设备信息，无法安全控制马达。");
        }

        if (!TryParseMotorMask(command.Payload, out var mask, out var mode, out var error) ||
            !TryValidateMotors(mask, info, out error))
        {
            return new DeviceCommandResult(
                command.CommandId,
                DeviceCommandStatus.Rejected,
                "command.invalid",
                error.Length == 0 ? "马达控制载荷无效。" : error);
        }

        if (mode > MaxModeCount(mask, info))
        {
            return new DeviceCommandResult(
                command.CommandId,
                DeviceCommandStatus.Rejected,
                "command.invalid",
                "模式编号超出该马达的固定模式数量。");
        }

        byte[] frame;
        lock (_sync)
        {
            frame = ToyV11Codec.BuildFixedMode(mask, (byte)mode);
        }

        return await SendBinaryAsync(
            ToyBleRoutes.Write,
            frame,
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
    }

    private async ValueTask<DeviceCommandResult> ExecuteSpeedAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        ToyDeviceInfo? info;
        lock (_sync)
        {
            info = _deviceInfo;
        }

        if (info is null)
        {
            return new DeviceCommandResult(
                command.CommandId,
                DeviceCommandStatus.Rejected,
                "toy.info_unknown",
                "尚未取得设备信息，无法安全控制马达。");
        }

        if (!TryParseSpeed(command.Payload, out var a, out var b, out var c, out var error))
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "command.invalid", error);
        }

        var mask = (a > 0 ? ToyMotorMask.A : ToyMotorMask.None)
                   | (b > 0 ? ToyMotorMask.B : ToyMotorMask.None)
                   | (c > 0 ? ToyMotorMask.C : ToyMotorMask.None);
        if (mask != ToyMotorMask.None && !TryValidateMotors(mask, info, out error))
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "command.invalid", error);
        }

        byte[] frame;
        lock (_sync)
        {
            frame = ToyV11Codec.BuildSpeed((byte)a, (byte)b, (byte)c);
        }

        return await SendBinaryAsync(
            ToyBleRoutes.Write,
            frame,
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
    }

    private async ValueTask<DeviceCommandResult> ExecuteInfoQueryAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken) =>
        await SendBinaryAsync(
            ToyBleRoutes.Write,
            ToyV11Codec.BuildDeviceInfoQuery(),
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);

    protected override async ValueTask<SafeStopResult> SafeStopCoreAsync(
        SafeStopContext context,
        IEndpointLease lease,
        CancellationToken cancellationToken)
    {
        byte[] frame;
        lock (_sync)
        {
            frame = ToyV11Codec.BuildSpeed(0, 0, 0);
        }

        var result = await SendBinaryAsync(
            ToyBleRoutes.Write,
            frame,
            EndpointResponseMode.WithoutResponse,
            CommandId.New(),
            cancellationToken);

        return result.Status switch
        {
            DeviceCommandStatus.OutcomeUnknown => new SafeStopResult(
                SafeStopStatus.OutcomeUnknown,
                "stop.outcome_unknown",
                "归零帧结果未知。"),
            DeviceCommandStatus.AcceptedByTransport or DeviceCommandStatus.Executed =>
                new SafeStopResult(SafeStopStatus.ConfirmedStopped),
            _ => new SafeStopResult(SafeStopStatus.Failed, "stop.send_failed", "归零帧发送失败。")
        };
    }

    /// <summary>
    /// 接收来自通知特性的原始帧。测试与组合根都通过此入口喂入设备回调。
    /// </summary>
    public void OnNotify(ReadOnlyMemory<byte> frame)
    {
        if (ToyV11Codec.TryParseDeviceInfo(frame.Span, out var info) && info is not null)
        {
            lock (_sync)
            {
                _deviceInfo = info;
            }

            EmitDeviceInfoTelemetry(info);
            return;
        }

        if (ToyV11Codec.TryParseBattery(frame.Span, out var battery) && battery is not null)
        {
            EmitBatteryTelemetry(battery);
        }
    }

    private void EmitDeviceInfoTelemetry(ToyDeviceInfo info)
    {
        if (TryCreateTelemetry(
                new CapabilityId("toy.telemetry"),
                $"{{\"kind\":\"device_info\",\"modelId\":{info.ModelId},\"version\":{info.Version}," +
                $"\"motorAModeCount\":{info.MotorAModeCount},\"motorBModeCount\":{info.MotorBModeCount}," +
                $"\"motorCModeCount\":{info.MotorCModeCount}}}",
                null,
                out var envelope))
        {
            EnqueueTelemetry(envelope);
        }
    }

    private void EmitBatteryTelemetry(ToyBatteryReport battery)
    {
        if (TryCreateTelemetry(
                new CapabilityId("toy.telemetry"),
                $"{{\"kind\":\"battery\",\"percent\":{battery.Percent}}}",
                null,
                out var envelope))
        {
            EnqueueTelemetry(envelope);
        }
    }

    private static bool TryValidateMotors(ToyMotorMask mask, ToyDeviceInfo info, out string error)
    {
        error = string.Empty;
        if ((mask & ToyMotorMask.A) != 0 && !info.HasMotorA)
        {
            error = "该设备没有 A 马达。";
            return false;
        }

        if ((mask & ToyMotorMask.B) != 0 && !info.HasMotorB)
        {
            error = "该设备没有 B 马达。";
            return false;
        }

        if ((mask & ToyMotorMask.C) != 0 && !info.HasMotorC)
        {
            error = "该设备没有 C 马达。";
            return false;
        }

        return true;
    }

    private static int MaxModeCount(ToyMotorMask mask, ToyDeviceInfo info)
    {
        var max = 0;
        if ((mask & ToyMotorMask.A) != 0)
        {
            max = Math.Max(max, info.MotorAModeCount);
        }

        if ((mask & ToyMotorMask.B) != 0)
        {
            max = Math.Max(max, info.MotorBModeCount);
        }

        if ((mask & ToyMotorMask.C) != 0)
        {
            max = Math.Max(max, info.MotorCModeCount);
        }

        return max;
    }

    private static bool TryParseMotorMask(JsonElement payload, out ToyMotorMask mask, out int mode, out string error)
    {
        mask = ToyMotorMask.None;
        mode = 0;
        error = string.Empty;

        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "马达控制载荷必须是对象。";
            return false;
        }

        if (payload.TryGetProperty("motors", out var motorsElement) && motorsElement.ValueKind == JsonValueKind.String)
        {
            var raw = motorsElement.GetString() ?? string.Empty;
            foreach (var character in raw.ToUpperInvariant())
            {
                switch (character)
                {
                    case 'A':
                        mask |= ToyMotorMask.A;
                        break;
                    case 'B':
                        mask |= ToyMotorMask.B;
                        break;
                    case 'C':
                        mask |= ToyMotorMask.C;
                        break;
                    default:
                        error = "motors 只能包含 A、B、C 或 none。";
                        return false;
                }
            }
        }

        mode = GetInt32(payload, "mode", 0);
        if (mode < 0 || mode > 255)
        {
            error = "mode 必须在 0 到 255 之间。";
            return false;
        }

        return true;
    }

    private static bool TryParseSpeed(JsonElement payload, out int a, out int b, out int c, out string error)
    {
        a = 0;
        b = 0;
        c = 0;
        error = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "力度控制载荷必须是对象。";
            return false;
        }

        a = GetInt32(payload, "a", 0);
        b = GetInt32(payload, "b", 0);
        c = GetInt32(payload, "c", 0);
        if (a is < 0 or > 20 || b is < 0 or > 20 || c is < 0 or > 20)
        {
            error = "每路力度必须在 0 到 20 之间。";
            return false;
        }

        return true;
    }

    private static int GetInt32(JsonElement element, string propertyName, int defaultValue) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : defaultValue;
}
