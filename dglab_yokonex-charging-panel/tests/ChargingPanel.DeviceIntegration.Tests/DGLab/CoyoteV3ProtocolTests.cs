using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Telemetry;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.DeviceIntegration.Tests.Fixtures.DGLab;
using ChargingPanel.Vendor.DGLab.Drivers;
using ChargingPanel.Vendor.DGLab.Protocol;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.DGLab;

/// <summary>
/// 郊狼 V3 协议黄金向量测试。全部十六进制取自官方文档。
/// </summary>
public sealed class CoyoteV3ProtocolTests
{
    // ---------- B0 指令黄金向量 ----------

    [Fact]
    public void EncodeB0_OfficialNo1_AOnlyWaveform_MatchesDocHex()
    {
        var frame = CoyoteV3Codec.EncodeB0(
            0, CoyoteV3Codec.StrengthNoChange, CoyoteV3Codec.StrengthNoChange, 0, 0,
            [10, 10, 10, 10], [0, 10, 20, 30],
            [0, 0, 0, 0], [0, 0, 0, 101]);

        Assert.Equal("B00000000A0A0A0A000A141E0000000000000065", Convert.ToHexString(frame));
    }

    [Fact]
    public void EncodeB0_OfficialNo1Step2_MatchesDocHex()
    {
        var frame = CoyoteV3Codec.EncodeB0(
            0, CoyoteV3Codec.StrengthNoChange, CoyoteV3Codec.StrengthNoChange, 0, 0,
            [15, 15, 15, 15], [40, 50, 60, 70],
            [0, 0, 0, 0], [0, 0, 0, 101]);

        Assert.Equal("B00000000F0F0F0F28323C460000000000000065", Convert.ToHexString(frame));
    }

    [Fact]
    public void EncodeB0_OfficialNo2RelativeIncreaseA_MatchesDocHex()
    {
        var frame = CoyoteV3Codec.EncodeB0(
            0, CoyoteV3Codec.StrengthIncrease, CoyoteV3Codec.StrengthNoChange, 5, 0,
            [10, 10, 10, 10], [0, 10, 20, 30],
            [0, 0, 0, 0], [0, 0, 0, 101]);

        Assert.Equal("B00405000A0A0A0A000A141E0000000000000065", Convert.ToHexString(frame));
    }

    [Fact]
    public void EncodeB0_OfficialNo2Step4_SequenceOneAndWaveform_MatchesDocHex()
    {
        var frame = CoyoteV3Codec.EncodeB0(
            1, CoyoteV3Codec.StrengthIncrease, CoyoteV3Codec.StrengthNoChange, 10, 0,
            [40, 60, 80, 100], [100, 90, 90, 90],
            [0, 0, 0, 0], [0, 0, 0, 101]);

        Assert.Equal("B0140A00283C5064645A5A5A0000000000000065", Convert.ToHexString(frame));
    }

    [Fact]
    public void EncodeB0_OfficialNo4_ABBothWaveform_MatchesDocHex()
    {
        var frame = CoyoteV3Codec.EncodeB0(
            0, CoyoteV3Codec.StrengthNoChange, CoyoteV3Codec.StrengthNoChange, 0, 0,
            [10, 10, 10, 10], [0, 10, 20, 30],
            [10, 10, 10, 10], [0, 0, 0, 0]);

        Assert.Equal("B00000000A0A0A0A000A141E0A0A0A0A00000000", Convert.ToHexString(frame));
    }

    [Fact]
    public void EncodeB0_OfficialNo4Step4_MatchesDocHex()
    {
        var frame = CoyoteV3Codec.EncodeB0(
            0, CoyoteV3Codec.StrengthNoChange, CoyoteV3Codec.StrengthNoChange, 0, 0,
            [40, 60, 80, 100], [0, 90, 90, 90],
            [10, 10, 10, 10], [0, 0, 0, 10]);

        Assert.Equal("B0000000283C5064005A5A5A0A0A0A0A0000000A", Convert.ToHexString(frame));
    }

    // ---------- 四种强度解读方式 ----------

