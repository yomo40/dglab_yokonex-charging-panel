namespace ChargingPanel.Core.Protocols;

/// <summary>
/// 波形数据。原定义于 legacy Devices/DGLab；Task 22 删除旧运行时后，
/// 波形预设交换服务仍使用该类型，故迁移到 Protocols。
/// </summary>
public class WaveformData
{
    /// <summary>频率 (10-1000ms 周期)</summary>
    public int Frequency { get; set; } = 100;

    /// <summary>强度百分比 (0-100)</summary>
    public int Strength { get; set; } = 50;

    /// <summary>持续时间 (毫秒)</summary>
    public int Duration { get; set; } = 1000;

    /// <summary>自定义 HEX 波形数据 (如果设置则优先使用)</summary>
    public string? HexData { get; set; }
}
