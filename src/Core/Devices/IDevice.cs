/**
 * 郊狼&役次元游戏适配面板 - 设备接口
 * 统一设备适配器接口，所有厂商的适配器都需要实现此接口
 */

namespace ChargingPanel.Core.Devices;

/// <summary>
/// 通道枚举
/// </summary>
public enum Channel
{
    A = 1,
    B = 2,
    AB = 3
}

/// <summary>
/// 强度变化模式
/// </summary>
public enum StrengthMode
{
    /// <summary>减少</summary>
    Decrease = 0,
    /// <summary>增加</summary>
    Increase = 1,
    /// <summary>设定为指定值</summary>
    Set = 2
}

/// <summary>
/// 设备状态
/// </summary>
public enum DeviceStatus
{
    /// <summary>未连接</summary>
    Disconnected,
    /// <summary>连接中</summary>
    Connecting,
    /// <summary>等待绑定（郊狼专用）</summary>
    WaitingForBind,
    /// <summary>已连接</summary>
    Connected,
    /// <summary>错误</summary>
    Error
}

/// <summary>
/// 设备类型
/// </summary>
public enum DeviceType
{
    /// <summary>郊狼 (DG-LAB)</summary>
    DGLab,
    /// <summary>役次元 (YOKONEX)</summary>
    Yokonex,
    /// <summary>虚拟设备（测试用）</summary>
    Virtual
}

/// <summary>
/// 强度信息
/// </summary>
public record StrengthInfo
{
    public int ChannelA { get; init; }
    public int ChannelB { get; init; }
    public int LimitA { get; init; }
    public int LimitB { get; init; }
    
    /// <summary>通用限制值（取最小值）</summary>
    public int Limit => Math.Min(LimitA > 0 ? LimitA : 200, LimitB > 0 ? LimitB : 200);
}

/// <summary>
/// 设备状态信息
/// </summary>
public record DeviceState
{
    public DeviceStatus Status { get; init; }
    public StrengthInfo Strength { get; init; } = new();
    public int? BatteryLevel { get; init; }
    public DateTime LastUpdate { get; init; }
}

/// <summary>
/// 连接配置
/// </summary>
public record ConnectionConfig
{
    // DG-LAB 配置
    public string? WebSocketUrl { get; init; }
    public string? TargetId { get; init; }
    
    // YOKONEX 配置
    public string? Uid { get; init; }
    public string? Token { get; init; }
    public string? UserId { get; init; }
    public string? TargetUserId { get; init; }
    
    // 蓝牙配置
    public string? Address { get; init; }  // 蓝牙 MAC 地址或设备 ID
    
    // 通用配置
    public bool AutoReconnect { get; init; } = true;
    public int ReconnectInterval { get; init; } = 5000;
}

/// <summary>
/// 设备适配器接口
/// </summary>
public interface IDevice
{
    /// <summary>设备唯一标识</summary>
    string Id { get; }
    
    /// <summary>设备名称</summary>
    string Name { get; set; }
    
    /// <summary>设备类型</summary>
    DeviceType Type { get; }
    
    /// <summary>连接状态</summary>
    DeviceStatus Status { get; }
    
    /// <summary>当前状态</summary>
    DeviceState State { get; }
    
    /// <summary>连接配置</summary>
    ConnectionConfig? Config { get; }
    
    /// <summary>状态变化事件</summary>
    event EventHandler<DeviceStatus>? StatusChanged;
    
    /// <summary>强度变化事件</summary>
    event EventHandler<StrengthInfo>? StrengthChanged;
    
    /// <summary>电量变化事件</summary>
    event EventHandler<int>? BatteryChanged;
    
    /// <summary>错误事件</summary>
    event EventHandler<Exception>? ErrorOccurred;
    
    /// <summary>
    /// 连接设备
    /// </summary>
    Task ConnectAsync(ConnectionConfig config, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 断开连接
    /// </summary>
    Task DisconnectAsync();
    
    /// <summary>
    /// 设置通道强度
    /// </summary>
    Task SetStrengthAsync(Channel channel, int value, StrengthMode mode = StrengthMode.Set);
    
    /// <summary>
    /// 发送波形数据
    /// </summary>
    Task SendWaveformAsync(Channel channel, DGLab.WaveformData waveform);
    
    /// <summary>
    /// 清空波形队列
    /// </summary>
    Task ClearWaveformQueueAsync(Channel channel);
    
    /// <summary>
    /// 设置强度上限
    /// </summary>
    Task SetLimitsAsync(int limitA, int limitB);
}
