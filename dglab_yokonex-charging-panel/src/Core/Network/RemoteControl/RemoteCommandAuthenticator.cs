using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Application.Devices;

namespace ChargingPanel.Core.Network.RemoteControl;

/// <summary>
/// 远端命令请求。必须携带唯一 CommandId、单调 nonce、时间戳和认证标签。
/// 所有外部请求只能经由 <see cref="IDeviceApplicationService"/> 下发。
/// </summary>
public sealed record RemoteCommandRequest
{
    public RemoteCommandRequest(
        CommandId commandId,
        string sessionId,
        long nonce,
        DateTimeOffset timestamp,
        DeviceInstanceId deviceInstanceId,
        string capabilityId,
        string operationId,
        JsonElement payload,
        string authTag)
    {
        _ = commandId.Value;
        _ = deviceInstanceId.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authTag);
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ArgumentException("命令载荷不能为 Undefined 或 null。", nameof(payload));
        }

        CommandId = commandId;
        SessionId = sessionId;
        Nonce = nonce;
        Timestamp = timestamp;
        DeviceInstanceId = deviceInstanceId;
        CapabilityId = capabilityId;
        OperationId = operationId;
        Payload = payload.Clone();
        AuthTag = authTag;
    }

    public CommandId CommandId { get; }

    public string SessionId { get; }

    public long Nonce { get; }

    public DateTimeOffset Timestamp { get; }

    public DeviceInstanceId DeviceInstanceId { get; }

    public string CapabilityId { get; }

    public string OperationId { get; }

    public JsonElement Payload { get; }

    public string AuthTag { get; }
}

/// <summary>
/// 远端命令认证器。负责会话认证、重放保护、权限范围、载荷上限与速率限制，
/// 然后把已验证的命令通过 <see cref="IDeviceApplicationService"/> 下发。
/// 认证标签的最终结果一律来自 Dispatcher 的 Executed/Rejected/OutcomeUnknown。
/// </summary>
public sealed class RemoteCommandAuthenticator
{
    private const int DefaultMaxPayloadBytes = 16 * 1024;
    private const int DefaultMaxCommandsPerWindow = 30;
    private static readonly TimeSpan DefaultRateWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultSessionTtl = TimeSpan.FromMinutes(30);

    private readonly IDeviceApplicationService? _applicationService;
    private readonly TimeProvider _timeProvider;
    private readonly ReplayWindow _replayWindow;
    private readonly int _maxPayloadBytes;
    private readonly int _maxCommandsPerWindow;
    private readonly TimeSpan _rateWindow;
    private readonly ConcurrentDictionary<string, RemoteControlSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RateWindowState> _rateWindows = new(StringComparer.OrdinalIgnoreCase);

