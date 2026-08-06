using ChargingPanel.Infrastructure.Windows.IM;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Networking;

/// <summary>
/// IM 账户池隔离测试。
///
/// 核心不变量：按账户主体隔离登录，释放一个目标不登出同账户其他目标，
/// 不同账户互不登出，userSig 过期只影响对应账户。
/// </summary>
public sealed class ImConnectionPoolTests
{
    private static readonly ImAccountKey AccountA = new(1400000001, "game_5", "cred:game_5");
    private static readonly ImAccountKey AccountB = new(1400000002, "game_9", "cred:game_9");

    [Fact]
    public async Task TwoAccounts_CoexistIndependently()
    {
        // 两个账户可同时存在；各自登录一次，互不登出。
        var harness = new Harness();
        await using var pool = harness.Pool;

        var targetA = await pool.AcquireTargetAsync(AccountA, "10", CancellationToken.None);
        var targetB = await pool.AcquireTargetAsync(AccountB, "20", CancellationToken.None);

        Assert.Equal(2, pool.AccountCount);
        Assert.Equal(1, harness.ClientFor(AccountA).LoginCount);
        Assert.Equal(1, harness.ClientFor(AccountB).LoginCount);

        await targetA.DisposeAsync();
        Assert.Equal(1, pool.AccountCount);

        await targetB.DisposeAsync();
        Assert.Equal(0, pool.AccountCount);
    }

    [Fact]
    public async Task ReleaseOneTarget_DoesNotLogoutOtherTargetsInSameAccount()
    {
        var harness = new Harness();
        await using var pool = harness.Pool;

        var first = await pool.AcquireTargetAsync(AccountA, "10", CancellationToken.None);
        var second = await pool.AcquireTargetAsync(AccountA, "11", CancellationToken.None);
        var client = harness.ClientFor(AccountA);

        await first.DisposeAsync();

        // 释放一个目标不得登出账户：账户仍在、原生客户端未登出。
        Assert.Equal(1, pool.AccountCount);
        Assert.Equal(0, client.LogoutCount);
        Assert.True(client.IsLoggedIn);

        // 发送仍走同一账户。
        var result = await second.SendC2CTextAsync("hello", CancellationToken.None);
        Assert.Equal(ImSendStatus.AcceptedByTransport, result.Status);
        Assert.Single(client.Sends);
        Assert.Equal("11", client.Sends[0].Target);

        await second.DisposeAsync();
        Assert.Equal(0, pool.AccountCount);
        Assert.Equal(1, client.LogoutCount);
    }

    [Fact]
    public async Task DifferentAccounts_DoNotLogoutEachOther()
    {
        var harness = new Harness();
        await using var pool = harness.Pool;

        var targetA = await pool.AcquireTargetAsync(AccountA, "10", CancellationToken.None);
        var targetB = await pool.AcquireTargetAsync(AccountB, "20", CancellationToken.None);
        var clientA = harness.ClientFor(AccountA);
        var clientB = harness.ClientFor(AccountB);

        await targetA.DisposeAsync();

        // 释放 A 不影响 B：B 仍登录、账户池只剩 B；A 因最后一个目标释放而登出。
        Assert.Equal(1, pool.AccountCount);
        Assert.Equal(0, clientB.LogoutCount);
        Assert.Equal(1, clientA.LogoutCount);

        await targetB.DisposeAsync();
        Assert.Equal(0, pool.AccountCount);
    }

    [Fact]
    public async Task UserSigExpiry_OnlyAffectsThatAccount()
    {
        // A 的 userSig 过期（解析器返回 null → 登录失败），B 不受影响。
        var harness = new Harness
        {
            ExpiredCredential = "cred:game_5"
        };
        await using var pool = harness.Pool;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pool.AcquireTargetAsync(AccountA, "10", CancellationToken.None).AsTask());

        var targetB = await pool.AcquireTargetAsync(AccountB, "20", CancellationToken.None);

        Assert.Equal(1, pool.AccountCount);
        Assert.Equal(1, harness.ClientFor(AccountB).LoginCount);

        await targetB.DisposeAsync();
        Assert.Equal(0, pool.AccountCount);
    }

    [Fact]
    public async Task SameTargetSharedAcrossLeases_TargetRefCounted()
    {
        var harness = new Harness();
        await using var pool = harness.Pool;

        var first = await pool.AcquireTargetAsync(AccountA, "10", CancellationToken.None);
        var second = await pool.AcquireTargetAsync(AccountA, "10", CancellationToken.None);

        await first.DisposeAsync();
        // 仍有第二个目标租约：账户不登出。
        Assert.Equal(1, pool.AccountCount);
        Assert.Equal(0, harness.ClientFor(AccountA).LogoutCount);

        await second.DisposeAsync();
        Assert.Equal(0, pool.AccountCount);
    }

    [Fact]
    public async Task MessageReceived_IsRoutedFromNativeClientToLeaseSubscribers()
    {
        var harness = new Harness();
        await using var pool = harness.Pool;
        await using var target = await pool.AcquireTargetAsync(AccountA, "10", CancellationToken.None);

        var received = new List<ImReceivedMessage>();
        target.MessageReceived += received.Add;

        harness.ClientFor(AccountA).FireMessage("100", "{\"code\":\"game_cmd\",\"id\":\"hit\"}");

        var message = Assert.Single(received);
        Assert.Equal("100", message.FromUserId);
        Assert.Contains("game_cmd", message.Text);
    }

    [Fact]
    public async Task NetworkStatusChanged_IsRoutedToLeaseSubscribers()
    {
        var harness = new Harness();
        await using var pool = harness.Pool;
        await using var target = await pool.AcquireTargetAsync(AccountA, "10", CancellationToken.None);

        var statuses = new List<ImNativeNetworkStatus>();
        target.NetworkStatusChanged += statuses.Add;

        harness.ClientFor(AccountA).FireNetworkStatus(ImNativeNetworkStatus.Disconnected);

        Assert.Equal([ImNativeNetworkStatus.Disconnected], statuses);
    }

    private sealed class Harness
    {
        private readonly Dictionary<ImAccountKey, FakeTencentImNativeClient> _clients = new();

        public Harness()
        {
            Pool = new ImConnectionPool(
                clientFactory: key =>
                {
                    var client = new FakeTencentImNativeClient
                    {
                        UserSigExpired = key.CredentialReference == ExpiredCredential
                    };
                    _clients[key] = client;
                    return client;
                },
                userSigResolver: (key, _) =>
                    Task.FromResult<string?>(
                        key.CredentialReference == ExpiredCredential ? null : $"sig-for-{key.LoginUserId}"));
        }

        public string? ExpiredCredential { get; init; }

        public ImConnectionPool Pool { get; }

        public FakeTencentImNativeClient ClientFor(ImAccountKey key) => _clients[key];
    }
}
