using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Telemetry;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Core.Devices.Catalog;
using ChargingPanel.Core.Devices.Discovery;
using ChargingPanel.Core.Devices.Registry;
using ChargingPanel.Core.Devices.Sessions;
using ChargingPanel.DeviceIntegration.Tests.Fixtures.DGLab;
using ChargingPanel.Vendor.DGLab.Discovery;
using ChargingPanel.Vendor.DGLab.Drivers;
using ChargingPanel.Vendor.DGLab.Protocol;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.DGLab;

/// <summary>
/// 爪印发现策略测试：V1.0 升级诊断固定策略与 V1.1 可连接。
/// </summary>
public sealed class PawPrintsDiscoveryTests
{
    [Fact]
    public void ScanV10_ReturnsUpgradeDiagnostic_WithoutInstanceOrLease()
    {
        using var harness = new DGLabRuntimeHarness();

        var result = harness.Discovery.Observe(DGLabAdvertisements.NamedBle("47L120100"));

        Assert.Equal(DeviceAvailability.UpgradeDiagnostic, result.Match.Availability);
        Assert.False(result.Match.CanCreateSession);
        Assert.Null(result.DeviceInstance);
        Assert.Contains(result.Match.Diagnostics, d => d.Code == DiscoveryDiagnosticCode.UpgradeRequired);
        Assert.Empty(harness.Registry.GetSnapshot());
    }

    [Fact]
    public void V10_ListedAsUpgradeDiagnostic_NotPublicAddable()
    {
        using var harness = new DGLabRuntimeHarness();

        var models = harness.Catalog.GetModels();

        var v10 = Assert.Single(models, m => m.Id.Value == "dglab.pawprints.v1.0");
        Assert.Equal(DeviceAvailability.UpgradeDiagnostic, v10.Availability);
        // 升级诊断模型不能创建会话，发现流程不得产生可添加实例。
        var match = harness.Catalog.Match(DGLabAdvertisements.NamedBle("47L120100"));
        Assert.False(match.CanCreateSession);
    }

    [Fact]
    public void V10_DirectRegistration_IsRejected()
    {
        using var harness = new DGLabRuntimeHarness();
        var endpoint = ConnectionEndpoint.FromAdvertisement(
            DGLabAdvertisements.NamedBle("47L120100"),
            new ProtocolProfileId("dglab.pawprints.v1.0.ble"));

        Assert.Throws<InvalidOperationException>(() => harness.Registry.Register(new DeviceRegistration(
            new PluginId("dglab"),
            new ModelId("dglab.pawprints.v1.0"),
            new ProtocolProfileId("dglab.pawprints.v1.0.ble"),
            endpoint)));
    }

    [Fact]
    public void V10UpgradeMessage_IsChineseUserFacing()
    {
        Assert.Equal("请使用 DG-LAB 3.0 APP 将配件固件升级至 V1.1", PawPrintsDiscoveryRule.V10UpgradeMessage);
    }

    [Fact]
    public void ScanV11_IsConnectable()
    {
        using var harness = new DGLabRuntimeHarness();

        var result = harness.Discovery.Observe(DGLabAdvertisements.NamedBle("47L120300"));

        Assert.Equal(DeviceAvailability.Public, result.Match.Availability);
        Assert.True(result.Match.CanCreateSession);
        Assert.NotNull(result.DeviceInstance);
    }

    [Fact]
    public async Task ConnectV11_HandshakeWritesSafeNoTriggerModeFirst()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.NamedBle("47L120300"));

        var lease = harness.Broker.GetLease(instance.Endpoints.Single().Id);
        var first = Assert.Single(lease.Writes);
        Assert.Equal("5001000000000000000000000000000000", first.Hex);
        Assert.Equal(SessionState.Ready, harness.Sessions.GetState(instance.Id));
    }

    [Fact]
    public async Task ConnectV11_SafeModeWriteFailure_ClosesSessionNotFakeConnected()
    {
        using var harness = new DGLabRuntimeHarness();
        var discovered = harness.Discovery.Observe(DGLabAdvertisements.NamedBle("47L120300"));
        var instance = discovered.DeviceInstance!;
        harness.Broker.ConfigureLease = lease => lease.FailNextSend = true;

        var connected = await harness.TryConnectInstanceAsync(instance, CancellationToken.None);

        Assert.False(connected);
        Assert.NotEqual(SessionState.Ready, harness.Sessions.GetState(instance.Id));
        Assert.Null(harness.Runtime.Resolve(instance.Id));
    }

    [Fact]
    public async Task TriggerModeConfigure_SendsFrameAndProducesEventTelemetry()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.NamedBle("47L120300"));
        var lease = harness.Broker.GetLease(instance.Endpoints.Single().Id);
        lease.ClearWrites();

        var frameHex = "50010307001E0032000A14143214000000";
        var command = harness.BuildCommand(
            instance,
            new CapabilityId("trigger.mode"),
            "configure",
            $"{{\"frame\":\"{frameHex}\"}}");
        var result = await harness.Dispatcher.DispatchAsync(command, CancellationToken.None);

        Assert.Equal(ChargingPanel.Contracts.Commands.DeviceCommandStatus.AcceptedByTransport, result.Status);
        Assert.Contains(lease.Writes, w => w.Hex == frameHex);

        var session = harness.Sessions.GetSession(instance.Id)!;
        var driver = harness.GetDriver<PawPrintsV11Driver>(instance);
        driver.OnNotify(new byte[] { 0x5A, 0x01, 0x07, 0x64 });

        var envelope = ReadSingleTelemetry(driver, TimeSpan.FromSeconds(1));
        Assert.Equal("button.status", envelope.CapabilityId.Value);
        Assert.Equal(session.SessionEpoch, envelope.SessionEpoch);
        Assert.NotNull(envelope.SequenceNumber);
        Assert.Equal(instance.Id, envelope.DeviceInstanceId);
    }

    [Fact]
    public async Task D0PhysicalData_DuplicateSequence_IsDeduplicated()
    {
        using var harness = new DGLabRuntimeHarness();
        var instance = await harness.ConnectAdvertisementAsync(DGLabAdvertisements.NamedBle("47L120300"));
        var driver = harness.GetDriver<PawPrintsV11Driver>(instance);

        byte[] d0Frame = [0xD0, 0x01, 0x05, 0x01, 0x64, 0x0A, 0x14, 0x1E, 0x32];
        driver.OnNotify(d0Frame);
        driver.OnNotify(d0Frame);

        var telemetry = new List<DeviceTelemetryEnvelope>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await foreach (var item in driver.ReadTelemetryAsync(cts.Token))
            {
                telemetry.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
        }

        // 两条 D0 帧只产生一组物理数据遥测（pose.acceleration + external.voltage）。
        Assert.Single(telemetry, t => t.CapabilityId.Value == "pose.acceleration");
        Assert.Single(telemetry, t => t.CapabilityId.Value == "external.voltage");
    }

    private static DeviceTelemetryEnvelope ReadSingleTelemetry(PawPrintsV11Driver driver, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var enumerator = driver.ReadTelemetryAsync(cts.Token).GetAsyncEnumerator();
        var move = enumerator.MoveNextAsync().AsTask();
        if (!move.Wait(timeout))
        {
            throw new TimeoutException("未收到遥测。");
        }

        return enumerator.Current;
    }
}