    public RemoteCommandAuthenticator(
        IDeviceApplicationService? applicationService,
        TimeProvider? timeProvider = null,
        ReplayWindow? replayWindow = null,
        int maxPayloadBytes = DefaultMaxPayloadBytes,
        int maxCommandsPerWindow = DefaultMaxCommandsPerWindow,
        TimeSpan? rateWindow = null)
    {
        if (maxPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
        }

        if (maxCommandsPerWindow <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCommandsPerWindow));
        }

        if (rateWindow is { } window && window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(rateWindow));
        }

        _applicationService = applicationService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _replayWindow = replayWindow ?? new ReplayWindow();
        _maxPayloadBytes = maxPayloadBytes;
        _maxCommandsPerWindow = maxCommandsPerWindow;
        _rateWindow = rateWindow ?? DefaultRateWindow;
    }

    public TimeProvider TimeProvider => _timeProvider;

    /// <summary>创建一个新的远端会话。key 为空时随机生成会话密钥。</summary>
    public RemoteControlSession CreateSession(
        string principal,
        RemotePermissionScope scope,
        TimeSpan? ttl = null,
        byte[]? key = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        ArgumentNullException.ThrowIfNull(scope);
        var sessionKey = key ?? RandomNumberGenerator.GetBytes(32);
        var session = new RemoteControlSession(
            principal,
            sessionKey,
            scope,
            ttl ?? DefaultSessionTtl,
            _timeProvider);
        _sessions[session.SessionId] = session;
        return session;
    }

    public RemoteControlSession? GetSession(string sessionId) =>
        string.IsNullOrWhiteSpace(sessionId)
            ? null
            : _sessions.TryGetValue(sessionId, out var session) ? session : null;

    /// <summary>断线：使会话失效，禁止旧 Session 重用。</summary>
    public bool DisconnectSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return false;
        }

        session.Disconnect();
        return true;
    }

    /// <summary>清除全部会话（离开房间 / 服务停止时）。</summary>
    public void TerminateSessions()
    {
        foreach (var session in _sessions.Values)
        {
            session.Disconnect();
        }

        _sessions.Clear();
        _rateWindows.Clear();
    }

    /// <summary>基于已存储会话验证并下发远端命令。</summary>
    public async Task<RemoteCommandResult> DispatchAsync(
        RemoteCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_applicationService is null)
        {
            return RemoteCommandResult.Rejected(request.CommandId, "service.unavailable", "远控未注入设备应用服务。");
        }

        if (!_sessions.TryGetValue(request.SessionId, out var session))
        {
            return RemoteCommandResult.Rejected(request.CommandId, "auth.session_not_found", "会话不存在或已失效。");
        }

        if (session.State != RemoteSessionState.Active)
        {
            return RemoteCommandResult.Rejected(request.CommandId, "session.disconnected", "会话已断开，不能复用。");
        }

        if (session.IsExpired(_timeProvider))
        {
            return RemoteCommandResult.Rejected(request.CommandId, "session.expired", "会话已过期。");
        }

        return await DispatchCoreAsync(session.Principal, session.Scope, session.Key, request, cancellationToken);
    }

    /// <summary>
    /// 基于显式主体/密钥/权限范围验证并下发（MOD Bridge 使用脚本令牌作为共享密钥）。
    /// </summary>
    public async Task<RemoteCommandResult> DispatchWithKeyAsync(
        string principal,
        byte[] hmacKey,
        RemotePermissionScope scope,
        RemoteCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        ArgumentNullException.ThrowIfNull(hmacKey);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        if (_applicationService is null)
        {
            return RemoteCommandResult.Rejected(request.CommandId, "service.unavailable", "远控未注入设备应用服务。");
        }

        return await DispatchCoreAsync(principal, scope, hmacKey, request, cancellationToken);
    }

    private async Task<RemoteCommandResult> DispatchCoreAsync(
        string principal,
        RemotePermissionScope scope,
        byte[] hmacKey,
        RemoteCommandRequest request,
        CancellationToken cancellationToken)
    {
        // 1. 载荷大小上限。
        var payloadRawText = request.Payload.GetRawText();
        var payloadBytes = Encoding.UTF8.GetBytes(payloadRawText);
        if (payloadBytes.Length > _maxPayloadBytes)
        {
            return RemoteCommandResult.Rejected(request.CommandId, "payload.too_large", "命令载荷超过上限。");
        }

        // 2. 重放保护：时间戳、CommandId、nonce。
        var replay = _replayWindow.CheckAndConsume(
            principal,
            request.CommandId,
            request.Nonce,
            request.Timestamp,
            _timeProvider);
        if (replay == ReplayCheckResult.ExpiredTimestamp)
        {
            return RemoteCommandResult.Rejected(request.CommandId, "timestamp.expired", "命令时间戳过期或超前。");
        }

        if (replay == ReplayCheckResult.ReplayedCommandId)
        {
            return RemoteCommandResult.Rejected(request.CommandId, "replay.command_id", "命令 ID 已使用过。");
        }

        if (replay == ReplayCheckResult.OutOfOrderNonce)
        {
            return RemoteCommandResult.Rejected(request.CommandId, "replay.nonce_out_of_order", "命令 nonce 乱序。");
        }

        // 3. 认证标签：必须与会话密钥对 canonical 的 HMAC 匹配。
        var expected = RemoteCommandSigning.ComputeAuthTag(
            hmacKey,
            request.SessionId,
            request.CommandId,
            request.Nonce,
            request.Timestamp,
            request.DeviceInstanceId,
            request.CapabilityId,
            request.OperationId,
            payloadBytes);
        if (!ConstantTimeEquals(expected, request.AuthTag))
        {
            return RemoteCommandResult.Rejected(request.CommandId, "auth.tag_mismatch", "认证标签无效。");
        }

        // 4. 权限范围：能力与操作都必须被允许。
        if (!scope.Allows(request.CapabilityId, request.OperationId))
        {
            return RemoteCommandResult.Rejected(request.CommandId, "permission.denied", "主体无权控制该能力或操作。");
        }

        // 5. 速率限制：按主体固定窗口。
        if (IsRateLimited(principal))
        {
            return RemoteCommandResult.Rejected(request.CommandId, "rate_limited", "命令频率超限。");
        }

        // 6. 唯一入口：只经由 IDeviceApplicationService。业务代码不构造协议字节。
        var source = new CommandSource(CommandSourceKind.Lan, principal);
        using var payloadDoc = JsonDocument.Parse(request.Payload.GetRawText());
        var capabilityRequest = new CapabilityCommandRequest(
            request.DeviceInstanceId,
            new CapabilityId(request.CapabilityId),
            request.OperationId,
            payloadDoc.RootElement.Clone(),
            source,
            CommandDelivery.AtMostOnce);

        var result = await _applicationService!.ExecuteCapabilityAsync(capabilityRequest, cancellationToken);
        var final = RemoteCommandResult.FromDispatcher(result);

        // 用远端 CommandId 关联，保证调用方能匹配自己的命令。
        return final.CommandId == request.CommandId
            ? final
            : new RemoteCommandResult(request.CommandId, final.Status, final.ReasonCode, final.Message, final.Details);
    }

    private bool IsRateLimited(string principal)
    {
        var now = _timeProvider.GetUtcNow();
        var state = _rateWindows.GetOrAdd(principal, static _ => new RateWindowState());
        lock (state.Gate)
        {
            if (now - state.WindowStart >= _rateWindow)
            {
                state.WindowStart = now;
                state.Count = 0;
            }

            state.Count++;
            return state.Count > _maxCommandsPerWindow;
        }
    }

    private static bool ConstantTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private sealed class RateWindowState
    {
        public DateTimeOffset WindowStart { get; set; } = DateTimeOffset.MinValue;

        public int Count { get; set; }

        public object Gate { get; } = new();
    }
}
