using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.DeviceIntegration.Tests.Fixtures.DGLab;
using ChargingPanel.Vendor.DGLab.Drivers;
using ChargingPanel.Vendor.DGLab.Protocol;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.DGLab;

/// <summary>
/// 负鼠 V1 协议黄金向量与驱动运行时行为测试。
/// 振动波形任一字节约束、0xFF 不修改语义取自官方文档。
/// </summary>
public sealed class OpossumV1ProtocolTests
{
    [Fact]
    public void EncodeLampAndButtonReport_EnableDisable()
    {
        Assert.Equal("500101", Convert.ToHexString(OpossumV1Codec.EncodeLampAndButtonReport(0x01, buttonReportEnabled: true)));
        Assert.Equal("500100", Convert.ToHexString(OpossumV1Codec.EncodeLampAndButtonReport(0x01, buttonReportEnabled: false)));
    }

    [Fact]
    public void EncodeVibrationWaveform_ValidChannels_EncodesBothGroups()
    {
        var frame = OpossumV1Codec.EncodeVibrationWaveform([10, 20, 30, 40], [50, 60, 70, 80]);

        Assert.Equal(20, frame.Length);
        Assert.Equal(0xB0, frame[0]);
        Assert.Equal("B0000000000000000A141E2800000000323C4650", Convert.ToHexString(frame));
    }

    [Fact]
    public void EncodeVibrationWaveform_InvalidByte_DropsWholeChannelGroup()
    {
        // A 通道第二个字节 200 超范围，A 整组弃用；B 通道合法保留。
        var frame = OpossumV1Codec.EncodeVibrationWaveform([10, 200, 30, 40], [50, 60, 70, 80]);

        Assert.All(frame.AsSpan(8, 4).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal([50, 60, 70, 80], frame.AsSpan(16, 4).ToArray());
    }

    [Fact]
    public void IsValidWaveform_BoundaryValues()
    {
        Assert.True(OpossumV1Codec.IsValidWaveform([0, 50, 100, 100]));
        Assert.False(OpossumV1Codec.IsValidWaveform([0, 101, 50, 50]));
    }

    [Fact]
    public void EncodeIntensity_NoModifySemantics()
    {
        Assert.Equal("B3A0FF", Convert.ToHexString(OpossumV1Codec.EncodeIntensity(160, null)));
        Assert.Equal("B3FFC8", Convert.ToHexString(OpossumV1Codec.EncodeIntensity(null, 200)));
        Assert.Equal("B30A14", Convert.ToHexString(OpossumV1Codec.EncodeIntensity(10, 20)));
    }

    [Fact]
    public void EncodeScreenSync_LaysOutFixedSegmentAndStrengths()
    {
        var frame = OpossumV1Codec.EncodeScreenSync(10, 20);

        Assert.Equal(24, frame.Length);
        Assert.Equal(0xB2, frame[0]);
        Assert.Equal(0xFF, frame[1]);
        Assert.Equal(0xFF, frame[2]);
        Assert.Equal(0x00, frame[3]);
        Assert.Equal(0xFF, frame[4]);
        Assert.Equal(10, frame[22]);
        Assert.Equal(20, frame[23]);
    }

    [Fact]
    public void TryDecodeB3_ValidFrame_ReturnsChannelStrengths()
    {
        var ok = OpossumV1Codec.TryDecodeB3([0xB3, 0x0A, 0x14], out var a, out var b);

        Assert.True(ok);
        Assert.Equal(10, a);
        Assert.Equal(20, b);
    }

    [Fact]
    public void TryDecodeD0_ValidFrame_ReturnsSequenceAndButtonState()
    {
        var ok = OpossumV1Codec.TryDecodeD0([0xD0, 0x05, 0x01, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], out var sequence, out var buttonState);

        Assert.True(ok);
        Assert.Equal(5, sequence);
        Assert.Equal(1, buttonState);
    }

    [Fact]
    public void TryDecodeBattery_ValidByte_ReturnsPercent()
    {
        Assert.True(OpossumV1Codec.TryDecodeBattery([90], out var percent));
        Assert.Equal(90, percent);
    }

    // ---------- 驱动运行时行为 ----------

    [Fact]
    public async Task ConnectAsync_BleOpossum_ReachesReady()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.NamedBle("47L127000"));

        Assert.Equal(ChargingPanel.Core.Devices.Sessions.SessionState.Ready, harness.Sessions.GetState(instance.Id));
    }

    [Fact]
    public async Task B3Notify_QueuesB2DisplaySync()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.NamedBle("47L127000"));
        var lease = harness.Broker.GetLease(instance.Endpoints.Single().Id);
        lease.ClearWrites();

        var driver = harness.GetDriver<OpossumV1Driver>(instance);
        driver.OnNotify(new byte[] { 0xB3, 0x0A, 0x14 });

        await WaitUntilAsync(() => lease.Writes.Any(w => w.Binary.Length == 24), 2000);
        var b2 = lease.Writes.Single(w => w.Binary.Length == 24);
        Assert.Equal(0xB2, b2.Binary[0]);
        Assert.Equal(10, b2.Binary[22]);
        Assert.Equal(20, b2.Binary[23]);
    }

    [Fact]
    public async Task SafeStop_ZeroesIntensityAndDisplay_CancelsWaveformFrames()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.NamedBle("47L127000"));
        var lease = harness.Broker.GetLease(instance.Endpoints.Single().Id);
        lease.ClearWrites();

        var waveCommand = harness.BuildCommand(
            instance,
            new CapabilityId("waveform.stream"),
            "start.frame",
            "{\"channelA\":[10,20,30,40],\"channelB\":[50,60,70,80],\"repeatCount\":10}");
        await harness.Dispatcher.DispatchAsync(waveCommand, CancellationToken.None);

        await harness.Safety.EmergencyStopAllAsync("runtime_test", CancellationToken.None);

        Assert.Contains(lease.Writes, w => w.Binary[0] == 0xB3 && w.Binary[1] == 0 && w.Binary[2] == 0);
        Assert.Contains(lease.Writes, w => w.Binary[0] == 0xB2 && w.Binary[22] == 0 && w.Binary[23] == 0);

        var writesAfterStop = lease.Writes.Count;
        await Task.Delay(300);
        Assert.Equal(writesAfterStop, lease.Writes.Count);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("条件未在期限内满足。");
    }
}