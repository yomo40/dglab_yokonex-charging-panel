using System.Security.Cryptography;

namespace ChargingPanel.Vendor.Yokonex.Protocol;

/// <summary>灌肠机泵状态。</summary>
public enum EnemaPumpState : byte
{
    Stop = 0x00,
    Forward = 0x01,
    Reverse = 0x02
}

/// <summary>灌肠机泵类型。</summary>
public enum EnemaPumpKind : byte
{
    Peristaltic = 0x01,
    Suction = 0x02
}

/// <summary>灌肠机泵控制命令。</summary>
public sealed record EnemaPumpCommand(EnemaPumpKind Pump, EnemaPumpState State, ushort DurationSeconds);

/// <summary>灌肠机状态报告（解密后的明文）。</summary>
public abstract record EnemaReport
{
    private EnemaReport()
    {
    }

    public sealed record WorkStatus(EnemaPumpState Peristaltic, EnemaPumpState Suction) : EnemaReport;

    public sealed record Pressure(ushort PressureA, ushort PressureB) : EnemaReport;

    public sealed record Battery(byte Percent) : EnemaReport;
}

/// <summary>
/// 灌肠机一代（TDL_YISKJ-003 V1.0）编解码。
/// 16 字节明文帧 + AES-128 ECB 加密，固定密钥，随机填充可注入以便确定性测试。
/// 安全约束：本类绝不把密钥或明/密文控制帧写入日志。
/// </summary>
public sealed class EnemaV10Codec
{
    public const byte HostMarker = 0xA0;
    public const byte DeviceMarker = 0xB0;

    /// <summary>官方固定 AES-128 密钥。</summary>
    public static ReadOnlySpan<byte> FixedKey =>
        [0xF6, 0x38, 0xBC, 0x9C, 0xFA, 0x47, 0x74, 0x80, 0xAB, 0x32, 0x42, 0xF6, 0xB0, 0x45, 0x57, 0xA1];

    private readonly Func<int, byte[]> _paddingSource;

    public EnemaV10Codec(Func<int, byte[]>? paddingSource = null)
    {
        _paddingSource = paddingSource ?? (length =>
        {
            var buffer = new byte[length];
            Random.Shared.NextBytes(buffer);
            return buffer;
        });
    }

    /// <summary>控制蠕动泵。状态 00 停止 / 01 正转 / 02 反转；时间 0-65535 秒。</summary>
    public byte[] BuildPeristalticPump(EnemaPumpState state, ushort durationSeconds) =>
        BuildHostFrame(
            0x01,
            [(byte)state, (byte)(durationSeconds >> 8), (byte)(durationSeconds & 0xFF)],
            paddingLength: 9);

    /// <summary>控制抽水泵。状态 00 停止 / 01 正转；时间 0-65535 秒。</summary>
    public byte[] BuildSuctionPump(EnemaPumpState state, ushort durationSeconds) =>
        BuildHostFrame(
            0x02,
            [(byte)state, (byte)(durationSeconds >> 8), (byte)(durationSeconds & 0xFF)],
            paddingLength: 9);

    /// <summary>暂停工作：蠕动泵与抽水泵都将停止。SafeStop 使用此指令。</summary>
    public byte[] BuildPause() => BuildHostFrame(0x03, [], paddingLength: 12);

    /// <summary>查询工作状态。</summary>
    public byte[] BuildQueryStatus() => BuildHostFrame(0x04, [], paddingLength: 12);

    /// <summary>获取电量。</summary>
    public byte[] BuildGetBattery() => BuildHostFrame(0x05, [], paddingLength: 12);

    /// <summary>
    /// 解析设备上报的密文帧。解密后按指令字解析为状态报告。
    /// 返回 false 表示不是可识别的灌肠机状态帧。
    /// </summary>
    public bool TryParseReport(ReadOnlySpan<byte> ciphertext, out EnemaReport? report)
    {
        report = null;
        if (ciphertext.Length != 16)
        {
            return false;
        }

        byte[] plaintext;
        try
        {
            plaintext = Decrypt(ciphertext);
        }
        catch (CryptographicException)
        {
            return false;
        }

        if (plaintext.Length != 16 || plaintext[0] != 0xBF || plaintext[1] != 0x0F || plaintext[2] != DeviceMarker)
        {
            return false;
        }

        switch (plaintext[3])
        {
            case 0x01:
                report = new EnemaReport.WorkStatus(
                    (EnemaPumpState)plaintext[4],
                    (EnemaPumpState)plaintext[5]);
                return true;
            case 0x02:
                report = new EnemaReport.Pressure(
                    (ushort)((plaintext[4] << 8) | plaintext[5]),
                    (ushort)((plaintext[6] << 8) | plaintext[7]));
                return true;
            case 0x03:
                report = new EnemaReport.Battery(plaintext[4]);
                return true;
            default:
                return false;
        }
    }

    private byte[] BuildHostFrame(byte command, ReadOnlySpan<byte> data, int paddingLength)
    {
        if (data.Length + paddingLength != 12)
        {
            throw new ArgumentException("命令数据与填充长度必须合计为 12 字节。", nameof(data));
        }

        var plaintext = new byte[16];
        plaintext[0] = 0xBF;
        plaintext[1] = 0x0F;
        plaintext[2] = HostMarker;
        plaintext[3] = command;
        data.CopyTo(plaintext.AsSpan(4, data.Length));
        var padding = _paddingSource(paddingLength);
        if (padding is null || padding.Length != paddingLength)
        {
            throw new InvalidOperationException("随机填充源必须返回指定长度的字节。");
        }

        padding.CopyTo(plaintext.AsSpan(4 + data.Length, paddingLength));
        return Encrypt(plaintext);
    }

    private static byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = FixedKey.ToArray();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext.ToArray(), 0, plaintext.Length);
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> ciphertext)
    {
        using var aes = Aes.Create();
        aes.Key = FixedKey.ToArray();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext.ToArray(), 0, ciphertext.Length);
    }
}
