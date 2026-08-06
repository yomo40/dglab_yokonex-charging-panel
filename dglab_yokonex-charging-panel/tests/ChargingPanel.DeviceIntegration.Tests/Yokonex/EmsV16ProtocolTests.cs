using System.Text.Json;
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
/// 电击器一代 V1.6 协议测试。
/// 覆盖 0x11 单通道帧、0x12 马达、0x13 计步、0x14 角度、0x71 查询与应答，
/// 累加校验和、276 级强度、16 种固定模式、自定义模式、五类异常上报，
/// 以及"传输已接受≠设备已执行""GATT 结果未知不重发"两个安全不变量。
/// </summary>
public sealed class EmsV16ProtocolTests
{
    private static readonly ProtocolProfileId ProfileId = new("yokonex.ems.v1_6");
    private static readonly EmsV16Codec Codec = new();

    // ---------- 码流构造：黄金向量 ----------

    [Fact]
    public void BuildChannelFrame_ChannelAOnFixedMode_ProducesGoldenVector()
    {
        var frame = Codec.BuildChannelOutputFrame(
            new EmsChannelOutputCommand(EmsChannel.A, new EmsChannelOutputState(true, 100, 3, 0, 0)),
            EmsChannelOutputState.Off());

        Assert.Equal(
            new byte[] { 0x35, 0x11, 0x01, 0x01, 0x00, 0x64, 0x03, 0x00, 0x00, 0xAF },
            frame);
    }

    [Fact]
    public void BuildChannelFrame_ChannelBCustomMode_ProducesGoldenVector()
    {
        var frame = Codec.BuildChannelOutputFrame(
            new EmsChannelOutputCommand(EmsChannel.B, new EmsChannelOutputState(true, 276, 0, 50, 30)),
            EmsChannelOutputState.Off());

        Assert.Equal(
            new byte[] { 0x35, 0x11, 0x02, 0x01, 0x01, 0x14, 0x11, 0x32, 0x1E, 0xBF },
            frame);
    }

    [Fact]
    public void BuildChannelFrame_ChannelOff_ClosesChannelAndIgnoresIntensity()
    {
        var frame = Codec.BuildChannelOutputFrame(
            new EmsChannelOutputCommand(EmsChannel.AB, new EmsChannelOutputState(false, 100, 3, 50, 30)),
            EmsChannelOutputState.Off());

        // 关闭时强度与模式必须被忽略（写 0）。
        Assert.Equal(
            new byte[] { 0x35, 0x11, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x49 },
            frame);
    }

    [Fact]
    public void BuildMotorFrame_On_ProducesGoldenVector()
    {
        Assert.Equal(new byte[] { 0x35, 0x12, 0x01, 0x48 }, Codec.BuildMotorFrame(EmsMotorState.On));
    }

    [Fact]
    public void BuildMotorFrame_ThreePresets_AreDistinct()
    {
        Assert.Equal(new byte[] { 0x35, 0x12, 0x11, 0x58 }, Codec.BuildMotorFrame(EmsMotorState.Preset1));
        Assert.Equal(new byte[] { 0x35, 0x12, 0x12, 0x59 }, Codec.BuildMotorFrame(EmsMotorState.Preset2));
        Assert.Equal(new byte[] { 0x35, 0x12, 0x13, 0x5A }, Codec.BuildMotorFrame(EmsMotorState.Preset3));
    }

    [Fact]
    public void BuildStepFrame_FiveCommands_MapToFiveStates()
    {
        Assert.Equal(new byte[] { 0x35, 0x13, 0x00, 0x48 }, Codec.BuildStepFrame(EmsStepCommand.Off));
        Assert.Equal(new byte[] { 0x35, 0x13, 0x01, 0x49 }, Codec.BuildStepFrame(EmsStepCommand.On));
        Assert.Equal(new byte[] { 0x35, 0x13, 0x02, 0x4A }, Codec.BuildStepFrame(EmsStepCommand.Clear));
        Assert.Equal(new byte[] { 0x35, 0x13, 0x03, 0x4B }, Codec.BuildStepFrame(EmsStepCommand.Pause));
        Assert.Equal(new byte[] { 0x35, 0x13, 0x04, 0x4C }, Codec.BuildStepFrame(EmsStepCommand.Resume));
    }

    [Fact]
    public void BuildAngleFrame_On_ProducesGoldenVector()
    {
        Assert.Equal(new byte[] { 0x35, 0x14, 0x01, 0x4A }, Codec.BuildAngleFrame(EmsAngleCommand.On));
    }

    [Fact]
    public void BuildQueryFrame_AllTypes_ProduceValidChecksum()
    {
        foreach (var type in Enum.GetValues<EmsQueryType>())
        {
            var frame = Codec.BuildQueryFrame(type);
            Assert.Equal(EmsCodecBase.Header, frame[0]);
            Assert.Equal(EmsCodecBase.Query, frame[1]);
            Assert.True(FrameChecksum.Verify(frame), $"{type} 查询帧校验和错误。");
        }
    }

