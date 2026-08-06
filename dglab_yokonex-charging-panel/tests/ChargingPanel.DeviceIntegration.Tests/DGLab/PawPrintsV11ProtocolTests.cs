using ChargingPanel.Vendor.DGLab.Protocol;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.DGLab;

/// <summary>
/// 爪印 V1.1 协议黄金向量测试。十六进制取自官方文档。
/// </summary>
public sealed class PawPrintsV11ProtocolTests
{
    [Fact]
    public void EncodeNoTriggerMode_ProducesSafeModeFrame()
    {
        var frame = PawPrintsV11Codec.EncodeNoTriggerMode();

        Assert.Equal("5001000000000000000000000000000000", Convert.ToHexString(frame));
    }

    [Fact]
    public void EncodeRandomReaction_MatchesDocHex()
    {
        var frame = PawPrintsV11Codec.EncodeRandomReaction(
            lampColor: 0x01,
            eventId: 0x07,
            greenTimeMinSeconds: 30,
            greenTimeMaxSeconds: 50,
            reactionTimeSeconds: 10,
            paramIncrease: 20,
            paramIncreaseSpeed: 20,
            paramDecrease: 50,
            paramDeclineSpeed: 20);

        Assert.Equal("50010307001E0032000A14143214000000", Convert.ToHexString(frame));
    }

    [Fact]
    public void EncodeProbability_MatchesDocHex()
    {
        var frame = PawPrintsV11Codec.EncodeProbability(
            0x01,
            [(0x05, 0x3C), (0x02, 0x50)],
            cooldownSeconds: 600);

        Assert.Equal("500104053C025000000000000000000258", Convert.ToHexString(frame));
    }

    [Fact]
    public void EncodeProbability_MoreThanSixEvents_Rejects()
    {
        Assert.Throws<ArgumentException>(() => PawPrintsV11Codec.EncodeProbability(
            0x01,
            [(0x01, 10), (0x02, 10), (0x03, 10), (0x04, 10), (0x05, 10), (0x06, 10), (0x07, 10)],
            cooldownSeconds: 10));
    }

    [Fact]
    public void EncodeExternalVoltage_LaysOutFieldsStructurally()
    {
        var frame = PawPrintsV11Codec.EncodeExternalVoltage(
            lampColor: 0x01,
            eventId: 0x07,
            pullup: 0x01,
            voltageMin: 130,
            voltageMax: 180,
            paramMappingRange: 80);

        Assert.Equal(17, frame.Length);
        Assert.Equal(0x50, frame[0]);
        Assert.Equal(0x01, frame[1]);
        Assert.Equal(PawPrintsV11Codec.TriggerModeExternalVoltage, frame[2]);
        Assert.Equal(0x07, frame[3]);
        Assert.Equal(0x01, frame[4]);
        Assert.Equal(130, frame[5]);
        Assert.Equal(180, frame[6]);
        Assert.Equal(80, frame[7]);
        Assert.All(frame[8..], value => Assert.Equal(0, value));
    }

    [Fact]
    public void EncodePressAndAcceleration_LaysOutFieldsStructurally()
    {
        var frame = PawPrintsV11Codec.EncodePressAndAcceleration(
            lampColor: 0x01,
            pressEventId: 20,
            settingsByte: 0b10011110,
            pressParamIncreaseSpeed: 20,
            pressParamDecrease: 50,
            pressParamDeclineSpeed: 20,
            pressParamIncrease: 20,
            accelEventId: 10,
            accelThreshold6: [0x01, 0x00, 0x64, 0x00, 0x00, 0x00],
            accelParamMapping: 100);

        Assert.Equal(0x50, frame[0]);
        Assert.Equal(0x01, frame[1]);
        Assert.Equal(PawPrintsV11Codec.TriggerModePressAndAcceleration, frame[2]);
        Assert.Equal(20, frame[3]);
        Assert.Equal(0b10011110, frame[4]);
        Assert.Equal(10, frame[9]);
        Assert.Equal(100, frame[16]);
    }

