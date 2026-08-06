namespace ChargingPanel.Core.Protocols;

/// <summary>
/// 统一强度标定策略：
/// 1) 业务层输入强度统一按 0-100 标准化；
/// 2) 下发前按目标设备能力描述做线性映射（由映射器按执行器合法范围执行）。
/// 本类型不接触 Driver/适配器，只做纯数值换算。
/// </summary>
internal static class StrengthScalingPolicy
{
    private static readonly HashSet<string> StrengthActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "set",
        "increase",
        "decrease",
        "wave",
        "pulse",
        "waveform"
    };

    public static bool RequiresScaling(string actionType)
    {
        return StrengthActions.Contains(actionType);
    }

    public static int ResolveNormalizedCap(int configuredCap)
    {
        return Math.Clamp(configuredCap, 1, 100);
    }

    public static int NormalizeInputValue(int rawValue, double multiplier, int normalizedCap)
    {
        var cap = ResolveNormalizedCap(normalizedCap);
        var value = (int)Math.Round(rawValue * multiplier, MidpointRounding.AwayFromZero);
        return Math.Clamp(value, 0, cap);
    }

    public static int ScaleNormalizedToDevice(int normalizedValue, int deviceMaxStrength, int normalizedCap = 100)
    {
        var cap = ResolveNormalizedCap(normalizedCap);
        var safeValue = Math.Clamp(normalizedValue, 0, cap);
        var safeDeviceMax = Math.Max(1, deviceMaxStrength);

        if (safeValue == 0)
        {
            return 0;
        }

        var ratio = (double)safeValue / cap;
        var scaled = (int)Math.Round(ratio * safeDeviceMax, MidpointRounding.AwayFromZero);
        return Math.Clamp(scaled, 0, safeDeviceMax);
    }
}
