using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.DGLab;

namespace ChargingPanel.Core.Protocols;

/// <summary>
/// 波形预设文件交换服务
/// 提供统一的预设导入/导出与跨设备解析能力。
/// </summary>
public static class WaveformPresetExchangeService
{
    private const string PackageFormat = "charging-panel.waveform-pack";
    private static readonly Regex HexRegex = new("^[0-9A-Fa-f]+$", RegexOptions.Compiled);
    private static readonly Regex FrequencyPulseRegex =
        new(@"^\s*(\d{1,4})\s*[,;:/|]\s*(\d{1,4})\s*$", RegexOptions.Compiled);

    public static string ExportPackage(IEnumerable<WaveformPresetRecord> presets, bool includeBuiltIn = false)
    {
        var package = new WaveformPresetPackage
        {
            Format = PackageFormat,
            Version = 1,
            ExportedAt = DateTime.UtcNow.ToString("o"),
            Presets = presets
                .Where(p => includeBuiltIn || !p.IsBuiltIn)
                .Select(MapToPackageItem)
                .ToList()
        };

        return JsonSerializer.Serialize(package, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    public static bool TryImportPackage(string json, out List<WaveformPresetRecord> presets, out string error)
    {
        presets = new List<WaveformPresetRecord>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "文件内容为空";
            return false;
        }

        WaveformPresetPackage? package;
        try
        {
            package = JsonSerializer.Deserialize<WaveformPresetPackage>(json);
        }
        catch (Exception ex)
        {
            error = $"文件解析失败: {ex.Message}";
            return false;
        }

        if (package == null)
        {
            error = "文件内容无效";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(package.Format) &&
            !string.Equals(package.Format, PackageFormat, StringComparison.OrdinalIgnoreCase))
        {
            error = $"不支持的文件格式: {package.Format}";
            return false;
        }

        if (package.Presets == null || package.Presets.Count == 0)
        {
            error = "文件中没有可导入的预设";
            return false;
        }

        var now = DateTime.UtcNow.ToString("o");
        var sortOrderOffset = 0;

        foreach (var item in package.Presets)
        {
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            var payload = item.WaveformData;
            if (string.IsNullOrWhiteSpace(payload) &&
                string.Equals(item.DataMode, "freq_pulse", StringComparison.OrdinalIgnoreCase) &&
                item.FrequencyHz.HasValue &&
                item.PulseWidthUs.HasValue)
            {
                payload = $"{item.FrequencyHz.Value},{item.PulseWidthUs.Value}";
            }

            if (!TryNormalizePayload(payload, out var normalizedPayload, out _, out _, out _))
            {
                error = $"预设 \"{item.Name}\" 的波形数据格式无效";
                presets.Clear();
                return false;
            }

            presets.Add(new WaveformPresetRecord
            {
                Name = item.Name.Trim(),
                Description = string.IsNullOrWhiteSpace(item.Description) ? null : item.Description.Trim(),
                Icon = string.IsNullOrWhiteSpace(item.Icon) ? "🌊" : item.Icon.Trim(),
                Channel = NormalizeChannel(item.Channel),
                WaveformData = normalizedPayload,
                Duration = Math.Clamp(item.Duration <= 0 ? 1000 : item.Duration, 1, 600000),
                Intensity = Math.Clamp(item.Intensity, 0, 100),
                IsBuiltIn = false,
                SortOrder = Math.Clamp(item.SortOrder + sortOrderOffset, 0, int.MaxValue),
                CreatedAt = now,
                UpdatedAt = now
            });

            sortOrderOffset++;
        }

        if (presets.Count == 0)
        {
            error = "文件中没有有效预设";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 将数据库预设解析为统一 WaveformData。
    /// 可直接交给 DeviceManager.SendWaveformAsync。
    /// </summary>
    public static WaveformData BuildWaveformData(WaveformPresetRecord preset)
    {
        var waveform = new WaveformData
        {
            Strength = Math.Clamp(preset.Intensity, 0, 100),
            Duration = Math.Clamp(preset.Duration <= 0 ? 1000 : preset.Duration, 1, 600000),
            Frequency = 100
        };

        if (TryNormalizePayload(preset.WaveformData, out var normalized, out var mode, out var frequency, out var pulseWidth))
        {
            if (mode == WaveformDataMode.Hex)
            {
                waveform.HexData = normalized;
            }
            else
            {
                waveform.Frequency = frequency;
                // 复用 HexData 通道作为 EMS 参数提示（frequency,pulse）
                waveform.HexData = $"{frequency},{pulseWidth}";
            }
        }
        else
        {
            // 保留原始数据，交由设备层兜底处理。
            waveform.HexData = preset.WaveformData;
        }

        return waveform;
    }

    /// <summary>
    /// 统一归一化用户输入的波形数据。
    /// 支持:
    /// 1) HEX 波形（单段/逗号多段/连续切片）
    /// 2) 频率脉宽（freq,pulse）
    /// </summary>
    public static bool TryNormalizePayload(
        string? rawInput,
        out string normalizedPayload,
        out WaveformDataMode mode,
        out int frequencyHz,
        out int pulseWidthUs)
    {
        normalizedPayload = string.Empty;
        mode = WaveformDataMode.Hex;
        frequencyHz = 0;
        pulseWidthUs = 0;

        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return false;
        }

        var input = rawInput.Trim();

        // 先尝试 freq,pulse
        if (TryParseFrequencyPulse(input, out frequencyHz, out pulseWidthUs))
        {
            mode = WaveformDataMode.FrequencyPulse;
            normalizedPayload = $"{frequencyHz},{pulseWidthUs}";
            return true;
        }

        // 再尝试 HEX
        var parts = input
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

        if (parts.Length == 0)
        {
            return false;
        }

        if (parts.Length > 1)
        {
            if (parts.Any(p => p.Length != 16 || !HexRegex.IsMatch(p)))
            {
                return false;
            }

            normalizedPayload = string.Join(",", parts.Select(p => p.ToUpperInvariant()));
            mode = WaveformDataMode.Hex;
            return true;
        }

        var single = parts[0];
        if (!HexRegex.IsMatch(single))
        {
            return false;
        }

        if (single.Length == 16)
        {
            normalizedPayload = single.ToUpperInvariant();
            mode = WaveformDataMode.Hex;
            return true;
        }

        if (single.Length > 16 && single.Length % 16 == 0)
        {
            var chunks = new List<string>();
            for (var i = 0; i < single.Length; i += 16)
            {
                chunks.Add(single.Substring(i, 16).ToUpperInvariant());
            }

            normalizedPayload = string.Join(",", chunks);
            mode = WaveformDataMode.Hex;
            return true;
        }

        return false;
    }

    public static bool TryParseFrequencyPulse(string? rawInput, out int frequencyHz, out int pulseWidthUs)
    {
        frequencyHz = 0;
        pulseWidthUs = 0;

        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return false;
        }

        var match = FrequencyPulseRegex.Match(rawInput);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups[1].Value, out var rawFreq) ||
            !int.TryParse(match.Groups[2].Value, out var rawPulse))
        {
            return false;
        }

