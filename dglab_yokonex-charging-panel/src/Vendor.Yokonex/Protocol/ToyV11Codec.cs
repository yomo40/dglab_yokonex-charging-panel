namespace ChargingPanel.Vendor.Yokonex.Protocol;

/// <summary>玩具设备信息（0x10 应答）。</summary>
public sealed record ToyDeviceInfo(byte ModelId, byte Version, byte MotorAModeCount, byte MotorBModeCount, byte MotorCModeCount)
{
    /// <summary>马达是否存在（模式数量非 0）。</summary>
    public bool HasMotorA => MotorAModeCount > 0;

    public bool HasMotorB => MotorBModeCount > 0;

    public bool HasMotorC => MotorCModeCount > 0;
}

/// <summary>玩具电量上报（0x13）。</summary>
public sealed record ToyBatteryReport(byte Percent);

/// <summary>玩具马达选择掩码。</summary>
[Flags]
public enum ToyMotorMask : byte
{
    None = 0x00,
    A = 0x01,
    B = 0x02,
    C = 0x04
}

/// <summary>
/// 跳蛋/飞机杯 V1.1（YSKJ_TOY_BLE V1.1）编解码。
/// 0x10 设备信息查询/应答、0x11 固定模式控制（马达掩码 + 模式）、
/// 0x12 三马达 0-20 实时力度、0x13 电量上报。全部使用累加校验和。
/// </summary>
public static class ToyV11Codec
{
    public const byte Header = 0x35;
    public const byte DeviceInfoCommand = 0x10;
    public const byte FixedModeCommand = 0x11;
    public const byte SpeedCommand = 0x12;
    public const byte BatteryCommand = 0x13;

    /// <summary>设备信息查询帧。</summary>
    public static byte[] BuildDeviceInfoQuery()
    {
        var frame = new byte[3];
        frame[0] = Header;
        frame[1] = DeviceInfoCommand;
        frame[2] = FrameChecksum.Compute(frame.AsSpan(0, 2));
        return frame;
    }

    /// <summary>固定模式控制帧。mode 0 = 关闭；1-N = 对应马达的固定模式。</summary>
    public static byte[] BuildFixedMode(ToyMotorMask motorMask, byte mode)
    {
        var frame = new byte[5];
        frame[0] = Header;
        frame[1] = FixedModeCommand;
        frame[2] = (byte)motorMask;
        frame[3] = mode;
        frame[4] = FrameChecksum.Compute(frame.AsSpan(0, 4));
        return frame;
    }

    /// <summary>三马达实时力度帧。每路 0-20。</summary>
    public static byte[] BuildSpeed(byte a, byte b, byte c)
    {
        var frame = new byte[6];
        frame[0] = Header;
        frame[1] = SpeedCommand;
        frame[2] = (byte)Math.Clamp((int)a, 0, 20);
        frame[3] = (byte)Math.Clamp((int)b, 0, 20);
        frame[4] = (byte)Math.Clamp((int)c, 0, 20);
        frame[5] = FrameChecksum.Compute(frame.AsSpan(0, 5));
        return frame;
    }

    /// <summary>解析设备信息应答（0x10，10 字节）。</summary>
    public static bool TryParseDeviceInfo(ReadOnlySpan<byte> frame, out ToyDeviceInfo? info)
    {
        info = null;
        if (frame.Length != 10 || frame[0] != Header || frame[1] != DeviceInfoCommand)
        {
            return false;
        }

        if (!FrameChecksum.Verify(frame))
        {
            return false;
        }

        info = new ToyDeviceInfo(frame[2], frame[3], frame[4], frame[5], frame[6]);
        return true;
    }

    /// <summary>解析电量上报（0x13，5 字节）。</summary>
    public static bool TryParseBattery(ReadOnlySpan<byte> frame, out ToyBatteryReport? report)
    {
        report = null;
        if (frame.Length != 5 || frame[0] != Header || frame[1] != BatteryCommand || frame[2] != 0x01)
        {
            return false;
        }

        if (!FrameChecksum.Verify(frame))
        {
            return false;
        }

        report = new ToyBatteryReport(frame[3]);
        return true;
    }
}
