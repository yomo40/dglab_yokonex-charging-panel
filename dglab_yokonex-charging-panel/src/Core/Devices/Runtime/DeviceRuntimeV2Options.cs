namespace ChargingPanel.Core.Devices.Runtime;

/// <summary>
/// Runtime v2 运行开关。
///
/// Task 22 删除旧运行时后，v2 是唯一路径：生产默认 <see cref="Enabled"/> 为 true。
/// <see cref="AutoConnectEnabled"/> 仅在 Enabled 时才有意义，由传输地址桥接决定。
/// </summary>
public sealed record DeviceRuntimeV2Options
{
    /// <summary>是否启用 Runtime v2。Task 22 起生产默认 true（旧运行时已删除）。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Runtime v2 是否允许自动连接。仅在 Enabled 时才有意义。</summary>
    public bool AutoConnectEnabled { get; init; }

    /// <summary>
    /// 是否在启动时自动启动业务服务（OCR/事件/脚本/传感器/对战/房间/MOD 桥接）。
    /// 生产默认 true；headless/诊断或测试隔离时置 false 只注册不启动。
    /// </summary>
    public bool StartBusinessServices { get; init; } = true;
}
