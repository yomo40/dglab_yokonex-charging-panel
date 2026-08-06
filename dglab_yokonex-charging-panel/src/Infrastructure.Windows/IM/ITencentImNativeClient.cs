namespace ChargingPanel.Infrastructure.Windows.IM;

/// <summary>
/// 物理 IM 登录键。同一 SDKAppId + 登录 UserId + 凭据引用视为同一账户，
/// 多个目标共享此登录；凭据引用只指向密钥存储，绝不内联凭据明文。
/// </summary>
public sealed record ImAccountKey(long SdkAppId, string LoginUserId, string CredentialReference);

/// <summary>一条收到的 IM 消息。载荷为原始文本，厂商语义解释在上层。</summary>
public sealed record ImReceivedMessage(string FromUserId, string Text);

public enum ImNativeNetworkStatus
{
    Connecting,
    Connected,
    Disconnected
}

public enum ImSendStatus
{
    /// <summary>SDK 已接受并交给 IM（不等于物理设备已执行）。</summary>
    AcceptedByTransport,

    Failed,

    /// <summary>发送超时或结果未知；是否重发由 Dispatcher 决定。</summary>
    OutcomeUnknown
}

public sealed record ImSendResult(ImSendStatus Status, string? Reason);

/// <summary>
/// 腾讯 IM 原生客户端窄抽象。账户池只依赖此接口，
/// 具体 ImSDK.dll P/Invoke 留在实现内部，测试用 Fake 替换。
/// </summary>
public interface ITencentImNativeClient : IAsyncDisposable
{
    /// <summary>初始化并登录。失败必须抛出，绝不伪报已就绪。</summary>
    Task InitializeAndLoginAsync(ImAccountKey identity, string userSig, CancellationToken cancellationToken);

    /// <summary>向目标用户发送一条 C2C 文本消息。目标 UserId 属于发送租约。</summary>
    Task<ImSendResult> SendC2CTextAsync(string targetUserId, string text, CancellationToken cancellationToken);

    /// <summary>登出当前账户。全局登出由账户池独占调用，Driver 不得直接调用。</summary>
    Task LogoutAsync(CancellationToken cancellationToken);

    bool IsLoggedIn { get; }

    event Action<ImReceivedMessage>? MessageReceived;

    event Action<ImNativeNetworkStatus>? NetworkStatusChanged;
}
