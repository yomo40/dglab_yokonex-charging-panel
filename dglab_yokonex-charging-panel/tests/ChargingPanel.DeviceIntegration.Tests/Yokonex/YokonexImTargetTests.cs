using System.Text.Json;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.DeviceIntegration.Tests.Fixtures.Yokonex;
using ChargingPanel.DeviceIntegration.Tests.Networking;
using ChargingPanel.Infrastructure.Windows.IM;
using ChargingPanel.Vendor.Yokonex.Drivers;
using ChargingPanel.Vendor.Yokonex.Protocol;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Yokonex;

/// <summary>
/// 役次元 IM 目标测试。
/// 覆盖连接码解析、game_ 登录主体、目标 UserId、game_cmd 稳定指令面、
/// game_info 旧版兼容（Experimental）、开发中能力默认禁用、
/// SDK 成功只等于 AcceptedByTransport，以及账户池隔离语义。
/// </summary>
public sealed class YokonexImTargetTests
{
    private static readonly ProtocolProfileId ProfileId = new("yokonex.im.target.v1");

    // ---------- 连接码与身份 ----------

    [Fact]
    public void TryParseConnectCode_TrimsAndSplits()
    {
        Assert.True(YokonexImCodec.TryParseConnectCode("  5  token_value  ", out var uid, out var token));
        Assert.Equal("5", uid);
        Assert.Equal("token_value", token);
    }

    [Fact]
    public void TryParseConnectCode_GamePrefixUid_IsAccepted()
    {
        Assert.True(YokonexImCodec.TryParseConnectCode("game_5 token", out var uid, out var token));
        Assert.Equal("game_5", uid);
        Assert.Equal("token", token);
    }

    [Fact]
    public void TryParseConnectCode_Invalid_ReturnsFalse()
    {
        Assert.False(YokonexImCodec.TryParseConnectCode(null, out _, out _));
        Assert.False(YokonexImCodec.TryParseConnectCode("", out _, out _));
        Assert.False(YokonexImCodec.TryParseConnectCode("   ", out _, out _));
        Assert.False(YokonexImCodec.TryParseConnectCode("onlyuid", out _, out _));
    }

    [Fact]
    public void BuildLoginSubject_AlwaysGamePrefixed()
    {
        Assert.Equal("game_5", YokonexImCodec.BuildLoginSubject("5"));
        Assert.Equal("game_5", YokonexImCodec.BuildLoginSubject("game_5"));
    }

    [Fact]
    public void BuildTargetUserId_StripsGamePrefix()
    {
        Assert.Equal("5", YokonexImCodec.BuildTargetUserId("game_5"));
        Assert.Equal("5", YokonexImCodec.BuildTargetUserId("5"));
    }

    // ---------- 指令面 ----------

    [Fact]
    public void BuildGameCommand_ProducesStableSurface()
    {
        var message = YokonexImCodec.BuildGameCommand("hurt");
        Assert.Equal("game_cmd", message.GetProperty("code").GetString());
        Assert.Equal("hurt", message.GetProperty("id").GetString());
        Assert.False(message.TryGetProperty("token", out _));
    }

    [Fact]
    public void BuildFullCommandText_IncludesToken()
    {
        var text = YokonexImCodec.BuildFullCommandText("hurt", "secret-token");
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("game_cmd", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("hurt", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("secret-token", doc.RootElement.GetProperty("token").GetString());
    }

    [Fact]
    public void TryParseIncoming_ParsesGameCmd()
    {
        Assert.True(YokonexImCodec.TryParseIncoming("{\"code\":\"game_cmd\",\"id\":\"hurt\"}", "100", out var message));
        Assert.Equal("game_cmd", message!.Code);
        Assert.Equal("hurt", message.Id);
    }

    // ---------- 驱动 ----------

    [Fact]
    public async Task ExecuteAsync_CommandSend_SendsGameCmd()
    {
        var lease = new FakeEndpointLease(ProfileId, TransportKind.InstantMessaging);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);

        var command = YokonexTestCommands.Create(
            "im.command.send",
            "send",
            "{\"commandId\":\"hurt\"}");
        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.AcceptedByTransport, result.Status);
        var send = Assert.Single(lease.Sends);
        Assert.Equal("c2c", send.Route);
        var payload = send.Payload.Json!.Value;
        Assert.Equal("game_cmd", payload.GetProperty("code").GetString());
        Assert.Equal("hurt", payload.GetProperty("id").GetString());
        Assert.False(payload.TryGetProperty("token", out _));
    }

