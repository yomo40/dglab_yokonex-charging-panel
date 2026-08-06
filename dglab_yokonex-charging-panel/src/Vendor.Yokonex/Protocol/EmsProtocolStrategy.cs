namespace ChargingPanel.Vendor.Yokonex.Protocol;

/// <summary>通道单侧输出状态。</summary>
public sealed record EmsChannelOutputState(bool On, int Intensity, int FixedMode, int Frequency, int PulseTime)
{
    public static EmsChannelOutputState Off() => new(false, 0, 0, 0, 0);
}

/// <summary>通道输出命令。AB 表示 A/B 同步输出同一组参数。</summary>
public sealed record EmsChannelOutputCommand(EmsChannel Channel, EmsChannelOutputState State);

/// <summary>
/// 电击器协议代际策略。V1.6 与 V2.0 的通道帧结构不同，
/// 马达/计步/角度/查询/应答解析两代一致，在此抽取公共实现。
/// </summary>
public interface IEmsProtocolStrategy
{
    /// <summary>
    /// 构建通道输出帧。V2.0 为同帧双通道结构，需要另一通道当前状态；
    /// V1.6 为单通道帧，忽略 other。
    /// </summary>
    byte[] BuildChannelOutputFrame(EmsChannelOutputCommand command, EmsChannelOutputState other);

    /// <summary>构建通道关闭帧（安全停止使用，一代正确的关闭 A/B 输出）。</summary>
    byte[] BuildCloseFrame();

    byte[] BuildMotorFrame(EmsMotorState state);

    byte[] BuildStepFrame(EmsStepCommand command);

    byte[] BuildAngleFrame(EmsAngleCommand command);

    byte[] BuildQueryFrame(EmsQueryType type);

    /// <summary>解析 FF32 通知流中的状态应答。返回 false 表示不是可识别的电击器状态帧。</summary>
    bool TryParseReport(ReadOnlySpan<byte> frame, out EmsStatusReport? report);
}
