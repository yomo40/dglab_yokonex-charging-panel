namespace ChargingPanel.Vendor.Yokonex.Protocol;

/// <summary>频率列表模式下的一组频率/脉冲时间。</summary>
public sealed record EmsFrequencyGroup(int Frequency, int PulseTime);

/// <summary>
/// 电击器二代（YSKJ_EMS_BLE V2.0）通道控制帧。
/// 固定模式 0x01 与实时模式 0x02 都是同帧双通道结构；
/// 频率模式 0x03 为单通道、最多 100 组频率/脉冲时间；关闭模式 0x04。
/// 注意：频率模式是"频率列表"，不是"实时模式"，二者必须区分。
/// </summary>
public sealed class EmsV20Codec : EmsCodecBase
{
    public override byte[] BuildChannelOutputFrame(
        EmsChannelOutputCommand command,
        EmsChannelOutputState other)
    {
        var state = command.State;
        EmsChannelOutputState a;
        EmsChannelOutputState b;
        switch (command.Channel)
        {
            case EmsChannel.A:
                a = state;
                b = other;
                break;
            case EmsChannel.B:
                a = other;
                b = state;
                break;
            default:
                a = state;
                b = state;
                break;
        }

        // 自定义模式（FixedMode == 0）走实时模式帧；固定模式走固定模式帧。
        return state.On && state.FixedMode == 0
            ? BuildRealtimeModeFrame(a, b)
            : BuildFixedModeFrame(a, b);
    }

    private static byte[] BuildFixedModeFrame(EmsChannelOutputState a, EmsChannelOutputState b)
    {
        var frame = new byte[10];
        frame[0] = Header;
        frame[1] = ChannelControl;
        frame[2] = 0x01;
        WriteIntensity(frame, 3, a.On ? Math.Clamp(a.Intensity, 0, 276) : 0);
        frame[5] = a.On ? (byte)Math.Clamp(a.FixedMode, 1, 16) : (byte)0x00;
        WriteIntensity(frame, 6, b.On ? Math.Clamp(b.Intensity, 0, 276) : 0);
        frame[8] = b.On ? (byte)Math.Clamp(b.FixedMode, 1, 16) : (byte)0x00;
        frame[9] = FrameChecksum.Compute(frame.AsSpan(0, 9));
        return frame;
    }

    private static byte[] BuildRealtimeModeFrame(EmsChannelOutputState a, EmsChannelOutputState b)
    {
        var frame = new byte[12];
        frame[0] = Header;
        frame[1] = ChannelControl;
        frame[2] = 0x02;
        WriteIntensity(frame, 3, a.On ? Math.Clamp(a.Intensity, 0, 276) : 0);
        frame[5] = a.On ? (byte)Math.Clamp(a.Frequency, 1, 100) : (byte)0x00;
        frame[6] = a.On ? (byte)Math.Clamp(a.PulseTime, 0, 100) : (byte)0x00;
        WriteIntensity(frame, 7, b.On ? Math.Clamp(b.Intensity, 0, 276) : 0);
        frame[9] = b.On ? (byte)Math.Clamp(b.Frequency, 1, 100) : (byte)0x00;
        frame[10] = b.On ? (byte)Math.Clamp(b.PulseTime, 0, 100) : (byte)0x00;
        frame[11] = FrameChecksum.Compute(frame.AsSpan(0, 11));
        return frame;
    }

    /// <summary>
    /// 构建频率列表模式帧。单通道、最多 100 组（频率 1-100Hz，脉冲 0-100us）。
    /// </summary>
    public byte[] BuildFrequencyListFrame(EmsChannel channel, int intensity, IReadOnlyList<EmsFrequencyGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count is 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(groups), "频率列表必须包含 1 到 100 组。");
        }

        var frame = new byte[7 + groups.Count * 2];
        frame[0] = Header;
        frame[1] = ChannelControl;
        frame[2] = 0x03;
        frame[3] = (byte)channel;
        WriteIntensity(frame, 4, Math.Clamp(intensity, 0, 276));
        for (var i = 0; i < groups.Count; i++)
        {
            frame[6 + i * 2] = (byte)Math.Clamp(groups[i].Frequency, 1, 100);
            frame[7 + i * 2] = (byte)Math.Clamp(groups[i].PulseTime, 0, 100);
        }

        frame[^1] = FrameChecksum.Compute(frame.AsSpan(0, frame.Length - 1));
        return frame;
    }

    /// <summary>二代安全停止：发送关闭模式 0x04 且通道 AB 的帧。</summary>
    public override byte[] BuildCloseFrame()
    {
        var frame = new byte[5];
        frame[0] = Header;
        frame[1] = ChannelControl;
        frame[2] = 0x04;
        frame[3] = (byte)EmsChannel.AB;
        frame[4] = FrameChecksum.Compute(frame.AsSpan(0, 4));
        return frame;
    }

    private static void WriteIntensity(byte[] frame, int offset, int intensity)
    {
        frame[offset] = (byte)(intensity >> 8);
        frame[offset + 1] = (byte)(intensity & 0xFF);
    }
}
