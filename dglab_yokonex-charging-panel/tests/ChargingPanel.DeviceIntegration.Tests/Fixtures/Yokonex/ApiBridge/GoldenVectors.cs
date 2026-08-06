namespace ChargingPanel.DeviceIntegration.Tests.Fixtures.Yokonex.ApiBridge;

/// <summary>
/// 役次元 API Bridge 协议黄金载荷（来自 yokonex-API-bridge docs）。
/// </summary>
internal static class GoldenVectors
{
    public const string SendCommand = "{\"commandId\":\"player_hurt\"}";

    public const string Login = "{\"uid\":\"5\",\"token\":\"token_value\"}";

    public const string Health = "{\"status\":\"ok\",\"imReady\":true,\"uid\":\"game_5\",\"userId\":\"5\"}";

    public const string Status = "{\"isReady\":true,\"config\":{\"uid\":\"game_5\",\"userId\":\"5\",\"appId\":\"1400000000\",\"hasToken\":true,\"hasSign\":true}}";

    public const string ConnectedPush = "{\"type\":\"connected\",\"message\":\"WebSocket 连接成功\",\"data\":{\"isReady\":true}}";

    public const string NetworkPush = "{\"type\":\"network\",\"data\":{\"state\":\"CONNECTED\"}}";

    public const string MessagePush = "{\"type\":\"message\",\"data\":{\"count\":1,\"messages\":[{\"from\":\"user_123\",\"to\":\"game_123456\",\"type\":\"TIMTextElem\",\"payload\":{\"text\":\"hi\"},\"time\":1234567890}]}}";

    public const string HeartbeatPush = "{\"type\":\"heartbeat\",\"data\":{\"isReady\":true,\"timestamp\":1234567890,\"clients\":2}}";

    public const string ErrorPush = "{\"type\":\"error\",\"message\":\"错误描述\"}";
}
