using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Telemetry;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.DeviceIntegration.Tests.Fixtures.Yokonex;
using ChargingPanel.Vendor.Yokonex.Drivers;
using ChargingPanel.Vendor.Yokonex.Protocol;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Yokonex;

/// <summary>
/// 跳蛋/飞机杯 V1.1（YSKJ_TOY_BLE V1.1）协议测试。
/// 覆盖 0x10 设备信息查询、0x11 固定模式、0x12 三马达实时力度、0x13 电量上报，
/// 以及"能力必须来自查询结果，不假设三马达/相同模式数"的安全不变量。
/// </summary>
public sealed class ToyV11ProtocolTests
{
    private static readonly ProtocolProfileId ProfileId = new("yokonex.toy.v1_1");

    // ---------- 码流构造：黄金向量 ----------

    [Fact]
    public void BuildDeviceInfoQuery_ProducesGoldenVector()
    {
        Assert.Equal(new byte[] { 0x35, 0x10, 0x45 }, ToyV11Codec.BuildDeviceInfoQuery());
    }

    [Fact]
    public void BuildFixedMode_MotorA_Mode3_ProducesGoldenVector()
    {
        Assert.Equal(new byte[] { 0x35, 0x11, 0x01, 0x03, 0x4A }, ToyV11Codec.BuildFixedMode(ToyMotorMask.A, 3));
    }

    [Fact]
    public void BuildFixedMode_MotorAbc_Mode2_ProducesGoldenVector()
    {
        Assert.Equal(new byte[] { 0x35, 0x11, 0x07, 0x02, 0x4F }, ToyV11Codec.BuildFixedMode(ToyMotorMask.A | ToyMotorMask.B | ToyMotorMask.C, 2));
    }

    [Fact]
    public void BuildSpeed_ThreeMotors_ProducesGoldenVector()
    {
        Assert.Equal(new byte[] { 0x35, 0x12, 0x0A, 0x05, 0x00, 0x56 }, ToyV11Codec.BuildSpeed(10, 5, 0));
    }

    [Fact]
    public void BuildSpeed_RangeRejectedByClamp()
    {
        // 力度超 20 被截断到 20（协议上限），绝不写非法字节。
        Assert.Equal(new byte[] { 0x35, 0x12, 0x14, 0x14, 0x14, 0x83 }, ToyV11Codec.BuildSpeed(99, 99, 99));
    }

    // ---------- 应答解析 ----------

    [Fact]
    public void TryParseDeviceInfo_DecodesMotorModeCounts()
    {
        var response = new byte[] { 0x35, 0x10, 0x01, 0x01, 0x05, 0x00, 0x03, 0x00, 0x00, 0x4F };

        Assert.True(ToyV11Codec.TryParseDeviceInfo(response, out var info));
        Assert.Equal(1, info!.ModelId);
        Assert.Equal(1, info.Version);
        Assert.Equal(5, info.MotorAModeCount);
        Assert.Equal(0, info.MotorBModeCount);
        Assert.Equal(3, info.MotorCModeCount);
        Assert.True(info.HasMotorA);
        Assert.False(info.HasMotorB);
        Assert.True(info.HasMotorC);
    }

    [Fact]
    public void TryParseBattery_DecodesPercent()
    {
        var response = new byte[] { 0x35, 0x13, 0x01, 0x64, 0xAD };

        Assert.True(ToyV11Codec.TryParseBattery(response, out var report));
        Assert.Equal(100, report!.Percent);
    }

    // ---------- 驱动 ----------

    [Fact]
    public async Task OpenAsync_SendsDeviceInfoQuery()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();

        await driver.OpenAsync(lease, CancellationToken.None);

