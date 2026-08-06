namespace ChargingPanel.Vendor.DGLab.Protocol;

/// <summary>
/// 郊狼脉冲主机 V3 蓝牙协议编解码器。
/// 纯确定性编码/解码，不持有连接、计时器或日志。
/// </summary>
public static class CoyoteV3Codec
{
    public const int B0Length = 20;
    public const int BfLength = 7;
    public const int B1Length = 4;

    public const int MinProtocolFrequency = 10;
    public const int MaxProtocolFrequency = 240;
    public const int MinWaveformStrength = 0;
    public const int MaxWaveformStrength = 100;
    public const int MaxChannelStrength = 200;
    public const int MaxUserFrequency = 1000;

    // 强度值解读方式
    public const int StrengthNoChange = 0b00;
    public const int StrengthIncrease = 0b01;
    public const int StrengthDecrease = 0b10;
    public const int StrengthAbsolute = 0b11;

    /// <summary>
    /// 用户频率 (10~1000) 转协议频率 (10~240)。
    /// </summary>
    public static int ConvertFrequency(int input)
    {
        if (input is >= 10 and <= 100)
        {
            return input;
        }

        if (input is >= 101 and <= 600)
        {
            return (input - 100) / 5 + 100;
        }

        if (input is >= 601 and <= 1000)
        {
            return (input - 600) / 10 + 200;
        }

        return 10;
    }

    /// <summary>判断某通道波形帧是否全部处于合法范围。</summary>
    public static bool IsValidChannelFrame(ReadOnlySpan<int> frequencies, ReadOnlySpan<int> strengths)
    {
        if (frequencies.Length != 4 || strengths.Length != 4)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            if (frequencies[i] is < MinProtocolFrequency or > MaxProtocolFrequency ||
                strengths[i] is < MinWaveformStrength or > MaxWaveformStrength)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 编码 B0 指令。序列号高 4 位、强度解读方式低 4 位。
    /// 波形频率输入已视为协议值 (10-240)。
    /// </summary>
    public static byte[] EncodeB0(
        int sequenceNumber,
        int strengthModeA,
        int strengthModeB,
        int strengthA,
        int strengthB,
        ReadOnlySpan<int> frequencyA,
        ReadOnlySpan<int> strengthWaveA,
        ReadOnlySpan<int> frequencyB,
        ReadOnlySpan<int> strengthWaveB)
    {
        if (frequencyA.Length != 4 || strengthWaveA.Length != 4 ||
            frequencyB.Length != 4 || strengthWaveB.Length != 4)
        {
            throw new ArgumentException("V3 B0 波形必须包含 4 组频率与 4 组强度。", nameof(frequencyA));
        }

        var buffer = new byte[B0Length];
        buffer[0] = 0xB0;
        buffer[1] = (byte)(((sequenceNumber & 0x0F) << 4) | (((strengthModeA & 0x03) << 2) | (strengthModeB & 0x03)));
        buffer[2] = EncodeStrengthSetting(strengthA);
        buffer[3] = EncodeStrengthSetting(strengthB);
        WriteFrame(buffer, 4, frequencyA, strengthWaveA);
        WriteFrame(buffer, 12, frequencyB, strengthWaveB);
        return buffer;
    }

    /// <summary>编码 BF 安全配置指令（软上限 + 频率/强度平衡参数）。</summary>
    public static byte[] EncodeBf(
        int limitA,
        int limitB,
        int freqBalanceA,
        int freqBalanceB,
        int strengthBalanceA,
        int strengthBalanceB)
    {
        var buffer = new byte[BfLength];
        buffer[0] = 0xBF;
        buffer[1] = (byte)Math.Clamp(limitA, 0, MaxChannelStrength);
        buffer[2] = (byte)Math.Clamp(limitB, 0, MaxChannelStrength);
        buffer[3] = (byte)Math.Clamp(freqBalanceA, 0, 255);
        buffer[4] = (byte)Math.Clamp(freqBalanceB, 0, 255);
        buffer[5] = (byte)Math.Clamp(strengthBalanceA, 0, 255);
        buffer[6] = (byte)Math.Clamp(strengthBalanceB, 0, 255);
        return buffer;
    }

    /// <summary>默认安全软上限：AB 均 200，平衡参数 128。</summary>
    public static byte[] EncodeDefaultBf() => EncodeBf(200, 200, 128, 128, 128, 128);

    /// <summary>构建 A/B 归零且禁用双通道波形的 B0 指令。</summary>
    public static byte[] EncodeZero() =>
        EncodeB0(
            0,
            StrengthAbsolute,
            StrengthAbsolute,
            0,
            0,
            [0, 0, 0, 0], [0, 0, 0, 101],
            [0, 0, 0, 0], [0, 0, 0, 101]);

    /// <summary>构建不修改强度、禁用双通道波形的 B0 指令。</summary>
    public static byte[] EncodeNoWaveform() =>
        EncodeB0(
            0,
            StrengthNoChange,
            StrengthNoChange,
            0,
            0,
            [0, 0, 0, 0], [0, 0, 0, 101],
            [0, 0, 0, 0], [0, 0, 0, 101]);

    /// <summary>解析 B1 强度回执消息；非 B1 或长度不足返回 false。</summary>
    public static bool TryDecodeB1(ReadOnlySpan<byte> frame, out byte sequenceNumber, out byte strengthA, out byte strengthB)
    {
        sequenceNumber = 0;
        strengthA = 0;
        strengthB = 0;
        if (frame.Length < B1Length || frame[0] != 0xB1)
        {
            return false;
        }

        sequenceNumber = frame[1];
        strengthA = frame[2];
        strengthB = frame[3];
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

    private static void WriteFrame(byte[] buffer, int offset, ReadOnlySpan<int> frequencies, ReadOnlySpan<int> strengths)
    {
        for (var i = 0; i < 4; i++)
        {
            buffer[offset + i] = (byte)Math.Clamp(frequencies[i], 0, 255);
            buffer[offset + 4 + i] = (byte)Math.Clamp(strengths[i], 0, 255);
        }
    }

    /// <summary>强度设定值在 0-200 之外一律编码为 0（协议语义）。</summary>
    private static byte EncodeStrengthSetting(int value) => value is >= 0 and <= MaxChannelStrength ? (byte)value : (byte)0;
}