    [Fact]
    public void EncodeB0_StrengthInterpretation_NoChangeEncodesModeByte()
    {
        // A=0b00 不改变，B=0b00 不改变 → 模式 = 0b0000 = 0x0
        var frame = CoyoteV3Codec.EncodeB0(
            0, CoyoteV3Codec.StrengthNoChange, CoyoteV3Codec.StrengthNoChange, 5, 8,
            [0, 0, 0, 0], [0, 0, 0, 101],
            [0, 0, 0, 0], [0, 0, 0, 101]);

        Assert.Equal("B0000508", Convert.ToHexString(frame)[..8]);
    }

    [Fact]
    public void EncodeB0_StrengthInterpretation_AIncreaseBNoChange_EncodesModeByte()
    {
        // A=0b01 增加，B=0b00 不改变 → 模式 = 0b0100 = 0x4
        var frame = CoyoteV3Codec.EncodeB0(
            0, CoyoteV3Codec.StrengthIncrease, CoyoteV3Codec.StrengthNoChange, 5, 8,
            [0, 0, 0, 0], [0, 0, 0, 101],
            [0, 0, 0, 0], [0, 0, 0, 101]);

        Assert.Equal("B0040508", Convert.ToHexString(frame)[..8]);
    }

    [Fact]
    public void EncodeB0_StrengthInterpretation_ADecreaseBIncrease_EncodesModeByte()
    {
        // A=0b10 减少，B=0b01 增加 → 模式 = 0b1001 = 0x9
        var frame = CoyoteV3Codec.EncodeB0(
            0, CoyoteV3Codec.StrengthDecrease, CoyoteV3Codec.StrengthIncrease, 5, 8,
            [0, 0, 0, 0], [0, 0, 0, 101],
            [0, 0, 0, 0], [0, 0, 0, 101]);

        Assert.Equal("B0090508", Convert.ToHexString(frame)[..8]);
    }

    [Fact]
    public void EncodeB0_StrengthInterpretation_AAbsoluteBAbsolute_EncodesModeByte()
    {
        // A=0b11 绝对，B=0b11 绝对 → 模式 = 0b1111 = 0xF
        var frame = CoyoteV3Codec.EncodeB0(
            0, CoyoteV3Codec.StrengthAbsolute, CoyoteV3Codec.StrengthAbsolute, 32, 8,
            [0, 0, 0, 0], [0, 0, 0, 101],
            [0, 0, 0, 0], [0, 0, 0, 101]);

        Assert.Equal("B00F2008", Convert.ToHexString(frame)[..8]);
    }

    // ---------- 非法范围 ----------

    [Fact]
    public void EncodeB0_StrengthValueOver200_EncodesAsZeroPerProtocol()
    {
        // 强度设定值 0-200 之外一律以 0 处理。
        var frame = CoyoteV3Codec.EncodeB0(
            0, CoyoteV3Codec.StrengthAbsolute, CoyoteV3Codec.StrengthNoChange, 201, 0,
            [0, 0, 0, 0], [0, 0, 0, 101],
            [0, 0, 0, 0], [0, 0, 0, 101]);

        Assert.Equal("B00C0000", Convert.ToHexString(frame)[..8]);
    }

    [Fact]
    public void IsValidChannelFrame_StrengthAbove100_ReturnsFalse()
    {
        Assert.False(CoyoteV3Codec.IsValidChannelFrame([10, 10, 10, 10], [0, 5, 10, 101]));
    }

    [Fact]
    public void IsValidChannelFrame_FrequencyOutside10To240_ReturnsFalse()
    {
        Assert.False(CoyoteV3Codec.IsValidChannelFrame([10, 240, 5, 10], [0, 5, 10, 20]));
    }

    [Fact]
    public void IsValidChannelFrame_AllInRange_ReturnsTrue()
    {
        Assert.True(CoyoteV3Codec.IsValidChannelFrame([10, 20, 30, 40], [0, 25, 50, 100]));
    }

    // ---------- 频率换算 ----------

