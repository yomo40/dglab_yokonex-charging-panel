using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Devices.Sessions;
using ChargingPanel.DeviceIntegration.Tests.Fixtures.DGLab;
using ChargingPanel.Vendor.DGLab.Protocol;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.DGLab;

/// <summary>
/// 郊狼 V2 运行时集成测试：发现、强度换算、波形特性映射与安全停止。
/// </summary>
public sealed class CoyoteV2RuntimeTests
{
    [Fact]
    public async Task ConnectAsync_BleV2_MatchesByNameAndReachesReady()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.NamedBle("D-LAB ESTIM01", serviceUuids: "0000180b-0000-1000-8000-00805f9b34fb"));

        Assert.Equal(SessionState.Ready, harness.Sessions.GetState(instance.Id));
        Assert.Equal("dglab.coyote.v2.ble", instance.ProfileId.Value);
    }

    [Fact]
    public async Task DispatchIntensity_DisplayScale_EncodesRawPwmAb2()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.NamedBle("D-LAB ESTIM01", serviceUuids: "0000180b-0000-1000-8000-00805f9b34fb"));
        var lease = harness.Broker.GetLease(instance.Endpoints.Single().Id);
        lease.ClearWrites();

        var command = harness.BuildCommand(
            instance,
            new CapabilityId("intensity.output"),
            "set.level",
            "{\"channelA\":100,\"channelB\":50}");
        var result = await harness.Dispatcher.DispatchAsync(command, CancellationToken.None);

        Assert.Equal(ChargingPanel.Contracts.Commands.DeviceCommandStatus.AcceptedByTransport, result.Status);
        var write = Assert.Single(lease.Writes);
        Assert.Equal(DGLabUuids.V2PwmAb2, write.Route);
        var (rawA, rawB) = CoyoteV2Codec.DecodePwmAb2(write.Binary);
        Assert.Equal(700, rawA);
        Assert.Equal(350, rawB);
    }

    [Fact]
    public async Task DispatchWaveform_SendsAtoB34AndBtoA34()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.NamedBle("D-LAB ESTIM01", serviceUuids: "0000180b-0000-1000-8000-00805f9b34fb"));
        var lease = harness.Broker.GetLease(instance.Endpoints.Single().Id);
        lease.ClearWrites();

        // A 通道频率 10、强度 20 → X=1,Y=9,Z=4；B 通道未指定 → 零帧。
        var command = harness.BuildCommand(
            instance,
            new CapabilityId("waveform.stream"),
            "start.frame",
            "{\"channelA\":{\"frequency\":10,\"strength\":20},\"repeatCount\":3}");
        var result = await harness.Dispatcher.DispatchAsync(command, CancellationToken.None);

        Assert.Equal(ChargingPanel.Contracts.Commands.DeviceCommandStatus.AcceptedByTransport, result.Status);
        await WaitUntilAsync(() => lease.Writes.Any(w => w.Route == DGLabUuids.V2PwmB34), 2000);
        Assert.Contains(lease.Writes, w => w.Route == DGLabUuids.V2PwmB34);
        Assert.Contains(lease.Writes, w => w.Route == DGLabUuids.V2PwmA34);
    }

    [Fact]
    public async Task SafeStop_SendsZeroPwmAb2_AndStopsWaveform()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.NamedBle("D-LAB ESTIM01", serviceUuids: "0000180b-0000-1000-8000-00805f9b34fb"));
        var lease = harness.Broker.GetLease(instance.Endpoints.Single().Id);
        lease.ClearWrites();

        var waveCommand = harness.BuildCommand(
            instance,
            new CapabilityId("waveform.stream"),
            "start.frame",
            "{\"channelA\":{\"frequency\":10,\"strength\":20},\"repeatCount\":10}");
        await harness.Dispatcher.DispatchAsync(waveCommand, CancellationToken.None);

        await harness.Safety.EmergencyStopAllAsync("runtime_test", CancellationToken.None);

        var zeroWrite = lease.Writes.Last(w => w.Route == DGLabUuids.V2PwmAb2);
        var (rawA, rawB) = CoyoteV2Codec.DecodePwmAb2(zeroWrite.Binary);
        Assert.Equal(0, rawA);
        Assert.Equal(0, rawB);

        var writesAfterStop = lease.Writes.Count;
        await Task.Delay(300);
        Assert.Equal(writesAfterStop, lease.Writes.Count);
    }

    [Fact]
    public async Task SessionRelease_AfterSafeStop_NoBackgroundWritesContinue()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.NamedBle("D-LAB ESTIM01", serviceUuids: "0000180b-0000-1000-8000-00805f9b34fb"));
        var lease = harness.Broker.GetLease(instance.Endpoints.Single().Id);
        lease.ClearWrites();

        var waveCommand = harness.BuildCommand(
            instance,
            new CapabilityId("waveform.stream"),
            "start.frame",
            "{\"channelA\":{\"frequency\":10,\"strength\":20},\"repeatCount\":20}");
        await harness.Dispatcher.DispatchAsync(waveCommand, CancellationToken.None);

        await harness.Safety.EmergencyStopAllAsync("runtime_test", CancellationToken.None);
        await harness.Sessions.DisconnectAsync(instance.Id, userInitiated: true, CancellationToken.None);

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