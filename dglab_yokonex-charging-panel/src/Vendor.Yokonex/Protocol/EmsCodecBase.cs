namespace ChargingPanel.Vendor.Yokonex.Protocol;

/// <summary>
/// 电击器两代协议共用的命令帧与状态应答解析基类。
/// 马达/计步/角度/查询/应答帧在两代中完全一致，只有通道控制帧不同。
/// </summary>
public abstract class EmsCodecBase : IEmsProtocolStrategy
{
    public const byte Header = 0x35;
    public const byte ChannelControl = 0x11;
    public const byte Motor = 0x12;
    public const byte Step = 0x13;
    public const byte Angle = 0x14;
    public const byte Query = 0x71;

    public abstract byte[] BuildChannelOutputFrame(EmsChannelOutputCommand command, EmsChannelOutputState other);

    public abstract byte[] BuildCloseFrame();

    public byte[] BuildMotorFrame(EmsMotorState state) => BuildCommandFrame(Motor, (byte)state);

    public byte[] BuildStepFrame(EmsStepCommand command) => BuildCommandFrame(Step, (byte)command);

    public byte[] BuildAngleFrame(EmsAngleCommand command) => BuildCommandFrame(Angle, (byte)command);

    public byte[] BuildQueryFrame(EmsQueryType type) => BuildCommandFrame(Query, (byte)type);

    private static byte[] BuildCommandFrame(byte command, byte value)
    {
        var frame = new byte[4];
        frame[0] = Header;
        frame[1] = command;
        frame[2] = value;
        frame[3] = FrameChecksum.Compute(frame.AsSpan(0, 3));
        return frame;
    }

    public bool TryParseReport(ReadOnlySpan<byte> frame, out EmsStatusReport? report)
    {
        report = null;
        if (frame.Length < 4 || frame[0] != Header || frame[1] != Query)
        {
            return false;
        }

        if (!FrameChecksum.Verify(frame))
        {
            return false;
        }

        switch (frame[2])
        {
            case 0x01:
            case 0x02:
                if (frame.Length != 9)
                {
                    return false;
                }

                var channel = frame[2] == 0x01 ? EmsChannel.A : EmsChannel.B;
                report = new EmsStatusReport.Channel(new EmsChannelStatus(
                    channel,
                    (EmsChannelConnection)frame[3],
                    frame[4] == 0x01,
                    (frame[5] << 8) | frame[6],
                    frame[7]));
                return true;
            case 0x03:
                if (frame.Length != 5)
                {
                    return false;
                }

                report = new EmsStatusReport.Motor((EmsMotorState)frame[3]);
                return true;
            case 0x04:
                if (frame.Length != 5)
                {
                    return false;
                }

                report = new EmsStatusReport.Battery(new EmsBatteryStatus(frame[3]));
                return true;
            case 0x05:
                if (frame.Length != 6)
                {
                    return false;
                }

                report = new EmsStatusReport.Step(new EmsStepStatus((frame[3] << 8) | frame[4]));
                return true;
            case 0x06:
                if (frame.Length != 16)
                {
                    return false;
                }

                report = new EmsStatusReport.Angle(new EmsAngleStatus(
                    (ushort)((frame[3] << 8) | frame[4]),
                    (ushort)((frame[5] << 8) | frame[6]),
                    (ushort)((frame[7] << 8) | frame[8]),
                    (ushort)((frame[9] << 8) | frame[10]),
                    (ushort)((frame[11] << 8) | frame[12]),
                    (ushort)((frame[13] << 8) | frame[14])));
                return true;
            case 0x55:
                if (frame.Length != 5)
                {
                    return false;
                }

                report = new EmsStatusReport.Exception(new EmsExceptionReport((EmsExceptionCode)frame[3]));
                return true;
            default:
                return false;
        }
    }
}
