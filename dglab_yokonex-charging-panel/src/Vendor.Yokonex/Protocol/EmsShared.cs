namespace ChargingPanel.Vendor.Yokonex.Protocol;

/// <summary>
/// 电击器 BLE 帧累加校验。除最后一字节外所有字节求和，取低 8 位。
/// 两代协议共用同一校验规则。
/// </summary>
public static class FrameChecksum
{
    public static byte Compute(ReadOnlySpan<byte> bytes)
    {
        uint sum = 0;
        foreach (var value in bytes)
        {
            sum += value;
        }

        return (byte)(sum & 0xFF);
    }

    /// <summary>末字节为校验和时，整帧校验是否通过。</summary>
    public static bool Verify(ReadOnlySpan<byte> frame) =>
        frame.Length >= 2 && Compute(frame[..^1]) == frame[^1];
}

/// <summary>电击器通道。</summary>
public enum EmsChannel : byte
{
    A = 0x01,
    B = 0x02,
    AB = 0x03
}

/// <summary>电击器马达状态。0x11-0x13 为三个固定预设频率。</summary>
public enum EmsMotorState : byte
{
    Off = 0x00,
    On = 0x01,
    Preset1 = 0x11,
    Preset2 = 0x12,
    Preset3 = 0x13
}

/// <summary>计步功能指令。</summary>
public enum EmsStepCommand : byte
{
    Off = 0x00,
    On = 0x01,
    Clear = 0x02,
    Pause = 0x03,
    Resume = 0x04
}

/// <summary>角度功能指令。</summary>
public enum EmsAngleCommand : byte
{
    Off = 0x00,
    On = 0x01
}

/// <summary>0x71 查询类型。</summary>
public enum EmsQueryType : byte
{
    ChannelA = 0x01,
    ChannelB = 0x02,
    Motor = 0x03,
    Battery = 0x04,
    Step = 0x05,
    Angle = 0x06
}

/// <summary>0x55 异常上报的五个异常类别。</summary>
public enum EmsExceptionCode : byte
{
    ChecksumError = 0x01,
    HeaderError = 0x02,
    CommandError = 0x03,
    DataError = 0x04,
    NotImplemented = 0x05
}

/// <summary>通道接入状态。</summary>
public enum EmsChannelConnection : byte
{
    NotConnected = 0x00,
    Discharging = 0x01,
    ConnectedNoDischarge = 0x02
}

/// <summary>通道状态应答。</summary>
public sealed record EmsChannelStatus(
    EmsChannel Channel,
    EmsChannelConnection Connection,
    bool On,
    int Intensity,
    int Mode);

/// <summary>电池电量应答。</summary>
public sealed record EmsBatteryStatus(int Percent);

/// <summary>计步数据应答。</summary>
public sealed record EmsStepStatus(int StepCount);

/// <summary>角度数据应答（六轴原始值）。</summary>
public sealed record EmsAngleStatus(
    ushort GravityX,
    ushort GravityY,
    ushort GravityZ,
    ushort GyroX,
    ushort GyroY,
    ushort GyroZ);

/// <summary>异常上报。</summary>
public sealed record EmsExceptionReport(EmsExceptionCode Code);

/// <summary>
/// 从 FF32 通知流解析出的一则电击器状态报告。
/// </summary>
public abstract record EmsStatusReport
{
    private EmsStatusReport()
    {
    }

    public sealed record Channel(EmsChannelStatus Status) : EmsStatusReport;

    public sealed record Motor(EmsMotorState State) : EmsStatusReport;

    public sealed record Battery(EmsBatteryStatus Status) : EmsStatusReport;

    public sealed record Step(EmsStepStatus Status) : EmsStatusReport;

    public sealed record Angle(EmsAngleStatus Status) : EmsStatusReport;

    public sealed record Exception(EmsExceptionReport Report) : EmsStatusReport;
}