        frequencyHz = Math.Clamp(rawFreq, 1, 100);
        pulseWidthUs = Math.Clamp(rawPulse, 0, 100);
        return true;
    }

    private static WaveformPresetPackageItem MapToPackageItem(WaveformPresetRecord preset)
    {
        var item = new WaveformPresetPackageItem
        {
            Name = preset.Name,
            Description = preset.Description,
            Icon = preset.Icon ?? "🌊",
            Channel = NormalizeChannel(preset.Channel),
            Duration = Math.Clamp(preset.Duration <= 0 ? 1000 : preset.Duration, 1, 600000),
            Intensity = Math.Clamp(preset.Intensity, 0, 100),
            SortOrder = preset.SortOrder,
            WaveformData = preset.WaveformData
        };

        if (TryNormalizePayload(preset.WaveformData, out var normalized, out var mode, out var frequency, out var pulse))
        {
            item.WaveformData = normalized;
            item.DataMode = mode == WaveformDataMode.Hex ? "hex" : "freq_pulse";
            if (mode == WaveformDataMode.FrequencyPulse)
            {
                item.FrequencyHz = frequency;
                item.PulseWidthUs = pulse;
            }
        }

        return item;
    }

    private static string NormalizeChannel(string? channel)
    {
        if (string.Equals(channel, "A", StringComparison.OrdinalIgnoreCase))
        {
            return "A";
        }

        if (string.Equals(channel, "B", StringComparison.OrdinalIgnoreCase))
        {
            return "B";
        }

        return "AB";
    }
}

public enum WaveformDataMode
{
    Hex,
    FrequencyPulse
}

public sealed class WaveformPresetPackage
{
    public string Format { get; set; } = "charging-panel.waveform-pack";
    public int Version { get; set; } = 1;
    public string ExportedAt { get; set; } = string.Empty;
    public List<WaveformPresetPackageItem> Presets { get; set; } = new();
}

public sealed class WaveformPresetPackageItem
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Icon { get; set; } = "🌊";
    public string Channel { get; set; } = "AB";
    public int Duration { get; set; } = 1000;
    public int Intensity { get; set; } = 50;
    public int SortOrder { get; set; } = 100;
    public string DataMode { get; set; } = "hex";
    public string WaveformData { get; set; } = string.Empty;
    public int? FrequencyHz { get; set; }
    public int? PulseWidthUs { get; set; }
}
