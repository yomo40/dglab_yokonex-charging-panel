using System.Text.Json;
using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Devices.Safety;

namespace ChargingPanel.Core.Application.Devices;

/// <summary>
/// 设置强度请求。业务模块只提供厂商中立的执行器与数值，不构造协议字节。
/// </summary>
public sealed record SetIntensityRequest
{
    public SetIntensityRequest(
        DeviceInstanceId deviceInstanceId,
        string actuatorId,
        double value,
        CommandSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actuatorId);
        ArgumentNullException.ThrowIfNull(source);
        DeviceInstanceId = deviceInstanceId;
        ActuatorId = actuatorId.Trim();
        Value = value;
        Source = source;
    }

    public DeviceInstanceId DeviceInstanceId { get; }

    public string ActuatorId { get; }

    public double Value { get; }

    public CommandSource Source { get; }
}

/// <summary>
/// 能力命令请求。业务模块把"动作语义 + 能力 + operation + 版本化 JSON 载荷"交给应用服务，
/// 由应用服务负责补齐 Epoch、截止时间与投递策略并构造信封下发。
/// 载荷必须是厂商中立/版本化的 JsonElement，业务代码不得构造协议字节。
/// </summary>
public sealed record CapabilityCommandRequest
{
    public CapabilityCommandRequest(
        DeviceInstanceId deviceInstanceId,
        CapabilityId capabilityId,
        string operationId,
        JsonElement payload,
        CommandSource source,
        CommandDelivery delivery,
        TimeSpan? timeout = null)
    {
        _ = deviceInstanceId.Value;
        _ = capabilityId.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(delivery))
        {
            throw new ArgumentOutOfRangeException(nameof(delivery));
        }

        if (timeout is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ArgumentException("能力命令载荷不能为 Undefined 或 null。", nameof(payload));
        }

        DeviceInstanceId = deviceInstanceId;
        CapabilityId = capabilityId;
        OperationId = operationId.Trim();
        Payload = payload.Clone();
        Source = source;
        Delivery = delivery;
        Timeout = timeout;
    }

    public DeviceInstanceId DeviceInstanceId { get; }

    public CapabilityId CapabilityId { get; }

    public string OperationId { get; }

    public JsonElement Payload { get; }

    public CommandSource Source { get; }

    public CommandDelivery Delivery { get; }

    public TimeSpan? Timeout { get; }
}

/// <summary>
/// 业务模块唯一的设备控制入口。规则、脚本、房间、远控与 UI 都只能经由本接口，
/// 不得直接获取 Driver 或调用适配器方法。
/// </summary>
public interface IDeviceApplicationService
{
    /// <summary>当前安全纪元。业务模块用它构造命令信封，确保急停后的旧命令被拒绝。</summary>
    long CurrentSafetyEpoch { get; }

    Task<DeviceCommandResult> SetIntensityAsync(
        SetIntensityRequest request,
        CancellationToken cancellationToken);

    /// <summary>原样派发已构造好的命令信封（由能力命令请求或映射器产生）。</summary>
    Task<DeviceCommandResult> DispatchAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken);

    /// <summary>按能力命令请求补齐 Epoch/截止时间并派发。业务模块不构造协议字节。</summary>
    Task<DeviceCommandResult> ExecuteCapabilityAsync(
        CapabilityCommandRequest request,
        CancellationToken cancellationToken);

    Task<EmergencyStopReport> EmergencyStopAsync(
        string reason,
        CancellationToken cancellationToken);

    Task<bool> ResetSafetyAsync(
        SafetyResetRequest request,
        CancellationToken cancellationToken);
}
