namespace ChargingPanel.Core.Application.Devices;

/// <summary>
/// 活动设备视图的"legacy 类型"分类器。
///
/// 仅用于规则目标过滤等兼容场景，把能力声明映射为旧的目标设备类型标签；
/// 命令下发本身一律走 CapabilityId + OperationId，不做厂商类型判断。
/// </summary>
public static class DeviceViewClassifier
{
    public const string DGLab = "DGLab";
    public const string YokonexEstim = "Yokonex_Estim";
    public const string YokonexEnema = "Yokonex_Enema";
    public const string YokonexVibrator = "Yokonex_Vibrator";
    public const string YokonexSmartLock = "Yokonex_SmartLock";
    public const string YokonexIm = "Yokonex_IM";
    public const string Unknown = "Unknown";

    public static string ResolveLegacyType(ActiveDeviceView device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var capabilityIds = device.Capabilities
            .Select(capability => capability.Id.Value)
            .ToHashSet(StringComparer.Ordinal);

        if (capabilityIds.Contains("intensity.output") || capabilityIds.Contains("waveform.stream"))
        {
            return DGLab;
        }

        if (capabilityIds.Contains("ems.channel.output"))
        {
            return YokonexEstim;
        }

        if (capabilityIds.Contains("enema.pump.control"))
        {
            return YokonexEnema;
        }

        if (capabilityIds.Contains("toy.motor.control") || capabilityIds.Contains("toy.speed.control"))
        {
            return YokonexVibrator;
        }

        if (capabilityIds.Contains("smartlock.control"))
        {
            return YokonexSmartLock;
        }

        if (capabilityIds.Contains("im.command.send") ||
            capabilityIds.Contains("im.global.stop") ||
            capabilityIds.Contains("bridge.command.send"))
        {
            return YokonexIm;
        }

        return Unknown;
    }
}
