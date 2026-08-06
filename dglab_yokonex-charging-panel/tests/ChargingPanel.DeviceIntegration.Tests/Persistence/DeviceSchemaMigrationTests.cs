using System.Text;
using System.Text.Json;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.Persistence;
using ChargingPanel.Infrastructure.Windows.Persistence;
using ChargingPanel.Infrastructure.Windows.Security;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Persistence;

public sealed class DeviceSchemaMigrationTests : IDisposable
{
    private static readonly string[] FixtureSecrets =
    [
        "fixture-dg-target-secret",
        "fixture_im_uid",
        "fixture_im_token_secret",
        "fixture_im_account_secret",
        "fixture_im_target_secret",
        "fixture_bridge_uid_secret",
        "fixture_bridge_token_secret",
        "fixture_bridge_target_secret"
    ];

    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "ChargingPanel.DeviceSchemaMigrationTests",
        Guid.NewGuid().ToString("N"));

    public DeviceSchemaMigrationTests() => Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public async Task MigrateAsync_V1Fixture_MapsKnownRecordsAndRejectsUnknownRecords()
    {
        var databasePath = CopyFixture();
        var secretStore = new DpapiSecretStore(Path.Combine(_temporaryDirectory, "secrets"));
        var migrator = new DeviceSchemaMigrator(databasePath, secretStore);

        var result = await migrator.MigrateAsync(CancellationToken.None);
        var repository = new SqliteDeviceInstanceRepository(databasePath, secretStore);
        var instances = await repository.GetAllAsync(CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal(5, result.MigratedInstanceCount);
        Assert.Equal(
            [("fixture-corrupt", "invalid_config_json"), ("fixture-unknown", "unknown_device_type")],
            result.Rejected.Select(item => (item.LegacyRecordId, item.ErrorCode)).OrderBy(item => item.LegacyRecordId));
        Assert.Equal(5, instances.Count);

        AssertInstance(instances, "Synthetic DG BLE", "dglab", "dglab.coyote.v2", "dglab.coyote.v2.ble", PersistedTransportKind.BluetoothLowEnergy);
        AssertInstance(instances, "Synthetic DG WS", "dglab", "dglab.coyote.v3", "dglab.coyote.v3.websocket", PersistedTransportKind.WebSocket);
        AssertInstance(instances, "Synthetic YOKONEX BLE", "yokonex", "yokonex.ems.gen1", "yokonex.ems.v1_6.ble", PersistedTransportKind.BluetoothLowEnergy);
        AssertInstance(instances, "Synthetic YOKONEX IM", "yokonex", "yokonex.im.target", "yokonex.im.target", PersistedTransportKind.InstantMessaging);
        AssertInstance(instances, "Synthetic YOKONEX API Bridge", "yokonex", "yokonex.api_bridge.target", "yokonex.api_bridge.target", PersistedTransportKind.ApiBridge);

        Assert.True(File.Exists(result.BackupPath));
        Assert.Single(Directory.GetFiles(_temporaryDirectory, "device.db.v1-*.bak"));
        Assert.Equal(1, await ExecuteScalarAsync<long>(databasePath, "SELECT COUNT(*) FROM schema_migrations WHERE version = 2"));
        Assert.Equal(7, await ExecuteScalarAsync<long>(databasePath, "SELECT COUNT(*) FROM devices"));
    }

    [Fact]
    public async Task MigrateAsync_V1Secrets_StoresOnlyReferencesInV2PublicColumns()
    {
        var databasePath = CopyFixture();
        var secretStore = new DpapiSecretStore(Path.Combine(_temporaryDirectory, "secrets"));
        var result = await new DeviceSchemaMigrator(databasePath, secretStore).MigrateAsync(CancellationToken.None);
        var repository = new SqliteDeviceInstanceRepository(databasePath, secretStore);

        var instances = await repository.GetAllAsync(CancellationToken.None);
        var publicSnapshot = await ReadPublicSnapshotAsync(databasePath);
        var diagnosticSnapshot = string.Join('|', result.Rejected.Select(item => item.ToString()));
        // 活动旧表也必须被扫描，否则清洗失败时明文残留不会被发现。
        var legacySnapshot = string.Join('|', await ReadColumnAsync(databasePath, "SELECT COALESCE(config,'') FROM devices ORDER BY id"));
        foreach (var secret in FixtureSecrets)
        {
            Assert.DoesNotContain(secret, publicSnapshot, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, diagnosticSnapshot, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, legacySnapshot, StringComparison.Ordinal);
        }

        var secretPayloads = new List<string>();
        foreach (var endpoint in instances.SelectMany(instance => instance.Endpoints).Where(endpoint => endpoint.SecretReference is not null))
        {
            var payload = await secretStore.ReadAsync(endpoint.SecretReference!, CancellationToken.None);
            Assert.NotNull(payload);
            secretPayloads.Add(Encoding.UTF8.GetString(payload!.Value.Span));
        }

        var combinedSecrets = string.Join('|', secretPayloads);
        foreach (var secret in FixtureSecrets)
        {
            Assert.Contains(secret, combinedSecrets, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task MigrateAsync_WhenCheckpointThrows_RollsBackAndReusesSingleBackup()
    {
        var databasePath = CopyFixture();
        var secretDirectory = Path.Combine(_temporaryDirectory, "secrets");
        var secretStore = new DpapiSecretStore(secretDirectory);
        var converted = 0;
        var failingMigrator = new DeviceSchemaMigrator(
            databasePath,
            secretStore,
            checkpoint =>
            {
                if (checkpoint.Stage == DeviceMigrationStage.LegacyRecordConverted && ++converted == 2)
                {
                    throw new InvalidOperationException("合成事务故障");
                }
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() => failingMigrator.MigrateAsync(CancellationToken.None));

        Assert.Equal(0, await CountV2TablesAsync(databasePath));
        Assert.Single(Directory.GetFiles(_temporaryDirectory, "device.db.v1-*.bak"));
        Assert.Empty(Directory.Exists(secretDirectory) ? Directory.GetFiles(secretDirectory) : []);

        var retryResult = await new DeviceSchemaMigrator(databasePath, secretStore).MigrateAsync(CancellationToken.None);
        Assert.True(retryResult.Applied);
        Assert.Single(Directory.GetFiles(_temporaryDirectory, "device.db.v1-*.bak"));
    }

    [Fact]
    public async Task MigrateAsync_WhenRunTwice_IsIdempotent()
    {
        var databasePath = CopyFixture();
        var secretStore = new DpapiSecretStore(Path.Combine(_temporaryDirectory, "secrets"));
        var migrator = new DeviceSchemaMigrator(databasePath, secretStore);

        var first = await migrator.MigrateAsync(CancellationToken.None);
        var repository = new SqliteDeviceInstanceRepository(databasePath, secretStore);
        var firstSnapshot = await repository.GetAllAsync(CancellationToken.None);
        var second = await migrator.MigrateAsync(CancellationToken.None);
        var secondSnapshot = await repository.GetAllAsync(CancellationToken.None);

        Assert.True(first.Applied);
        Assert.False(second.Applied);
        Assert.Equal(firstSnapshot.Select(item => item.InstanceId), secondSnapshot.Select(item => item.InstanceId));
        Assert.Equal(5, await ExecuteScalarAsync<long>(databasePath, "SELECT COUNT(*) FROM device_instances"));
        Assert.Equal(5, await ExecuteScalarAsync<long>(databasePath, "SELECT COUNT(*) FROM device_endpoints"));
        Assert.Single(Directory.GetFiles(_temporaryDirectory, "device.db.v1-*.bak"));
    }

    [Fact]
    public async Task SaveAsync_NewInstance_WritesOnlyV2AndReturnsImmutableCopies()
    {
        var databasePath = CopyFixture();
        var secretStore = new DpapiSecretStore(Path.Combine(_temporaryDirectory, "secrets"));
        var repository = new SqliteDeviceInstanceRepository(databasePath, secretStore);
        await repository.GetAllAsync(CancellationToken.None);
        var endpoints = new List<PersistedDeviceEndpoint>
        {
            new(
                EndpointId.New(),
                PersistedTransportKind.BluetoothLowEnergy,
                "{\"address\":\"AA:BB:CC:DD:EE:99\"}",
                null,
                9,
                true,
                DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
                DateTimeOffset.Parse("2026-01-02T03:04:05Z"))
        };
        var instance = new PersistedDeviceInstance(
            DeviceInstanceId.New(),
            new PluginId("dglab"),
            new ModelId("dglab.coyote.v2"),
            new ProtocolProfileId("dglab.coyote.v2.ble"),
            "新设备",
            false,
            2,
            endpoints,
            DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
            DateTimeOffset.Parse("2026-01-02T03:04:05Z"));
        endpoints.Clear();

        await repository.SaveAsync(instance, CancellationToken.None);
        var snapshot = await repository.GetAllAsync(CancellationToken.None);

        Assert.Equal(7, await ExecuteScalarAsync<long>(databasePath, "SELECT COUNT(*) FROM devices"));
        Assert.Contains(snapshot, item => item.InstanceId == instance.InstanceId && item.Endpoints.Count == 1);
        Assert.IsAssignableFrom<IReadOnlyList<PersistedDeviceInstance>>(snapshot);
        Assert.Throws<NotSupportedException>(() => ((IList<PersistedDeviceInstance>)snapshot).Clear());
    }

    [Fact]
    public async Task Repository_MissingDatabase_BootstrapsV2WithoutLegacyBackup()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "greenfield.db");
        var secretStore = new DpapiSecretStore(Path.Combine(_temporaryDirectory, "greenfield-secrets"));
        using var repository = new SqliteDeviceInstanceRepository(databasePath, secretStore);

        var instances = await repository.GetAllAsync(CancellationToken.None);

        Assert.Empty(instances);
        Assert.True(File.Exists(databasePath));
        Assert.Equal(1, await ExecuteScalarAsync<long>(databasePath, "SELECT COUNT(*) FROM schema_migrations WHERE version = 2"));
        Assert.Empty(Directory.GetFiles(_temporaryDirectory, "greenfield.db.v1-*.bak"));
    }

    [Fact]
    public async Task SaveAsync_PublicConfigContainsAliasNestedOrCredentialUri_Rejects()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "public-config.db");
        var secretStore = new DpapiSecretStore(Path.Combine(_temporaryDirectory, "public-config-secrets"));
        using var repository = new SqliteDeviceInstanceRepository(databasePath, secretStore);
        var invalidConfigurations = new[]
        {
            (PersistedTransportKind.WebSocket, "{\"apiKey\":\"secret\",\"url\":\"wss://example.invalid\"}"),
            (PersistedTransportKind.WebSocket, "{\"url\":\"wss://user:password@example.invalid/socket\"}"),
            (PersistedTransportKind.WebSocket, "{\"url\":\"wss://example.invalid/socket?token=secret\"}"),
            (PersistedTransportKind.BluetoothLowEnergy, "{\"address\":\"AA:BB:CC:DD:EE:01\",\"rawPayload\":{\"value\":\"secret\"}}")
        };

        foreach (var (transportKind, publicConfigJson) in invalidConfigurations)
        {
            var instance = CreatePersistedInstance(transportKind, publicConfigJson);
            await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(instance, CancellationToken.None).AsTask());
        }

        // 正向用例证明校验并非无条件拒绝一切输入。
        var allowed = CreatePersistedInstance(
            PersistedTransportKind.WebSocket,
            "{\"url\":\"wss://example.invalid/socket\",\"reconnectInterval\":5000}");
        await repository.SaveAsync(allowed, CancellationToken.None);
        Assert.Contains(
            await repository.GetAllAsync(CancellationToken.None),
            item => item.InstanceId == allowed.InstanceId);
    }

    private static PersistedDeviceInstance CreatePersistedInstance(
        PersistedTransportKind transportKind,
        string publicConfigJson)
    {
        var now = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        return new PersistedDeviceInstance(
            DeviceInstanceId.New(),
            new PluginId("dglab"),
            new ModelId("dglab.coyote.v3"),
            new ProtocolProfileId("dglab.coyote.v3.websocket"),
            "公共配置测试",
            false,
            2,
            [new PersistedDeviceEndpoint(EndpointId.New(), transportKind, publicConfigJson, null, 0, true, now, now)],
            now,
            now);
    }

    [Fact]
    public async Task MigrateAsync_RejectedSecretConfigs_AreQuarantinedSanitizedAndDurable()
    {
        var databasePath = CopyFixture();
        const string unknownSecret = "rejected-unknown-token";
        const string corruptSecret = "rejected-corrupt-token";
        await ExecuteNonQueryAsync(
            databasePath,
            "UPDATE devices SET config = @unknownConfig WHERE id = 'fixture-unknown'; " +
            "UPDATE devices SET config = @corruptConfig WHERE id = 'fixture-corrupt';",
            ("@unknownConfig", $"{{\"Token\":\"{unknownSecret}\",\"ConnectionMode\":2}}"),
            ("@corruptConfig", $"{{\"Token\":\"{corruptSecret}\""));
        var secretStore = new DpapiSecretStore(Path.Combine(_temporaryDirectory, "rejected-secrets"));
        var migrator = new DeviceSchemaMigrator(databasePath, secretStore);

        var first = await migrator.MigrateAsync(CancellationToken.None);
        var second = await migrator.MigrateAsync(CancellationToken.None);
        var activeConfigs = await ReadColumnAsync(databasePath, "SELECT config FROM devices ORDER BY id");
        var rejectionRows = await ReadRowsAsync(
            databasePath,
            "SELECT legacy_record_id, error_code, secret_reference FROM device_migration_rejections ORDER BY legacy_record_id");

        Assert.Equal(first.Rejected, second.Rejected);
        Assert.Equal(2, rejectionRows.Count);
        Assert.All(activeConfigs, value =>
        {
            Assert.DoesNotContain(unknownSecret, value, StringComparison.Ordinal);
            Assert.DoesNotContain(corruptSecret, value, StringComparison.Ordinal);
        });
        foreach (var row in rejectionRows)
        {
            Assert.DoesNotContain(unknownSecret, string.Join('|', row), StringComparison.Ordinal);
            Assert.DoesNotContain(corruptSecret, string.Join('|', row), StringComparison.Ordinal);
            var quarantined = await secretStore.ReadAsync(row[2], CancellationToken.None);
            Assert.NotNull(quarantined);
        }
    }

    [Fact]
    public async Task MigrateAsync_AlreadyApplied_DoesNotReportCorruptBackupPath()
    {
        var databasePath = CopyFixture();
        var secretStore = new DpapiSecretStore(Path.Combine(_temporaryDirectory, "applied-secrets"));
        var migrator = new DeviceSchemaMigrator(databasePath, secretStore);
        var first = await migrator.MigrateAsync(CancellationToken.None);
        Assert.True(File.Exists(first.BackupPath));

        // 模拟备份文件在迁移后损坏。
        await File.WriteAllTextAsync(first.BackupPath, "not a sqlite database");
        var second = await migrator.MigrateAsync(CancellationToken.None);

        Assert.False(second.Applied);
        // 不能把损坏文件当作可用回滚基线报告给调用方；
        // 必须明确断言为空，否则任何其他路径也会让断言通过。
        Assert.Equal(string.Empty, second.BackupPath);
    }

    [Fact]
    public async Task MigrateAsync_ForeignBackupWithSameRowCount_DoesNotReuseIt()
    {
        var databasePath = CopyFixture();
        var expectedCount = await ExecuteScalarAsync<long>(databasePath, "SELECT COUNT(*) FROM devices");
        var foreignBackup = Path.Combine(_temporaryDirectory, "device.db.v1-19980101T0000000000000Z.bak");
        var rows = string.Join(
            " ",
            Enumerable.Range(0, (int)expectedCount).Select(index =>
                $"INSERT INTO devices VALUES ('other-{index}','其他安装','dglab','{{}}',0,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z');"));
        await CreateDatabaseAsync(
            foreignBackup,
            "CREATE TABLE devices (id TEXT PRIMARY KEY, name TEXT, type TEXT, config TEXT, autoConnect INTEGER, createdAt TEXT, updatedAt TEXT); " + rows);
        var secretStore = new DpapiSecretStore(Path.Combine(_temporaryDirectory, "same-count-secrets"));

        var result = await new DeviceSchemaMigrator(databasePath, secretStore).MigrateAsync(CancellationToken.None);

        // 行数相同但来源不同，不能被当作本库的回滚基线。
        Assert.NotEqual(foreignBackup, result.BackupPath);
        Assert.Equal(
            1,
            await ExecuteScalarAsync<long>(result.BackupPath, "SELECT COUNT(*) FROM devices WHERE id = 'fixture-unknown'"));
    }

    [Fact]
    public async Task MigrateAsync_ForeignValidBackup_DoesNotReuseIt()
    {
        var databasePath = CopyFixture();
        var foreignBackup = Path.Combine(_temporaryDirectory, "device.db.v1-19990101T0000000000000Z.bak");
        await CreateDatabaseAsync(
            foreignBackup,
            "CREATE TABLE devices (id TEXT PRIMARY KEY, name TEXT, type TEXT, config TEXT, autoConnect INTEGER, createdAt TEXT, updatedAt TEXT); " +
            "INSERT INTO devices VALUES ('foreign-1','外来设备','dglab','{}',0,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z');");
        var secretStore = new DpapiSecretStore(Path.Combine(_temporaryDirectory, "foreign-secrets"));

        var result = await new DeviceSchemaMigrator(databasePath, secretStore).MigrateAsync(CancellationToken.None);

        Assert.NotEqual(foreignBackup, result.BackupPath);
        Assert.Equal(7, await ExecuteScalarAsync<long>(result.BackupPath, "SELECT COUNT(*) FROM devices"));
    }

    [Fact]
    public async Task MigrateAsync_CorruptExistingBackup_DoesNotReuseIt()
    {
        var databasePath = CopyFixture();
        var corruptBackup = Path.Combine(_temporaryDirectory, "device.db.v1-20000101T0000000000000Z.bak");
        await File.WriteAllTextAsync(corruptBackup, "not a sqlite database");
        var secretStore = new DpapiSecretStore(Path.Combine(_temporaryDirectory, "backup-secrets"));

        var result = await new DeviceSchemaMigrator(databasePath, secretStore).MigrateAsync(CancellationToken.None);

        Assert.NotEqual(corruptBackup, result.BackupPath);
        Assert.Equal("ok", await ExecuteScalarAsync<string>(result.BackupPath, "PRAGMA integrity_check"));
        Assert.Equal(7, await ExecuteScalarAsync<long>(result.BackupPath, "SELECT COUNT(*) FROM devices"));
        // 损坏文件必须被隔离改名，而不是留在原位继续被当作候选。
        Assert.False(File.Exists(corruptBackup));
        Assert.Single(Directory.GetFiles(_temporaryDirectory, "device.db.v1-*.bak.invalid-*"));
    }

    private static async Task CreateDatabaseAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(CreateTestConnectionString(databasePath, allowCreate: true));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteNonQueryAsync(
        string databasePath,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(CreateTestConnectionString(databasePath));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<string>> ReadColumnAsync(string databasePath, string sql)
    {
        var rows = await ReadRowsAsync(databasePath, sql);
        return rows.Select(row => row[0]).ToArray();
    }

    private static async Task<IReadOnlyList<string[]>> ReadRowsAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(CreateTestConnectionString(databasePath));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string[]>();
        while (await reader.ReadAsync())
        {
            var values = new string[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[index] = reader.IsDBNull(index) ? string.Empty : reader.GetValue(index).ToString()!;
            }

            rows.Add(values);
        }

        return rows;
    }

    private string CopyFixture()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Databases", "device-schema-v1.db");
        var destination = Path.Combine(_temporaryDirectory, "device.db");
        File.Copy(source, destination, overwrite: true);
        return destination;
    }

    private static void AssertInstance(
        IReadOnlyList<PersistedDeviceInstance> instances,
        string displayName,
        string pluginId,
        string modelId,
        string profileId,
        PersistedTransportKind transportKind)
    {
        var instance = Assert.Single(instances, item => item.DisplayName == displayName);
        Assert.Equal(pluginId, instance.PluginId.Value);
        Assert.Equal(modelId, instance.ModelId.Value);
        Assert.Equal(profileId, instance.ProfileId.Value);
        Assert.Equal(transportKind, Assert.Single(instance.Endpoints).TransportKind);
    }

    private static async Task<string> ReadPublicSnapshotAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(CreateTestConnectionString(databasePath));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT public_config_json, COALESCE(secret_reference, '') FROM device_endpoints ORDER BY endpoint_id";
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
            values.Add(reader.GetString(1));
        }

        return string.Join('|', values);
    }

    private static async Task<long> CountV2TablesAsync(string databasePath) =>
        await ExecuteScalarAsync<long>(databasePath,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('schema_migrations','device_instances','device_endpoints')");

    private static async Task<T> ExecuteScalarAsync<T>(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(CreateTestConnectionString(databasePath));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        Assert.NotNull(value);
        return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string CreateTestConnectionString(string databasePath, bool allowCreate = false) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = allowCreate ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
