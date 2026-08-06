namespace ChargingPanel.Vendor.DGLab.Protocol;

/// <summary>
/// 爪印无线按钮传感器 V1.1 蓝牙协议编解码器。
/// 所有指令 17 字节经 0x150A 写入，所有回应经 0x150B 通知返回。
/// 纯确定性编码/解码，不持有连接、计时器或日志。
/// </summary>
public static class PawPrintsV11Codec
{
    public const byte DeviceTypeV11 = 0x03;

    public const int CommandLength = 17;
    public const int D0MessageLength = 9;
    public const int F1MessageLength = 14;

    // 触发模式
    public const byte TriggerModeNone = 0x00;
    public const byte TriggerModeRandomReaction = 0x03;
    public const byte TriggerModeProbability = 0x04;
    public const byte TriggerModePressAndAcceleration = 0x05;
    public const byte TriggerModeExternalVoltage = 0x0F;
    public const byte TriggerModeD0Passthrough = 0xD0;

    // ---------- 50 指令 ----------

    /// <summary>编码 50 指令：设置主指示灯颜色并写入 15 字节触发模式设置。</summary>
    public static byte[] EncodeSetLampAndTrigger(byte lampColor, ReadOnlySpan<byte> triggerSettings15)
    {
        if (triggerSettings15.Length != 15)
        {
            throw new ArgumentException("爪印触发模式设置必须为 15 字节。", nameof(triggerSettings15));
        }

        var buffer = new byte[CommandLength];
        buffer[0] = 0x50;
        buffer[1] = lampColor;
        triggerSettings15.CopyTo(buffer.AsSpan(2, 15));
        return buffer;
    }

    /// <summary>编码 50 指令：无任何触发模式（连接后立即写入的安全模式）。</summary>
    public static byte[] EncodeNoTriggerMode(byte lampColor = 0x01)
    {
        var buffer = new byte[CommandLength];
        buffer[0] = 0x50;
        buffer[1] = lampColor;
        buffer[2] = TriggerModeNone;
        return buffer;
    }

    /// <summary>编码随机反应触发设置。</summary>
    public static byte[] EncodeRandomReaction(
        byte lampColor,
        byte eventId,
        ushort greenTimeMinSeconds,
        ushort greenTimeMaxSeconds,
        ushort reactionTimeSeconds,
        byte paramIncrease,
        byte paramIncreaseSpeed,
        byte paramDecrease,
        byte paramDeclineSpeed)
    {
        var buffer = new byte[CommandLength];
        buffer[0] = 0x50;
        buffer[1] = lampColor;
        buffer[2] = TriggerModeRandomReaction;
        buffer[3] = eventId;
        WriteUInt16(buffer, 4, greenTimeMinSeconds);
        WriteUInt16(buffer, 6, greenTimeMaxSeconds);
        WriteUInt16(buffer, 8, reactionTimeSeconds);
        buffer[10] = paramIncrease;
        buffer[11] = paramIncreaseSpeed;
        buffer[12] = paramDecrease;
        buffer[13] = paramDeclineSpeed;
        return buffer;
    }

    /// <summary>编码概率触发设置。events 为 (事件 ID, 概率 0-200) 对，最多 6 个。</summary>
    public static byte[] EncodeProbability(
        byte lampColor,
        ReadOnlySpan<(byte EventId, byte Probability)> events,
        ushort cooldownSeconds)
    {
        if (events.Length > 6)
        {
            throw new ArgumentException("爪印概率触发最多 6 个事件。", nameof(events));
        }

        var buffer = new byte[CommandLength];
        buffer[0] = 0x50;
        buffer[1] = lampColor;
        buffer[2] = TriggerModeProbability;
        for (var i = 0; i < 6; i++)
        {
            var offset = 3 + (i * 2);
            if (i < events.Length)
            {
                buffer[offset] = events[i].EventId;
                buffer[offset + 1] = events[i].Probability;
            }
        }

        WriteUInt16(buffer, 15, cooldownSeconds);
        return buffer;
    }

    /// <summary>编码外部电压检测触发设置。</summary>
    public static byte[] EncodeExternalVoltage(
        byte lampColor,
        byte eventId,
        byte pullup,
        byte voltageMin,
        byte voltageMax,
        byte paramMappingRange)
    {
        var buffer = new byte[CommandLength];
        buffer[0] = 0x50;
        buffer[1] = lampColor;
        buffer[2] = TriggerModeExternalVoltage;
        buffer[3] = eventId;
        buffer[4] = pullup;
        buffer[5] = voltageMin;
        buffer[6] = voltageMax;
        buffer[7] = paramMappingRange;
        return buffer;
    }

    /// <summary>编码按下/抬起 + 加速度/角度触发设置。settings6 为加速度/角度 6 字节阈值设置。</summary>
    public static byte[] EncodePressAndAcceleration(
        byte lampColor,
        byte pressEventId,
        byte settingsByte,
        byte pressParamIncreaseSpeed,
        byte pressParamDecrease,
        byte pressParamDeclineSpeed,
        byte pressParamIncrease,
        byte accelEventId,
        ReadOnlySpan<byte> accelThreshold6,
        byte accelParamMapping)
    {
        if (accelThreshold6.Length != 6)
        {
            throw new ArgumentException("加速度/角度阈值设置必须为 6 字节。", nameof(accelThreshold6));
        }

        var buffer = new byte[CommandLength];
        buffer[0] = 0x50;
        buffer[1] = lampColor;
        buffer[2] = TriggerModePressAndAcceleration;
        buffer[3] = pressEventId;
        buffer[4] = settingsByte;
        buffer[5] = pressParamIncreaseSpeed;
        buffer[6] = pressParamDecrease;
        buffer[7] = pressParamDeclineSpeed;
        buffer[8] = pressParamIncrease;
        buffer[9] = accelEventId;
        accelThreshold6.CopyTo(buffer.AsSpan(10, 6));
        buffer[16] = accelParamMapping;
        return buffer;
    }

