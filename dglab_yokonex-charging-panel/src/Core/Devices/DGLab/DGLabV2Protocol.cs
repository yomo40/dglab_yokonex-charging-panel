using System;

namespace ChargingPanel.Core.Devices.DGLab;

/// <summary>
/// DG-LAB V2 蓝牙协议编解码器
/// 基于郊狼情趣脉冲主机 V2 蓝牙协议
/// </summary>
public class DGLabV2Protocol
{
    // 蓝牙 UUID 常量 (V2)
    // 基础 UUID: 955Axxxx-0FE2-F5AA-A094-84B8D4F3E8AD
    public const string BLE_BASE_UUID = "955a{0:x4}-0fe2-f5aa-a094-84b8d4f3e8ad";
    public const string BLE_BATTERY_SERVICE = "955a180a-0fe2-f5aa-a094-84b8d4f3e8ad";
    public const string BLE_BATTERY_CHARACTERISTIC = "955a1500-0fe2-f5aa-a094-84b8d4f3e8ad";
    public const string BLE_PWM_SERVICE = "955a180b-0fe2-f5aa-a094-84b8d4f3e8ad";
    public const string BLE_PWM_AB2 = "955a1504-0fe2-f5aa-a094-84b8d4f3e8ad";  // AB两通道强度
    // 注意: 官方协议中名称与实际控制通道相反
    // PWM_A34 (0x1505) 实际控制 B 通道，PWM_B34 (0x1506) 实际控制 A 通道
    public const string BLE_PWM_A34 = "955a1505-0fe2-f5aa-a094-84b8d4f3e8ad";  // 实际控制 B 通道波形
    public const string BLE_PWM_B34 = "955a1506-0fe2-f5aa-a094-84b8d4f3e8ad";  // 实际控制 A 通道波形

    // 蓝牙设备名称前缀
    public const string DEVICE_NAME_PREFIX = "D-LAB ESTIM01";

    /// <summary>
    /// 构建 AB 通道强度数据 (3字节, 小端序)
    /// 官方文档: PWM_AB2: 23-22bit(保留) + 21-11bit(A通道强度) + 10-0bit(B通道强度)
    /// </summary>
    public byte[] BuildStrengthCommand(int strengthA, int strengthB)
    {
        // V2 强度范围 0-2047，APP 每级增加 7
        var safeA = Math.Clamp(strengthA, 0, 2047);
        var safeB = Math.Clamp(strengthB, 0, 2047);

        // 官方文档: A 在高 11 位 (21-11bit)，B 在低 11 位 (10-0bit)
        int combined = ((safeA & 0x7FF) << 11) | (safeB & 0x7FF);

        // 小端序: 低字节在前
        return new byte[]
        {
            (byte)(combined & 0xFF),          // 低字节
            (byte)((combined >> 8) & 0xFF),   // 中字节
            (byte)((combined >> 16) & 0xFF)   // 高字节
        };
    }

    /// <summary>
    /// 解析 AB 通道强度数据 (3字节, 小端序)
    /// 官方文档: 21-11bit(A通道) + 10-0bit(B通道)
    /// </summary>
    public (int strengthA, int strengthB) ParseStrengthData(byte[] data)
    {
        if (data.Length < 3) return (0, 0);

        // 小端序: 低字节在前
        int combined = data[0] | (data[1] << 8) | (data[2] << 16);
        int strengthA = (combined >> 11) & 0x7FF;
        int strengthB = combined & 0x7FF;

        return (strengthA, strengthB);
    }

    /// <summary>
    /// 构建波形数据 (3字节, 小端序)
    /// 官方文档: PWM_x34: 19-15bit(Z) + 14-5bit(Y) + 4-0bit(X)
    /// 示例: x=1,y=9,z=20 → bytes=21010A
    /// </summary>
    public byte[] BuildWaveformCommand(int x, int y, int z)
    {
        // X: 0-31, Y: 0-1023, Z: 0-31
        var safeX = Math.Clamp(x, 0, 31);
        var safeY = Math.Clamp(y, 0, 1023);
        var safeZ = Math.Clamp(z, 0, 31);

        int combined = ((safeZ & 0x1F) << 15) | ((safeY & 0x3FF) << 5) | (safeX & 0x1F);

        // 小端序: 低字节在前
        return new byte[]
        {
            (byte)(combined & 0xFF),          // 低字节
            (byte)((combined >> 8) & 0xFF),   // 中字节
            (byte)((combined >> 16) & 0xFF)   // 高字节
        };
    }

    /// <summary>
    /// 解析波形数据 (3字节, 小端序)
    /// </summary>
    public (int x, int y, int z) ParseWaveformData(byte[] data)
    {
        if (data.Length < 3) return (0, 0, 0);

        // 小端序: 低字节在前
        int combined = data[0] | (data[1] << 8) | (data[2] << 16);
        int x = combined & 0x1F;
        int y = (combined >> 5) & 0x3FF;
        int z = (combined >> 15) & 0x1F;

        return (x, y, z);
    }

    /// <summary>
    /// 根据频率值计算 X 和 Y
    /// Frequency = X + Y, 范围 10-1000
    /// X = ((Frequency / 1000) ^ 0.5) * 15
    /// Y = Frequency - X
    /// </summary>
    public (int x, int y) CalculateXYFromFrequency(int frequency)
    {
        frequency = Math.Clamp(frequency, 10, 1000);
        
        int x = (int)(Math.Pow(frequency / 1000.0, 0.5) * 15);
        x = Math.Clamp(x, 1, 31);
        
        int y = frequency - x;
        y = Math.Clamp(y, 0, 1023);

        return (x, y);
    }

    /// <summary>
    /// 将 0-200 的强度转换为 V2 的 0-2047 范围
    /// </summary>
    public int ConvertStrengthToV2(int strength)
    {
        // V2 每级 = 7，所以 strength * 7 ≈ V2 值
        // 但最大值是 2047，所以需要映射
        return (int)(strength / 200.0 * 2047);
    }

    /// <summary>
    /// 将 V2 的 0-2047 范围转换为 0-200
    /// </summary>
    public int ConvertStrengthFromV2(int v2Strength)
    {
        return (int)(v2Strength / 2047.0 * 200);
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
/// V2 波形数据结构
/// </summary>
public class V2WaveformData
{
    /// <summary>脉冲数量 X (0-31)</summary>
    public int X { get; set; }
    /// <summary>间隔时间 Y (0-1023)</summary>
    public int Y { get; set; }
    /// <summary>脉冲宽度 Z (0-31), 实际宽度 = Z * 5us</summary>
    public int Z { get; set; } = 20;

    /// <summary>
    /// 从频率值创建波形数据
    /// </summary>
    public static V2WaveformData FromFrequency(int frequency, int pulseWidth = 20)
    {
        var protocol = new DGLabV2Protocol();
        var (x, y) = protocol.CalculateXYFromFrequency(frequency);
        return new V2WaveformData { X = x, Y = y, Z = pulseWidth };
    }
}
