namespace ChargingPanel.Vendor.DGLab.Protocol;

/// <summary>
/// 灵猫边缘控制传感器 V1 蓝牙协议编解码器。
/// 气压值换算使用官方算法：第 8、9 字节小端 int16 除以 100 得到 kPa。
/// </summary>
public static class CivetV1Codec
{
    public const int CommandLength = 17;
    public const int PressureZeroCommandLength = 13;

    /// <summary>编码 50 指令：开启（D0）或停止（00）100ms 气压主动上报。</summary>
    public static byte[] EncodePressureReport(bool enabled, byte lampColor = 0x01)
    {
        var buffer = new byte[CommandLength];
        buffer[0] = 0x50;
        buffer[1] = lampColor;
        buffer[2] = enabled ? (byte)0xD0 : (byte)0x00;
        return buffer;
    }

    /// <summary>编码 66 指令：气压读值重置。</summary>
    public static byte[] EncodePressureZero() =>
        [0x66, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02];

    /// <summary>编码 66 指令：设置屏幕显示方向（1 或 3）。</summary>
    public static byte[] EncodeScreenRotate(byte direction)
    {
        var buffer = new byte[PressureZeroCommandLength];
        buffer[0] = 0x66;
        buffer[10] = direction;
        return buffer;
    }

    /// <summary>
    /// 解码 D0 气压消息（17 字节）。气压值位于第 8、9 字节，小端 int16 除以 100 得到 kPa。
    /// </summary>
    public static bool TryDecodeD0Pressure(ReadOnlySpan<byte> frame, out double pressureKpa)
    {
        pressureKpa = 0;
        if (frame.Length < 17 || frame[0] != 0xD0)
        {
            return false;
        }

        pressureKpa = (short)(frame[8] | (frame[9] << 8)) / 100.0;
        return true;
    }

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