using System;
using System.Collections.Generic;

namespace ChargingPanel.Core.Devices.DGLab;

/// <summary>
/// 波形数据
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

/// <summary>
/// 波形生成器
/// 负责将用户定义的波形转换为设备可识别的格式
/// </summary>
public static class WaveformGenerator
{
    /// <summary>
    /// 生成 HEX 波形数组（用于 WebSocket 协议）
    /// 每条波形数据必须是 8 字节的 HEX 格式，代表 100ms 的数据
    /// 格式: [freq1, strength1, freq2, strength2, freq3, strength3, freq4, strength4]
    /// 每组 freq+strength 代表 25ms
    /// </summary>
    public static List<string> GenerateHexArray(WaveformData data)
    {
        var result = new List<string>();
        var totalTime = 0;
        
        // 每个 HEX 数据代表 100ms (包含 4 个 25ms 的小周期)
        while (totalTime < data.Duration)
        {
            // 计算压缩后的频率值 (10-240)
            int freq = DGLabBluetoothProtocol.ConvertFrequency(data.Frequency);
            int strength = Math.Clamp(data.Strength, 0, 100);
            
            // 构建 8 字节数据 (4 组 freq+strength，每组 25ms)
            // 格式: [freq1, strength1, freq2, strength2, freq3, strength3, freq4, strength4]
            var hexBytes = new byte[]
            {
                (byte)freq, (byte)strength,
                (byte)freq, (byte)strength,
                (byte)freq, (byte)strength,
                (byte)freq, (byte)strength
            };
            
            result.Add(BitConverter.ToString(hexBytes).Replace("-", ""));
            totalTime += 100;
        }
        
        return result;
    }

    /// <summary>
    /// 生成 ChannelWaveform 数组（用于蓝牙协议）
    /// </summary>
    public static List<ChannelWaveform> GenerateWaveforms(WaveformData data)
    {
        var result = new List<ChannelWaveform>();
        var totalTime = 0;
        
        // 每个 ChannelWaveform 代表 100ms (包含 4 个 25ms 的小周期)
        while (totalTime < data.Duration)
        {
            int freqValue = DGLabBluetoothProtocol.ConvertFrequency(data.Frequency);
            
            result.Add(new ChannelWaveform
            {
                Frequency = new[] { freqValue, freqValue, freqValue, freqValue },
                Strength = new[] { data.Strength, data.Strength, data.Strength, data.Strength }
            });
            
            totalTime += 100;
        }
        
        return result;
    }

    /// <summary>
    /// 生成渐变波形
    /// </summary>
    public static List<ChannelWaveform> GenerateGradientWaveform(int startStrength, int endStrength, int duration, int frequency = 100)
    {
        var result = new List<ChannelWaveform>();
        var steps = duration / 100;
        if (steps <= 0) steps = 1;
        
        var strengthStep = (endStrength - startStrength) / (float)steps;
        int freqValue = DGLabBluetoothProtocol.ConvertFrequency(frequency);
        
        for (int i = 0; i < steps; i++)
        {
            int currentStrength = (int)(startStrength + strengthStep * i);
            currentStrength = Math.Clamp(currentStrength, 0, 100);
            
            result.Add(new ChannelWaveform
            {
                Frequency = new[] { freqValue, freqValue, freqValue, freqValue },
                Strength = new[] { currentStrength, currentStrength, currentStrength, currentStrength }
            });
        }
        
        return result;
    }

    /// <summary>
    /// 生成脉冲波形
    /// </summary>
    public static List<ChannelWaveform> GeneratePulseWaveform(int strength, int onTime, int offTime, int cycles, int frequency = 100)
    {
        var result = new List<ChannelWaveform>();
        int freqValue = DGLabBluetoothProtocol.ConvertFrequency(frequency);
        
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            // 开启周期
            int onSteps = Math.Max(1, onTime / 100);
            for (int i = 0; i < onSteps; i++)
            {
                result.Add(new ChannelWaveform
                {
                    Frequency = new[] { freqValue, freqValue, freqValue, freqValue },
                    Strength = new[] { strength, strength, strength, strength }
                });
            }
            
            // 关闭周期
            int offSteps = Math.Max(1, offTime / 100);
            for (int i = 0; i < offSteps; i++)
            {
                result.Add(new ChannelWaveform
                {
                    Frequency = new[] { 0, 0, 0, 0 },
                    Strength = new[] { 0, 0, 0, 101 } // 101 表示不输出
                });
            }
        }
        
        return result;
    }

    /// <summary>
    /// 生成正弦波形
    /// </summary>
    public static List<ChannelWaveform> GenerateSineWaveform(int minStrength, int maxStrength, int period, int duration, int frequency = 100)
    {
        var result = new List<ChannelWaveform>();
        var steps = duration / 100;
        int freqValue = DGLabBluetoothProtocol.ConvertFrequency(frequency);
        var periodSteps = Math.Max(1, period / 100);
        
        for (int i = 0; i < steps; i++)
        {
            double phase = 2 * Math.PI * (i % periodSteps) / periodSteps;
            double sinValue = (Math.Sin(phase) + 1) / 2; // 0-1
            int currentStrength = (int)(minStrength + (maxStrength - minStrength) * sinValue);
            currentStrength = Math.Clamp(currentStrength, 0, 100);
            
            result.Add(new ChannelWaveform
            {
                Frequency = new[] { freqValue, freqValue, freqValue, freqValue },
                Strength = new[] { currentStrength, currentStrength, currentStrength, currentStrength }
            });
        }
        
        return result;
    }

    /// <summary>
    /// 预设波形 - 呼吸灯效果
    /// </summary>
    public static List<ChannelWaveform> PresetBreathing(int maxStrength, int duration)
    {
        return GenerateSineWaveform(0, maxStrength, 2000, duration);
    }

    /// <summary>
    /// 预设波形 - 心跳效果
    /// </summary>
    public static List<ChannelWaveform> PresetHeartbeat(int strength, int cycles = 3)
    {
        var result = new List<ChannelWaveform>();
        int freqValue = DGLabBluetoothProtocol.ConvertFrequency(50);
        
        for (int i = 0; i < cycles; i++)
        {
            // 第一次跳动
            result.Add(new ChannelWaveform { Frequency = new[] { freqValue, freqValue, freqValue, freqValue }, Strength = new[] { strength, strength, 0, 0 } });
            result.Add(new ChannelWaveform { Frequency = new[] { 0, 0, 0, 0 }, Strength = new[] { 0, 0, 0, 101 } });
            
            // 第二次跳动（稍弱）
            result.Add(new ChannelWaveform { Frequency = new[] { freqValue, freqValue, freqValue, freqValue }, Strength = new[] { strength * 70 / 100, strength * 70 / 100, 0, 0 } });
            result.Add(new ChannelWaveform { Frequency = new[] { 0, 0, 0, 0 }, Strength = new[] { 0, 0, 0, 101 } });
            
            // 间隔
            result.Add(new ChannelWaveform { Frequency = new[] { 0, 0, 0, 0 }, Strength = new[] { 0, 0, 0, 101 } });
            result.Add(new ChannelWaveform { Frequency = new[] { 0, 0, 0, 0 }, Strength = new[] { 0, 0, 0, 101 } });
        }
        
        return result;
    }

    /// <summary>
    /// 预设波形 - 震动效果
    /// </summary>
    public static List<ChannelWaveform> PresetVibration(int strength, int duration)
    {
        return GeneratePulseWaveform(strength, 50, 50, duration / 100, 200);
    }
}
