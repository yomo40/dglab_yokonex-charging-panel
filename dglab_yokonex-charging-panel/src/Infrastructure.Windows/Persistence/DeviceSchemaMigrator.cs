using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChargingPanel.Core.Devices.Persistence;
using ChargingPanel.Core.Security;
using Microsoft.Data.Sqlite;

namespace ChargingPanel.Infrastructure.Windows.Persistence;

public enum DeviceMigrationStage
{
    LegacyRecordConverted
}

public sealed record DeviceMigrationCheckpoint(DeviceMigrationStage Stage, string LegacyRecordId);

public sealed record MigrationRejected(string LegacyRecordId, string ErrorCode);

public sealed class DeviceMigrationResult
{
    public DeviceMigrationResult(
        bool applied,
        int migratedInstanceCount,
        string backupPath,
        IEnumerable<MigrationRejected> rejected)
    {
        ArgumentNullException.ThrowIfNull(rejected);
        Applied = applied;
        MigratedInstanceCount = migratedInstanceCount;
        BackupPath = backupPath;
        Rejected = new ReadOnlyCollection<MigrationRejected>(rejected.ToArray());
    }

    public bool Applied { get; }

    public int MigratedInstanceCount { get; }

    public string BackupPath { get; }

    public IReadOnlyList<MigrationRejected> Rejected { get; }
}

public sealed class DeviceSchemaMigrator
{
    private const int SchemaVersion = 2;
    private const string MigratedLegacyConfig = "{\"migratedToSchemaVersion\":2}";
    private static readonly string[] CanonicalMigrationStatements =
    [
        "CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL, checksum TEXT NOT NULL)",
        "CREATE TABLE device_instances (instance_id TEXT PRIMARY KEY, plugin_id TEXT NOT NULL, model_id TEXT NOT NULL, profile_id TEXT NOT NULL, display_name TEXT NOT NULL, auto_connect INTEGER NOT NULL CHECK(auto_connect IN (0,1)), config_schema_version INTEGER NOT NULL CHECK(config_schema_version > 0), created_at TEXT NOT NULL, updated_at TEXT NOT NULL)",
        "CREATE TABLE device_endpoints (endpoint_id TEXT PRIMARY KEY, instance_id TEXT NOT NULL, transport_kind TEXT NOT NULL CHECK(transport_kind IN ('BluetoothLowEnergy','WebSocket','InstantMessaging','ApiBridge')), public_config_json TEXT NOT NULL, secret_reference TEXT, priority INTEGER NOT NULL CHECK(priority >= 0), enabled INTEGER NOT NULL CHECK(enabled IN (0,1)), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, FOREIGN KEY(instance_id) REFERENCES device_instances(instance_id) ON DELETE CASCADE)",
        "CREATE INDEX ix_device_endpoints_instance_id ON device_endpoints(instance_id)",
        "CREATE TABLE secret_cleanup_outbox (secret_reference TEXT PRIMARY KEY, created_at TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0 CHECK(attempts >= 0))",
        "CREATE TABLE device_migration_rejections (legacy_record_id TEXT PRIMARY KEY, error_code TEXT NOT NULL, secret_reference TEXT NOT NULL, rejected_at TEXT NOT NULL)"
    ];