    [Fact]
    public void EncodeD0Passthrough_Produces17ByteFrame()
    {
        var frame = PawPrintsV11Codec.EncodeD0Passthrough();

        Assert.Equal(17, frame.Length);
        Assert.Equal(0x50, frame[0]);
        Assert.Equal(0xD0, frame[2]);
    }

    [Fact]
    public void EncodeParamResetAndAutoDetect_SingleByteCommands()
    {
        Assert.Equal("5F", Convert.ToHexString(PawPrintsV11Codec.EncodeParamReset()));
        Assert.Equal("60", Convert.ToHexString(PawPrintsV11Codec.EncodeAutoDetect()));
    }

    [Fact]
    public void EncodeShoulderLamp_SolidAndBlink()
    {
        Assert.Equal("7005", Convert.ToHexString(PawPrintsV11Codec.EncodeShoulderLampSolid(0x05)));
        Assert.Equal("70010203", Convert.ToHexString(PawPrintsV11Codec.EncodeShoulderLampBlink(0x01, 0x02, 0x03)));
    }

    [Fact]
    public void TryDecode51_ValidFrame_ReturnsLampDeviceTypeBattery()
    {
        var ok = PawPrintsV11Codec.TryDecode51([0x51, 0x01, 0x03, 0x4B], out var lamp, out var deviceType, out var battery);

        Assert.True(ok);
        Assert.Equal(0x01, lamp);
        Assert.Equal(0x03, deviceType);
        Assert.Equal(75, battery);
    }

    [Fact]
    public void TryDecode5A_ValidFrame_ReturnsEventAndParam()
    {
        var ok = PawPrintsV11Codec.TryDecode5A([0x5A, 0x01, 0x07, 0x64], out var lamp, out var eventId, out var param);

        Assert.True(ok);
        Assert.Equal(7, eventId);
        Assert.Equal(100, param);
    }

    [Fact]
    public void TryDecode5B_ValidFrame_ReturnsEvent()
    {
        var ok = PawPrintsV11Codec.TryDecode5B([0x5B, 0x02, 0x07], out var lamp, out var eventId);

        Assert.True(ok);
        Assert.Equal(7, eventId);
    }

    [Fact]
    public void TryDecode5C_ValidFrame_ReturnsEventAndParam()
    {
        var ok = PawPrintsV11Codec.TryDecode5C([0x5C, 0x01, 0x0A, 0x20], out var lamp, out var eventId, out var param);

        Assert.True(ok);
        Assert.Equal(10, eventId);
        Assert.Equal(32, param);
    }

    [Fact]
    public void TryDecodeD0_ValidFrame_ReturnsPhysicalDataSet()
    {
        var ok = PawPrintsV11Codec.TryDecodeD0(
            [0xD0, 0x01, 0x05, 0x01, 0x64, 0x0A, 0x14, 0x1E, 0x32],
            out var lamp,
            out var sequence,
            out var press,
            out var accel,
            out var x,
            out var y,
            out var z,
            out var voltage);

        Assert.True(ok);
        Assert.Equal(5, sequence);
        Assert.Equal(1, press);
        Assert.Equal(100, accel);
        Assert.Equal(10, x);
        Assert.Equal(20, y);
        Assert.Equal(30, z);
        Assert.Equal(50, voltage);
    }

    [Fact]
    public void TryDecodeF1_ValidFrame_ReturnsAngleRanges()
    {
        var ok = PawPrintsV11Codec.TryDecodeF1(
            [0xF1, 0x61, 0x00, 0x0A, 0x00, 0x14, 0x00, 0x1E, 0x00, 0x28, 0x00, 0x32, 0x00, 0x3C],
            out var xMin,
            out var xMax,
            out var yMin,
            out var yMax,
            out var zMin,
            out var zMax);

        Assert.True(ok);
        Assert.Equal(10, xMin);
        Assert.Equal(20, xMax);
        Assert.Equal(30, yMin);
        Assert.Equal(40, yMax);
        Assert.Equal(50, zMin);
        Assert.Equal(60, zMax);
    }
}