namespace ChargingPanel.Core.Services;

/// <summary>蠕动泵状态</summary>
public enum PeristalticPumpState
{
    Stop = 0x00,
    Forward = 0x01,   // 正转
    Reverse = 0x02    // 反转
}

/// <summary>抽水泵状态</summary>
public enum WaterPumpState
{
    Stop = 0x00,
    Forward = 0x01    // 正转
}

/// <summary>灌肠器工作状态</summary>
public class EnemaDeviceStatus
{
    public PeristalticPumpState PeristalticPumpState { get; set; }
    public WaterPumpState WaterPumpState { get; set; }
    public int PressureA { get; set; }
    public int PressureB { get; set; }
    public int BatteryLevel { get; set; }
}

/// <summary>跳蛋/飞机杯设备信息</summary>
public class ToyDeviceInfo
{
    public int ProductId { get; set; }       // 产品型号 ID (1-255)
    public int Version { get; set; }         // 产品版本号 (1-255)
    public int MotorAModeCount { get; set; } // A 马达模式数量 (0=无该马达)
    public int MotorBModeCount { get; set; } // B 马达模式数量
    public int MotorCModeCount { get; set; } // C 马达模式数量

    public bool HasMotorA => MotorAModeCount > 0;
    public bool HasMotorB => MotorBModeCount > 0;
    public bool HasMotorC => MotorCModeCount > 0;
}
