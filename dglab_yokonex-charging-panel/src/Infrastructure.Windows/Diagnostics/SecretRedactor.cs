using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChargingPanel.Infrastructure.Windows.Diagnostics;

/// <summary>
/// 日志脱敏。防御性兜底：即使某条日志路径不小心携带了密钥，
/// 输出前也要把敏感值替换掉。
///
/// 覆盖：密钥、Token、连接码、userSig（含 JWT/base64 形态）、
/// 完整 IM 控制载荷与稳定用户标识。与"根本不记录"并用——
/// 结构化日志应当从源头就不带这些字段，脱敏是最后一道防线。
/// </summary>
public static partial class SecretRedactor
{
    private static readonly string[] SecretKeys =
    [
        "token",
        "sign",
        "user_sig",
        "userSig",
        "signature",
        "connect_code",
        "connectCode",
        "password",
        "secret",
        "api_key",
        "apikey",
        "credential",
        "user_sig"
    ];

    private static readonly string[] RedactableJsonShapeKeys = ["code", "id", "data"];

    /// <summary>把输入文本中所有可识别的敏感值替换为固定掩码。</summary>
    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        var result = RedactKeyValuePairs(input);
        result = RedactJwtLikeTokens(result);
        result = RedactImPayloads(result);
        return result;
    }

    /// <summary>
    /// 把 JSON 对象里"密钥键→值"替换为掩码。
    /// 处理单引号与双引号两种形态，也处理顶层 k:v 而非完整 JSON 的片段。
    /// </summary>
    public static string RedactKeyValuePairs(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        var result = input;
        foreach (var key in SecretKeys)
        {
            result = SecretKeyValueRegex().Replace(
                result,
                match => $"\"{key}\":\"{Mask}\"");
        }

        return result;
    }

    /// <summary>
    /// 脱敏 JWT / 腾讯 userSig 这类 base64url 分段 token：
    /// header.payload.signature 三段的形态，或以 "eJ" 开头的长 base64 串。
    /// 仅对超过一定长度的候选生效，避免误伤普通日志。
    /// </summary>
    public static string RedactJwtLikeTokens(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        var result = JwtLikeTokenRegex().Replace(input, Mask);
        return Base64PayloadRegex().Replace(result, Mask);
    }

    /// <summary>
    /// 脱敏完整 IM 控制载荷：包含 "code":"game_cmd" / "game_info" 的 JSON 文本
    /// 整体替换为掩码，因为载荷同时含目标与 token。
    /// </summary>
    public static string RedactImPayloads(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        return ImPayloadRegex().Replace(input, Mask);
    }

    /// <summary>尝试把完整 JSON 字符串序列化后的文本也脱敏（含嵌套对象）。</summary>
    public static string RedactJsonDocument(JsonElement element)
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        var raw = JsonSerializer.Serialize(element, options);
        return Redact(raw);
    }

    public const string Mask = "***REDACTED***";

    [GeneratedRegex(
        """(?<q>["'])(?<k>token|sign|user_sig|userSig|signature|connect_code|connectCode|password|secret|api_key|apikey|credential)(?<q2>["'])\s*[:=]\s*(?<q3>["'])(?<v>[^"']*?)(?<q4>["'])""",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretKeyValueRegex();

    [GeneratedRegex(
        """[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}""")]
    private static partial Regex JwtLikeTokenRegex();

    [GeneratedRegex(
        """(?<![A-Za-z0-9])eJ[A-Za-z0-9_+/]{40,}""")]
    private static partial Regex Base64PayloadRegex();

    [GeneratedRegex(
        """\{(?:[^{}]|(?:\{[^{}]*\}))*?"(?:code|id|data)"\s*:\s*"(?:game_cmd|game_info)"[^}]*\}""",
        RegexOptions.IgnoreCase)]
    private static partial Regex ImPayloadRegex();
}