    private static readonly string MigrationChecksum = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(";\n", CanonicalMigrationStatements))))
        .ToLowerInvariant();

    private static readonly HashSet<string> SecretPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConnectCode",
        "ConnectionCode",
        "Uid",
        "Token",
        "UserId",
        "TargetUserId",
        "TargetId",
        "UserSig",
        "Sign",
        "PairingKey",
        "PairingCode"
    };

    private readonly string _databasePath;
    private readonly ISecretStore _secretStore;
    private readonly Action<DeviceMigrationCheckpoint>? _checkpoint;

    public DeviceSchemaMigrator(
        string databasePath,
        ISecretStore secretStore,
        Action<DeviceMigrationCheckpoint>? checkpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(secretStore);
        _databasePath = Path.GetFullPath(databasePath);
        _secretStore = secretStore;
        _checkpoint = checkpoint;
    }

    public async Task<DeviceMigrationResult> MigrateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = new SqliteConnection(CreateConnectionString(_databasePath));
        await connection.OpenAsync(cancellationToken);
        await EnableForeignKeysAsync(connection, cancellationToken);

        if (await IsMigrationAppliedAsync(connection, cancellationToken))
        {
            var count = await CountInstancesAsync(connection, cancellationToken);
            var rejected = await ReadPersistedRejectionsAsync(connection, cancellationToken);
            var validatedBackup = await FindValidatedBackupForAppliedMigrationAsync(connection, cancellationToken);
            return new DeviceMigrationResult(false, count, validatedBackup ?? string.Empty, rejected);
        }

        if (!await LegacyDevicesTableExistsAsync(connection, cancellationToken))
        {
            await using var bootstrapTransaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await CreateV2TablesAsync(connection, bootstrapTransaction, cancellationToken);
                await RecordMigrationAsync(connection, bootstrapTransaction, cancellationToken);
                await bootstrapTransaction.CommitAsync(cancellationToken);
                return new DeviceMigrationResult(true, 0, string.Empty, []);
            }
            catch
            {
                await bootstrapTransaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        var backupPath = await EnsureBackupAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var createdSecretReferences = new List<string>();
        try
        {
            await CreateV2TablesAsync(connection, transaction, cancellationToken);
            var legacyRecords = await ReadLegacyRecordsAsync(connection, transaction, cancellationToken);
            var rejected = new List<MigrationRejected>();
            var migratedCount = 0;
            foreach (var legacyRecord in legacyRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryMapLegacyRecord(legacyRecord, out var mapped, out var errorCode))
                {
                    var quarantineReference = await _secretStore.SaveAsync(
                        Encoding.UTF8.GetBytes(legacyRecord.Config ?? string.Empty),
                        cancellationToken);
                    createdSecretReferences.Add(quarantineReference);
                    await InsertMigrationRejectionAsync(
                        connection,
                        transaction,
                        legacyRecord.Id,
                        errorCode,
                        quarantineReference,
                        cancellationToken);
                    await SanitizeLegacyRecordAsync(connection, transaction, legacyRecord.Id, cancellationToken);
                    rejected.Add(new MigrationRejected(legacyRecord.Id, errorCode));
                    continue;
                }

                string? secretReference = null;
                if (mapped.SecretJson is not null)
                {
                    secretReference = await _secretStore.SaveAsync(
                        Encoding.UTF8.GetBytes(mapped.SecretJson),
                        cancellationToken);
                    createdSecretReferences.Add(secretReference);
                }

                await InsertMappedRecordAsync(
                    connection,
                    transaction,
                    mapped,
                    secretReference,
                    cancellationToken);
                await SanitizeLegacyRecordAsync(connection, transaction, legacyRecord.Id, cancellationToken);
                migratedCount++;
                _checkpoint?.Invoke(new DeviceMigrationCheckpoint(
                    DeviceMigrationStage.LegacyRecordConverted,
                    legacyRecord.Id));
            }

            await RecordMigrationAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new DeviceMigrationResult(true, migratedCount, backupPath, rejected);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            foreach (var secretReference in createdSecretReferences)
            {
                try
                {
                    await _secretStore.DeleteAsync(secretReference, CancellationToken.None);
                }
                catch
                {
                    // 已知残留风险：此处发生在事务回滚后，outbox 表随事务一并回滚，
                    // 因此该引用没有任何持久记录，启动清理也无法回收。
                    // 密文文件会成为无引用孤儿，需由安装/运维层清理密钥目录。
                }
            }

            throw;
        }
    }

    private async Task<string> EnsureBackupAsync(
        SqliteConnection sourceConnection,
        CancellationToken cancellationToken)
    {
        var expectedIdentity = await ComputeLegacyIdentityAsync(sourceConnection, cancellationToken);
        var existing = await FindExistingValidBackupAsync(expectedIdentity, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(_databasePath)
            ?? throw new InvalidOperationException("数据库路径缺少父目录。");
        Directory.CreateDirectory(directory);
        var backupPath = Path.Combine(
            directory,
            $"{Path.GetFileName(_databasePath)}.v1-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}.bak");
        var temporaryBackupPath = backupPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var backupConnection = new SqliteConnection(new SqliteConnectionStringBuilder
                         {
                             DataSource = temporaryBackupPath,
                             Mode = SqliteOpenMode.ReadWriteCreate,
                             Pooling = false
                         }.ToString()))
            {
                await backupConnection.OpenAsync(cancellationToken);
                // BackupDatabase 是同步 API，Microsoft.Data.Sqlite 未提供中途取消能力。
                // 这里移出调用线程以免阻塞异步管道；大库仍会完整复制后才响应取消。
                var connectionToBackup = backupConnection;
                await Task.Run(() => sourceConnection.BackupDatabase(connectionToBackup), cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!await IsValidBackupAsync(temporaryBackupPath, expectedIdentity, cancellationToken))
            {
                throw new InvalidDataException("v1 备份完整性验证失败。");
            }

            File.Move(temporaryBackupPath, backupPath, overwrite: false);
            return backupPath;
        }
        finally
        {
            if (File.Exists(temporaryBackupPath))
            {
                File.Delete(temporaryBackupPath);
            }
        }
    }

    private async Task<string?> FindExistingValidBackupAsync(
        string expectedIdentity,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        foreach (var candidate in Directory.GetFiles(directory, $"{Path.GetFileName(_databasePath)}.v1-*.bak")
                     .Order(StringComparer.Ordinal))
        {
            if (await IsValidBackupAsync(candidate, expectedIdentity, cancellationToken))
            {
                return candidate;
            }

            // 并发迁移可能已经隔离同一文件；此时不视为错误。
            try
            {
                File.Move(candidate, candidate + $".invalid-{Guid.NewGuid():N}", overwrite: false);
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        return null;
    }

    /// <summary>
    /// 迁移已应用后仍可核对备份身份：清洗只改写 config 列，
    /// 而身份指纹仅由 id 与时间戳构成，因此保持不变。
    /// 找不到通过校验的备份时返回 null，绝不报告未经验证的路径。
    /// </summary>
    private async Task<string?> FindValidatedBackupForAppliedMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await LegacyDevicesTableExistsAsync(connection, cancellationToken))
        {
            return null;
        }

        var expectedIdentity = await ComputeLegacyIdentityAsync(connection, cancellationToken);
        var directory = Path.GetDirectoryName(_databasePath);
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        foreach (var candidate in Directory.GetFiles(directory, $"{Path.GetFileName(_databasePath)}.v1-*.bak")
                     .Order(StringComparer.Ordinal))
        {
            if (await IsValidBackupAsync(candidate, expectedIdentity, cancellationToken))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 计算旧库的逻辑身份指纹。只使用记录 ID 与时间戳等非敏感字段，
    /// 不纳入 config，避免把明文凭据带入摘要计算。
    /// </summary>
    private static async Task<string> ComputeLegacyIdentityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, createdAt, updatedAt FROM devices ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var builder = new StringBuilder();
        while (await reader.ReadAsync(cancellationToken))
        {
            builder.Append(reader.GetString(0)).Append('\u001F')
                .Append(reader.GetString(1)).Append('\u001F')
                .Append(reader.GetString(2)).Append('\u001F');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static async Task<bool> IsValidBackupAsync(
        string backupPath,
        string expectedIdentity,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            await using var integrityCommand = connection.CreateCommand();
            integrityCommand.CommandText = "PRAGMA integrity_check";
            if (!string.Equals(
                    await integrityCommand.ExecuteScalarAsync(cancellationToken) as string,
                    "ok",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            await using var schemaCommand = connection.CreateCommand();
            schemaCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='devices'";
            if (Convert.ToInt64(await schemaCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
            {
                return false;
            }

            // 逻辑身份必须与源库一致；仅比对行数无法区分其他安装留下的同名备份。
            return string.Equals(
                await ComputeLegacyIdentityAsync(connection, cancellationToken),
                expectedIdentity,
                StringComparison.Ordinal);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async Task CreateV2TablesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var statement in CanonicalMigrationStatements)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<LegacyDeviceRecord>> ReadLegacyRecordsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, name, type, config, autoConnect, createdAt, updatedAt FROM devices ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<LegacyDeviceRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new LegacyDeviceRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4) == 1,
                reader.GetString(5),
                reader.GetString(6)));
        }

        return records;
    }

    private static bool TryMapLegacyRecord(
        LegacyDeviceRecord record,
        out MappedDeviceRecord mapped,
        out string errorCode)
    {
        mapped = null!;
        if (record.Type is not ("dglab" or "yokonex"))
        {
            errorCode = "unknown_device_type";
            return false;
        }

        if (string.IsNullOrWhiteSpace(record.Config))
        {
            errorCode = "missing_config_json";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(record.Config);
        }
        catch (JsonException)
        {
            errorCode = "invalid_config_json";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetInt32(document.RootElement, "ConnectionMode", out var connectionMode))
            {
                errorCode = "missing_connection_mode";
                return false;
            }

            if (!DateTimeOffset.TryParse(record.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt) ||
                !DateTimeOffset.TryParse(record.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedAt))
            {
                errorCode = "invalid_timestamp";
                return false;
            }

            if (!TryResolveStableIds(
                    record.Type,
                    connectionMode,
                    document.RootElement,
                    out var modelId,
                    out var profileId,
                    out var transportKind,
                    out errorCode))
            {
                return false;
            }

            if (!TryBuildPublicConfiguration(
                    transportKind,
                    document.RootElement,
                    out var publicConfigJson,
                    out errorCode))
            {
                return false;
            }

            var secretValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (SecretPropertyNames.Contains(property.Name) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    secretValues[property.Name] = property.Value.GetString()!;
                }
            }

            mapped = new MappedDeviceRecord(
                Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"),
                record.Type,
                modelId,
                profileId,
                record.Name,
                record.AutoConnect,
                transportKind,
                publicConfigJson,
                secretValues.Count == 0 ? null : JsonSerializer.Serialize(secretValues),
                createdAt,
                updatedAt);
            errorCode = string.Empty;
            return true;
        }
    }

    private static bool TryResolveStableIds(
        string deviceType,
        int connectionMode,
        JsonElement config,
        out string modelId,
        out string profileId,
        out PersistedTransportKind transportKind,
        out string errorCode)
    {
        modelId = string.Empty;
        profileId = string.Empty;
        transportKind = default;
        errorCode = string.Empty;

        if (deviceType == "dglab")
        {
            if (!TryGetInt32(config, "DGLabVersion", out var version))
            {
                errorCode = "missing_model_metadata";
                return false;
            }

            if (connectionMode == 1 && version == 0)
            {
                modelId = "dglab.coyote.v2";
                profileId = "dglab.coyote.v2.ble";
                transportKind = PersistedTransportKind.BluetoothLowEnergy;
                return true;
            }

            if (connectionMode == 1 && version == 1)
            {
                modelId = "dglab.coyote.v3";
                profileId = "dglab.coyote.v3.ble";
                transportKind = PersistedTransportKind.BluetoothLowEnergy;
                return true;
            }

            if (connectionMode == 0 && version == 1)
            {
                modelId = "dglab.coyote.v3";
                profileId = "dglab.coyote.v3.websocket";
                transportKind = PersistedTransportKind.WebSocket;
                return true;
            }

            errorCode = "unsupported_dglab_mapping";
            return false;
        }

        if (connectionMode == 2)
        {
            modelId = "yokonex.im.target";
            profileId = "yokonex.im.target";
            transportKind = PersistedTransportKind.InstantMessaging;
            return true;
        }

        if (connectionMode == 3)
        {
            modelId = "yokonex.api_bridge.target";
            profileId = "yokonex.api_bridge.target";
            transportKind = PersistedTransportKind.ApiBridge;
            return true;
        }

        if (connectionMode != 1 ||
            !TryGetInt32(config, "YokonexType", out var yokonexType) ||
            !TryGetInt32(config, "YokonexProtocolGeneration", out var generation))
        {
            errorCode = "missing_model_metadata";
            return false;
        }

        (modelId, profileId) = (yokonexType, generation) switch
        {
            (0, 1) => ("yokonex.ems.gen1", "yokonex.ems.v1_6.ble"),
            (0, 2) => ("yokonex.ems.gen2", "yokonex.ems.v2_0.ble"),
            (1, 3) => ("yokonex.enema.gen1", "yokonex.enema.v1_0.ble"),
            (2, 4) or (3, 4) => ("yokonex.toy.v1_1", "yokonex.toy.v1_1.ble"),
            _ => (string.Empty, string.Empty)
        };
        if (modelId.Length == 0)
        {
            errorCode = "unsupported_yokonex_mapping";
            return false;
        }

        transportKind = PersistedTransportKind.BluetoothLowEnergy;
        return true;
    }

    private static bool TryBuildPublicConfiguration(
        PersistedTransportKind transportKind,
        JsonElement config,
        out string publicConfigJson,
        out string errorCode)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (transportKind == PersistedTransportKind.BluetoothLowEnergy)
        {
            if (!TryGetString(config, "Address", out var address))
            {
                publicConfigJson = string.Empty;
                errorCode = "missing_endpoint_address";
                return false;
            }

            values["address"] = address;
        }
        else if (transportKind is PersistedTransportKind.WebSocket or PersistedTransportKind.ApiBridge &&
                 TryGetString(config, "WebSocketUrl", out var url))
        {
            values["url"] = url;
        }

        if (TryGetInt32(config, "ReconnectInterval", out var reconnectInterval))
        {
            values["reconnectInterval"] = reconnectInterval;
        }

        publicConfigJson = JsonSerializer.Serialize(values);
        errorCode = string.Empty;
        return true;
    }

    private static bool TryGetInt32(JsonElement element, string propertyName, out int value)
    {
        value = default;
        if (!TryGetProperty(element, propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(property.GetString(), CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(element, propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static async Task InsertMappedRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MappedDeviceRecord record,
        string? secretReference,
        CancellationToken cancellationToken)
    {
        await using (var instanceCommand = connection.CreateCommand())
        {
            instanceCommand.Transaction = transaction;
            instanceCommand.CommandText = """
                INSERT INTO device_instances
                    (instance_id, plugin_id, model_id, profile_id, display_name, auto_connect, config_schema_version, created_at, updated_at)
                VALUES
                    (@instanceId, @pluginId, @modelId, @profileId, @displayName, @autoConnect, @configSchemaVersion, @createdAt, @updatedAt)
                """;
            instanceCommand.Parameters.AddWithValue("@instanceId", record.InstanceId);
            instanceCommand.Parameters.AddWithValue("@pluginId", record.PluginId);
            instanceCommand.Parameters.AddWithValue("@modelId", record.ModelId);
            instanceCommand.Parameters.AddWithValue("@profileId", record.ProfileId);
            instanceCommand.Parameters.AddWithValue("@displayName", record.DisplayName);
            instanceCommand.Parameters.AddWithValue("@autoConnect", record.AutoConnect ? 1 : 0);
            instanceCommand.Parameters.AddWithValue("@configSchemaVersion", SchemaVersion);
            instanceCommand.Parameters.AddWithValue("@createdAt", record.CreatedAt.ToString("O"));
            instanceCommand.Parameters.AddWithValue("@updatedAt", record.UpdatedAt.ToString("O"));
            await instanceCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var endpointCommand = connection.CreateCommand();
        endpointCommand.Transaction = transaction;
        endpointCommand.CommandText = """
            INSERT INTO device_endpoints
                (endpoint_id, instance_id, transport_kind, public_config_json, secret_reference, priority, enabled, created_at, updated_at)
            VALUES
                (@endpointId, @instanceId, @transportKind, @publicConfigJson, @secretReference, @priority, @enabled, @createdAt, @updatedAt)
            """;
        endpointCommand.Parameters.AddWithValue("@endpointId", record.EndpointId);
        endpointCommand.Parameters.AddWithValue("@instanceId", record.InstanceId);
        endpointCommand.Parameters.AddWithValue("@transportKind", record.TransportKind.ToString());
        endpointCommand.Parameters.AddWithValue("@publicConfigJson", record.PublicConfigJson);
        endpointCommand.Parameters.AddWithValue("@secretReference", (object?)secretReference ?? DBNull.Value);
        endpointCommand.Parameters.AddWithValue("@priority", 0);
        endpointCommand.Parameters.AddWithValue("@enabled", 1);
        endpointCommand.Parameters.AddWithValue("@createdAt", record.CreatedAt.ToString("O"));
        endpointCommand.Parameters.AddWithValue("@updatedAt", record.UpdatedAt.ToString("O"));
        await endpointCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMigrationRejectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string legacyRecordId,
        string errorCode,
        string secretReference,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO device_migration_rejections
                (legacy_record_id, error_code, secret_reference, rejected_at)
            VALUES
                (@legacyRecordId, @errorCode, @secretReference, @rejectedAt)
            """;
        command.Parameters.AddWithValue("@legacyRecordId", legacyRecordId);
        command.Parameters.AddWithValue("@errorCode", errorCode);
        command.Parameters.AddWithValue("@secretReference", secretReference);
        command.Parameters.AddWithValue("@rejectedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<MigrationRejected>> ReadPersistedRejectionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT legacy_record_id, error_code FROM device_migration_rejections ORDER BY legacy_record_id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rejected = new List<MigrationRejected>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rejected.Add(new MigrationRejected(reader.GetString(0), reader.GetString(1)));
        }

        return rejected;
    }

    private static async Task SanitizeLegacyRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string legacyRecordId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE devices SET config = @config WHERE id = @id";
        command.Parameters.AddWithValue("@config", MigratedLegacyConfig);
        command.Parameters.AddWithValue("@id", legacyRecordId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RecordMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO schema_migrations(version, applied_at, checksum) VALUES (@version, @appliedAt, @checksum)";
        command.Parameters.AddWithValue("@version", SchemaVersion);
        command.Parameters.AddWithValue("@appliedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("@checksum", MigrationChecksum);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> IsMigrationAppliedAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations'";
        if (Convert.ToInt64(await tableCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
        {
            return false;
        }

        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT checksum FROM schema_migrations WHERE version = @version";
        versionCommand.Parameters.AddWithValue("@version", SchemaVersion);
        var checksum = await versionCommand.ExecuteScalarAsync(cancellationToken) as string;
        if (checksum is null)
        {
            return false;
        }

        if (!string.Equals(checksum, MigrationChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("schema v2 迁移校验和不匹配。");
        }

        return true;
    }

    private static async Task<bool> LegacyDevicesTableExistsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'devices'";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<int> CountInstancesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM device_instances";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task EnableForeignKeysAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string CreateConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

    private sealed record LegacyDeviceRecord(
        string Id,
        string Name,
        string Type,
        string? Config,
        bool AutoConnect,
        string CreatedAt,
        string UpdatedAt);

    private sealed record MappedDeviceRecord(
        string InstanceId,
        string EndpointId,
        string PluginId,
        string ModelId,
        string ProfileId,
        string DisplayName,
        bool AutoConnect,
        PersistedTransportKind TransportKind,
        string PublicConfigJson,
        string? SecretJson,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