    [Fact]
    public void ConvertFrequency_LinearSamples_MatchesOfficialFormula()
    {
        Assert.Equal(100, CoyoteV3Codec.ConvertFrequency(100));
        Assert.Equal(120, CoyoteV3Codec.ConvertFrequency(200));
        Assert.Equal(140, CoyoteV3Codec.ConvertFrequency(300));
        Assert.Equal(180, CoyoteV3Codec.ConvertFrequency(500));
        Assert.Equal(200, CoyoteV3Codec.ConvertFrequency(600));
        Assert.Equal(210, CoyoteV3Codec.ConvertFrequency(700));
        Assert.Equal(240, CoyoteV3Codec.ConvertFrequency(1000));
    }

    [Fact]
    public void ConvertFrequency_OutOfRange_FallsBackToTen()
    {
        Assert.Equal(10, CoyoteV3Codec.ConvertFrequency(5));
        Assert.Equal(10, CoyoteV3Codec.ConvertFrequency(2000));
    }

    // ---------- BF 指令 ----------

    [Fact]
    public void EncodeBf_DefaultSafeLimits_MatchesExpectedBytes()
    {
        var frame = CoyoteV3Codec.EncodeDefaultBf();

        Assert.Equal("BFC8C880808080", Convert.ToHexString(frame));
    }

    [Fact]
    public void EncodeBf_CustomValues_EncodesAllSevenBytes()
    {
        var frame = CoyoteV3Codec.EncodeBf(150, 30, 100, 200, 50, 255);

        Assert.Equal("BF961E64C832FF", Convert.ToHexString(frame));
    }

    [Fact]
    public void EncodeBf_OutOfRangeSoftLimit_ClampsToBoundary()
    {
        // 软上限超范围的值会被钳制到边界，驱动层不会主动发送越界值。
        var frame = CoyoteV3Codec.EncodeBf(201, -1, 128, 128, 128, 128);

        Assert.Equal("BFC800", Convert.ToHexString(frame)[..6]);
    }

    // ---------- B1 回执 ----------

    [Fact]
    public void TryDecodeB1_ValidFrame_ReturnsSequenceAndStrengths()
    {
        var ok = CoyoteV3Codec.TryDecodeB1([0xB1, 0x01, 0x19, 0x0A], out var seq, out var a, out var b);

        Assert.True(ok);
        Assert.Equal(1, seq);
        Assert.Equal(25, a);
        Assert.Equal(10, b);
    }

    [Fact]
    public void TryDecodeB1_NotB1_ReturnsFalse()
    {
        Assert.False(CoyoteV3Codec.TryDecodeB1([0xB0, 0x01, 0x19, 0x0A], out _, out _, out _));
    }

    // ---------- WebSocket 消息 ----------

    [Fact]
    public async Task WebSocketDriver_OpenAsync_SendsBindMessage()
    {
        var driver = new CoyoteV3WebSocketDriver(new FakePluginContext());
        var lease = new FakeEndpointLease(new ProtocolProfileId("dglab.coyote.v3.ws"), TransportKind.WebSocket);

        await driver.OpenAsync(lease, CancellationToken.None);

        var bind = Assert.Single(lease.Writes);
        Assert.Equal("bind", bind.Route);
        Assert.Equal("{\"target\":\"47L121000\"}", bind.JsonText);
    }

    [Fact]
    public async Task WebSocketDriver_IntensityCommand_SendsStrengthJsonAndWaitsForAck()
    {
        var driver = new CoyoteV3WebSocketDriver(new FakePluginContext());
        var lease = new FakeEndpointLease(new ProtocolProfileId("dglab.coyote.v3.ws"), TransportKind.WebSocket);
        await driver.OpenAsync(lease, CancellationToken.None);
        driver.Configure(DeviceInstanceId.New(), 1);
        lease.ClearWrites();

        using var payload = System.Text.Json.JsonDocument.Parse("{\"channelA\":50,\"channelB\":60}");
        var command = new DeviceCommandEnvelope(
            CommandId.New(),
            DeviceInstanceId.New(),
            new CapabilityId("intensity.output"),
            "set.level",
            payload.RootElement.Clone(),
            new CommandSource(CommandSourceKind.UserInterface, "test"),
            CommandDelivery.Idempotent,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5),
            0,
            1);

        var executing = driver.ExecuteAsync(command, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => lease.Writes.Any(w => w.Route == "strength"), 2000);