    [Fact]
    public void BuildCloseFrame_ClosesAbWithoutTouchStepOrAngle()
    {
        var frame = Codec.BuildCloseFrame();
        Assert.Equal(
            new byte[] { 0x35, 0x11, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x49 },
            frame);
        // 帧内没有任何计步/角度指令字（0x13/0x14）。
        Assert.DoesNotContain(frame, value => value is 0x13 or 0x14);
    }

    // ---------- 状态应答解析 ----------

    [Fact]
    public void TryParseReport_ChannelStatus_DecodesStatus()
    {
        var response = new byte[] { 0x35, 0x71, 0x01, 0x01, 0x01, 0x01, 0x14, 0x03, 0xC1 };

        Assert.True(Codec.TryParseReport(response, out var report));
        var channel = Assert.IsType<EmsStatusReport.Channel>(report);
        Assert.Equal(EmsChannel.A, channel.Status.Channel);
        Assert.Equal(EmsChannelConnection.Discharging, channel.Status.Connection);
        Assert.True(channel.Status.On);
        Assert.Equal(276, channel.Status.Intensity);
        Assert.Equal(3, channel.Status.Mode);
    }

    [Fact]
    public void TryParseReport_Motor_DecodesPreset()
    {
        var response = new byte[] { 0x35, 0x71, 0x03, 0x11, 0xBA };

        Assert.True(Codec.TryParseReport(response, out var report));
        var motor = Assert.IsType<EmsStatusReport.Motor>(report);
        Assert.Equal(EmsMotorState.Preset1, motor.State);
    }

    [Fact]
    public void TryParseReport_Battery_DecodesPercent()
    {
        var response = new byte[] { 0x35, 0x71, 0x04, 0x32, 0xDC };

        Assert.True(Codec.TryParseReport(response, out var report));
        var battery = Assert.IsType<EmsStatusReport.Battery>(report);
        Assert.Equal(50, battery.Status.Percent);
    }

    [Fact]
    public void TryParseReport_Step_DecodesStepCount()
    {
        var response = new byte[] { 0x35, 0x71, 0x05, 0x01, 0x2C, 0xD8 };

        Assert.True(Codec.TryParseReport(response, out var report));
        var step = Assert.IsType<EmsStatusReport.Step>(report);
        Assert.Equal(300, step.Status.StepCount);
    }

    [Fact]
    public void TryParseReport_Angle_DecodesSixAxis()
    {
        var response = new byte[]
        {
            0x35, 0x71, 0x06, 0x00, 0x0A, 0x00, 0x14, 0x00, 0x1E,
            0x00, 0x28, 0x00, 0x32, 0x00, 0x3C, 0x7E
        };

        Assert.True(Codec.TryParseReport(response, out var report));
        var angle = Assert.IsType<EmsStatusReport.Angle>(report);
        Assert.Equal(10, angle.Status.GravityX);
        Assert.Equal(20, angle.Status.GravityY);
        Assert.Equal(30, angle.Status.GravityZ);
        Assert.Equal(40, angle.Status.GyroX);
        Assert.Equal(50, angle.Status.GyroY);
        Assert.Equal(60, angle.Status.GyroZ);
    }

    [Fact]
    public void TryParseReport_Exception_CoversAllFiveClasses()
    {
        var vectors = new Dictionary<byte, EmsExceptionCode>
        {
            [0x01] = EmsExceptionCode.ChecksumError,
            [0x02] = EmsExceptionCode.HeaderError,
            [0x03] = EmsExceptionCode.CommandError,
            [0x04] = EmsExceptionCode.DataError,
            [0x05] = EmsExceptionCode.NotImplemented
        };

        foreach (var (code, expected) in vectors)
        {
            var response = new byte[] { 0x35, 0x71, 0x55, code, (byte)((0x35 + 0x71 + 0x55 + code) & 0xFF) };
            Assert.True(Codec.TryParseReport(response, out var report), $"异常类 {code} 应被识别。");
            var exception = Assert.IsType<EmsStatusReport.Exception>(report);
            Assert.Equal(expected, exception.Report.Code);
        }
    }

    [Fact]
    public void TryParseReport_BadChecksum_IsRejected()
    {
        var response = new byte[] { 0x35, 0x71, 0x04, 0x32, 0x00 };
        Assert.False(Codec.TryParseReport(response, out _));
    }

    // ---------- 驱动 ----------

    [Fact]
    public async Task OpenAsync_SendsQueriesForABMotorBattery()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();

        await driver.OpenAsync(lease, CancellationToken.None);

