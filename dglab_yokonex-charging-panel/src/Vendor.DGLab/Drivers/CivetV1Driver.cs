using System.Text.Json;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.DGLab.Common;
using ChargingPanel.Vendor.DGLab.Protocol;

namespace ChargingPanel.Vendor.DGLab.Drivers;

/// <summary>
/// 灵猫边缘控制传感器 V1 蓝牙驱动。
/// 停止 Session 时必须先关闭气压主动上报，再返回已停止。
/// </summary>
public sealed class CivetV1Driver : DGLabDriverBase
{
    private readonly object _sync = new();
    private bool _reportingEnabled;

    public CivetV1Driver(IPluginContext context)
        : base(new ProtocolProfileId(DGLabStableIds.ProfileCivetV1Ble))
    {
        ArgumentNullException.ThrowIfNull(context);
    }

    protected override async ValueTask<DeviceCommandResult> ExecuteCoreAsync(
        DeviceCommandEnvelope command,
        IEndpointLease lease,
        CancellationToken cancellationToken)
    {
        switch (command.CapabilityId.Value)
        {
            case DGLabStableIds.CapPressureReporting:
                return await ExecutePressureReportingAsync(command, cancellationToken);
            case DGLabStableIds.CapPressureZero:
                return await ExecutePressureZeroAsync(command, cancellationToken);
            case DGLabStableIds.CapBatteryStatus:
                return await ExecuteBatteryReadAsync(command, cancellationToken);
            default:
                return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "capability.unsupported", "灵猫驱动不支持该能力。");
        }
    }

    private async ValueTask<DeviceCommandResult> ExecutePressureReportingAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        var enabled = string.Equals(command.OperationId, "start", StringComparison.Ordinal);
        var result = await SendBinaryAsync(
            DGLabUuids.WriteRoute,
            CivetV1Codec.EncodePressureReport(enabled),
            EndpointResponseMode.WithoutResponse,
            command.CommandId,
            cancellationToken);
        if (result.Status is DeviceCommandStatus.AcceptedByTransport or DeviceCommandStatus.Executed)
        {
            lock (_sync)
            {
                _reportingEnabled = enabled;
            }
        }

        return result;
    }

    private async ValueTask<DeviceCommandResult> ExecutePressureZeroAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        byte[] frame;
        if (string.Equals(command.OperationId, "rotate.screen", StringComparison.Ordinal))
        {
            if (!TryGetInt(command.Payload, "direction", out var direction))
            {
                return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "command.invalid_payload", "缺少屏幕方向参数。");
            }

            frame = CivetV1Codec.EncodeScreenRotate((byte)Math.Clamp(direction, 1, 3));
        }
        else
        {
            frame = CivetV1Codec.EncodePressureZero();
        }

        return await SendBinaryAsync(DGLabUuids.WriteRoute, frame, EndpointResponseMode.WithoutResponse, command.CommandId, cancellationToken);
    }

    private async ValueTask<DeviceCommandResult> ExecuteBatteryReadAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        var lease = Lease;
        if (lease is null)
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Rejected, "driver.not_open", "驱动尚未打开。");
        }

        try
        {
            var read = await lease.ReadAsync(new EndpointReadRequest(DGLabUuids.BatteryRoute), cancellationToken);
            if (read.Status != EndpointOperationStatus.Completed ||
                read.Payload is null ||
                read.Payload.Kind != EndpointPayloadKind.Binary)
            {
                return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.TransportFailed, "battery.read_failed", "电量读取失败。");
            }

            if (CivetV1Codec.TryDecodeBattery(read.Payload.Binary.Span, out var percent))
            {
                EmitBatteryTelemetry(percent);
            }

            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Executed);
        }
        catch (OperationCanceledException)
        {
            return new DeviceCommandResult(command.CommandId, DeviceCommandStatus.OutcomeUnknown, "battery.read_cancelled", "电量读取被取消。");
        }
    }

    protected override async ValueTask<SafeStopResult> SafeStopCoreAsync(
        SafeStopContext context,
        IEndpointLease lease,
        CancellationToken cancellationToken)
    {
        // 停止 Session 必须先关闭主动上报，再确认停止。
        if (_reportingEnabled)
        {
            var stopReport = await SendBinaryAsync(
                DGLabUuids.WriteRoute,
                CivetV1Codec.EncodePressureReport(enabled: false),
                EndpointResponseMode.WithoutResponse,
                CommandId.New(),
                cancellationToken);
            if (stopReport.Status is not DeviceCommandStatus.AcceptedByTransport and not DeviceCommandStatus.Executed)
            {
                return new SafeStopResult(SafeStopStatus.Failed, "safety.report_stop_failed", "关闭气压上报失败。");
            }

            lock (_sync)
            {
                _reportingEnabled = false;
            }
        }

        return new SafeStopResult(SafeStopStatus.ConfirmedStopped);
    }

    /// <summary>接收通知帧并产生遥测。</summary>
    public void OnNotify(ReadOnlyMemory<byte> frame)
    {
        var span = frame.Span;
        if (CivetV1Codec.TryDecodeD0Pressure(span, out var pressureKpa))
        {
            EmitPressureTelemetry(pressureKpa);
            return;
        }

        if (CivetV1Codec.TryDecodeBattery(span, out var percent))
        {
            EmitBatteryTelemetry(percent);
        }
    }

    private void EmitPressureTelemetry(double pressureKpa)
    {
        if (TryCreateTelemetry(
            new CapabilityId(DGLabStableIds.CapPressureStatus),
            $"{{\"pressureKpa\":{pressureKpa.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}}}",
            null,
            out var envelope))
        {
            EnqueueTelemetry(envelope);
        }
    }

    private void EmitBatteryTelemetry(byte percent)
    {
        if (TryCreateTelemetry(
            new CapabilityId(DGLabStableIds.CapBatteryStatus),
            $"{{\"percent\":{percent}}}",
            null,
            out var envelope))
        {
            EnqueueTelemetry(envelope);
        }
    }

    private static bool TryGetInt(JsonElement element, string property, out int value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var child) &&
            child.ValueKind == JsonValueKind.Number)
        {
            value = child.GetInt32();
            return true;
        }

        value = 0;
        return false;
    }
}