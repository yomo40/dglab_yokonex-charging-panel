using System.Text.Json;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Telemetry;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.DeviceIntegration.Tests.Fixtures.Yokonex;
using ChargingPanel.DeviceIntegration.Tests.Networking;
using ChargingPanel.Infrastructure.Windows.IM;
using ChargingPanel.Vendor.Yokonex.Drivers;
using ChargingPanel.Vendor.Yokonex.Protocol;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Yokonex;

/// <summary>
/// 役次元 API Bridge 测试。
/// 覆盖 HTTP 与 WS 两套路由、WS 六类推送、send-command 载荷，
/// 以及"Bridge 失败不影响 Native IM 状态"的独立 Endpoint 不变量。
/// </summary>
public sealed class YokonexApiBridgeTests
{
    private static readonly ProtocolProfileId ProfileId = new("yokonex.api_bridge.v1");

    // ---------- 编解码 ----------

    [Fact]
    public void BuildSendCommandPayload_ProducesCommandId()
    {
        var payload = YokonexApiBridgeCodec.BuildSendCommandPayload("player_hurt");
        Assert.Equal("player_hurt", payload.GetProperty("commandId").GetString());
    }

    [Fact]
    public void BuildLoginPayload_ContainsUidAndToken()
    {
        var payload = YokonexApiBridgeCodec.BuildLoginPayload("5", "token-value");
        Assert.Equal("5", payload.GetProperty("uid").GetString());
        Assert.Equal("token-value", payload.GetProperty("token").GetString());
    }

    [Fact]
    public void TryParsePush_AllSixPublicPushTypes()
    {
        var vectors = new (string Json, BridgePushKind Kind)[]
        {
            ("{\"type\":\"connected\",\"data\":{\"isReady\":true}}", BridgePushKind.Connected),
            ("{\"type\":\"status\",\"data\":{\"isReady\":true}}", BridgePushKind.Status),
            ("{\"type\":\"network\",\"data\":{\"state\":\"CONNECTED\"}}", BridgePushKind.Network),
            ("{\"type\":\"message\",\"data\":{\"count\":1}}", BridgePushKind.Message),
            ("{\"type\":\"heartbeat\",\"data\":{\"isReady\":true}}", BridgePushKind.Heartbeat),
            ("{\"type\":\"error\",\"message\":\"boom\"}", BridgePushKind.Error)
        };

        foreach (var (json, expected) in vectors)
        {
            Assert.True(YokonexApiBridgeCodec.TryParsePush(json, out var push));
            Assert.Equal(expected, push!.Kind);
        }
    }

    [Fact]
    public void TryParseHealth_AndStatus_AndSendResult()
    {
        using var health = JsonDocument.Parse("{\"status\":\"ok\",\"imReady\":true}");
        Assert.True(YokonexApiBridgeCodec.TryParseHealth(health.RootElement, out var ok, out var imReady));
        Assert.True(ok);
        Assert.True(imReady);

        using var status = JsonDocument.Parse("{\"isReady\":true,\"config\":{}}");
        Assert.True(YokonexApiBridgeCodec.TryParseStatus(status.RootElement, out var isReady));
        Assert.True(isReady);

        using var sendOk = JsonDocument.Parse("{\"success\":true}");
        Assert.True(YokonexApiBridgeCodec.TryParseSendResult(sendOk.RootElement, out var success));
        Assert.True(success);

        using var sendFail = JsonDocument.Parse("{\"success\":false,\"message\":\"IM 未就绪\"}");
        Assert.True(YokonexApiBridgeCodec.TryParseSendResult(sendFail.RootElement, out var fail));
        Assert.False(fail);
    }

    // ---------- 驱动：HTTP ----------

    [Fact]
    public async Task HttpDriver_Open_ChecksHealth()
    {
        var lease = new FakeEndpointLease(ProfileId, TransportKind.Http);
        var driver = CreateDriver();

        await driver.OpenAsync(lease, CancellationToken.None);

        var send = Assert.Single(lease.Sends);
        Assert.Equal(YokonexApiBridgeCodec.Http.Health, send.Route);
    }

