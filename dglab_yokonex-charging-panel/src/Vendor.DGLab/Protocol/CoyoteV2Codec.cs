namespace ChargingPanel.Vendor.DGLab.Protocol;

/// <summary>
/// 郊狼脉冲主机 V2 蓝牙协议编解码器。
/// 独立于 V3 实现：V2 使用 955A 基础 UUID、PWM_AB2/PWM_A34/PWM_B34 特性与 0-2047 原始强度。
/// 纯确定性编码/解码，不持有连接、计时器或日志。
/// </summary>
public static class CoyoteV2Codec
{
    public const int MaxRawIntensity = 2047;
    public const int MaxDisplayIntensity = 200;
    public const int MinX = 0;
    public const int MaxX = 31;
    public const int MinY = 0;
    public const int MaxY = 1023;
    public const int MinZ = 0;
    public const int MaxZ = 31;
    public const int MinFrequency = 10;
    public const int MaxFrequency = 1000;

    /// <summary>
    /// 编码 PWM_AB2 通道强度（3 字节小端）。
    /// 21-11bit 为 A 通道实际强度，10-0bit 为 B 通道实际强度，23-22bit 保留。
    /// </summary>
    public static byte[] EncodePwmAb2(int rawStrengthA, int rawStrengthB)
    {
        var safeA = Math.Clamp(rawStrengthA, 0, MaxRawIntensity);
        var safeB = Math.Clamp(rawStrengthB, 0, MaxRawIntensity);
        var combined = ((safeA & 0x7FF) << 11) | (safeB & 0x7FF);
        return
        [
            (byte)(combined & 0xFF),
            (byte)((combined >> 8) & 0xFF),
            (byte)((combined >> 16) & 0xFF)
        ];
    }

    /// <summary>
    /// 解析 PWM_AB2 通道强度（3 字节小端）。
    /// </summary>
    public static (int RawStrengthA, int RawStrengthB) DecodePwmAb2(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3)
        {
            return (0, 0);
        }

        var combined = data[0] | (data[1] << 8) | (data[2] << 16);
        return ((combined >> 11) & 0x7FF, combined & 0x7FF);
    }

    /// <summary>
    /// 编码 PWM_x34 波形（3 字节小端）。X=0-31 连续脉冲毫秒，Y=0-1023 间隔毫秒，Z=0-31 脉冲宽度。
    /// </summary>
    public static byte[] EncodePwmWaveform(int x, int y, int z)
    {
        var safeX = Math.Clamp(x, MinX, MaxX);
        var safeY = Math.Clamp(y, MinY, MaxY);
        var safeZ = Math.Clamp(z, MinZ, MaxZ);
        var combined = ((safeZ & 0x1F) << 15) | ((safeY & 0x3FF) << 5) | (safeX & 0x1F);
        return
        [
            (byte)(combined & 0xFF),
            (byte)((combined >> 8) & 0xFF),
            (byte)((combined >> 16) & 0xFF)
        ];
    }

    /// <summary>
    /// 解析 PWM_x34 波形（3 字节小端）。
    /// </summary>
    public static (int X, int Y, int Z) DecodePwmWaveform(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3)
        {
            return (0, 0, 0);
        }

        var combined = data[0] | (data[1] << 8) | (data[2] << 16);
        return (combined & 0x1F, (combined >> 5) & 0x3FF, (combined >> 15) & 0x1F);
    }

    /// <summary>
    /// 依据 Frequency = X + Y 与官方最优比例公式计算 X/Y。
    /// </summary>
    public static (int X, int Y) CalculateXYFromFrequency(int frequency)
    {
        var safeFrequency = Math.Clamp(frequency, MinFrequency, MaxFrequency);
        var x = (int)(Math.Sqrt(safeFrequency / 1000.0) * 15);
        x = Math.Clamp(x, 1, MaxX);
        var y = Math.Clamp(safeFrequency - x, MinY, MaxY);
        return (x, y);
    }

    /// <summary>APP 显示强度 (0-200) 转原始强度 (0-2047)。官方约定每增加 1 显示值增加 7 原始值。</summary>
    public static int DisplayToRaw(int displayStrength) =>
        Math.Clamp(Math.Clamp(displayStrength, 0, MaxDisplayIntensity) * 7, 0, MaxRawIntensity);

    /// <summary>原始强度 (0-2047) 转 APP 显示强度 (0-200)。</summary>
    public static int RawToDisplay(int rawStrength) =>
        Math.Clamp(rawStrength / 7, 0, MaxDisplayIntensity);

    /// <summary>解析电量通知（1 字节）。</summary>
    public static bool TryDecodeBattery(ReadOnlySpan<byte> frame, out byte percent)
    {
        percent = 0;
        if (frame.Length < 1)
        {
            return false;
        }

        percent = Math.Min(frame[0], (byte)100);
        return true;
    }
}