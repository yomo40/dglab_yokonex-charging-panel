using ChargingPanel.Core.Data;
using ChargingPanel.Core.Protocols;
using Xunit;

namespace ChargingPanel.Core.Tests;

public sealed class WaveformPresetExchangeServiceTests
{
    [Fact]
    public void TryNormalizePayload_CommaOnly_ShouldReturnFalse()
    {
        var ok = WaveformPresetExchangeService.TryNormalizePayload(
            ",",
            out var normalized,
            out _,
            out _,
            out _);

        Assert.False(ok);
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public void TryNormalizePayload_FrequencyPulse_ShouldNormalize()
    {
        var ok = WaveformPresetExchangeService.TryNormalizePayload(
            "50,20",
            out var normalized,
            out var mode,
            out var frequency,
            out var pulseWidth);

        Assert.True(ok);
        Assert.Equal(WaveformDataMode.FrequencyPulse, mode);
        Assert.Equal("50,20", normalized);
        Assert.Equal(50, frequency);
        Assert.Equal(20, pulseWidth);
    }

    [Fact]
    public void BuildWaveformData_FromFrequencyPulsePreset_ShouldSetFrequencyAndHint()
    {
        var preset = new WaveformPresetRecord
        {
            Name = "custom",
            Channel = "AB",
            WaveformData = "50,20",
            Duration = 1234,
            Intensity = 70
        };

        var waveform = WaveformPresetExchangeService.BuildWaveformData(preset);

        Assert.Equal(70, waveform.Strength);
        Assert.Equal(1234, waveform.Duration);
        Assert.Equal(50, waveform.Frequency);
        Assert.Equal("50,20", waveform.HexData);
    }

    [Fact]
    public void ExportAndImportPackage_ShouldRoundTripPresets()
    {
        var source = new[]
        {
            new WaveformPresetRecord
            {
                Name = "hex",
                Channel = "A",
                WaveformData = "0011223344556677",
                Duration = 1000,
                Intensity = 50,
                IsBuiltIn = false
            },
            new WaveformPresetRecord
            {
                Name = "freq",
                Channel = "B",
                WaveformData = "45,18",
                Duration = 1500,
                Intensity = 60,
                IsBuiltIn = false
            }
        };

        var json = WaveformPresetExchangeService.ExportPackage(source, includeBuiltIn: false);
        var ok = WaveformPresetExchangeService.TryImportPackage(json, out var imported, out var error);

        Assert.True(ok, error);
        Assert.Equal(2, imported.Count);
        Assert.Contains(imported, p => p.Name == "hex" && p.WaveformData == "0011223344556677");
        Assert.Contains(imported, p => p.Name == "freq" && p.WaveformData == "45,18");
    }
}