    [Fact]
    public async Task HttpDriver_ExecuteSendCommand_UsesSendCommandRoute()
    {
        var lease = new FakeEndpointLease(ProfileId, TransportKind.Http);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        lease.Sends.Clear();

        var command = YokonexTestCommands.Create("bridge.command.send", "send", "{\"commandId\":\"player_hurt\"}");
        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.AcceptedByTransport, result.Status);
        var send = Assert.Single(lease.Sends);
        Assert.Equal(YokonexApiBridgeCodec.Http.SendCommand, send.Route);
        Assert.Equal("player_hurt", send.Payload.Json!.Value.GetProperty("commandId").GetString());
    }

    [Fact]
    public async Task HttpDriver_ExecuteLogin_DoesNotLogCredentials()
    {
        var logger = new FakePluginLogger();
        var lease = new FakeEndpointLease(ProfileId, TransportKind.Http);
        var driver = new YokonexApiBridgeDriver(ProfileId, logger);
        await driver.OpenAsync(lease, CancellationToken.None);
        lease.Sends.Clear();

        var command = YokonexTestCommands.Create("bridge.login", "login", "{\"uid\":\"5\",\"token\":\"super-secret\"}");
        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.AcceptedByTransport, result.Status);
        var send = Assert.Single(lease.Sends);
        Assert.Equal(YokonexApiBridgeCodec.Http.Login, send.Route);
        Assert.DoesNotContain("super-secret", logger.AllText, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- 驱动：WS ----------

    [Fact]
    public async Task WsDriver_Open_Pings()
    {
        var lease = new FakeEndpointLease(ProfileId, TransportKind.WebSocket);
        var driver = CreateDriver();

        await driver.OpenAsync(lease, CancellationToken.None);

        var send = Assert.Single(lease.Sends);
        Assert.Equal(YokonexApiBridgeCodec.Ws.Ping, send.Route);
    }

    [Fact]
    public async Task WsDriver_ExecuteSendCommand_UsesSendCommandRoute()
    {
        var lease = new FakeEndpointLease(ProfileId, TransportKind.WebSocket);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        lease.Sends.Clear();

        var command = YokonexTestCommands.Create("bridge.command.send", "send", "{\"commandId\":\"player_hurt\"}");
        var result = await driver.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(DeviceCommandStatus.AcceptedByTransport, result.Status);
        var send = Assert.Single(lease.Sends);
        Assert.Equal(YokonexApiBridgeCodec.Ws.SendCommand, send.Route);
    }

    [Fact]
    public async Task WsDriver_OnMessage_EmitsTelemetryForEachPush()
    {
        var lease = new FakeEndpointLease(ProfileId, TransportKind.WebSocket);
        var driver = CreateDriver();
        await driver.OpenAsync(lease, CancellationToken.None);
        driver.Configure(YokonexTestCommands.InstanceId, 1);

        driver.OnMessage("{\"type\":\"connected\",\"data\":{\"isReady\":true}}");
        driver.OnMessage("{\"type\":\"network\",\"data\":{\"state\":\"CONNECTED\"}}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var items = ReadAll(driver, cts.Token).Take(2).ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal("bridge.telemetry", items[0].CapabilityId.Value);
        Assert.Equal("connected", items[0].Payload.GetProperty("kind").GetString());
        Assert.Equal("network", items[1].Payload.GetProperty("kind").GetString());
    }

    // ---------- 独立 Endpoint 不变量 ----------

    [Fact]
    public async Task BridgeFailure_DoesNotChangeNativeImState()
    {
        // Bridge 与 Native IM 是独立 Endpoint：Bridge 指向故障租约，
        // Native IM 账户池的登录状态不受任何影响。
        var nativeClient = new FakeTencentImNativeClient();
        await using var pool = new ImConnectionPool(
            clientFactory: _ => nativeClient,
            userSigResolver: (_, _) => Task.FromResult<string?>("sig"));
        var key = new ImAccountKey(1400000003, "game_7", "cred:game_7");
        await using var target = await pool.AcquireTargetAsync(key, "30", CancellationToken.None);
        Assert.True(nativeClient.IsLoggedIn);

        // Bridge 驱动使用故障租约，OpenAsync 的握手失败。
        var failingLease = new FakeEndpointLease(ProfileId, TransportKind.Http) { NextSendStatus = EndpointOperationStatus.Failed };
        var bridgeDriver = CreateDriver();
        await Assert.ThrowsAnyAsync<Exception>(() => bridgeDriver.OpenAsync(failingLease, CancellationToken.None).AsTask());

        // Native IM 账户仍然登录，未受影响。
        Assert.Equal(1, nativeClient.LoginCount);
        Assert.Equal(0, nativeClient.LogoutCount);
        Assert.True(nativeClient.IsLoggedIn);
    }

    private static YokonexApiBridgeDriver CreateDriver() =>
        new(ProfileId, new FakePluginLogger());

    private static IEnumerable<DeviceTelemetryEnvelope> ReadAll(YokonexApiBridgeDriver driver, CancellationToken cancellationToken)
    {
        var enumerator = driver.ReadTelemetryAsync(cancellationToken).GetAsyncEnumerator();
        while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
        {
            yield return enumerator.Current;
        }
    }
}
