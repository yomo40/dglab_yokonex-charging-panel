using System.Globalization;

namespace ChargingPanel.Infrastructure.Windows.Bluetooth;

/// <summary>
/// 蓝牙地址规范化。同一台设备在扫描结果、用户输入和持久化配置中
/// 可能出现 "AA:BB:CC:DD:EE:01"、"aa-bb-cc-dd-ee-01"、"AABBCCDDEE01" 等形式；
/// 不统一就会对同一 GATT 句柄重复连接并互相踢掉。
/// </summary>
public static class BluetoothAddress
{
    /// <summary>
    /// 规范化为无分隔符小写十六进制。仅接受 12 位十六进制（48 位 MAC）；
    /// 非法输入必须抛出，绝不"尽力猜测"成另一台设备的地址。
    /// </summary>
    public static string Normalize(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        Span<char> buffer = stackalloc char[12];
        var length = 0;
        foreach (var ch in address)
        {
            if (ch is ':' or '-' or ' ' or '.')
            {
                continue;
            }

            if (!Uri.IsHexDigit(ch))
            {
                throw new FormatException($"蓝牙地址包含非十六进制字符: {ch}");
            }

            if (length == buffer.Length)
            {
                throw new FormatException("蓝牙地址超过 48 位。");
            }

            buffer[length++] = char.ToLowerInvariant(ch);
        }

        if (length != buffer.Length)
        {
            throw new FormatException("蓝牙地址必须为 12 位十六进制。");
        }

        return new string(buffer);
    }

    /// <summary>从平台的 64 位地址整数规范化。</summary>
    public static string FromUInt64(ulong address) =>
        address.ToString("x12", CultureInfo.InvariantCulture);
}