        var write = Assert.Single(lease.BinaryWrites);
        Assert.Equal(new byte[] { 0x35, 0x10, 0x45 }, write.Payload);
    }

    [Fact]
    public async Task ExecuteAsync_FixedMode_BeforeDeviceInfo_IsRejected()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);

        var command = YokonexTestCommands.Create(
            "toy.motor.control",
            "set.fixed_mode",
            "{\"motors\":\"A\",\"mode\":3}");
        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
        Assert.Equal("toy.info_unknown", result.ReasonCode);
    }

    [Fact]
    public async Task ExecuteAsync_FixedMode_AfterDeviceInfo_WritesFrame()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        FeedDeviceInfo(driver);

        var command = YokonexTestCommands.Create(
            "toy.motor.control",
            "set.fixed_mode",
            "{\"motors\":\"A\",\"mode\":3}");
        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.AcceptedByTransport, result.Status);
        var write = lease.BinaryWrites.Last();
        Assert.Equal(new byte[] { 0x35, 0x11, 0x01, 0x03, 0x4A }, write.Payload);
    }

    [Fact]
    public async Task ExecuteAsync_FixedMode_NonexistentMotor_IsRejected()
    {
        // 设备只有 A 马达（B=0、C=3），B 不存在。
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        FeedDeviceInfo(driver);

        var command = YokonexTestCommands.Create(
            "toy.motor.control",
            "set.fixed_mode",
            "{\"motors\":\"B\",\"mode\":1}");
        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
        Assert.Equal("command.invalid", result.ReasonCode);
    }

    [Fact]
    public async Task ExecuteAsync_FixedMode_ModeBeyondDeviceCount_IsRejected()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        FeedDeviceInfo(driver);

        // A 马达只有 5 个固定模式，请求模式 6 被拒绝。
        var command = YokonexTestCommands.Create(
            "toy.motor.control",
            "set.fixed_mode",
            "{\"motors\":\"A\",\"mode\":6}");
        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_SpeedControl_WritesFrame()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        FeedDeviceInfo(driver);

        var command = YokonexTestCommands.Create(
            "toy.speed.control",
            "set.speed",
            "{\"a\":10,\"b\":0,\"c\":5}");
        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.AcceptedByTransport, result.Status);
        var write = lease.BinaryWrites.Last();
        Assert.Equal(new byte[] { 0x35, 0x12, 0x0A, 0x00, 0x05, 0x56 }, write.Payload);
    }

    [Fact]
    public async Task ExecuteAsync_SpeedControl_SpeedOutOfRange_IsRejected()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        FeedDeviceInfo(driver);

        var command = YokonexTestCommands.Create(
            "toy.speed.control",
            "set.speed",
            "{\"a\":21,\"b\":0,\"c\":0}");
        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task SafeStop_SendsZeroSpeedFrame()
    {
        var lease = new FakeEndpointLease(ProfileId) { NextSendStatus = EndpointOperationStatus.Completed };
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        lease.BinaryWrites.Clear();

        var result = await driver.SafeStopAsync(
            new SafeStopContext(YokonexTestCommands.InstanceId, 1, 1, DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(SafeStopStatus.ConfirmedStopped, result.Status);
        var write = Assert.Single(lease.BinaryWrites);
        Assert.Equal(new byte[] { 0x35, 0x12, 0x00, 0x00, 0x00, 0x47 }, write.Payload);
    }

    [Fact]
    public async Task ExecuteAsync_AfterSafeStopStarts_IsRejected()
    {
        // 急停进行中拒绝新命令（紧急停止优先级最高）。
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        FeedDeviceInfo(driver);

        // 用发送闸门使 SafeStop 停留在进行中，再执行普通命令。
        lease.SendGate = new TaskCompletionSource();
        var safeStopTask = driver.SafeStopAsync(
            new SafeStopContext(YokonexTestCommands.InstanceId, 1, 1, DateTimeOffset.UtcNow),
            CancellationToken.None);
        await Task.Delay(50);

        var command = YokonexTestCommands.Create(
            "toy.speed.control",
            "set.speed",
            "{\"a\":5,\"b\":0,\"c\":0}");
        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
        Assert.Equal("driver.safe_stopping", result.ReasonCode);

        lease.SendGate.TrySetResult();
        await safeStopTask;
    }

    [Fact]
    public async Task ExecuteAsync_BrokenConnection_ReturnsTransportFailed()
    {
        var lease = new FakeEndpointLease(ProfileId) { NextSendStatus = EndpointOperationStatus.Failed };
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        FeedDeviceInfo(driver);

        var command = YokonexTestCommands.Create(
            "toy.speed.control",
            "set.speed",
            "{\"a\":5,\"b\":0,\"c\":0}");
        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.TransportFailed, result.Status);
    }

    [Fact]
    public async Task OnNotify_DeviceInfo_EmitsTelemetry()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        driver.Configure(YokonexTestCommands.InstanceId, 1);

        driver.OnNotify(new byte[] { 0x35, 0x10, 0x01, 0x01, 0x05, 0x00, 0x03, 0x00, 0x00, 0x4F });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var telemetry = ReadAll(driver, cts.Token).FirstOrDefault();
        Assert.NotNull(telemetry);
        Assert.Equal(5, telemetry!.Payload.GetProperty("motorAModeCount").GetInt32());
    }

    [Fact]
    public async Task OnNotify_BatteryReport_EmitsTelemetry()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        driver.Configure(YokonexTestCommands.InstanceId, 1);

        driver.OnNotify(new byte[] { 0x35, 0x13, 0x01, 0x64, 0xAD });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var telemetry = ReadAll(driver, cts.Token).FirstOrDefault();
        Assert.NotNull(telemetry);
        Assert.Equal(100, telemetry!.Payload.GetProperty("percent").GetInt32());
    }

    private static void FeedDeviceInfo(ToyV11Driver driver) =>
        driver.OnNotify(new byte[] { 0x35, 0x10, 0x01, 0x01, 0x05, 0x00, 0x03, 0x00, 0x00, 0x4F });

    private static ToyV11Driver CreateDriver() =>
        new(ProfileId, new FakePluginLogger());

    private static IEnumerable<DeviceTelemetryEnvelope> ReadAll(ToyV11Driver driver, CancellationToken cancellationToken)
    {
        var enumerator = driver.ReadTelemetryAsync(cancellationToken).GetAsyncEnumerator();
        while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
        {
            yield return enumerator.Current;
        }
    }
}
