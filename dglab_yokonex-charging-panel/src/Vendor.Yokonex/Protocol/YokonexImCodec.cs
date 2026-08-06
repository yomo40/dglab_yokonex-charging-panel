using System.Text.Json;

namespace ChargingPanel.Vendor.Yokonex.Protocol;

/// <summary>一条收到的 IM 消息（解析后的语义）。</summary>
public sealed record ImIncomingMessage(string FromUserId, string Code, string? Id, int? Data);

/// <summary>
/// 役次元 IM 控制协议编解码。
/// 连接码为 "UID 空格 Token"，登录主体统一为 game_&lt;n&gt;，目标 UserId 不带前缀。
/// 稳定指令面使用 code=game_cmd + 字符串 id；game_info 为旧版兼容，不构成正式能力。
/// 厂商标记"正在开发"的自定义参数/全局指令/元数据更新保持 Experimental，默认禁用。
/// 凭据（token）绝不进入日志——本编解码不承担任何日志职责。
/// </summary>
public static class YokonexImCodec
{
    /// <summary>
    /// 解析连接码：去除首尾空白后按空白分割，第一段为 UID（可为 "5" 或 "game_5"），
    /// 第二段为 Token。
    /// </summary>
    public static bool TryParseConnectCode(string? connectCode, out string uid, out string token)
    {
        uid = string.Empty;
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(connectCode))
        {
            return false;
        }

        var parts = connectCode.Trim().Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            return false;
        }

        uid = parts[0];
        token = parts[1];
        return true;
    }

    /// <summary>登录主体：统一为 game_&lt;n&gt;。</summary>
    public static string BuildLoginSubject(string uid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        return uid.StartsWith("game_", StringComparison.Ordinal) ? uid : $"game_{uid}";
    }

    /// <summary>目标 UserId：去掉 game_ 前缀。</summary>
    public static string BuildTargetUserId(string uid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        return uid.StartsWith("game_", StringComparison.Ordinal) ? uid["game_".Length..] : uid;
    }

    /// <summary>构建稳定指令面消息（不含 token；token 由适配层按密钥存储追加）。</summary>
    public static JsonElement BuildGameCommand(string commandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        using var document = JsonDocument.Parse(
            $"{{\"code\":\"game_cmd\",\"id\":{JsonSerializer.Serialize(commandId)}}}");
        return document.RootElement.Clone();
    }

    /// <summary>构建全局停止指令（开发中，Experimental）。</summary>
    public static JsonElement BuildGlobalStop() => BuildGameCommand("_stop_all");

    /// <summary>构建旧版 game_info 兼容消息（即将废弃，Experimental）。</summary>
    public static JsonElement BuildGameInfoLegacy(int data)
    {
        using var document = JsonDocument.Parse($"{{\"code\":\"game_info\",\"data\":{data}}}");
        return document.RootElement.Clone();
    }

    /// <summary>构建带 token 的完整指令文本。用于验证适配层追加 token 后的最终载荷。</summary>
    public static string BuildFullCommandText(string commandId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return $"{{\"code\":\"game_cmd\",\"id\":{JsonSerializer.Serialize(commandId)}," +
               $"\"token\":{JsonSerializer.Serialize(token)}}}";
    }

    /// <summary>解析收到的 IM 消息文本。</summary>
    public static bool TryParseIncoming(string text, string fromUserId, out ImIncomingMessage? message)
    {
        message = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("code", out var codeElement) ||
                codeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var code = codeElement.GetString();
            string? id = null;
            int? data = null;
            if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
            {
                id = idElement.GetString();
            }

            if (root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Number)
            {
                data = dataElement.GetInt32();
            }

            message = new ImIncomingMessage(fromUserId, code!, id, data);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
