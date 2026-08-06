using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChargingPanel.Core.Application.Devices;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Events;
using ChargingPanel.Core.Network;
using ChargingPanel.Core.Network.RemoteControl;
using Xunit;

namespace ChargingPanel.Core.Tests;

/// <summary>
/// Task 21 端到端测试：完整认证会话下发命令并接收 Dispatcher 级最终结果。
/// 覆盖 Room 远控路径与 MOD Bridge（令牌 + 能力权限 + 直接命令）路径。
/// </summary>
public sealed class RemoteControlEndToEndTests : IDisposable
{
    private readonly string _dbPath;

    public RemoteControlEndToEndTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"charging_panel_remote_e2e_{Guid.NewGuid():N}.db");
        Database.Initialize(_dbPath);
    }

    [Fact]
    public async Task AuthenticatedSession_IssueCommand_ReturnsDispatcherFinalResult()
    {
        using var harness = RuntimeHarness.CreateDglab(out var instanceId);
        var authenticator = new RemoteCommandAuthenticator(harness.AppService);
        var session = authenticator.CreateSession("room-controller", RemotePermissionScope.All);

        var request = BuildSignedRequest(session, authenticator.TimeProvider.GetUtcNow(), nonce: 1, instanceId);
        var result = await authenticator.DispatchAsync(request);

        Assert.Equal(RemoteCommandFinalStatus.Executed, result.Status);
        Assert.True(result.IsExecuted);
        Assert.Equal(request.CommandId, result.CommandId);
        Assert.Contains(harness.Driver.Calls, call => call.StartsWith("set.level:"));
    }

    [Fact]
    public async Task AuthenticatedSession_MultipleCommands_ReplayBlocked_ButFreshCommandsExecute()
    {
        using var harness = RuntimeHarness.CreateDglab(out var instanceId);
        var authenticator = new RemoteCommandAuthenticator(harness.AppService);
        var session = authenticator.CreateSession("room-controller", RemotePermissionScope.All);
        var now = authenticator.TimeProvider.GetUtcNow();

        var firstRequest = BuildSignedRequest(session, now, nonce: 1, instanceId);
        var secondRequest = BuildSignedRequest(session, now, nonce: 2, instanceId);

        var first = await authenticator.DispatchAsync(firstRequest);
        var second = await authenticator.DispatchAsync(secondRequest);
        var replayed = await authenticator.DispatchAsync(firstRequest);

        Assert.Equal(RemoteCommandFinalStatus.Executed, first.Status);
        Assert.Equal(RemoteCommandFinalStatus.Executed, second.Status);
        Assert.Equal(RemoteCommandFinalStatus.Rejected, replayed.Status);
        Assert.Equal("replay.command_id", replayed.ReasonCode);
        Assert.Equal(2, harness.Driver.Calls.Count);
    }

    [Fact]
    public async Task RoomService_AuthenticatedSession_DispatchRemoteCommand_ExecutesOnDriver()
    {
        using var harness = RuntimeHarness.CreateDglab(out var instanceId);
        using var room = new RoomService(new FakePresenceQuery());
        room.AttachRemoteControlService(harness.AppService);

        var session = room.CreateRemoteSession("room-controller", RemotePermissionScope.All);
        Assert.NotNull(session);

        var request = BuildSignedRequest(session!, room.GetRemoteTimeProvider().GetUtcNow(), nonce: 1, instanceId);
        var result = await room.DispatchRemoteCommandAsync(request);

        Assert.Equal(RemoteCommandFinalStatus.Executed, result.Status);
        Assert.True(result.IsExecuted);
        Assert.Contains(harness.Driver.Calls, call => call.StartsWith("set.level:"));
    }

    [Fact]
    public async Task ModBridgeService_TokenRequired_HelloWithoutToken_Rejected()
    {
        using var harness = RuntimeHarness.CreateDglab(out _);
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);
        using var modBridge = new ModBridgeService(EventBus.Instance, eventService, harness.Query);
        await modBridge.StartAsync();

        var scriptId = $"token_script_{Guid.NewGuid():N}";
        await modBridge.RegisterScriptAsync(scriptId, "Token Script", "1.0.0");
        await modBridge.StartWebSocketForScriptAsync(scriptId);
        modBridge.SetScriptToken(scriptId, "test-token");

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{ModBridgeService.FixedWebSocketPort}/"),
            CancellationToken.None);

        await SendWebSocketJsonAsync(socket, new { type = "hello", scriptId, name = "Token Script", game = "UnitTest", version = "1.0.0" });

        using var error = await ReceiveWebSocketJsonAsync(socket);
        Assert.Equal("error", error.RootElement.GetProperty("type").GetString());
        Assert.Equal("auth.invalid_token", error.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ModBridgeService_AuthenticatedCommand_ExecutesOnDriver_WithDispatcherFinalResult()
    {
        using var harness = RuntimeHarness.CreateDglab(out var instanceId);
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);
        using var modBridge = new ModBridgeService(EventBus.Instance, eventService, harness.Query);
        await modBridge.StartAsync();

        var scriptId = $"cmd_script_{Guid.NewGuid():N}";
        await modBridge.RegisterScriptAsync(scriptId, "Cmd Script", "1.0.0");
        await modBridge.StartWebSocketForScriptAsync(scriptId);
        const string token = "test-token";
        modBridge.SetScriptToken(scriptId, token);
        modBridge.GrantScriptCapability(scriptId, "intensity.output");
        modBridge.AttachRemoteControlService(harness.AppService);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{ModBridgeService.FixedWebSocketPort}/"),
            CancellationToken.None);

        await SendWebSocketJsonAsync(socket, new
        {
            type = "hello",
            scriptId,
            name = "Cmd Script",
            game = "UnitTest",
            version = "1.0.0",
            token
        });

        string sessionId;
        using (var welcome = await ReceiveWebSocketJsonAsync(socket))
        {
            Assert.Equal("welcome", welcome.RootElement.GetProperty("type").GetString());
            sessionId = welcome.RootElement.GetProperty("sessionId").GetString()!;
        }

        var commandId = ChargingPanel.Contracts.Identifiers.CommandId.New();
        var nonce = 1L;
        var timestamp = DateTimeOffset.UtcNow;
        using var payloadDoc = JsonDocument.Parse("{\"actuatorId\":\"main\",\"level\":42}");
        var payload = payloadDoc.RootElement.Clone();
        var payloadBytes = Encoding.UTF8.GetBytes(payload.GetRawText());
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var authTag = RemoteCommandSigning.ComputeAuthTag(
            key,
            sessionId,
            commandId,
            nonce,
            timestamp,
            instanceId,
            "intensity.output",
            "set.level",
            payloadBytes);

        await SendWebSocketJsonAsync(socket, new
        {
            type = "command",
            commandId = commandId.Value.ToString("D"),
            capability = "intensity.output",
            operation = "set.level",
            deviceId = instanceId.Value.ToString("D"),
            nonce,
            timestamp = timestamp.ToUnixTimeMilliseconds(),
            authTag,
            payload = JsonDocument.Parse(payload.GetRawText()).RootElement
        });

        using (var ack = await ReceiveWebSocketJsonAsync(socket))
        {
            Assert.Equal("command_result", ack.RootElement.GetProperty("type").GetString());
            Assert.Equal("Executed", ack.RootElement.GetProperty("status").GetString());
            Assert.Equal(commandId.Value.ToString("D"), ack.RootElement.GetProperty("commandId").GetString());
        }

        Assert.Contains(harness.Driver.Calls, call => call.StartsWith("set.level:"));
    }

    // ---------- 工具 ----------

    private static RemoteCommandRequest BuildSignedRequest(
        RemoteControlSession session,
        DateTimeOffset timestamp,
        long nonce,
        ChargingPanel.Contracts.Identifiers.DeviceInstanceId deviceInstanceId)
    {
        var commandId = ChargingPanel.Contracts.Identifiers.CommandId.New();
        using var doc = JsonDocument.Parse("{\"actuatorId\":\"main\",\"level\":42}");
        var payload = doc.RootElement.Clone();
        var payloadBytes = Encoding.UTF8.GetBytes(payload.GetRawText());
        var authTag = session.ComputeAuthTag(commandId, nonce, timestamp, deviceInstanceId, "intensity.output", "set.level", payloadBytes);

        return new RemoteCommandRequest(
            commandId,
            session.SessionId,
            nonce,
            timestamp,
            deviceInstanceId,
            "intensity.output",
            "set.level",
            payload,
            authTag);
    }

    private sealed class FakePresenceQuery : IDevicePresenceQuery
    {
        public bool HasRegisteredDevices() => true;
    }

    private static async Task SendWebSocketJsonAsync(ClientWebSocket socket, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private static async Task<JsonDocument> ReceiveWebSocketJsonAsync(ClientWebSocket socket)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("WebSocket closed unexpectedly");
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        ms.Position = 0;
        return await JsonDocument.ParseAsync(ms);
    }

    public void Dispose()
    {
        try
        {
            Database.Instance.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch
        {
            // ignore
        }
    }
}
