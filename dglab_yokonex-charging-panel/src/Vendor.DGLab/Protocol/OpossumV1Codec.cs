namespace ChargingPanel.Vendor.DGLab.Protocol;

/// <summary>
/// 负鼠振动控制器 V1 蓝牙协议编解码器。
/// 振动波形任一字节超范围时该通道整组数据弃用；0xFF 表示强度不修改。
/// </summary>
public static class OpossumV1Codec
{
    public const int WaveformCommandLength = 20;
    public const int IntensityCommandLength = 3;
    public const int ScreenSyncCommandLength = 24;
    public const byte NoModify = 0xFF;
    public const byte MaxWaveformIntensity = 100;
    public const byte MaxChannelIntensity = 200;

    /// <summary>编码 50 指令：设置主指示灯颜色并控制按键状态集上报。</summary>
    public static byte[] EncodeLampAndButtonReport(byte lampColor, bool buttonReportEnabled) =>
        [0x50, lampColor, buttonReportEnabled ? (byte)0x01 : (byte)0x00];

    /// <summary>
    /// 编码 B0 振动波形指令（20 字节）。任一通道 4 字节中存在超范围值时整组弃用（置零）。
    /// </summary>
    public static byte[] EncodeVibrationWaveform(ReadOnlySpan<byte> channelA4, ReadOnlySpan<byte> channelB4)
    {
        if (channelA4.Length != 4 || channelB4.Length != 4)
        {
            throw new ArgumentException("负鼠振动波形必须为每通道 4 字节。", nameof(channelA4));
        }

        var buffer = new byte[WaveformCommandLength];
        buffer[0] = 0xB0;
        if (IsValidWaveform(channelA4))
        {
            channelA4.CopyTo(buffer.AsSpan(8, 4));
        }

        if (IsValidWaveform(channelB4))
        {
            channelB4.CopyTo(buffer.AsSpan(16, 4));
        }

        return buffer;
    }

    /// <summary>判断 4 字节振动波形是否全部处于 0-100 合法范围。</summary>
    public static bool IsValidWaveform(ReadOnlySpan<byte> channel)
    {
        if (channel.Length != 4)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            if (channel[i] > MaxWaveformIntensity)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>编码 B3 通道强度指令。null 表示该通道不修改（0xFF）。</summary>
    public static byte[] EncodeIntensity(byte? channelA, byte? channelB) =>
        [0xB3, channelA ?? NoModify, channelB ?? NoModify];

    /// <summary>编码 B2 屏幕显示同步指令（固定 21 字节 + A/B 强度值）。</summary>
    public static byte[] EncodeScreenSync(byte channelA, byte channelB)
    {
        var buffer = new byte[ScreenSyncCommandLength];
        buffer[0] = 0xB2;
        // 固定写入段：FFFF00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF
        ReadOnlySpan<byte> fixedSegment = [0xFF, 0xFF, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        fixedSegment.CopyTo(buffer.AsSpan(1, 21));
        buffer[22] = Math.Min(channelA, MaxChannelIntensity);
        buffer[23] = Math.Min(channelB, MaxChannelIntensity);
        return buffer;
    }

    /// <summary>解码 B3 强度消息。</summary>
    public static bool TryDecodeB3(ReadOnlySpan<byte> frame, out byte channelA, out byte channelB)
    {
        channelA = 0;
        channelB = 0;
        if (frame.Length < 3 || frame[0] != 0xB3)
        {
            return false;
        }

        channelA = frame[1];
        channelB = frame[2];
        return true;
    }

    /// <summary>解码 D0 按键状态消息（报文序号 + 按钮状态集）。</summary>
    public static bool TryDecodeD0(ReadOnlySpan<byte> frame, out byte sequence, out ushort buttonState)
    {
        sequence = 0;
        buttonState = 0;
        if (frame.Length < 4 || frame[0] != 0xD0)
        {
            return false;
        }

        sequence = frame[1];
        buttonState = (ushort)(frame[2] | (frame[3] << 8));
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