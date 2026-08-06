using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.Core.Network.RemoteControl;

/// <summary>
/// 会话权限范围：允许的能力与操作。全部规范化为小写稳定 token。
/// null 集合表示不限制；空集合表示不允许任何。
/// </summary>
public sealed record RemotePermissionScope
{
    /// <summary>不限制任何能力与操作（Room 房主本地远控等可信场景）。</summary>
    public static RemotePermissionScope All { get; } = new(null, null);

    private readonly HashSet<string>? _capabilities;
    private readonly HashSet<string>? _operations;

    public RemotePermissionScope(
        IEnumerable<string>? capabilities = null,
        IEnumerable<string>? operations = null)
    {
        _capabilities = capabilities?.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        _operations = operations?.Select(Normalize).ToHashSet(StringComparer.Ordinal);
    }

    public bool Allows(string capabilityId, string operationId)
    {
        var cap = Normalize(capabilityId);
        var op = Normalize(operationId);
        if (_capabilities is not null && !_capabilities.Contains(cap))
        {
            return false;
        }

        if (_operations is not null && !_operations.Contains(op))
        {
            return false;
        }

        return true;
    }

    private static string Normalize(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
}

/// <summary>会话状态。断线后状态即失效，旧 Session 不得被复用。</summary>
public enum RemoteSessionState
{
    Active,
    Disconnected
}

/// <summary>
/// 已认证的远端控制会话：持有主体、会话密钥、权限范围与过期时间。
/// 会话密钥用于计算/验证命令认证标签。
/// </summary>
public sealed class RemoteControlSession
{
    private readonly object _gate = new();

    public RemoteControlSession(
        string principal,
        byte[] key,
        RemotePermissionScope scope,
        TimeSpan ttl,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl));
        }

        if (key.Length == 0)
        {
            throw new ArgumentException("会话密钥不能为空。", nameof(key));
        }

        Principal = principal.Trim();
        Key = key.ToArray();
        Scope = scope;
        SessionId = $"ses_{Guid.NewGuid():N}";
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        CreatedAt = timeProvider.GetUtcNow();
        ExpiresAt = CreatedAt + ttl;
        State = RemoteSessionState.Active;
    }

    public string SessionId { get; }

    public string Principal { get; }

    /// <summary>与会话密钥配套的 Bearer 令牌（握手用）。</summary>
    public string Token { get; }

    /// <summary>HMAC 会话密钥。秘密，不得写入日志。</summary>
    public byte[] Key { get; }

    public RemotePermissionScope Scope { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public RemoteSessionState State { get; private set; }

    public bool IsExpired(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return timeProvider.GetUtcNow() >= ExpiresAt;
    }

    /// <summary>断线：状态置为失效，之后任何复用都会被拒绝。</summary>
    public void Disconnect()
    {
        lock (_gate)
        {
            State = RemoteSessionState.Disconnected;
        }
    }

    /// <summary>用会话密钥计算命令认证标签。</summary>
    public string ComputeAuthTag(
        CommandId commandId,
        long nonce,
        DateTimeOffset timestamp,
        DeviceInstanceId deviceInstanceId,
        string capabilityId,
        string operationId,
        byte[] payloadUtf8)
    {
        var canonical = RemoteCommandSigning.BuildCanonical(
            SessionId,
            commandId,
            nonce,
            timestamp,
            deviceInstanceId,
            capabilityId,
            operationId,
            payloadUtf8);
        using var hmac = new HMACSHA256(Key);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}

/// <summary>
/// 命令签名规范。签名方与验证方必须使用同一 canonical 格式，
/// 否则标签永远无法匹配。
/// </summary>
public static class RemoteCommandSigning
{
    /// <summary>
    /// canonical = SessionId|CommandId|Nonce|TimestampMs|DeviceInstanceId|CapabilityId|OperationId|PayloadSha256
    /// 密钥无关，仅构成待签内容；会话密钥通过 HMAC-SHA256 施加。
    /// </summary>
    public static string BuildCanonical(
        string sessionId,
        CommandId commandId,
        long nonce,
        DateTimeOffset timestamp,
        DeviceInstanceId deviceInstanceId,
        string capabilityId,
        string operationId,
        byte[] payloadUtf8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(payloadUtf8);
        _ = commandId.Value;
        _ = deviceInstanceId.Value;

        var payloadHash = Convert.ToHexString(SHA256.HashData(payloadUtf8)).ToLowerInvariant();
        var timestampMs = timestamp.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        return string.Join(
            '|',
            sessionId,
            commandId.Value.ToString("D"),
            nonce.ToString(CultureInfo.InvariantCulture),
            timestampMs,
            deviceInstanceId.Value.ToString("D"),
            Normalize(capabilityId),
            Normalize(operationId),
            payloadHash);
    }

    /// <summary>用给定密钥计算认证标签。</summary>
    public static string ComputeAuthTag(
        byte[] key,
        string sessionId,
        CommandId commandId,
        long nonce,
        DateTimeOffset timestamp,
        DeviceInstanceId deviceInstanceId,
        string capabilityId,
        string operationId,
        byte[] payloadUtf8)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0)
        {
            throw new ArgumentException("密钥不能为空。", nameof(key));
        }

        var canonical = BuildCanonical(
            sessionId,
            commandId,
            nonce,
            timestamp,
            deviceInstanceId,
            capabilityId,
            operationId,
            payloadUtf8);
        using var hmac = new HMACSHA256(key);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string Normalize(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
}
