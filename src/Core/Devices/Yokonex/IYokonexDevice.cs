/**
 * 郊狼&役次元游戏适配面板 - 役次元设备扩展接口
 * 定义役次元设备特有的功能API
 */

using System;
using System.Threading.Tasks;

namespace ChargingPanel.Core.Devices.Yokonex;

// 注意: YokonexDeviceType 枚举在 YokonexIMAdapter.cs 中定义

/// <summary>
/// 役次元设备扩展接口
/// 定义役次元设备特有的功能
/// </summary>
public interface IYokonexDevice : IDevice
{
    /// <summary>设备类型</summary>
    YokonexDeviceType YokonexType { get; }
}

/// <summary>
/// 役次元电击器扩展接口
/// </summary>
public interface IYokonexEmsDevice : IYokonexDevice
{
    #region 马达控制
    
    /// <summary>
    /// 设置马达状态
    /// </summary>
    /// <param name="state">马达状态</param>
    Task SetMotorStateAsync(YokonexMotorState state);
    
    /// <summary>
    /// 马达状态变化事件
    /// </summary>
    event EventHandler<YokonexMotorState>? MotorStateChanged;
    
    #endregion
    
    #region 计步器功能
    
    /// <summary>
    /// 设置计步器状态
    /// </summary>
    /// <param name="state">计步器状态</param>
    Task SetPedometerStateAsync(PedometerState state);
    
    /// <summary>
    /// 获取当前步数
    /// </summary>
    int StepCount { get; }
    
    /// <summary>
    /// 步数变化事件
    /// </summary>
    event EventHandler<int>? StepCountChanged;
    
    #endregion
    
    #region 角度传感器
    
    /// <summary>
    /// 启用/禁用角度传感器
    /// </summary>
    /// <param name="enabled">是否启用</param>
    Task SetAngleSensorEnabledAsync(bool enabled);
    
    /// <summary>
    /// 当前角度 (x, y, z)
    /// </summary>
    (float X, float Y, float Z) CurrentAngle { get; }
    
    /// <summary>
    /// 角度变化事件
    /// </summary>
    event EventHandler<(float X, float Y, float Z)>? AngleChanged;
    
    #endregion
    
    #region 通道连接检测
    
    /// <summary>
    /// 获取通道连接状态
    /// </summary>
    (bool ChannelA, bool ChannelB) ChannelConnectionState { get; }
    
    /// <summary>
    /// 通道连接状态变化事件
    /// </summary>
    event EventHandler<(bool ChannelA, bool ChannelB)>? ChannelConnectionChanged;
    
    #endregion
    
    #region 自定义波形
    
    /// <summary>
    /// 设置自定义波形参数
    /// </summary>
    /// <param name="channel">通道</param>
    /// <param name="frequency">频率 1-100 Hz</param>
    /// <param name="pulseTime">脉冲时间 0-100 us</param>
    Task SetCustomWaveformAsync(Channel channel, int frequency, int pulseTime);
    
    /// <summary>
    /// 设置固定模式
    /// </summary>
    /// <param name="channel">通道</param>
    /// <param name="mode">模式编号 1-16</param>
    Task SetFixedModeAsync(Channel channel, int mode);
    
    #endregion
}

/// <summary>
/// 役次元灌肠器扩展接口
/// </summary>
public interface IYokonexEnemaDevice : IYokonexDevice
{
    /// <summary>
    /// 设置注入强度
    /// </summary>
    /// <param name="strength">强度 0-100%</param>
    Task SetInjectionStrengthAsync(int strength);
    
    /// <summary>
    /// 开始注入
    /// </summary>
    Task StartInjectionAsync();
    
    /// <summary>
    /// 停止注入
    /// </summary>
    Task StopInjectionAsync();
    
    /// <summary>
    /// 设置振动强度
    /// </summary>
    /// <param name="strength">强度 0-100%</param>
    Task SetVibrationStrengthAsync(int strength);
    
    /// <summary>
    /// 当前注入状态
    /// </summary>
    bool IsInjecting { get; }
    
    /// <summary>
    /// 当前注入强度
    /// </summary>
    int InjectionStrength { get; }
    
    /// <summary>
    /// 当前振动强度
    /// </summary>
    int VibrationStrength { get; }
    
    /// <summary>
    /// 注入状态变化事件
    /// </summary>
    event EventHandler<bool>? InjectionStateChanged;
}

/// <summary>
/// 役次元跳蛋/飞机杯扩展接口
/// </summary>
public interface IYokonexToyDevice : IYokonexDevice
{
    /// <summary>
    /// 设置马达强度
    /// </summary>
    /// <param name="motor">马达编号 1-3</param>
    /// <param name="strength">强度 0-20</param>
    Task SetMotorStrengthAsync(int motor, int strength);
    
    /// <summary>
    /// 设置所有马达强度
    /// </summary>
    /// <param name="strength1">马达1强度 0-20</param>
    /// <param name="strength2">马达2强度 0-20</param>
    /// <param name="strength3">马达3强度 0-20</param>
    Task SetAllMotorsAsync(int strength1, int strength2, int strength3);
    
    /// <summary>
    /// 设置固定模式
    /// </summary>
    /// <param name="mode">模式编号 1-8</param>
    Task SetFixedModeAsync(int mode);
    
    /// <summary>
    /// 停止所有马达
    /// </summary>
    Task StopAllMotorsAsync();
    
    /// <summary>
    /// 当前各马达强度
    /// </summary>
    (int Motor1, int Motor2, int Motor3) MotorStrengths { get; }
    
    /// <summary>
    /// 马达强度变化事件
    /// </summary>
    event EventHandler<(int Motor1, int Motor2, int Motor3)>? MotorStrengthChanged;
}
