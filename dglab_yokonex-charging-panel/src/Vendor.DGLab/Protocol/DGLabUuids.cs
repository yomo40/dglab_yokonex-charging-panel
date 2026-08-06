namespace ChargingPanel.Vendor.DGLab.Protocol;

/// <summary>
/// DG-LAB 配件蓝牙 UUID 常量。
/// </summary>
public static class DGLabUuids
{
    // V3 / 配件基础 UUID：0000xxxx-0000-1000-8000-00805f9b34fb
    public const string BaseSuffix = "-0000-1000-8000-00805f9b34fb";

    public const string V3Service = "0000180c-0000-1000-8000-00805f9b34fb";
    public const string V3Write = "0000150a-0000-1000-8000-00805f9b34fb";
    public const string V3Notify = "0000150b-0000-1000-8000-00805f9b34fb";
    public const string BatteryService = "0000180a-0000-1000-8000-00805f9b34fb";
    public const string BatteryCharacteristic = "00001500-0000-1000-8000-00805f9b34fb";

    // V2 基础 UUID：955Axxxx-0FE2-F5AA-A094-84B8D4F3E8AD
    public const string V2BatteryService = "955a180a-0fe2-f5aa-a094-84b8d4f3e8ad";
    public const string V2BatteryCharacteristic = "955a1500-0fe2-f5aa-a094-84b8d4f3e8ad";
    public const string V2PwmService = "955a180b-0fe2-f5aa-a094-84b8d4f3e8ad";
    public const string V2PwmAb2 = "955a1504-0fe2-f5aa-a094-84b8d4f3e8ad";
    public const string V2PwmA34 = "955a1505-0fe2-f5aa-a094-84b8d4f3e8ad";
    public const string V2PwmB34 = "955a1506-0fe2-f5aa-a094-84b8d4f3e8ad";

    /// <summary>V3/配件标准写入特性路由。</summary>
    public const string WriteRoute = V3Write;

    /// <summary>V3/配件标准通知特性路由。</summary>
    public const string NotifyRoute = V3Notify;

    /// <summary>标准电量特性路由。</summary>
    public const string BatteryRoute = BatteryCharacteristic;
}