using System.Text;
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
/// 灌肠机一代（TDL_YISKJ-003 V1.0）协议测试。
/// 覆盖 16 字节明文 + AES-128 ECB 固定密钥、随机填充可注入、
/// 蠕动泵/抽水泵/暂停/查询/电量，双压 200ms 上报，以及安全停止使用官方暂停指令。
/// </summary>
public sealed class EnemaV10ProtocolTests
{
    private static readonly ProtocolProfileId ProfileId = new("yokonex.enema.v1_0");

    private static readonly byte[] PeristalticPadding =
        [0x5A, 0x15, 0xD8, 0x1E, 0xC2, 0xD3, 0x72, 0x4A, 0x63];

    private static readonly byte[] SuctionPadding =
        [0x4E, 0x59, 0xDF, 0x2B, 0x42, 0x48, 0xF3, 0xA8, 0xB1];

    private static readonly byte[] TwelveBytePadding =
        [0x9B, 0x2F, 0x53, 0xC0, 0x15, 0x74, 0x5D, 0xE1, 0xA6, 0x39, 0xB4, 0x78];

    private static EnemaV10Codec CreateCodec() => new(_ => TwelveBytePadding);

    // ---------- AES 黄金向量 ----------

    [Fact]
    public void BuildPeristalticPump_OfficialPlaintext_EncryptsToOfficialCiphertext()
    {
        var codec = new EnemaV10Codec(_ => PeristalticPadding);
        var ciphertext = codec.BuildPeristalticPump(EnemaPumpState.Forward, 60);

        // 官方明文 BF 0F A0 01 01 00 3C ... 加密后为 1A 46 4D ...
        Assert.Equal(
            new byte[] { 0x1A, 0x46, 0x4D, 0x2F, 0xC2, 0x99, 0xC5, 0x56, 0x14, 0x0A, 0xCE, 0x01, 0x50, 0x84, 0x75, 0x1D },
            ciphertext);
    }

    [Fact]
    public void BuildPeristalticPump_DecryptsBackToPlaintext()
    {
        var codec = new EnemaV10Codec(_ => PeristalticPadding);
        var ciphertext = codec.BuildPeristalticPump(EnemaPumpState.Forward, 60);

        // 通过 TryParseReport 之外的解密路径验证明文结构。
        var plaintext = Decrypt(ciphertext);
        Assert.Equal(
            new byte[] { 0xBF, 0x0F, 0xA0, 0x01, 0x01, 0x00, 0x3C, 0x5A, 0x15, 0xD8, 0x1E, 0xC2, 0xD3, 0x72, 0x4A, 0x63 },
            plaintext);
    }

    [Fact]
    public void BuildSuctionPump_DecryptsToOfficialPlaintext()
    {
        var codec = new EnemaV10Codec(_ => SuctionPadding);
        var ciphertext = codec.BuildSuctionPump(EnemaPumpState.Forward, 60);

        var plaintext = Decrypt(ciphertext);
        Assert.Equal(
            new byte[] { 0xBF, 0x0F, 0xA0, 0x02, 0x01, 0x00, 0x3C, 0x4E, 0x59, 0xDF, 0x2B, 0x42, 0x48, 0xF3, 0xA8, 0xB1 },
            plaintext);
    }

    [Fact]
    public void BuildPause_DecryptsToOfficialPlaintext()
    {
        var codec = new EnemaV10Codec(_ => TwelveBytePadding);
        var ciphertext = codec.BuildPause();

        var plaintext = Decrypt(ciphertext);
        Assert.Equal(
            new byte[] { 0xBF, 0x0F, 0xA0, 0x03, 0x9B, 0x2F, 0x53, 0xC0, 0x15, 0x74, 0x5D, 0xE1, 0xA6, 0x39, 0xB4, 0x78 },
            plaintext);
    }

    // ---------- 报告解析 ----------

    [Fact]
    public void TryParseReport_WorkStatus_OfficialCiphertext()
    {
        var codec = CreateCodec();
        var ciphertext = new byte[]
        {
            0x8B, 0x3D, 0x70, 0x15, 0x08, 0xC0, 0x5F, 0xFE,
            0xF9, 0x0B, 0xC3, 0x58, 0x76, 0x35, 0xF8, 0xC4
        };

        Assert.True(codec.TryParseReport(ciphertext, out var report));
        var status = Assert.IsType<EnemaReport.WorkStatus>(report);
        Assert.Equal(EnemaPumpState.Forward, status.Peristaltic);
        Assert.Equal(EnemaPumpState.Stop, status.Suction);
    }

    [Fact]
    public void TryParseReport_Pressure_DecodesDualPressure()
    {
        var codec = CreateCodec();
        var ciphertext = new byte[]
        {
            0xB1, 0x84, 0x91, 0xA1, 0x55, 0xFF, 0x59, 0xD7,
            0xC7, 0x2E, 0xDF, 0xB0, 0xF3, 0x7B, 0x65, 0x41
        };

        Assert.True(codec.TryParseReport(ciphertext, out var report));
        var pressure = Assert.IsType<EnemaReport.Pressure>(report);
        Assert.Equal(0x0103, pressure.PressureA);
        Assert.Equal(0x0210, pressure.PressureB);
    }

    [Fact]
    public void TryParseReport_Battery_DecodesPercent()
    {
        var codec = CreateCodec();
        var ciphertext = new byte[]
        {
            0x69, 0xA9, 0xA8, 0xEA, 0x85, 0x64, 0x7D, 0x15,
            0x65, 0x2E, 0x9E, 0x06, 0xF8, 0xFB, 0x7A, 0xFF
        };

        Assert.True(codec.TryParseReport(ciphertext, out var report));
        var battery = Assert.IsType<EnemaReport.Battery>(report);
        Assert.Equal(0x10, battery.Percent);
    }

