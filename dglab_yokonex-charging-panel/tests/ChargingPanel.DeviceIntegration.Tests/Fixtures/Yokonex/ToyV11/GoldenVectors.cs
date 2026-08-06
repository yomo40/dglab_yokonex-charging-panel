namespace ChargingPanel.DeviceIntegration.Tests.Fixtures.Yokonex.ToyV11;

/// <summary>
/// 跳蛋/飞机杯 V1.1 黄金向量（来自 YSKJ_TOY_BLE V1.1 协议 PDF）。
/// 校验和 = 除末字节外所有字节累加，取低 8 位。
/// </summary>
internal static class GoldenVectors
{
    public static readonly byte[] DeviceInfoQuery = [0x35, 0x10, 0x45];

    public static readonly byte[] FixedModeA =
        [0x35, 0x11, 0x01, 0x03, 0x4A];

    public static readonly byte[] SpeedTenFiveZero =
        [0x35, 0x12, 0x0A, 0x05, 0x00, 0x56];

    public static readonly byte[] DeviceInfoResponse =
        [0x35, 0x10, 0x01, 0x01, 0x05, 0x00, 0x03, 0x00, 0x00, 0x4F];

    public static readonly byte[] BatteryReport = [0x35, 0x13, 0x01, 0x64, 0xAD];
}
