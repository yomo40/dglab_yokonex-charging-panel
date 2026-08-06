using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using ChargingPanel.Infrastructure.Windows.ApiBridge;
using ChargingPanel.Infrastructure.Windows.IM;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Networking;

/// <summary>
/// API Bridge 客户端测试。对进程内假 Bridge 服务器做真实 HTTP/WS 集成，
/// 并验证 Bridge 故障不改变 Native IM 状态（两者是独立 Endpoint）。
/// </summary>
public sealed class ApiBridgeClientTests
{
    [Fact]
    public async Task HttpGetHealth_ReturnsBridgeHealth()
    {
        using var server = new FakeApiBridgeServer();
        using var client = new ApiBridgeHttpClient(server.Uri);

        var health = await client.GetHealthAsync(CancellationToken.None);

        Assert.Equal("ok", health.Status);
        Assert.True(health.ImReady);
        Assert.Equal("game_5", health.Uid);
        Assert.Equal("5", health.UserId);
    }

    [Fact]
    public async Task HttpStatus_ReturnsReadyConfig()
    {
        using var server = new FakeApiBridgeServer();
        using var client = new ApiBridgeHttpClient(server.Uri);

        var status = await client.GetStatusAsync(CancellationToken.None);

        Assert.True(status.IsReady);
        Assert.Equal("game_5", status.Uid);
        Assert.Equal("1400000000", status.AppId);
        Assert.True(status.HasToken);
        Assert.True(status.HasSign);
    }

    [Fact]
    public async Task HttpLoginAndSendCommand_Succeeds()
    {
        using var server = new FakeApiBridgeServer();
        using var client = new ApiBridgeHttpClient(server.Uri);

        var login = await client.LoginAsync("5", "test-token", CancellationToken.None);
        Assert.True(login.Success);
        Assert.Equal("game_5", login.Uid);

        var send = await client.SendCommandAsync("player_hurt", CancellationToken.None);
        Assert.True(send.Success);
    }

    [Fact]
    public async Task HttpReinit_Succeeds()
    {
        using var server = new FakeApiBridgeServer();
        using var client = new ApiBridgeHttpClient(server.Uri);

        var reinit = await client.ReinitAsync(CancellationToken.None);
        Assert.True(reinit.Success);
    }

    [Fact]
    public async Task WebSocketPing_ReturnsPong()
    {
        using var server = new FakeApiBridgeServer();
        await using var client = new ApiBridgeWebSocketClient(server.WebSocketUri);

        await client.ConnectAsync(CancellationToken.None);
        var response = await client.PingAsync(CancellationToken.None);

        Assert.Equal("pong", response.GetProperty("type").GetString());
    }

    [Fact]
    public async Task WebSocketSendCommand_ReturnsCommandResult()
    {
        using var server = new FakeApiBridgeServer();
        await using var client = new ApiBridgeWebSocketClient(server.WebSocketUri);

        await client.ConnectAsync(CancellationToken.None);
        var response = await client.SendCommandAsync("player_hurt", CancellationToken.None);

        Assert.Equal("commandResult", response.GetProperty("type").GetString());
        Assert.True(response.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task WebSocketGetStatus_AndLogin_Succeed()
    {
        using var server = new FakeApiBridgeServer();
        await using var client = new ApiBridgeWebSocketClient(server.WebSocketUri);

        await client.ConnectAsync(CancellationToken.None);

        var status = await client.GetStatusAsync(CancellationToken.None);
        Assert.Equal("status", status.GetProperty("type").GetString());

        var login = await client.LoginAsync("5", "token", CancellationToken.None);
        Assert.Equal("status", login.GetProperty("type").GetString());
    }

    [Fact]
    public async Task WebSocketConnectedPush_ReceivedOnConnect()
    {
        using var server = new FakeApiBridgeServer();
        await using var client = new ApiBridgeWebSocketClient(server.WebSocketUri);

        await client.ConnectAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var push in client.ReadPushesAsync(cts.Token))
        {
            Assert.Equal("connected", push.GetProperty("type").GetString());
            Assert.Equal("game_5", push.GetProperty("data").GetProperty("uid").GetString());
            return;
        }

        Assert.Fail("未收到 connected 推送。");
    }

    [Fact]
    public async Task BridgeFailure_DoesNotAffectNativeImAccount()
    {
        // Bridge 与 Native IM 是独立 Endpoint：Bridge 指向不可达服务器，
        // Native IM 账户池的登录状态不受任何影响。
        using var server = new FakeApiBridgeServer();
        var deadPort = server.Uri.Port + 1;
        using var client = new ApiBridgeHttpClient(new Uri($"http://127.0.0.1:{deadPort}"));

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetHealthAsync(CancellationToken.None));

        var nativeClient = new FakeTencentImNativeClient();
        await using var pool = new ImConnectionPool(
            clientFactory: _ => nativeClient,
            userSigResolver: (_, _) => Task.FromResult<string?>("sig"));
        var key = new ImAccountKey(1400000003, "game_7", "cred:game_7");
        await using var target = await pool.AcquireTargetAsync(key, "30", CancellationToken.None);

        Assert.True(nativeClient.IsLoggedIn);
        Assert.Equal(1, nativeClient.LoginCount);
        Assert.Equal(0, nativeClient.LogoutCount);
    }
}