    /// <summary>编码 D0 物理数据直传模式。</summary>
    public static byte[] EncodeD0Passthrough(byte lampColor = 0x01)
    {
        var buffer = new byte[CommandLength];
        buffer[0] = 0x50;
        buffer[1] = lampColor;
        buffer[2] = TriggerModeD0Passthrough;
        return buffer;
    }

    // ---------- 5F / 60 / 70 指令 ----------

    /// <summary>编码 5F 指令：触发模式参数值清零。</summary>
    public static byte[] EncodeParamReset() => [0x5F];

    /// <summary>编码 60 指令：启动 XYZ 角度自动检测。</summary>
    public static byte[] EncodeAutoDetect() => [0x60];

    /// <summary>编码 70 指令：肩灯常亮。</summary>
    public static byte[] EncodeShoulderLampSolid(byte color) => [0x70, color];

    /// <summary>编码 70 指令：肩灯闪烁。speed 1-慢速，2-快速，3-停止。</summary>
    public static byte[] EncodeShoulderLampBlink(byte color1, byte color2, byte speed) => [0x70, color1, color2, speed];

    // ---------- 回应消息解码 ----------

    /// <summary>解码 51 消息：主指示灯颜色 + 设备类型 + 电量。</summary>
    public static bool TryDecode51(ReadOnlySpan<byte> frame, out byte lampColor, out byte deviceType, out byte battery)
    {
        lampColor = 0;
        deviceType = 0;
        battery = 0;
        if (frame.Length < 4 || frame[0] != 0x51)
        {
            return false;
        }

        lampColor = frame[1];
        deviceType = frame[2];
        battery = Math.Min(frame[3], (byte)100);
        return true;
    }

    /// <summary>解码 5A 消息：事件触发上报。</summary>
    public static bool TryDecode5A(ReadOnlySpan<byte> frame, out byte lampColor, out byte eventId, out byte paramValue)
    {
        lampColor = 0;
        eventId = 0;
        paramValue = 0;
        if (frame.Length < 4 || frame[0] != 0x5A)
        {
            return false;
        }

        lampColor = frame[1];
        eventId = frame[2];
        paramValue = frame[3];
        return true;
    }

    /// <summary>解码 5B 消息：事件取消触发上报。</summary>
    public static bool TryDecode5B(ReadOnlySpan<byte> frame, out byte lampColor, out byte eventId)
    {
        lampColor = 0;
        eventId = 0;
        if (frame.Length < 3 || frame[0] != 0x5B)
        {
            return false;
        }

        lampColor = frame[1];
        eventId = frame[2];
        return true;
    }

    /// <summary>解码 5C 消息：事件参数变化上报。</summary>
    public static bool TryDecode5C(ReadOnlySpan<byte> frame, out byte lampColor, out byte eventId, out byte paramValue)
    {
        lampColor = 0;
        eventId = 0;
        paramValue = 0;
        if (frame.Length < 4 || frame[0] != 0x5C)
        {
            return false;
        }

        lampColor = frame[1];
        eventId = frame[2];
        paramValue = frame[3];
        return true;
    }

    /// <summary>解码 D0 消息：物理数据集（主灯颜色、序号、按键、加速度、XYZ 角度、外部电压）。</summary>
    public static bool TryDecodeD0(
        ReadOnlySpan<byte> frame,
        out byte lampColor,
        out byte sequence,
        out byte pressState,
        out byte acceleration,
        out byte xAngle,
        out byte yAngle,
        out byte zAngle,
        out byte externalVoltage)
    {
        lampColor = 0;
        sequence = 0;
        pressState = 0;
        acceleration = 0;
        xAngle = 0;
        yAngle = 0;
        zAngle = 0;
        externalVoltage = 0;
        if (frame.Length < D0MessageLength || frame[0] != 0xD0)
        {
            return false;
        }

        lampColor = frame[1];
        sequence = frame[2];
        pressState = frame[3];
        acceleration = frame[4];
        xAngle = frame[5];
        yAngle = frame[6];
        zAngle = frame[7];
        externalVoltage = frame[8];
        return true;
    }

    /// <summary>解码 F1 消息：自动检测到的 XYZ 角度阈值范围（除以 2 使用）。</summary>
    public static bool TryDecodeF1(
        ReadOnlySpan<byte> frame,
        out short xMin,
        out short xMax,
        out short yMin,
        out short yMax,
        out short zMin,
        out short zMax)
    {
        xMin = 0;
        xMax = 0;
        yMin = 0;
        yMax = 0;
        zMin = 0;
        zMax = 0;
        if (frame.Length < F1MessageLength || frame[0] != 0xF1 || frame[1] != 0x61)
        {
            return false;
        }

        xMin = ReadInt16(frame, 2);
        xMax = ReadInt16(frame, 4);
        yMin = ReadInt16(frame, 6);
        yMax = ReadInt16(frame, 8);
        zMin = ReadInt16(frame, 10);
        zMax = ReadInt16(frame, 12);
        return true;
    }

    // 协议 16 位字段为大端序（高字节在前），见官方文档黄金向量。
    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 1] = (byte)(value & 0xFF);
    }

    private static short ReadInt16(ReadOnlySpan<byte> buffer, int offset) =>
        (short)((buffer[offset] << 8) | buffer[offset + 1]);
}