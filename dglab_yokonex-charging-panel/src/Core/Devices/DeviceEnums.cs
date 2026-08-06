namespace ChargingPanel.Core.Devices;

/// <summary>
/// 通道枚举。原定义于旧版设备接口文件；Task 22 删除旧运行时后，
/// 业务逻辑（事件规则、传感器规则、脚本）仍使用该类型，故迁移到独立文件保留。
/// </summary>
public enum Channel
{
    A = 1,
    B = 2,
    AB = 3
}
