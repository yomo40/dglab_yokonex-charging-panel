using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Telemetry;
using ChargingPanel.DeviceIntegration.Tests.Fixtures.DGLab;
using ChargingPanel.Vendor.DGLab.Drivers;
using ChargingPanel.Vendor.DGLab.Protocol;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.DGLab;

/// <summary>
/// 灵猫 V1 协议黄金向量与驱动运行时行为测试。气压换算使用官方算法。
/// </summary>
public sealed class CivetV1ProtocolTests
{
    [Fact]
    public void EncodePressureReport_On_ProducesD0Start()
    {
        var frame = CivetV1Codec.EncodePressureReport(enabled: true);

        Assert.Equal(17, frame.Length);
        Assert.Equal(0x50, frame[0]);
        Assert.Equal(0x01, frame[1]);
        Assert.Equal(0xD0, frame[2]);
        Assert.All(frame[3..], value => Assert.Equal(0, value));
    }

    [Fact]
    public void EncodePressureReport_Off_ProducesZeroStop()
    {
        var frame = CivetV1Codec.EncodePressureReport(enabled: false);

        Assert.Equal(0x50, frame[0]);
        Assert.Equal(0x00, frame[2]);
    }

    [Fact]
    public void EncodePressureZero_MatchesDocHex()
    {
        var frame = CivetV1Codec.EncodePressureZero();

        Assert.Equal("66000000000000000000000002", Convert.ToHexString(frame));
    }

    [Fact]
    public void EncodeScreenRotate_Direction3_MatchesDocHex()
    {
        var frame = CivetV1Codec.EncodeScreenRotate(3);

        Assert.Equal("66000000000000000000030000", Convert.ToHexString(frame));
    }

    [Fact]
    public void TryDecodeD0Pressure_OfficialVector1603_Returns7_9Kpa()
    {
        var frame = new byte[17];
        frame[0] = 0xD0;
        frame[8] = 0x16;
        frame[9] = 0x03;

        var ok = CivetV1Codec.TryDecodeD0Pressure(frame, out var pressureKpa);

        Assert.True(ok);
        Assert.Equal(7.9, pressureKpa, precision: 2);
    }

    [Fact]
    public void TryDecodeD0Pressure_OfficialVectorA806_Returns17_04Kpa()
    {
        var frame = new byte[17];
        frame[0] = 0xD0;
        frame[8] = 0xA8;
        frame[9] = 0x06;

        var ok = CivetV1Codec.TryDecodeD0Pressure(frame, out var pressureKpa);

        Assert.True(ok);
        Assert.Equal(17.04, pressureKpa, precision: 2);
    }

    [Fact]
    public void TryDecodeD0Pressure_NotD0_ReturnsFalse()
    {
        var frame = new byte[17];
        frame[0] = 0x51;

        Assert.False(CivetV1Codec.TryDecodeD0Pressure(frame, out _));
    }

    [Fact]
    public void TryDecodeBattery_ValidByte_ReturnsPercent()
    {
        Assert.True(CivetV1Codec.TryDecodeBattery([80], out var percent));
        Assert.Equal(80, percent);
    }

    // ---------- 驱动运行时行为 ----------

    [Fact]
    public async Task ConnectAsync_BleCivet_ReachesReady()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.NamedBle("47L124000"));

        Assert.Equal(ChargingPanel.Core.Devices.Sessions.SessionState.Ready, harness.Sessions.GetState(instance.Id));
    }

    [Fact]
    public async Task PressureReportingStart_EncodesD0ActiveReport()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.NamedBle("47L124000"));
        var lease = harness.Broker.GetLease(instance.Endpoints.Single().Id);
        lease.ClearWrites();

        var command = harness.BuildCommand(instance, new CapabilityId("pressure.reporting"), "start", "{}");
        var result = await harness.Dispatcher.DispatchAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.AcceptedByTransport, result.Status);
        var write = Assert.Single(lease.Writes);
        Assert.Equal(0xD0, write.Binary[2]);
    }

    [Fact]
    public async Task SafeStop_WhenReportingEnabled_StopsReportingFirst()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.NamedBle("47L124000"));
        var lease = harness.Broker.GetLease(instance.Endpoints.Single().Id);
        var startCommand = harness.BuildCommand(instance, new CapabilityId("pressure.reporting"), "start", "{}");
        await harness.Dispatcher.DispatchAsync(startCommand, CancellationToken.None);
        lease.ClearWrites();

        var stop = await harness.Safety.EmergencyStopAllAsync("runtime_test", CancellationToken.None);

        Assert.True(stop.DriverSafeStopAttempted);
        var last = lease.Writes.Last();
        Assert.Equal(0x50, last.Binary[0]);
        Assert.Equal(0x00, last.Binary[2]);
    }

    [Fact]
    public void OnNotify_D0Pressure_ProducesPressureTelemetry()
    {
        var driver = new CivetV1Driver(new FakePluginContext());
        driver.Configure(DeviceInstanceId.New(), 1);

        var frame = new byte[17];
        frame[0] = 0xD0;
        frame[8] = 0x16;
        frame[9] = 0x03;
        driver.OnNotify(frame);

        var envelope = ReadSingleTelemetry(driver);
        Assert.Equal("pressure.status", envelope.CapabilityId.Value);
        Assert.Equal(7.9, envelope.Payload.GetProperty("pressureKpa").GetDouble(), precision: 2);
        Assert.Equal(1, envelope.SessionEpoch);
    }

    private static DeviceTelemetryEnvelope ReadSingleTelemetry(CivetV1Driver driver)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var enumerator = driver.ReadTelemetryAsync(cts.Token).GetAsyncEnumerator();
        var move = enumerator.MoveNextAsync().AsTask();
        if (!move.Wait(TimeSpan.FromSeconds(1)))
        {
            throw new TimeoutException("未收到遥测。");
        }

        return enumerator.Current;
    }
}