using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.DeviceIntegration.Tests.Fixtures.Yokonex;
using ChargingPanel.Vendor.Yokonex.Drivers;
using ChargingPanel.Vendor.Yokonex.Protocol;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Yokonex;

/// <summary>
/// 电击器二代 V2.0 协议测试。
/// 覆盖同帧 A+B 固定模式、同帧 A+B 实时模式、频率列表模式（单通道最多 100 组）、
/// 关闭模式，以及"频率列表≠实时模式"的区分。
/// </summary>
public sealed class EmsV20ProtocolTests
{
    private static readonly ProtocolProfileId ProfileId = new("yokonex.ems.v2_0");
    private static readonly EmsV20Codec Codec = new();

    [Fact]
    public void BuildFixedModeFrame_SameFrameAB_ProducesGoldenVector()
    {
        var frame = Codec.BuildChannelOutputFrame(
            new EmsChannelOutputCommand(EmsChannel.A, new EmsChannelOutputState(true, 100, 3, 0, 0)),
            new EmsChannelOutputState(true, 50, 1, 0, 0));

        // 固定模式 0x01：A 强度/模式 + B 强度/模式 同一帧。
        Assert.Equal(
            new byte[] { 0x35, 0x11, 0x01, 0x00, 0x64, 0x03, 0x00, 0x32, 0x01, 0xE1 },
            frame);
    }

    [Fact]
    public void BuildRealtimeModeFrame_SameFrameAB_ProducesGoldenVector()
    {
        var frame = Codec.BuildChannelOutputFrame(
            new EmsChannelOutputCommand(EmsChannel.A, new EmsChannelOutputState(true, 100, 0, 50, 30)),
            new EmsChannelOutputState(true, 200, 0, 40, 20));

        // 实时模式 0x02：A/B 各自的强度、频率、脉冲时间同一帧。
        Assert.Equal(
            new byte[] { 0x35, 0x11, 0x02, 0x00, 0x64, 0x32, 0x1E, 0x00, 0xC8, 0x28, 0x14, 0x00 },
            frame);
    }

    [Fact]
    public void BuildFrequencyListFrame_IsNotRealtimeMode()
    {
        var frame = Codec.BuildFrequencyListFrame(
            EmsChannel.A,
            100,
            [new EmsFrequencyGroup(50, 30), new EmsFrequencyGroup(60, 10)]);

        // 频率列表模式 0x03，绝不是实时模式 0x02。
        Assert.Equal(
            new byte[] { 0x35, 0x11, 0x03, 0x01, 0x00, 0x64, 0x32, 0x1E, 0x3C, 0x0A, 0x44 },
            frame);
        Assert.Equal(0x03, frame[2]);
    }

    [Fact]
    public void BuildFrequencyListFrame_UpToOneHundredGroups_IsAllowed()
    {
        var groups = Enumerable.Range(1, 100).Select(i => new EmsFrequencyGroup(50, i % 101)).ToArray();
        var frame = Codec.BuildFrequencyListFrame(EmsChannel.B, 100, groups);

        Assert.Equal(7 + 100 * 2, frame.Length);
        Assert.True(FrameChecksum.Verify(frame));
    }

