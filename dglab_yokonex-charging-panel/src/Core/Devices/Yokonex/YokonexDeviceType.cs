namespace ChargingPanel.Core.Devices.Yokonex;

/// <summary>
/// 役次元设备类型
/// </summary>
public enum YokonexDeviceType
{
    /// <summary>电击器</summary>
    Estim,
    /// <summary>灌肠器</summary>
    Enema,
    /// <summary>跳蛋</summary>
    Vibrator,
    /// <summary>飞机杯</summary>
    Cup,
    /// <summary>智能锁（预留）</summary>
    SmartLock
}

/// <summary>
/// 役次元协议代际标识
/// </summary>
public enum YokonexProtocolGeneration
{
    /// <summary>自动推断</summary>
    Auto = 0,
    /// <summary>电击器协议 V1.6</summary>
    EmsV1_6,
    /// <summary>电击器协议 V2.0</summary>
    EmsV2_0,
    /// <summary>灌肠器协议 V1.0</summary>
    EnemaV1_0,
    /// <summary>跳蛋/飞机杯协议 V1.1</summary>
    ToyV1_1,
    /// <summary>智能锁协议预留（未上市）</summary>
    SmartLockReserved,
    /// <summary>IM 事件协议（game_cmd / game_info）</summary>
    IMEvent,
    /// <summary>WebSocket 协议预留（厂商暂未开放）</summary>
    WebSocketReserved
}
