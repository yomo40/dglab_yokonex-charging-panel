using ChargingPanel.Vendor.DGLab.Protocol;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.DGLab;

/// <summary>
/// 郊狼 V2 协议黄金向量测试。波形字节取自官方 example.md。
/// </summary>
public sealed class CoyoteV2ProtocolTests
{
    // ---------- PWM_AB2 通道强度 ----------

    [Fact]
    public void EncodePwmAb2_AAmpereHighBZero_EncodesBitfield()
    {
        var frame = CoyoteV2Codec.EncodePwmAb2(2047, 0);

        Assert.Equal("00F83F", Convert.ToHexString(frame));
    }

    [Fact]
    public void EncodePwmAb2_AAmpereZeroBHigh_EncodesBitfield()
    {
        var frame = CoyoteV2Codec.EncodePwmAb2(0, 2047);

        Assert.Equal("FF0700", Convert.ToHexString(frame));
    }

    [Fact]
    public void EncodePwmAb2_ABBoth_EncodesBitfield()
    {
        var frame = CoyoteV2Codec.EncodePwmAb2(100, 200);

        Assert.Equal("C82003", Convert.ToHexString(frame));
    }

    [Fact]
    public void DecodePwmAb2_RoundTrip_PreservesChannelStrengths()
    {
        var frame = CoyoteV2Codec.EncodePwmAb2(1234, 567);
        var (rawA, rawB) = CoyoteV2Codec.DecodePwmAb2(frame);

        Assert.Equal(1234, rawA);
        Assert.Equal(567, rawB);
    }

    [Fact]
    public void EncodePwmAb2_OutOfRange_ClampsTo2047()
    {
        var frame = CoyoteV2Codec.EncodePwmAb2(3000, -5);

        Assert.Equal("00F83F", Convert.ToHexString(frame));
    }

    // ---------- PWM_x34 波形 ----------

    [Fact]
    public void EncodePwmWaveform_OfficialBreathingVectors_MatchesDocHex()
    {
        Assert.Equal("210100", Convert.ToHexString(CoyoteV2Codec.EncodePwmWaveform(1, 9, 0)));
        Assert.Equal("210102", Convert.ToHexString(CoyoteV2Codec.EncodePwmWaveform(1, 9, 4)));
        Assert.Equal("210104", Convert.ToHexString(CoyoteV2Codec.EncodePwmWaveform(1, 9, 8)));
        Assert.Equal("210106", Convert.ToHexString(CoyoteV2Codec.EncodePwmWaveform(1, 9, 12)));
        Assert.Equal("210108", Convert.ToHexString(CoyoteV2Codec.EncodePwmWaveform(1, 9, 16)));
        Assert.Equal("21010A", Convert.ToHexString(CoyoteV2Codec.EncodePwmWaveform(1, 9, 20)));
    }

    [Fact]
    public void EncodePwmWaveform_OfficialTideVectors_MatchesDocHex()
    {
        Assert.Equal("418101", Convert.ToHexString(CoyoteV2Codec.EncodePwmWaveform(1, 10, 3)));
        Assert.Equal("810103", Convert.ToHexString(CoyoteV2Codec.EncodePwmWaveform(1, 12, 6)));
        Assert.Equal("A10105", Convert.ToHexString(CoyoteV2Codec.EncodePwmWaveform(1, 13, 10)));
        Assert.Equal("41020A", Convert.ToHexString(CoyoteV2Codec.EncodePwmWaveform(1, 18, 20)));
        Assert.Equal("210300", Convert.ToHexString(CoyoteV2Codec.EncodePwmWaveform(1, 25, 0)));
    }

    [Fact]
    public void DecodePwmWaveform_RoundTrip_PreservesXYZ()
    {
        var frame = CoyoteV2Codec.EncodePwmWaveform(5, 135, 20);
        var (x, y, z) = CoyoteV2Codec.DecodePwmWaveform(frame);

        Assert.Equal(5, x);
        Assert.Equal(135, y);
        Assert.Equal(20, z);
    }

    // ---------- 频率换算 ----------

    [Fact]
    public void CalculateXYFromFrequency_10HzBeat_MatchesOfficialOneNine()
    {
        var (x, y) = CoyoteV2Codec.CalculateXYFromFrequency(10);

        Assert.Equal(1, x);
        Assert.Equal(9, y);
    }

    [Fact]
    public void CalculateXYFromFrequency_HighFrequency_StaysInRange()
    {
        var (x, y) = CoyoteV2Codec.CalculateXYFromFrequency(1000);

        Assert.InRange(x, 1, 31);
        Assert.InRange(y, 0, 1023);
        Assert.Equal(1000, x + y);
    }

    // ---------- 尺度换算 ----------

    [Fact]
    public void DisplayToRaw_OfficialScale_SevenPerDisplayStep()
    {
        Assert.Equal(0, CoyoteV2Codec.DisplayToRaw(0));
        Assert.Equal(7, CoyoteV2Codec.DisplayToRaw(1));
        Assert.Equal(700, CoyoteV2Codec.DisplayToRaw(100));
        Assert.Equal(1400, CoyoteV2Codec.DisplayToRaw(200));
        Assert.Equal(1400, CoyoteV2Codec.DisplayToRaw(300));
    }

    [Fact]
    public void RawToDisplay_OfficialScale_DividesBySeven()
    {
        Assert.Equal(0, CoyoteV2Codec.RawToDisplay(0));
        Assert.Equal(100, CoyoteV2Codec.RawToDisplay(700));
        Assert.Equal(200, CoyoteV2Codec.RawToDisplay(1400));
        Assert.Equal(200, CoyoteV2Codec.RawToDisplay(2047));
    }

    // ---------- 电量 ----------

    [Fact]
    public void TryDecodeBattery_ValidByte_ReturnsPercent()
    {
        Assert.True(CoyoteV2Codec.TryDecodeBattery([80], out var percent));
        Assert.Equal(80, percent);
    }

    [Fact]
    public void TryDecodeBattery_EmptyFrame_ReturnsFalse()
    {
        Assert.False(CoyoteV2Codec.TryDecodeBattery(ReadOnlySpan<byte>.Empty, out _));
    }
}