    [Fact]
    public void TryParseReport_ShortCiphertext_IsRejected()
    {
        var codec = CreateCodec();
        Assert.False(codec.TryParseReport(new byte[] { 0x01, 0x02 }, out _));
    }

    // ---------- 驱动 ----------

    [Fact]
    public async Task OpenAsync_SendsStatusAndBatteryQueries()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();

        await driver.OpenAsync(lease, CancellationToken.None);

        Assert.Equal(2, lease.BinaryWrites.Count);
        var queryStatus = Decrypt(lease.BinaryWrites[0].Payload);
        var getBattery = Decrypt(lease.BinaryWrites[1].Payload);
        Assert.Equal(0x04, queryStatus[3]);
        Assert.Equal(0x05, getBattery[3]);
    }

    [Fact]
    public async Task ExecuteAsync_PeristalticForward_WritesEncryptedFrame()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = new EnemaV10Driver(
            ProfileId,
            new FakePluginLogger(),
            new EnemaV10Codec(len => len == 9 ? PeristalticPadding : TwelveBytePadding));
        await driver.OpenAsync(lease, CancellationToken.None);
        lease.BinaryWrites.Clear();

        var command = YokonexTestCommands.Create(
            "enema.pump.control",
            "set.pump",
            "{\"pump\":\"peristaltic\",\"state\":\"forward\",\"durationSeconds\":60}");

        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.AcceptedByTransport, result.Status);
        var write = Assert.Single(lease.BinaryWrites);
        var plaintext = Decrypt(write.Payload);
        Assert.Equal(
            new byte[] { 0xBF, 0x0F, 0xA0, 0x01, 0x01, 0x00, 0x3C, 0x5A, 0x15, 0xD8, 0x1E, 0xC2, 0xD3, 0x72, 0x4A, 0x63 },
            plaintext);
    }

    [Fact]
    public async Task ExecuteAsync_SuctionReverse_IsRejected()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);

        var command = YokonexTestCommands.Create(
            "enema.pump.control",
            "set.pump",
            "{\"pump\":\"suction\",\"state\":\"reverse\",\"durationSeconds\":60}");
        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task SafeStop_SendsOfficialPauseWork_StoppingBothPumps()
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
        var plaintext = Decrypt(write.Payload);
        // 官方暂停工作指令：BF 0F A0 03 ...
        Assert.Equal(0x03, plaintext[3]);
    }

    [Fact]
    public async Task SafeStop_UnknownGatt_ReturnsOutcomeUnknown()
    {
        var lease = new FakeEndpointLease(ProfileId) { NextSendStatus = EndpointOperationStatus.OutcomeUnknown };
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        lease.BinaryWrites.Clear();

        var result = await driver.SafeStopAsync(
            new SafeStopContext(YokonexTestCommands.InstanceId, 1, 1, DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(SafeStopStatus.OutcomeUnknown, result.Status);
        Assert.Single(lease.BinaryWrites);
    }

    [Fact]
    public async Task OnNotify_BatteryCiphertext_EmitsTelemetry()
    {
        var lease = new FakeEndpointLease(ProfileId);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        driver.Configure(YokonexTestCommands.InstanceId, 1);

        driver.OnNotify(new byte[]
        {
            0x69, 0xA9, 0xA8, 0xEA, 0x85, 0x64, 0x7D, 0x15,
            0x65, 0x2E, 0x9E, 0x06, 0xF8, 0xFB, 0x7A, 0xFF
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var telemetry = ReadAll(driver, cts.Token).FirstOrDefault();
        Assert.NotNull(telemetry);
        Assert.Equal(16, telemetry!.Payload.GetProperty("percent").GetInt32());
    }

    [Fact]
    public async Task Logs_NeverContainProtocolKeyOrControlFrames()
    {
        var logger = new FakePluginLogger();
        var driver = new EnemaV10Driver(ProfileId, logger);

        // 握手查询失败会让驱动写日志；随后执行泵控制也会失败。
        var lease = new FakeEndpointLease(ProfileId) { NextSendStatus = EndpointOperationStatus.Failed };
        await driver.OpenAsync(lease, CancellationToken.None);
        var command = YokonexTestCommands.Create(
            "enema.pump.control",
            "set.pump",
            "{\"pump\":\"peristaltic\",\"state\":\"forward\",\"durationSeconds\":60}");
        await driver.ExecuteAsync(command, CancellationToken.None);

        var text = logger.AllText;
        var keyHex = Convert.ToHexString(EnemaV10Codec.FixedKey);
        Assert.DoesNotContain(keyHex, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1A464D2F", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BF0FA0", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0xA0", text, StringComparison.OrdinalIgnoreCase);
    }

    private static EnemaV10Driver CreateDriver() =>
        new(ProfileId, new FakePluginLogger());

    private static byte[] Decrypt(byte[] ciphertext)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = EnemaV10Codec.FixedKey.ToArray();
        aes.Mode = System.Security.Cryptography.CipherMode.ECB;
        aes.Padding = System.Security.Cryptography.PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    private static IEnumerable<DeviceTelemetryEnvelope> ReadAll(EnemaV10Driver driver, CancellationToken cancellationToken)
    {
        var enumerator = driver.ReadTelemetryAsync(cancellationToken).GetAsyncEnumerator();
        while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
        {
            yield return enumerator.Current;
        }
    }
}
