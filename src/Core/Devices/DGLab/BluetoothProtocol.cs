using System;
using System.Threading;
using System.Threading.Tasks;

namespace ChargingPanel.Core.Devices.DGLab;

/// <summary>
/// DG-LAB 蓝牙 V3 协议编解码器
/// 基于郊狼情趣脉冲主机 V3 蓝牙协议
/// </summary>
public class DGLabBluetoothProtocol
{
    // 蓝牙 UUID 常量
    public const string BLE_SERVICE_UUID = "0000180c-0000-1000-8000-00805f9b34fb";
    public const string BLE_WRITE_CHARACTERISTIC = "0000150a-0000-1000-8000-00805f9b34fb";
    public const string BLE_NOTIFY_CHARACTERISTIC = "0000150b-0000-1000-8000-00805f9b34fb";
    public const string BLE_BATTERY_SERVICE = "0000180a-0000-1000-8000-00805f9b34fb";
    public const string BLE_BATTERY_CHARACTERISTIC = "00001500-0000-1000-8000-00805f9b34fb";

    // 蓝牙设备名称前缀
    public const string DEVICE_NAME_PREFIX_V3 = "47L121000";  // 脉冲主机 3.0

    private int _sequenceNo = 0;

    /// <summary>
    /// 获取下一个序列号 (0-15 循环)
    /// </summary>
    public int GetNextSequenceNo()
    {
        return Interlocked.Increment(ref _sequenceNo) % 16;
    }

    /// <summary>
    /// 频率值转换：用户输入 (10-1000) -> 蓝牙协议值 (10-240)
    /// </summary>
    public static int ConvertFrequency(int inputValue)
    {
        if (inputValue >= 10 && inputValue <= 100)
        {
            return inputValue;
        }
        else if (inputValue >= 101 && inputValue <= 600)
        {
            return (inputValue - 100) / 5 + 100;
        }
        else if (inputValue >= 601 && inputValue <= 1000)
        {
            return (inputValue - 600) / 10 + 200;
        }
        return 10; // 默认值
    }

    /// <summary>
    /// 构建 B0 指令 (20字节)
    /// 每100ms发送一次，包含强度变化和波形数据
    /// </summary>
    public byte[] BuildB0Command(B0CommandData data)
    {
        var buffer = new byte[20];

        // 指令 HEAD
        buffer[0] = 0xB0;

        // 序列号 (高4位) + 强度值解读方式 (低4位)
        var strengthMode = ((int)data.StrengthModeA & 0x03) << 2 | ((int)data.StrengthModeB & 0x03);
        buffer[1] = (byte)((data.SequenceNo & 0x0F) << 4 | (strengthMode & 0x0F));

        // 强度设定值
        buffer[2] = (byte)Math.Clamp(data.StrengthValueA, 0, 200);
        buffer[3] = (byte)Math.Clamp(data.StrengthValueB, 0, 200);

        // A通道波形频率 (4字节)
        // 注意: 频率值不在有效范围(10-240)会使该通道放弃全部4组数据
        for (int i = 0; i < 4; i++)
        {
            buffer[4 + i] = (byte)Math.Clamp(data.WaveformA.Frequency[i], 0, 255);
        }

        // A通道波形强度 (4字节)
        // 注意: 强度值 > 100 会使该通道放弃全部4组数据，用于禁用单通道
        for (int i = 0; i < 4; i++)
        {
            buffer[8 + i] = (byte)Math.Clamp(data.WaveformA.Strength[i], 0, 255);
        }

        // B通道波形频率 (4字节)
        // 注意: 频率值不在有效范围(10-240)会使该通道放弃全部4组数据
        for (int i = 0; i < 4; i++)
        {
            buffer[12 + i] = (byte)Math.Clamp(data.WaveformB.Frequency[i], 0, 255);
        }

        // B通道波形强度 (4字节)
        // 注意: 强度值 > 100 会使该通道放弃全部4组数据，用于禁用单通道
        for (int i = 0; i < 4; i++)
        {
            buffer[16 + i] = (byte)Math.Clamp(data.WaveformB.Strength[i], 0, 255);
        }

        return buffer;
    }

    /// <summary>
    /// 构建 BF 指令 (7字节)
    /// 设置软上限和平衡参数 (断电保存)
    /// </summary>
    public byte[] BuildBFCommand(BFCommandData data)
    {
        var buffer = new byte[7];

        // 指令 HEAD
        buffer[0] = 0xBF;

        // 通道强度软上限
        buffer[1] = (byte)Math.Clamp(data.LimitA, 0, 200);
        buffer[2] = (byte)Math.Clamp(data.LimitB, 0, 200);

        // 波形频率平衡参数
        buffer[3] = (byte)Math.Clamp(data.FreqBalanceA, 0, 255);
        buffer[4] = (byte)Math.Clamp(data.FreqBalanceB, 0, 255);

        // 波形强度平衡参数
        buffer[5] = (byte)Math.Clamp(data.StrengthBalanceA, 0, 255);
        buffer[6] = (byte)Math.Clamp(data.StrengthBalanceB, 0, 255);

        return buffer;
    }

