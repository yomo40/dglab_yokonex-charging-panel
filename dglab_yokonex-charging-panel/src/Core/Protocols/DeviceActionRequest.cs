using ChargingPanel.Core.Devices;

namespace ChargingPanel.Core.Protocols;

/// <summary>
/// 抽象设备动作请求。业务模块把"动作语义 + 数值 + 通道 + 时长"交给映射器，
/// 由映射器按目标能力描述生成命令信封。本类型不携带厂商细节。
/// </summary>
public sealed record DeviceActionRequest
{
    public string EventId { get; init; } = string.Empty;

    public string ActionType { get; init; } = "set";

    public int Value { get; init; }

    public int RawValue { get; init; }

    public int DurationMs { get; init; }

    public string? WaveformData { get; init; }

    public IReadOnlyList<Channel> Channels { get; init; } = Array.Empty<Channel>();
}
