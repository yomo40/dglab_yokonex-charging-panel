using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Devices.Persistence;
using ChargingPanel.Core.Security;
using ChargingPanel.Infrastructure.Windows.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Persistence;

public sealed class RepositorySecretLifecycleTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "ChargingPanel.RepositorySecretLifecycleTests",
        Guid.NewGuid().ToString("N"));

    public RepositorySecretLifecycleTests() => Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public async Task DeleteAsync_SharedSecretThenLastDeleteFailure_RetainsAndRetriesCleanup()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "shared.db");
        var secretStore = new ControllableSecretStore();
        secretStore.Add("secret:shared");
        using var repository = new SqliteDeviceInstanceRepository(databasePath, secretStore);
        var first = CreateInstance("第一台", "secret:shared");
        var second = CreateInstance("第二台", "secret:shared");
        await repository.SaveAsync(first, CancellationToken.None);
        await repository.SaveAsync(second, CancellationToken.None);

        await repository.DeleteAsync(first.InstanceId, CancellationToken.None);

        Assert.Contains("secret:shared", secretStore.References);
        Assert.Equal(0, await CountCleanupEntriesAsync(databasePath));

        secretStore.DeleteException = new IOException("合成密钥删除失败");
        await repository.DeleteAsync(second.InstanceId, CancellationToken.None);

        Assert.Empty(await repository.GetAllAsync(CancellationToken.None));
        Assert.Contains("secret:shared", secretStore.References);
        Assert.Equal(1, await CountCleanupEntriesAsync(databasePath));

        secretStore.DeleteException = null;
        await repository.RetryPendingSecretCleanupAsync(CancellationToken.None);

        Assert.DoesNotContain("secret:shared", secretStore.References);
        Assert.Equal(0, await CountCleanupEntriesAsync(databasePath));
    }

    [Fact]
    public async Task SaveAsync_ReplacingEndpoint_DeletesUnreferencedOldSecret()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "replace.db");
        var secretStore = new ControllableSecretStore();
        secretStore.Add("secret:old");
        secretStore.Add("secret:new");
        using var repository = new SqliteDeviceInstanceRepository(databasePath, secretStore);
        var original = CreateInstance("替换设备", "secret:old");
        await repository.SaveAsync(original, CancellationToken.None);
        var replacement = CreateInstance(
            "替换设备",
            "secret:new",
            original.InstanceId,
            original.CreatedAt);

        await repository.SaveAsync(replacement, CancellationToken.None);

        Assert.DoesNotContain("secret:old", secretStore.References);
        Assert.Contains("secret:new", secretStore.References);
        Assert.Equal(0, await CountCleanupEntriesAsync(databasePath));
    }

    [Fact]
    public async Task GetAllAsync_AfterRestartWithPendingCleanup_DrainsOrphanedSecrets()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "restart-drain.db");
        var secretStore = new ControllableSecretStore();
        secretStore.Add("secret:orphan");
        var instance = CreateInstance("崩溃前设备", "secret:orphan");
        using (var repository = new SqliteDeviceInstanceRepository(databasePath, secretStore))
        {
            await repository.SaveAsync(instance, CancellationToken.None);
            secretStore.DeleteException = new IOException("合成密钥删除失败");
            await repository.DeleteAsync(instance.InstanceId, CancellationToken.None);
        }

        Assert.Equal(1, await CountCleanupEntriesAsync(databasePath));
        Assert.Contains("secret:orphan", secretStore.References);

        // 模拟重启：新实例首次读取时必须排空遗留的清理队列。
        secretStore.DeleteException = null;
        using var restarted = new SqliteDeviceInstanceRepository(databasePath, secretStore);
        await restarted.GetAllAsync(CancellationToken.None);

        Assert.DoesNotContain("secret:orphan", secretStore.References);
        Assert.Equal(0, await CountCleanupEntriesAsync(databasePath));
    }

    [Fact]
    public async Task GetAllAsync_WhenStartupDrainFails_RetriesOnNextAccess()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "drain-retry.db");
        var secretStore = new ControllableSecretStore();
        secretStore.Add("secret:retry");
        var instance = CreateInstance("排空失败设备", "secret:retry");
        using (var repository = new SqliteDeviceInstanceRepository(databasePath, secretStore))
        {
            await repository.SaveAsync(instance, CancellationToken.None);
            secretStore.DeleteException = new IOException("合成密钥删除失败");
            await repository.DeleteAsync(instance.InstanceId, CancellationToken.None);
        }

        Assert.Equal(1, await CountCleanupEntriesAsync(databasePath));

        using var restarted = new SqliteDeviceInstanceRepository(databasePath, secretStore);
        // 首次排空以取消方式失败，不能把该实例永久标记为已排空。
        secretStore.DeleteException = new OperationCanceledException("合成取消");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => restarted.GetAllAsync(CancellationToken.None).AsTask());
        Assert.Equal(1, await CountCleanupEntriesAsync(databasePath));

        secretStore.DeleteException = null;
        await restarted.GetAllAsync(CancellationToken.None);

        Assert.DoesNotContain("secret:retry", secretStore.References);
        Assert.Equal(0, await CountCleanupEntriesAsync(databasePath));
    }

    [Fact]
    public async Task RetryPendingSecretCleanupAsync_ConcurrentCalls_DeletesEachSecretOnce()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "outbox-race.db");
        var secretStore = new ControllableSecretStore();
        secretStore.Add("secret:race");
        using var repository = new SqliteDeviceInstanceRepository(databasePath, secretStore);
        var instance = CreateInstance("并发清理设备", "secret:race");
        await repository.SaveAsync(instance, CancellationToken.None);
        secretStore.DeleteException = new IOException("合成密钥删除失败");
        await repository.DeleteAsync(instance.InstanceId, CancellationToken.None);
        Assert.Equal(1, await CountCleanupEntriesAsync(databasePath));

        secretStore.DeleteException = null;
        secretStore.ResetDeleteCount();
        var releaseDelete = new TaskCompletionSource();
        secretStore.DeleteGate = releaseDelete.Task;
        var first = repository.RetryPendingSecretCleanupAsync(CancellationToken.None).AsTask();
        var second = repository.RetryPendingSecretCleanupAsync(CancellationToken.None).AsTask();
        releaseDelete.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, secretStore.DeleteCallCount);
        Assert.Equal(0, await CountCleanupEntriesAsync(databasePath));
    }

    private static PersistedDeviceInstance CreateInstance(
        string displayName,
        string secretReference,
        DeviceInstanceId? instanceId = null,
        DateTimeOffset? createdAt = null)
    {
        var now = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        return new PersistedDeviceInstance(
            instanceId ?? DeviceInstanceId.New(),
            new PluginId("dglab"),
            new ModelId("dglab.coyote.v2"),
            new ProtocolProfileId("dglab.coyote.v2.ble"),
            displayName,
            false,
            2,
            [
                new PersistedDeviceEndpoint(
                    EndpointId.New(),
                    PersistedTransportKind.BluetoothLowEnergy,
                    "{\"address\":\"AA:BB:CC:DD:EE:10\",\"reconnectInterval\":5000}",
                    secretReference,
                    0,
                    true,
                    now,
                    now)
            ],
            createdAt ?? now,
            now);
    }

    private static async Task<long> CountCleanupEntriesAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM secret_cleanup_outbox";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private sealed class ControllableSecretStore : ISecretStore
    {
        private readonly Dictionary<string, byte[]> _secrets = new(StringComparer.Ordinal);
        private int _deleteCallCount;

        public Exception? DeleteException { get; set; }

        public Task? DeleteGate { get; set; }

        public int DeleteCallCount => Volatile.Read(ref _deleteCallCount);

        public IReadOnlyCollection<string> References => _secrets.Keys.ToArray();

        public void Add(string secretReference) => _secrets[secretReference] = [1, 2, 3];

        public void ResetDeleteCount() => Volatile.Write(ref _deleteCallCount, 0);

        public ValueTask<string> SaveAsync(ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = $"secret:{Guid.NewGuid():N}";
            _secrets[reference] = secret.ToArray();
            return ValueTask.FromResult(reference);
        }

        public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                _secrets.TryGetValue(secretReference, out var value) ? value : null);
        }

        public async ValueTask DeleteAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _deleteCallCount);
            if (DeleteGate is not null)
            {
                await DeleteGate;
            }

            if (DeleteException is not null)
            {
                throw DeleteException;
            }

            _secrets.Remove(secretReference);
        }
    }
}
