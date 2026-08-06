namespace ChargingPanel.Infrastructure.Windows.IM;

/// <summary>
/// IM 账户池。按账户主体隔离登录：同一物理登录键只存在一个原生会话，
/// 一账户可向多个目标提供发送租约；释放一个目标不登出其他目标，
/// 不同账户互不登出。userSig 过期等故障只影响对应账户。
/// </summary>
public sealed class ImConnectionPool : IAsyncDisposable
{
    private readonly Func<ImAccountKey, ITencentImNativeClient> _clientFactory;
    private readonly Func<ImAccountKey, CancellationToken, Task<string?>> _userSigResolver;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<ImAccountKey, AccountSlot> _accounts = [];
    private bool _disposed;

    public ImConnectionPool(
        Func<ImAccountKey, ITencentImNativeClient>? clientFactory = null,
        Func<ImAccountKey, CancellationToken, Task<string?>>? userSigResolver = null)
    {
        _clientFactory = clientFactory ?? (key => new TencentImNativeClient());
        _userSigResolver = userSigResolver ?? ((_, _) => Task.FromResult<string?>(null));
    }

    /// <summary>
    /// 获取一个目标发送租约。目标 UserId 属于租约；账户物理登录
    /// 由池按账户键持有，最后一个目标释放且账户无引用时才登出。
    /// </summary>
    public async ValueTask<ImTargetLease> AcquireTargetAsync(
        ImAccountKey accountKey,
        string targetUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken);
        AccountSlot slot;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_accounts.TryGetValue(accountKey, out var existing))
            {
                existing = new AccountSlot(accountKey, _clientFactory(accountKey), _userSigResolver);
                _accounts[accountKey] = existing;
            }
            slot = existing;
            slot.AccountRefCount++;
            slot.TargetRefCounts.TryGetValue(targetUserId, out var targetCount);
            slot.TargetRefCounts[targetUserId] = targetCount + 1;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            // 登录失败必须回滚引用计数，否则账户槽会残留无人持有的计数。
            await slot.EnsureLoggedInAsync(cancellationToken);
            return new ImTargetLease(this, slot, targetUserId);
        }
        catch
        {
            await ReleaseTargetAsync(slot, targetUserId);
            throw;
        }
    }

    /// <summary>当前已登录账户数。仅供测试与诊断。</summary>
    public int AccountCount
    {
        get
        {
            _gate.Wait();
            try
            {
                return _accounts.Count;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    internal async ValueTask ReleaseTargetAsync(AccountSlot slot, string targetUserId)
    {
        AccountSlot? toLogout = null;
        await _gate.WaitAsync();
        try
        {
            if (slot.TargetRefCounts.TryGetValue(targetUserId, out var targetCount))
            {
                if (targetCount <= 1)
                {
                    slot.TargetRefCounts.Remove(targetUserId);
                }
                else
                {
                    slot.TargetRefCounts[targetUserId] = targetCount - 1;
                }
            }

            slot.AccountRefCount--;
            if (slot.AccountRefCount <= 0 && slot.TargetRefCounts.Count == 0)
            {
                _accounts.Remove(slot.Key);
                toLogout = slot;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (toLogout is not null)
        {
            await toLogout.LogoutAndDisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 在触碰 _gate 之前先检查：DI 容器会在关闭参与者释放后再次释放单例。
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        AccountSlot[] slots;
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            slots = _accounts.Values.ToArray();
            _accounts.Clear();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var slot in slots)
        {
            await slot.LogoutAndDisposeAsync();
        }

        _gate.Dispose();
    }

    /// <summary>一个账户的物理登录槽位。</summary>
    internal sealed class AccountSlot
    {
        private readonly SemaphoreSlim _loginGate = new(1, 1);
        private readonly Func<ImAccountKey, CancellationToken, Task<string?>> _userSigResolver;

        public AccountSlot(
            ImAccountKey key,
            ITencentImNativeClient client,
            Func<ImAccountKey, CancellationToken, Task<string?>> userSigResolver)
        {
            Key = key;
            Client = client;
            _userSigResolver = userSigResolver;
        }

        public ImAccountKey Key { get; }

        public ITencentImNativeClient Client { get; }

        public int AccountRefCount { get; set; }

        public Dictionary<string, int> TargetRefCounts { get; } = new(StringComparer.Ordinal);

        public async ValueTask EnsureLoggedInAsync(CancellationToken cancellationToken)
        {
            if (Client.IsLoggedIn)
            {
                return;
            }

            await _loginGate.WaitAsync(cancellationToken);
            try
            {
                if (Client.IsLoggedIn)
                {
                    return;
                }

                // userSig 由解析器从密钥存储提供；池只负责登录与隔离。
                var userSig = await _userSigResolver(Key, cancellationToken);
                if (string.IsNullOrWhiteSpace(userSig))
                {
                    throw new InvalidOperationException(
                        $"凭据引用 {Key.CredentialReference} 未解析到 userSig。");
                }

                await Client.InitializeAndLoginAsync(Key, userSig, cancellationToken);
            }
            finally
            {
                _loginGate.Release();
            }
        }

        public async ValueTask LogoutAndDisposeAsync()
        {
            try
            {
                await Client.LogoutAsync(CancellationToken.None);
            }
            catch
            {
                // 登出失败不阻止释放。
            }

            await Client.DisposeAsync();
        }
    }
}

/// <summary>
/// 目标发送租约。释放只归还该目标；同一账户其他目标不受影响。
/// </summary>
public sealed class ImTargetLease : IAsyncDisposable
{
    private readonly ImConnectionPool _pool;
    private readonly ImConnectionPool.AccountSlot _slot;
    private bool _closed;

    internal ImTargetLease(
        ImConnectionPool pool,
        ImConnectionPool.AccountSlot slot,
        string targetUserId)
    {
        _pool = pool;
        _slot = slot;
        TargetUserId = targetUserId;
    }

    /// <summary>该租约绑定的目标用户 ID（不带 game_ 前缀）。</summary>
    public string TargetUserId { get; }

    /// <summary>账户是否已登录。</summary>
    public bool IsLoggedIn => _slot.Client.IsLoggedIn;

    public event Action<ImReceivedMessage>? MessageReceived
    {
        add => _slot.Client.MessageReceived += value;
        remove => _slot.Client.MessageReceived -= value;
    }

    public event Action<ImNativeNetworkStatus>? NetworkStatusChanged
    {
        add => _slot.Client.NetworkStatusChanged += value;
        remove => _slot.Client.NetworkStatusChanged -= value;
    }

    /// <summary>
    /// 以账户身份向目标发送 C2C 文本。SDK 成功只是"已交给 IM"，
    /// 不得伪造成物理设备已执行。
    /// </summary>
    public Task<ImSendResult> SendC2CTextAsync(string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        return _slot.Client.SendC2CTextAsync(TargetUserId, text, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        await _pool.ReleaseTargetAsync(_slot, TargetUserId);
    }
}