        Assert.Equal(4, lease.BinaryWrites.Count);
        Assert.Equal(new byte[] { 0x35, 0x71, 0x01, 0xA7 }, lease.BinaryWrites[0].Payload);
        Assert.Equal(new byte[] { 0x35, 0x71, 0x02, 0xA8 }, lease.BinaryWrites[1].Payload);
        Assert.Equal(new byte[] { 0x35, 0x71, 0x03, 0xA9 }, lease.BinaryWrites[2].Payload);
        Assert.Equal(new byte[] { 0x35, 0x71, 0x04, 0xAA }, lease.BinaryWrites[3].Payload);
    }

    [Fact]
    public async Task ExecuteAsync_ChannelOutput_WritesSingleChannelFrame()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        lease.BinaryWrites.Clear();

        var command = YokonexTestCommands.Create(
            "ems.channel.output",
            "set.channel",
            "{\"channel\":\"A\",\"on\":true,\"intensity\":100,\"mode\":3,\"frequency\":0,\"pulseTime\":0}");

        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.AcceptedByTransport, result.Status);
        var write = Assert.Single(lease.BinaryWrites);
        Assert.Equal(new byte[] { 0x35, 0x11, 0x01, 0x01, 0x00, 0x64, 0x03, 0x00, 0x00, 0xAF }, write.Payload);
    }

    [Fact]
    public async Task ExecuteAsync_GattCompleted_IsAcceptedByTransportNotExecuted()
    {
        // 传输已接受 ≠ 设备已执行。GATT 写完成只返回 AcceptedByTransport。
        var lease = new FakeEndpointLease(ProfileId) { NextSendStatus = EndpointOperationStatus.Completed };
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);

        var command = YokonexTestCommands.Create(
            "ems.channel.output",
            "set.channel",
            "{\"channel\":\"B\",\"on\":true,\"intensity\":50,\"mode\":1,\"frequency\":0,\"pulseTime\":0}");

        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.AcceptedByTransport, result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_GattOutcomeUnknown_ReturnsOutcomeUnknownAndDoesNotResend()
    {
        var lease = new FakeEndpointLease(ProfileId) { NextSendStatus = EndpointOperationStatus.OutcomeUnknown };
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        lease.BinaryWrites.Clear();

        var command = YokonexTestCommands.Create(
            "ems.channel.output",
            "set.channel",
            "{\"channel\":\"A\",\"on\":true,\"intensity\":100,\"mode\":3,\"frequency\":0,\"pulseTime\":0}");

        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.OutcomeUnknown, result.Status);
        Assert.Single(lease.BinaryWrites);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidIntensity_IsRejected()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);

        var command = YokonexTestCommands.Create(
            "ems.channel.output",
            "set.channel",
            "{\"channel\":\"A\",\"on\":true,\"intensity\":300,\"mode\":3,\"frequency\":0,\"pulseTime\":0}");

        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
        Assert.Equal("command.invalid", result.ReasonCode);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownCapability_IsRejected()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);

        var command = YokonexTestCommands.Create("ems.does_not_exist", "set", "{}");
        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
        Assert.Equal("capability.unsupported", result.ReasonCode);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutOpen_IsRejected()
    {
        var driver = CreateDriver();
        var command = YokonexTestCommands.Create(
            "ems.channel.output",
            "set.channel",
            "{\"channel\":\"A\",\"on\":true,\"intensity\":100,\"mode\":3,\"frequency\":0,\"pulseTime\":0}");

        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
        Assert.Equal("driver.not_open", result.ReasonCode);
    }

    [Fact]
    public async Task SafeStop_CompletedGatt_ReturnsConfirmedStopped()
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
        Assert.Equal(new byte[] { 0x35, 0x11, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x49 }, lease.BinaryWrites[0].Payload);
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
        // 绝不重发 AtMostOnce 停止帧：关闭 + 马达关，恰好两帧。
        Assert.Equal(2, lease.BinaryWrites.Count);
    }

    [Fact]
    public async Task OnNotify_BatteryReport_EmitsTelemetry()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        driver.Configure(YokonexTestCommands.InstanceId, 1);

        driver.OnNotify(new byte[] { 0x35, 0x71, 0x04, 0x32, 0xDC });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var telemetry = ReadAll(driver, cts.Token).FirstOrDefault();
        Assert.NotNull(telemetry);
        Assert.Equal(50, telemetry!.Payload.GetProperty("percent").GetInt32());
        Assert.Equal(TelemetryQuality.Good, telemetry.Quality);
    }

    [Fact]
    public async Task OnNotify_ExceptionReport_EmitsTelemetryWithExceptionCode()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        driver.Configure(YokonexTestCommands.InstanceId, 1);

        driver.OnNotify(new byte[] { 0x35, 0x71, 0x55, 0x02, 0xFD });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var telemetry = ReadAll(driver, cts.Token).FirstOrDefault();
        Assert.NotNull(telemetry);
        Assert.Equal("header_error", telemetry!.Payload.GetProperty("code").GetString());
    }

    private static IEnumerable<DeviceTelemetryEnvelope> ReadAll(EmsDriver driver, CancellationToken cancellationToken)
    {
        var enumerator = driver.ReadTelemetryAsync(cancellationToken).GetAsyncEnumerator();
        while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
        {
            yield return enumerator.Current;
        }
    }

    private static EmsDriver CreateDriver() =>
        new(ProfileId, new EmsV16Codec(), new FakePluginLogger());
}
