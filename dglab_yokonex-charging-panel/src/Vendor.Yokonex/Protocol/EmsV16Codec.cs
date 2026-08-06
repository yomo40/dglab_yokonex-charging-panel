namespace ChargingPanel.Vendor.Yokonex.Protocol;

/// <summary>
/// 电击器一代（YSKJ_EMS_BLE V1.6）通道控制帧。
/// 单通道帧：通道号在字节3，通道开关在字节4，
/// 强度 2 字节（0x0001-0x0114，即 1-276），模式 0x01-0x10 或自定义 0x11，
/// 频率与脉冲仅在自定义模式有效。
/// </summary>
public sealed class EmsV16Codec : EmsCodecBase
{
    public override byte[] BuildChannelOutputFrame(
        EmsChannelOutputCommand command,
        EmsChannelOutputState other)
    {
        var state = command.State;
        var frame = new byte[10];
        frame[0] = Header;
        frame[1] = ChannelControl;
        frame[2] = (byte)command.Channel;
        frame[3] = state.On ? (byte)0x01 : (byte)0x00;

        var strength = state.On ? Math.Clamp(state.Intensity, 0, 276) : 0;
        frame[4] = (byte)(strength >> 8);
        frame[5] = (byte)(strength & 0xFF);

        var isCustom = state.On && state.FixedMode == 0;
        frame[6] = state.On
            ? (byte)(isCustom ? 0x11 : Math.Clamp(state.FixedMode, 1, 16))
            : (byte)0x00;
        frame[7] = isCustom ? (byte)Math.Clamp(state.Frequency, 1, 100) : (byte)0x00;
        frame[8] = isCustom ? (byte)Math.Clamp(state.PulseTime, 0, 100) : (byte)0x00;
        frame[9] = FrameChecksum.Compute(frame.AsSpan(0, 9));
        return frame;
    }

    /// <summary>
    /// 一代安全停止：发送通道号 AB（0x03）且通道开关 0x00 的关闭帧，
    /// 从机忽略强度与模式。不触碰计步/角度数据。
    /// </summary>
    public override byte[] BuildCloseFrame()
    {
        var frame = new byte[10];
        frame[0] = Header;
        frame[1] = ChannelControl;
        frame[2] = (byte)EmsChannel.AB;
        frame[3] = 0x00;
        frame[9] = FrameChecksum.Compute(frame.AsSpan(0, 9));
        return frame;
    }
}