    [Fact]
    public void BuildFrequencyListFrame_OverOneHundredGroups_IsRejected()
    {
        var groups = Enumerable.Range(1, 101).Select(i => new EmsFrequencyGroup(50, 20)).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Codec.BuildFrequencyListFrame(EmsChannel.A, 100, groups));
    }

    [Fact]
    public void BuildCloseFrame_ClosesAbWithV20CloseMode()
    {
        // V2.0 关闭通道使用模式 0x04。
        Assert.Equal(new byte[] { 0x35, 0x11, 0x04, 0x03, 0x4D }, Codec.BuildCloseFrame());
    }

    [Fact]
    public void TryParseReport_SharedResponseFormat_DecodesBattery()
    {
        // V2.0 的状态应答格式与 V1.6 一致。
        Assert.True(Codec.TryParseReport(new byte[] { 0x35, 0x71, 0x04, 0x32, 0xDC }, out var report));
        Assert.Equal(50, Assert.IsType<EmsStatusReport.Battery>(report).Status.Percent);
    }

    [Fact]
    public async Task OpenAsync_SendsQueriesForABMotorBattery()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);

        Assert.Equal(4, lease.BinaryWrites.Count);
    }

    [Fact]
    public async Task ExecuteAsync_ChannelOutput_BuildsSameFrameWithCachedOtherChannel()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);

        // 先设置 A，B 未设置过 → B 按关闭处理。
        var commandA = YokonexTestCommands.Create(
            "ems.channel.output",
            "set.channel",
            "{\"channel\":\"A\",\"on\":true,\"intensity\":100,\"mode\":3,\"frequency\":0,\"pulseTime\":0}");
        var resultA = await driver.ExecuteAsync(commandA, CancellationToken.None);
        Assert.Equal(DeviceCommandStatus.AcceptedByTransport, resultA.Status);

        var writeA = lease.BinaryWrites.Last();
        Assert.Equal(new byte[] { 0x35, 0x11, 0x01, 0x00, 0x64, 0x03, 0x00, 0x00, 0x00, 0xAE }, writeA.Payload);

        // 再设置 B，同一帧保留 A 的当前状态（100/模式3）。
        var commandB = YokonexTestCommands.Create(
            "ems.channel.output",
            "set.channel",
            "{\"channel\":\"B\",\"on\":true,\"intensity\":50,\"mode\":1,\"frequency\":0,\"pulseTime\":0}");
        var resultB = await driver.ExecuteAsync(commandB, CancellationToken.None);
        Assert.Equal(DeviceCommandStatus.AcceptedByTransport, resultB.Status);

        var writeB = lease.BinaryWrites.Last();
        Assert.Equal(new byte[] { 0x35, 0x11, 0x01, 0x00, 0x64, 0x03, 0x00, 0x32, 0x01, 0xE1 }, writeB.Payload);
    }

    [Fact]
    public async Task ExecuteAsync_RealtimeCommand_BuildsRealtimeFrame()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);

        var command = YokonexTestCommands.Create(
            "ems.channel.output",
            "set.channel",
            "{\"channel\":\"A\",\"on\":true,\"intensity\":100,\"mode\":0,\"frequency\":50,\"pulseTime\":30}");
        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.AcceptedByTransport, result.Status);
        var write = lease.BinaryWrites.Last();
        Assert.Equal(new byte[] { 0x35, 0x11, 0x02, 0x00, 0x64, 0x32, 0x1E, 0x00, 0x00, 0x00, 0x00, 0xFC }, write.Payload);
    }

    [Fact]
    public async Task ExecuteAsync_FrequencyList_WritesFrequencyListFrame()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);

        var command = YokonexTestCommands.Create(
            "ems.frequency_list",
            "set.frequency_list",
            "{\"channel\":\"A\",\"intensity\":100,\"groups\":[{\"frequency\":50,\"pulseTime\":30},{\"frequency\":60,\"pulseTime\":10}]}");
        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.AcceptedByTransport, result.Status);
        var write = lease.BinaryWrites.Last();
        Assert.Equal(new byte[] { 0x35, 0x11, 0x03, 0x01, 0x00, 0x64, 0x32, 0x1E, 0x3C, 0x0A, 0x44 }, write.Payload);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidFrequencyListRange_IsRejected()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);

        var command = YokonexTestCommands.Create(
            "ems.frequency_list",
            "set.frequency_list",
            "{\"channel\":\"A\",\"intensity\":100,\"groups\":[{\"frequency\":200,\"pulseTime\":30}]}");
        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task SafeStop_CompletedGatt_SendsV20CloseAndMotorOff()
    {
        var lease = new FakeEndpointLease(ProfileId) { NextSendStatus = EndpointOperationStatus.Completed };
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        lease.BinaryWrites.Clear();

        var result = await driver.SafeStopAsync(
            new SafeStopContext(YokonexTestCommands.InstanceId, 1, 1, DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(SafeStopStatus.ConfirmedStopped, result.Status);
        Assert.Equal(2, lease.BinaryWrites.Count);
        Assert.Equal(new byte[] { 0x35, 0x11, 0x04, 0x03, 0x4D }, lease.BinaryWrites[0].Payload);
        Assert.Equal(new byte[] { 0x35, 0x12, 0x00, 0x47 }, lease.BinaryWrites[1].Payload);
    }

    [Fact]
    public async Task SafeStop_UnknownGatt_ReturnsOutcomeUnknownAndDoesNotResend()
    {
        var lease = new FakeEndpointLease(ProfileId) { NextSendStatus = EndpointOperationStatus.OutcomeUnknown };
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        lease.BinaryWrites.Clear();

        var result = await driver.SafeStopAsync(
            new SafeStopContext(YokonexTestCommands.InstanceId, 1, 1, DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(SafeStopStatus.OutcomeUnknown, result.Status);
        Assert.Equal(2, lease.BinaryWrites.Count);
    }

    private static EmsDriver CreateDriver() =>
        new(ProfileId, new EmsV20Codec(), new FakePluginLogger());
}
