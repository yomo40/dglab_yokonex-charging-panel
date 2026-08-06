using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Devices.Sessions;
using ChargingPanel.DeviceIntegration.Tests.Fixtures.DGLab;
using ChargingPanel.Vendor.DGLab.Drivers;
using ChargingPanel.Vendor.DGLab.Protocol;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.DGLab;

/// <summary>
/// 郊狼 V3 运行时集成测试：Catalog→Registry→Session→Dispatcher→Driver→Telemetry 全链路。
/// </summary>
public sealed class CoyoteV3RuntimeTests
{
    [Fact]
    public async Task ConnectAsync_BleV3_HandshakeWritesBfBeforeReady()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.CoyoteV3Ble());

        var lease = harness.Broker.GetLease(instance.Endpoints.Single().Id);
        var firstWrite = lease.Writes.First();
        Assert.Equal("BFC8C880808080", firstWrite.Hex);
        Assert.Equal(SessionState.Ready, harness.Sessions.GetState(instance.Id));
        Assert.Equal(DeviceCommandStatus.AcceptedByTransport, new DeviceCommandResult(CommandId.New(), DeviceCommandStatus.AcceptedByTransport).Status);
    }

    [Fact]
    public async Task DispatchIntensity_Absolute_EncodesB0AndWaitsForMatchingB1()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.CoyoteV3Ble());
        var lease = harness.Broker.GetLease(instance.Endpoints.Single().Id);
        lease.ClearWrites();

        var command = harness.BuildCommand(
            instance,
            new CapabilityId("intensity.output"),
            "set.level",
            "{\"channelA\":50,\"channelB\":60}");
        var dispatch = harness.Dispatcher.DispatchAsync(command, CancellationToken.None);

        await WaitUntilAsync(() => lease.Writes.Any(w => w.Route.Equals(DGLabUuids.V3Write, StringComparison.OrdinalIgnoreCase)), 2000);
        var b0 = lease.Writes.Last(w => w.Route.Equals(DGLabUuids.V3Write, StringComparison.OrdinalIgnoreCase));
        var bytes = b0.Binary;
        Assert.Equal(0xB0, bytes[0]);
        // 模式 = 0b1111（A/B 均为绝对变化）。
        Assert.Equal(0x0F, bytes[1] & 0x0F);
        Assert.Equal(50, bytes[2]);
        Assert.Equal(60, bytes[3]);
        var sequence = (bytes[1] >> 4) & 0x0F;
        Assert.NotEqual(0, sequence);

        var driver = harness.GetDriver<CoyoteV3BleDriver>(instance);
        driver.OnNotify(new byte[] { 0xB1, (byte)sequence, 50, 60 });

        var result = await dispatch.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(DeviceCommandStatus.Executed, result.Status);
    }

    [Fact]
    public async Task EmergencyStop_ZeroesBothChannelsAndClearsWaveformQueue()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.CoyoteV3Ble());
        var lease = harness.Broker.GetLease(instance.Endpoints.Single().Id);
        lease.ClearWrites();

        var driver = harness.GetDriver<CoyoteV3BleDriver>(instance);
        var waveCommand = harness.BuildCommand(
            instance,
            new CapabilityId("waveform.stream"),
            "start.frame",
            "{\"channelA\":{\"frequencies\":[10,10,10,10],\"strengths\":[20,20,20,20]},\"repeatCount\":10}");
        await harness.Dispatcher.DispatchAsync(waveCommand, CancellationToken.None);
        Assert.True(driver.HasPendingWaveformFrames);

        var stop = await harness.Safety.EmergencyStopAllAsync("runtime_test", CancellationToken.None);

        Assert.True(stop.DriverSafeStopAttempted);
        Assert.True(harness.Safety.Current.IsLatched);
        Assert.False(driver.HasPendingWaveformFrames);
        var lastWrite = lease.Writes.Last(w => w.Route.Equals(DGLabUuids.V3Write, StringComparison.OrdinalIgnoreCase));
        // 归零帧：B0 + 0x0F(绝对) + A=0 + B=0 + 双通道禁用波形。
        Assert.Equal("B00F000000000000000000650000000000000065", lastWrite.Hex);
    }

    [Fact]
    public async Task StaleSessionEpochCommand_IsSuperseded()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.CoyoteV3Ble());
        var currentEpoch = harness.Sessions.GetSession(instance.Id)!.SessionEpoch;

        using var payload = System.Text.Json.JsonDocument.Parse("{\"channelA\":10}");
        var stale = new DeviceCommandEnvelope(
            CommandId.New(),
            instance.Id,
            new CapabilityId("intensity.output"),
            "set.level",
            payload.RootElement.Clone(),
            new CommandSource(CommandSourceKind.UserInterface, "test"),
            CommandDelivery.Idempotent,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5),
            harness.Safety.Current.SafetyEpoch,
            currentEpoch - 1);

        var result = await harness.Dispatcher.DispatchAsync(stale, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Superseded, result.Status);
        Assert.Equal("session.epoch_mismatch", result.ReasonCode);
    }

    [Fact]
    public async Task Reconnect_RewritesBfBeforeAnyNormalCommand()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.CoyoteV3Ble());

        // 首次连接：写入 BF，然后执行一条强度命令产生 B0 写入。
        var firstLease = harness.Broker.GetLease(instance.Endpoints.Single().Id);
        firstLease.ClearWrites();
        var firstCommand = harness.BuildCommand(
            instance, new CapabilityId("intensity.output"), "set.level", "{\"channelA\":30}");
        var firstDispatch = harness.Dispatcher.DispatchAsync(firstCommand, CancellationToken.None);
        await WaitUntilAsync(() => firstLease.Writes.Any(w => w.Binary.Length == 20), 2000);
        var firstB0 = firstLease.Writes.Last(w => w.Binary.Length == 20);
        Assert.Equal(0xB0, firstB0.Binary[0]);

        // 断开并重连。
        await harness.Sessions.DisconnectAsync(instance.Id, userInitiated: true, CancellationToken.None);
        var reconnected = await harness.TryConnectInstanceAsync(instance, CancellationToken.None);
        Assert.True(reconnected);

        var secondLease = harness.Broker.GetLease(instance.Endpoints.Single().Id);
        // 重连后第一个写入必须是 BF 安全配置，绝不重放上次强度。
        var firstAfterReconnect = secondLease.Writes.First();
        Assert.Equal("BFC8C880808080", firstAfterReconnect.Hex);
        Assert.DoesNotContain(secondLease.Writes, w => w.Binary.Length == 20 && w.Binary[0] == 0xB0);
    }

    [Fact]
    public async Task WsBleSwitch_DoesNotRestoreOutput()
    {
        using var harness = new DGLabRuntimeHarness();

        // BLE 连接并输出强度。
        var bleInstance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.CoyoteV3Ble());
        var bleLease = harness.Broker.GetLease(bleInstance.Endpoints.Single().Id);
        bleLease.ClearWrites();
        var command = harness.BuildCommand(
            bleInstance, new CapabilityId("intensity.output"), "set.level", "{\"channelA\":40}");
        var dispatch = harness.Dispatcher.DispatchAsync(command, CancellationToken.None);
        await WaitUntilAsync(() => bleLease.Writes.Any(w => w.Binary.Length == 20), 2000);
        Assert.Contains(bleLease.Writes, w => w.Binary.Length == 20);
        await harness.Sessions.DisconnectAsync(bleInstance.Id, userInitiated: true, CancellationToken.None);

        // 切换到 WS 端点。
        var wsInstance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.CoyoteV3WebSocket());
        var wsLease = harness.Broker.GetLease(wsInstance.Endpoints.Single().Id);

        // WS 连接只做绑定，绝不恢复 BLE 的强度输出。
        Assert.DoesNotContain(wsLease.Writes, w => w.Route == "strength");
        Assert.DoesNotContain(wsLease.Writes, w => w.Route == "waveform");
        Assert.Contains(wsLease.Writes, w => w.Route == "bind");
    }

    [Fact]
    public async Task SafeStop_AfterSessionRelease_NoBackgroundWritesContinue()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.CoyoteV3Ble());
        var lease = harness.Broker.GetLease(instance.Endpoints.Single().Id);
        lease.ClearWrites();

        var waveCommand = harness.BuildCommand(
            instance,
            new CapabilityId("waveform.stream"),
            "start.frame",
            "{\"channelA\":{\"frequencies\":[10,10,10,10],\"strengths\":[30,30,30,30]},\"repeatCount\":20}");
        await harness.Dispatcher.DispatchAsync(waveCommand, CancellationToken.None);

        await harness.Safety.EmergencyStopAllAsync("runtime_test", CancellationToken.None);
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