using System.Text.Json;

namespace ChargingPanel.Vendor.Yokonex.Protocol;

/// <summary>API Bridge WS 推送类型。</summary>
public enum BridgePushKind
{
    Connected,
    Status,
    Network,
    Message,
    Heartbeat,
    Error
}

/// <summary>API Bridge 的一则推送帧。</summary>
public sealed record BridgePush(BridgePushKind Kind, JsonElement Data);

/// <summary>
/// 官方 YOKONEX API Bridge 编解码。
/// 覆盖 HTTP（health/status/login/send-command/reinit）与
/// WS（ping/getStatus/login/sendCommand/reinit + connected/status/network/message/heartbeat/error 推送）。
/// 只负责 JSON 载荷形状与解析，不执行任何 I/O。
/// </summary>
public static class YokonexApiBridgeCodec
{
    /// <summary>HTTP 路由。</summary>
    public static class Http
    {
        public const string Health = "health";
        public const string Status = "status";
        public const string Login = "login";
        public const string SendCommand = "send-command";
        public const string Reinit = "reinit";
    }

    /// <summary>WS 路由。</summary>
    public static class Ws
    {
        public const string Ping = "ping";
        public const string GetStatus = "getStatus";
        public const string Login = "login";
        public const string SendCommand = "sendCommand";
        public const string Reinit = "reinit";
    }

    public static JsonElement BuildSendCommandPayload(string commandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        using var document = JsonDocument.Parse($"{{\"commandId\":{JsonSerializer.Serialize(commandId)}}}");
        return document.RootElement.Clone();
    }

    public static JsonElement BuildLoginPayload(string uid, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        using var document = JsonDocument.Parse(
            $"{{\"uid\":{JsonSerializer.Serialize(uid)},\"token\":{JsonSerializer.Serialize(token)}}}");
        return document.RootElement.Clone();
    }

    public static JsonElement BuildEmptyPayload()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    /// <summary>解析 WS 推送帧。返回 false 表示不是可识别的推送类型。</summary>
    public static bool TryParsePush(string json, out BridgePush? push)
    {
        push = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return TryParsePush(document.RootElement, out push);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParsePush(JsonElement root, out BridgePush? push)
    {
        push = null;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var type = typeElement.GetString();
        var data = root.TryGetProperty("data", out var dataElement)
            ? dataElement.Clone()
            : JsonDocument.Parse("{}").RootElement.Clone();

        var kind = type switch
        {
            "connected" => BridgePushKind.Connected,
            "status" => BridgePushKind.Status,
            "network" => BridgePushKind.Network,
            "message" => BridgePushKind.Message,
            "heartbeat" => BridgePushKind.Heartbeat,
            "error" => BridgePushKind.Error,
            _ => (BridgePushKind?)null
        };
        if (kind is null)
        {
            return false;
        }

        push = new BridgePush(kind.Value, data);
        return true;
    }

    /// <summary>解析 HTTP 健康检查响应。</summary>
    public static bool TryParseHealth(JsonElement root, out bool ok, out bool imReady)
    {
        ok = false;
        imReady = false;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        ok = string.Equals(status.GetString(), "ok", StringComparison.Ordinal);
        imReady = root.TryGetProperty("imReady", out var ready) && ready.ValueKind == JsonValueKind.True;
        return true;
    }

    /// <summary>解析 HTTP/WS 状态响应。</summary>
    public static bool TryParseStatus(JsonElement root, out bool isReady)
    {
        isReady = false;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (root.TryGetProperty("isReady", out var ready))
        {
            isReady = ready.ValueKind == JsonValueKind.True;
            return true;
        }

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("isReady", out var dataReady))
        {
            isReady = dataReady.ValueKind == JsonValueKind.True;
            return true;
        }

        return false;
    }

    /// <summary>解析 send-command / sendCommand 响应。</summary>
    public static bool TryParseSendResult(JsonElement root, out bool success)
    {
        success = false;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (root.TryGetProperty("success", out var successElement))
        {
            success = successElement.ValueKind == JsonValueKind.True;
            return true;
        }

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("success", out var dataSuccess))
        {
            success = dataSuccess.ValueKind == JsonValueKind.True;
            return true;
        }

        return false;
    }

    /// <summary>解析 reinit / reinitResult 响应。</summary>
    public static bool TryParseReinitResult(JsonElement root, out bool success)
    {
        success = false;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("success", out var successElement))
        {
            return false;
        }

        success = successElement.ValueKind == JsonValueKind.True;
        return true;
    }
}
