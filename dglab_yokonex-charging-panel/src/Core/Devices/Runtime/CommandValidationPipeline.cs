using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.Core.Devices.Runtime;

/// <summary>
/// 值域校验结果。校验必须在命令进入 Actor 与 Driver 之前完成。
/// </summary>
public sealed record ActuatorValidationResult
{
    private ActuatorValidationResult(bool isValid, string? reasonCode, string? message)
    {
        IsValid = isValid;
        ReasonCode = reasonCode;
        Message = message;
    }

    public bool IsValid { get; }

    public string? ReasonCode { get; }

    public string? Message { get; }

    public static ActuatorValidationResult Valid { get; } = new(true, null, null);

    public static ActuatorValidationResult Invalid(string reasonCode, string message) =>
        new(false, reasonCode, message);
}

/// <summary>
/// 能力/执行器值域校验管线。只做与厂商无关的声明式校验，
/// 不构造任何协议字节，也不接触 Driver。
/// </summary>
public static class CommandValidationPipeline
{
    /// <summary>
    /// 校验执行器存在性与取值范围。超出合法范围一律拒绝，不做静默夹紧，
    /// 避免调用方以为已按请求值执行。
    /// </summary>
    public static ActuatorValidationResult ValidateActuatorValue(
        CapabilityDescriptor capability,
        string actuatorId,
        double value)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentException.ThrowIfNullOrWhiteSpace(actuatorId);

        var actuator = capability.Actuators
            .FirstOrDefault(item => string.Equals(item.ActuatorId, actuatorId, StringComparison.Ordinal));
        if (actuator is null)
        {
            return ActuatorValidationResult.Invalid(
                "actuator.unknown",
                $"能力未声明执行器 {actuatorId}。");
        }

        if (!double.IsFinite(value) ||
            value < actuator.ValidRange.Minimum ||
            value > actuator.ValidRange.Maximum)
        {
            return ActuatorValidationResult.Invalid(
                "value.out_of_range",
                $"取值超出执行器 {actuatorId} 的合法范围。");
        }

        // 默认安全范围必须真正生效：声明了却不执行会给调用方虚假的安全感。
        // 超出安全范围一律拒绝，不做静默夹紧——调用方必须显式知晓请求未被执行。
        // 上下越界必须给出各自准确的 reason code：低于下限时声称"高于"会直接误导调用方。
        if (value > actuator.DefaultSafetyRange.Maximum)
        {
            return ActuatorValidationResult.Invalid(
                "value.above_safety_range",
                $"取值高于执行器 {actuatorId} 的默认安全范围上限。");
        }

        if (value < actuator.DefaultSafetyRange.Minimum)
        {
            return ActuatorValidationResult.Invalid(
                "value.below_safety_range",
                $"取值低于执行器 {actuatorId} 的默认安全范围下限。");
        }

        return ActuatorValidationResult.Valid;
    }

    /// <summary>
    /// 查找实例声明的能力；未声明时返回 null 由调用方拒绝。
    /// </summary>
    public static CapabilityDescriptor? FindCapability(
        DeviceRuntimeBinding binding,
        CapabilityId capabilityId)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return binding.Capabilities.FirstOrDefault(item => item.Id == capabilityId);
    }
}
