using ChargingPanel.Infrastructure.Windows.IM;

namespace ChargingPanel.DeviceIntegration.Tests.Networking;

/// <summary>
/// 合成腾讯 IM 原生客户端。记录登录/登出/发送次数与载荷，
/// 可注入 userSig 过期等故障；绝不伪造"已连接"。
/// </summary>
internal sealed class FakeTencentImNativeClient : ITencentImNativeClient
{
    public string? LoginUserId { get; private set; }

    public int LoginCount { get; private set; }

    public int LogoutCount { get; private set; }

    public int SendCount { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>置为 true 时模拟 userSig 过期：登录必然失败。</summary>
    public bool UserSigExpired { get; set; }

    /// <summary>下一次发送返回的结果。</summary>
    public ImSendResult NextSendResult { get; set; } = new(ImSendStatus.AcceptedByTransport, null);

    public List<(string Target, string Text)> Sends { get; } = [];

    public bool IsLoggedIn => LoginCount > 0 && LogoutCount == 0;

    public event Action<ImReceivedMessage>? MessageReceived;

    public event Action<ImNativeNetworkStatus>? NetworkStatusChanged;

    public Task InitializeAndLoginAsync(
        ImAccountKey identity,
        string userSig,
        CancellationToken cancellationToken)
    {
        if (UserSigExpired || string.IsNullOrWhiteSpace(userSig))
        {
            throw new InvalidOperationException("userSig 已过期或无效。");
        }

        LoginCount++;
        LoginUserId = identity.LoginUserId;
        return Task.CompletedTask;
    }

    public Task<ImSendResult> SendC2CTextAsync(string targetUserId, string text, CancellationToken cancellationToken)
    {
        SendCount++;
        Sends.Add((targetUserId, text));
        return Task.FromResult(NextSendResult);
    }

    public Task LogoutAsync(CancellationToken cancellationToken)
    {
        LogoutCount++;
        return Task.CompletedTask;
    }

    public void FireMessage(string fromUserId, string text) =>
        MessageReceived?.Invoke(new ImReceivedMessage(fromUserId, text));

    public void FireNetworkStatus(ImNativeNetworkStatus status) =>
        NetworkStatusChanged?.Invoke(status);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