        var strength = lease.Writes.Single(w => w.Route == "strength");
        using var json = System.Text.Json.JsonDocument.Parse(strength.JsonText!);
        Assert.Equal(50, json.RootElement.GetProperty("a").GetInt32());
        Assert.Equal(60, json.RootElement.GetProperty("b").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("seq").GetInt32());
        Assert.False(executing.IsCompleted);

        // 喂入对应 seq 的强度回执。
        driver.OnMessage("{\"type\":\"strength\",\"seq\":1,\"a\":50,\"b\":60}");
        var result = await executing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(DeviceCommandStatus.Executed, result.Status);
    }

    [Fact]
    public async Task WebSocketDriver_WaveformCommand_QueuesWaveformJson()
    {
        var driver = new CoyoteV3WebSocketDriver(new FakePluginContext());
        var lease = new FakeEndpointLease(new ProtocolProfileId("dglab.coyote.v3.ws"), TransportKind.WebSocket);
        await driver.OpenAsync(lease, CancellationToken.None);
        lease.ClearWrites();

        using var payload = System.Text.Json.JsonDocument.Parse(
            "{\"channelA\":{\"frequencies\":[10,10,10,10],\"strengths\":[0,10,20,30]},\"channelB\":{\"frequencies\":[10,10,10,10],\"strengths\":[0,0,0,0]}}");
        var command = new DeviceCommandEnvelope(
            CommandId.New(),
            DeviceInstanceId.New(),
            new CapabilityId("waveform.stream"),
            "start.frame",
            payload.RootElement.Clone(),
            new CommandSource(CommandSourceKind.UserInterface, "test"),
            CommandDelivery.StreamingFrame,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5),
            0,
            1);

        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.AcceptedByTransport, result.Status);
        await WaitUntilAsync(() => lease.Writes.Any(w => w.Route == "waveform"), 2000);
        Assert.Contains(lease.Writes, w => w.Route == "waveform");
    }

    [Fact]
    public async Task WebSocketDriver_ClearQueue_SendsClearMessage()
    {
        var driver = new CoyoteV3WebSocketDriver(new FakePluginContext());
        var lease = new FakeEndpointLease(new ProtocolProfileId("dglab.coyote.v3.ws"), TransportKind.WebSocket);
        await driver.OpenAsync(lease, CancellationToken.None);
        lease.ClearWrites();

        using var payload = System.Text.Json.JsonDocument.Parse("{}");
        var command = new DeviceCommandEnvelope(
            CommandId.New(),
            DeviceInstanceId.New(),
            new CapabilityId("waveform.clear"),
            "clear.queue",
            payload.RootElement.Clone(),
            new CommandSource(CommandSourceKind.UserInterface, "test"),
            CommandDelivery.Idempotent,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5),
            0,
            1);

        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.AcceptedByTransport, result.Status);
        Assert.Contains(lease.Writes, w => w.Route == "clear");
    }

    [Fact]
    public void WebSocketDriver_OnMessage_ErrorAndDisconnect_ProduceTelemetry()
    {
        var driver = new CoyoteV3WebSocketDriver(new FakePluginContext());
        var instanceId = DeviceInstanceId.New();
        driver.Configure(instanceId, 1);

        driver.OnMessage("{\"type\":\"error\",\"code\":\"link_lost\"}");
        driver.OnMessage("{\"type\":\"disconnect\"}");

        var telemetry = new List<DeviceTelemetryEnvelope>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            foreach (var item in ReadAll(driver, cts.Token).Take(2))
            {
                telemetry.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Equal(2, telemetry.Count);
        Assert.Equal("connection.status", telemetry[0].CapabilityId.Value);
        Assert.Equal(instanceId, telemetry[0].DeviceInstanceId);
        Assert.Equal(1, telemetry[0].SessionEpoch);
    }

    private static IEnumerable<DeviceTelemetryEnvelope> ReadAll(CoyoteV3WebSocketDriver driver, CancellationToken cancellationToken)
    {
        var enumerator = driver.ReadTelemetryAsync(cancellationToken).GetAsyncEnumerator();
        while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
        {
            yield return enumerator.Current;
        }
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