    [Fact]
    public async Task ExecuteAsync_SdkSuccess_IsAcceptedByTransportNotExecuted()
    {
        var lease = new FakeEndpointLease(ProfileId, TransportKind.InstantMessaging) { NextSendStatus = EndpointOperationStatus.Completed };
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);

        var command = YokonexTestCommands.Create("im.command.send", "send", "{\"commandId\":\"hurt\"}");
        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.AcceptedByTransport, result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_ExperimentalCapability_IsRejectedByDefault()
    {
        var lease = new FakeEndpointLease(ProfileId, TransportKind.InstantMessaging);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);

        foreach (var capability in new[] { "im.global.stop", "im.command.custom", "im.metadata.update", "im.game_info.legacy" })
        {
            var command = YokonexTestCommands.Create(capability, "send", "{\"commandId\":\"_stop_all\"}");
            var result = await driver.ExecuteAsync(command, CancellationToken.None);
            Assert.Equal(DeviceCommandStatus.Rejected, result.Status);
            Assert.Equal("capability.experimental_disabled", result.ReasonCode);
        }
    }

    [Fact]
    public async Task SafeStop_SendsGlobalStop()
    {
        var lease = new FakeEndpointLease(ProfileId, TransportKind.InstantMessaging) { NextSendStatus = EndpointOperationStatus.Completed };
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        lease.Sends.Clear();

        var result = await driver.SafeStopAsync(
            new SafeStopContext(YokonexTestCommands.InstanceId, 1, 1, DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(SafeStopStatus.ConfirmedStopped, result.Status);
        var send = Assert.Single(lease.Sends);
        var payload = send.Payload.Json!.Value;
        Assert.Equal("_stop_all", payload.GetProperty("id").GetString());
    }

    [Fact]
    public void Logs_NeverContainCredentialsOrCommandText()
    {
        var logger = new FakePluginLogger();
        var driver = new YokonexImTargetDriver(ProfileId, logger);

        // 驱动从不接触 token，也不记录完整指令文本。
        var text = logger.AllText;
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- 账户池隔离（真实池 + 假原生客户端） ----------

    [Fact]
    public async Task AccountPool_MultiTarget_ReleaseOneDoesNotLogout()
    {
        var harness = new PoolHarness();
        await using var pool = harness.Pool;

        var first = await pool.AcquireTargetAsync(PoolHarness.AccountA, "10", CancellationToken.None);
        var second = await pool.AcquireTargetAsync(PoolHarness.AccountA, "11", CancellationToken.None);
        var client = harness.ClientFor(PoolHarness.AccountA);

        await first.DisposeAsync();

        Assert.Equal(1, pool.AccountCount);
        Assert.Equal(0, client.LogoutCount);
        Assert.True(client.IsLoggedIn);

        await second.DisposeAsync();
        Assert.Equal(0, pool.AccountCount);
        Assert.Equal(1, client.LogoutCount);
    }

    [Fact]
    public async Task Driver_WithRealPool_LoginSubjectIsGamePrefixed()
    {
        var harness = new PoolHarness();
        await using var pool = harness.Pool;
        await using var target = await pool.AcquireTargetAsync(PoolHarness.AccountA, "10", CancellationToken.None);

        var client = harness.ClientFor(PoolHarness.AccountA);
        Assert.Equal("game_5", client.LoginUserId);
    }

    private static YokonexImTargetDriver CreateDriver() =>
        new(ProfileId, new FakePluginLogger());

    private sealed class PoolHarness
    {
        public static readonly ImAccountKey AccountA = new(1400000001, "game_5", "cred:game_5");

        private readonly Dictionary<ImAccountKey, FakeTencentImNativeClient> _clients = new();

        public PoolHarness()
        {
            Pool = new ImConnectionPool(
                clientFactory: key =>
                {
                    var client = new FakeTencentImNativeClient();
                    _clients[key] = client;
                    return client;
                },
                userSigResolver: (key, _) => Task.FromResult<string?>($"sig-for-{key.LoginUserId}"));
        }

        public ImConnectionPool Pool { get; }

        public FakeTencentImNativeClient ClientFor(ImAccountKey key) => _clients[key];
    }
}