    /// <summary>
    /// 构建简单的强度设置 B0 指令
    /// 只修改强度，不发送波形
    /// </summary>
    public byte[] BuildStrengthCommand(Channel channel, int value, StrengthMode mode, bool needResponse = true)
    {
        var parsingMode = mode switch
        {
            StrengthMode.Decrease => StrengthParsingMode.Decrease,
            StrengthMode.Increase => StrengthParsingMode.Increase,
            _ => StrengthParsingMode.Absolute
        };

        // 对于不使用的通道，设置无效数据
        // 官方示例: 频率{0,0,0,0} + 强度{0,0,0,101} 使该通道放弃全部4组数据
        var invalidWaveform = new ChannelWaveform
        {
            Frequency = new[] { 0, 0, 0, 0 },
            Strength = new[] { 0, 0, 0, 101 }
        };

        var data = new B0CommandData
        {
            SequenceNo = needResponse ? GetNextSequenceNo() : 0,
            StrengthModeA = channel == Channel.A || channel == Channel.AB ? parsingMode : StrengthParsingMode.NoChange,
            StrengthModeB = channel == Channel.B || channel == Channel.AB ? parsingMode : StrengthParsingMode.NoChange,
            StrengthValueA = channel == Channel.A || channel == Channel.AB ? value : 0,
            StrengthValueB = channel == Channel.B || channel == Channel.AB ? value : 0,
            WaveformA = invalidWaveform,
            WaveformB = invalidWaveform
        };

        return BuildB0Command(data);
    }

    /// <summary>
    /// 构建波形输出 B0 指令
    /// </summary>
    public byte[] BuildWaveformCommand(Channel channel, ChannelWaveform waveform)
    {
        // 对于不使用的通道，设置无效数据
        // 官方示例: 频率{0,0,0,0} + 强度{0,0,0,101} 使该通道放弃全部4组数据
        var invalidWaveform = new ChannelWaveform
        {
            Frequency = new[] { 0, 0, 0, 0 },
            Strength = new[] { 0, 0, 0, 101 }
        };

        var data = new B0CommandData
        {
            SequenceNo = 0,
            StrengthModeA = StrengthParsingMode.NoChange,
            StrengthModeB = StrengthParsingMode.NoChange,
            StrengthValueA = 0,
            StrengthValueB = 0,
            WaveformA = channel == Channel.A || channel == Channel.AB ? waveform : invalidWaveform,
            WaveformB = channel == Channel.B || channel == Channel.AB ? waveform : invalidWaveform
        };

        return BuildB0Command(data);
    }

    /// <summary>
    /// 解析 B1 回应消息
    /// </summary>
    public B1Response? ParseB1Response(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xB1)
        {
            return null;
        }

        return new B1Response
        {
            SequenceNo = data[1],
            StrengthA = data[2],
            StrengthB = data[3]
        };
    }

    /// <summary>
    /// 将数据转换为 HEX 字符串 (调试用)
    /// </summary>
    public static string ToHexString(byte[] data)
    {
        return "0x" + BitConverter.ToString(data).Replace("-", "");
    }
}

/// <summary>
/// 强度解读方式
/// </summary>
public enum StrengthParsingMode
{
    NoChange = 0b00,    // 不改变
    Increase = 0b01,    // 相对增加
    Decrease = 0b10,    // 相对减少
    Absolute = 0b11     // 绝对值设定
}

/// <summary>
/// 波形数据结构 (单通道)
/// </summary>
public class ChannelWaveform
{
    /// <summary>4个频率值 (10-240)</summary>
    public int[] Frequency { get; set; } = new int[4];
    /// <summary>4个强度值 (0-100)</summary>
    public int[] Strength { get; set; } = new int[4];
}

/// <summary>
/// B0 指令数据结构
/// </summary>
public class B0CommandData
{
    public int SequenceNo { get; set; }
    public StrengthParsingMode StrengthModeA { get; set; }
    public StrengthParsingMode StrengthModeB { get; set; }
    public int StrengthValueA { get; set; }
    public int StrengthValueB { get; set; }
    public ChannelWaveform WaveformA { get; set; } = new();
    public ChannelWaveform WaveformB { get; set; } = new();
}

/// <summary>
/// BF 指令数据结构 (软上限和平衡参数)
/// </summary>
public class BFCommandData
{
    public int LimitA { get; set; } = 200;
    public int LimitB { get; set; } = 200;
    public int FreqBalanceA { get; set; } = 128;
    public int FreqBalanceB { get; set; } = 128;
    public int StrengthBalanceA { get; set; } = 128;
    public int StrengthBalanceB { get; set; } = 128;
}

/// <summary>
/// B1 回应消息结构
/// </summary>
public class B1Response
{
    public int SequenceNo { get; set; }
    public int StrengthA { get; set; }
    public int StrengthB { get; set; }
}
