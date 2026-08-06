using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Devices.Persistence;
using ChargingPanel.Core.Security;
using Microsoft.Data.Sqlite;

namespace ChargingPanel.Infrastructure.Windows.Persistence;

public sealed class SqliteDeviceInstanceRepository : IDeviceInstanceRepository, IDisposable
{
    private static readonly IReadOnlyDictionary<PersistedTransportKind, HashSet<string>> AllowedPublicProperties =
        new Dictionary<PersistedTransportKind, HashSet<string>>
        {
            [PersistedTransportKind.BluetoothLowEnergy] = new(StringComparer.Ordinal)
            {
                "address",
                "reconnectInterval"
            },
            [PersistedTransportKind.WebSocket] = new(StringComparer.Ordinal)
            {
                "url",
                "reconnectInterval"
            },
            [PersistedTransportKind.InstantMessaging] = new(StringComparer.Ordinal)
            {
                "reconnectInterval"
            },
            [PersistedTransportKind.ApiBridge] = new(StringComparer.Ordinal)
            {
                "url",
                "reconnectInterval"
            }
        };

    private static readonly HashSet<string> SensitiveUriQueryProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "token",
        "access_token",
        "api_key",
        "apikey",
        "key",
        "secret",
        "signature",
        "usersig",
        "credential"
    };

    private readonly string _databasePath;
    private readonly ISecretStore _secretStore;
    private readonly DeviceSchemaMigrator _migrator;
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);
    private bool _migrationChecked;
    private bool _startupCleanupDrained;
    private bool _disposed;

    public SqliteDeviceInstanceRepository(string databasePath, ISecretStore secretStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(secretStore);
        _databasePath = Path.GetFullPath(databasePath);
        _secretStore = secretStore;
        _migrator = new DeviceSchemaMigrator(_databasePath, secretStore);
    }

    public async ValueTask<IReadOnlyList<PersistedDeviceInstance>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureMigratedAsync(cancellationToken);
        await EnsureStartupCleanupDrainedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rawInstances = await ReadInstancesAsync(connection, cancellationToken);
        var endpoints = await ReadEndpointsAsync(connection, cancellationToken);
        var result = rawInstances
            .Select(instance => new PersistedDeviceInstance(
                DeviceInstanceId.Parse(instance.InstanceId),
                new PluginId(instance.PluginId),
                new ModelId(instance.ModelId),
                new ProtocolProfileId(instance.ProfileId),
                instance.DisplayName,
                instance.AutoConnect,
                instance.ConfigSchemaVersion,
                endpoints.Where(endpoint => endpoint.InstanceId == instance.InstanceId)
                    .Select(endpoint => new PersistedDeviceEndpoint(
                        EndpointId.Parse(endpoint.EndpointId),
                        endpoint.TransportKind,
                        endpoint.PublicConfigJson,
                        endpoint.SecretReference,
                        endpoint.Priority,
                        endpoint.Enabled,
                        endpoint.CreatedAt,
                        endpoint.UpdatedAt)),
                instance.CreatedAt,
                instance.UpdatedAt))
            .ToArray();
        return new ReadOnlyCollection<PersistedDeviceInstance>(result);
    }

    public async ValueTask SaveAsync(
        PersistedDeviceInstance instance,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(instance);
        foreach (var endpoint in instance.Endpoints)
        {
            ValidatePublicConfiguration(endpoint.TransportKind, endpoint.PublicConfigJson);
        }

        await EnsureMigratedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var oldSecretReferences = await ReadSecretReferencesAsync(
                connection,
                transaction,
                instance.InstanceId,
                cancellationToken);
            await UpsertInstanceAsync(connection, transaction, instance, cancellationToken);
            await DeleteEndpointsAsync(connection, transaction, instance.InstanceId, cancellationToken);
            foreach (var endpoint in instance.Endpoints)
            {
                await InsertEndpointAsync(connection, transaction, instance.InstanceId, endpoint, cancellationToken);
            }

            foreach (var secretReference in oldSecretReferences.Except(
                         instance.Endpoints.Select(endpoint => endpoint.SecretReference).OfType<string>(),
                         StringComparer.Ordinal))
            {
                await EnqueueSecretCleanupIfUnreferencedAsync(
                    connection,
                    transaction,
                    secretReference,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        await RetryPendingSecretCleanupAsync(cancellationToken);
    }

    public async ValueTask DeleteAsync(
        DeviceInstanceId instanceId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _ = instanceId.Value;
        await EnsureMigratedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var oldSecretReferences = await ReadSecretReferencesAsync(
                connection,
                transaction,
                instanceId,
                cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM device_instances WHERE instance_id = @instanceId";
            command.Parameters.AddWithValue("@instanceId", instanceId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
            foreach (var secretReference in oldSecretReferences)
            {
                await EnqueueSecretCleanupIfUnreferencedAsync(
                    connection,
                    transaction,
                    secretReference,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        await RetryPendingSecretCleanupAsync(cancellationToken);
    }

    public async ValueTask RetryPendingSecretCleanupAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureMigratedAsync(cancellationToken);

        // 清理必须串行：并发排空会让同一 secret 被重复删除。
        await _cleanupLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var pendingReferences = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT secret_reference FROM secret_cleanup_outbox ORDER BY created_at, secret_reference";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    pendingReferences.Add(reader.GetString(0));
                }
            }

            foreach (var secretReference in pendingReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _secretStore.DeleteAsync(secretReference, cancellationToken);
                    await using var deleteCommand = connection.CreateCommand();
                    deleteCommand.CommandText = "DELETE FROM secret_cleanup_outbox WHERE secret_reference = @secretReference";
                    deleteCommand.Parameters.AddWithValue("@secretReference", secretReference);
                    await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    await using var failureCommand = connection.CreateCommand();
                    failureCommand.CommandText = "UPDATE secret_cleanup_outbox SET attempts = attempts + 1 WHERE secret_reference = @secretReference";
                    failureCommand.Parameters.AddWithValue("@secretReference", secretReference);
                    await failureCommand.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }
        }
        finally
        {
            _cleanupLock.Release();
        }
    }

    private static async Task<IReadOnlyList<string>> ReadSecretReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DeviceInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT DISTINCT secret_reference FROM device_endpoints WHERE instance_id = @instanceId AND secret_reference IS NOT NULL";
        command.Parameters.AddWithValue("@instanceId", instanceId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var references = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            references.Add(reader.GetString(0));
        }

        return references;
    }

    private static async Task EnqueueSecretCleanupIfUnreferencedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string secretReference,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO secret_cleanup_outbox(secret_reference, created_at, attempts)
            SELECT @secretReference, @createdAt, 0
            WHERE NOT EXISTS (
                SELECT 1 FROM device_endpoints WHERE secret_reference = @secretReference
            )
            ON CONFLICT(secret_reference) DO NOTHING
            """;
        command.Parameters.AddWithValue("@secretReference", secretReference);
        command.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureStartupCleanupDrainedAsync(CancellationToken cancellationToken)
    {
        // 上次运行若在删除密钥前崩溃，孤立引用只能在启动后的首次访问时回收。
        if (_startupCleanupDrained)
        {
            return;
        }

        // 只有排空真正完成后才置位，否则一次失败会让本实例余生都不再重试。
        await RetryPendingSecretCleanupAsync(cancellationToken);
        _startupCleanupDrained = true;
    }

    private async Task EnsureMigratedAsync(CancellationToken cancellationToken)
    {
        if (_migrationChecked)
        {
            return;
        }

        await _migrationLock.WaitAsync(cancellationToken);
        try
        {
            if (!_migrationChecked)
            {
                await _migrator.MigrateAsync(cancellationToken);
                _migrationChecked = true;
            }
        }
        finally
        {
            _migrationLock.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<IReadOnlyList<RawInstance>> ReadInstancesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT instance_id, plugin_id, model_id, profile_id, display_name, auto_connect,
                   config_schema_version, created_at, updated_at
            FROM device_instances
            ORDER BY created_at, instance_id
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var instances = new List<RawInstance>();
        while (await reader.ReadAsync(cancellationToken))
        {
            instances.Add(new RawInstance(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5) == 1,
                reader.GetInt32(6),
                ParseTimestamp(reader.GetString(7)),
                ParseTimestamp(reader.GetString(8))));
        }

        return instances;
    }

    private static async Task<IReadOnlyList<RawEndpoint>> ReadEndpointsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT endpoint_id, instance_id, transport_kind, public_config_json, secret_reference,
                   priority, enabled, created_at, updated_at
            FROM device_endpoints
            ORDER BY priority, endpoint_id
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var endpoints = new List<RawEndpoint>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!Enum.TryParse<PersistedTransportKind>(reader.GetString(2), out var transportKind) ||
                !Enum.IsDefined(transportKind))
            {
                throw new InvalidDataException("数据库包含未知的 Endpoint transport_kind。");
            }

            var publicConfigJson = reader.GetString(3);
            ValidatePublicConfiguration(transportKind, publicConfigJson);
            endpoints.Add(new RawEndpoint(
                reader.GetString(0),
                reader.GetString(1),
                transportKind,
                publicConfigJson,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6) == 1,
                ParseTimestamp(reader.GetString(7)),
                ParseTimestamp(reader.GetString(8))));
        }

        return endpoints;
    }

    private static async Task UpsertInstanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PersistedDeviceInstance instance,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO device_instances
                (instance_id, plugin_id, model_id, profile_id, display_name, auto_connect, config_schema_version, created_at, updated_at)
            VALUES
                (@instanceId, @pluginId, @modelId, @profileId, @displayName, @autoConnect, @configSchemaVersion, @createdAt, @updatedAt)
            ON CONFLICT(instance_id) DO UPDATE SET
                plugin_id = excluded.plugin_id,
                model_id = excluded.model_id,
                profile_id = excluded.profile_id,
                display_name = excluded.display_name,
                auto_connect = excluded.auto_connect,
                config_schema_version = excluded.config_schema_version,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("@instanceId", instance.InstanceId.ToString());
        command.Parameters.AddWithValue("@pluginId", instance.PluginId.Value);
        command.Parameters.AddWithValue("@modelId", instance.ModelId.Value);
        command.Parameters.AddWithValue("@profileId", instance.ProfileId.Value);
        command.Parameters.AddWithValue("@displayName", instance.DisplayName);
        command.Parameters.AddWithValue("@autoConnect", instance.AutoConnect ? 1 : 0);
        command.Parameters.AddWithValue("@configSchemaVersion", instance.ConfigSchemaVersion);
        command.Parameters.AddWithValue("@createdAt", instance.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@updatedAt", instance.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteEndpointsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DeviceInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM device_endpoints WHERE instance_id = @instanceId";
        command.Parameters.AddWithValue("@instanceId", instanceId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertEndpointAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DeviceInstanceId instanceId,
        PersistedDeviceEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO device_endpoints
                (endpoint_id, instance_id, transport_kind, public_config_json, secret_reference, priority, enabled, created_at, updated_at)
            VALUES
                (@endpointId, @instanceId, @transportKind, @publicConfigJson, @secretReference, @priority, @enabled, @createdAt, @updatedAt)
            """;
        command.Parameters.AddWithValue("@endpointId", endpoint.EndpointId.ToString());
        command.Parameters.AddWithValue("@instanceId", instanceId.ToString());
        command.Parameters.AddWithValue("@transportKind", endpoint.TransportKind.ToString());
        command.Parameters.AddWithValue("@publicConfigJson", endpoint.PublicConfigJson);
        command.Parameters.AddWithValue("@secretReference", (object?)endpoint.SecretReference ?? DBNull.Value);
        command.Parameters.AddWithValue("@priority", endpoint.Priority);
        command.Parameters.AddWithValue("@enabled", endpoint.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("@createdAt", endpoint.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@updatedAt", endpoint.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidatePublicConfiguration(
        PersistedTransportKind transportKind,
        string publicConfigJson)
    {
        using var document = JsonDocument.Parse(publicConfigJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Endpoint 公共配置必须是 JSON 对象。");
        }

        if (!AllowedPublicProperties.TryGetValue(transportKind, out var allowedProperties))
        {
            throw new InvalidDataException("Endpoint 使用未知的公共配置类型。");
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!allowedProperties.Contains(property.Name))
            {
                throw new InvalidDataException("Endpoint 公共配置包含未允许字段。");
            }

            if (property.Name == "reconnectInterval")
            {
                if (property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetInt32(out var interval) ||
                    interval < 0)
                {
                    throw new InvalidDataException("Endpoint 重连间隔格式无效。");
                }

                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                throw new InvalidDataException("Endpoint 公共配置字段格式无效。");
            }

            if (property.Name == "url")
            {
                ValidatePublicUri(property.Value.GetString()!);
            }
        }
    }

    private static void ValidatePublicUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("ws" or "wss" or "http" or "https"))
        {
            throw new InvalidDataException("Endpoint 公共 URL 格式无效。");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("Endpoint 公共 URL 不能包含用户凭据。");
        }

        var query = uri.Query.TrimStart('?');
        if (query.Length == 0)
        {
            return;
        }

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var name = Uri.UnescapeDataString(separator < 0 ? part : part[..separator]);
            if (SensitiveUriQueryProperties.Contains(name))
            {
                throw new InvalidDataException("Endpoint 公共 URL 不能包含敏感查询参数。");
            }
        }
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result))
        {
            throw new InvalidDataException("数据库包含无效 UTC 时间戳。");
        }

        return result;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _migrationLock.Dispose();
        _cleanupLock.Dispose();
    }

    private sealed record RawInstance(
        string InstanceId,
        string PluginId,
        string ModelId,
        string ProfileId,
        string DisplayName,
        bool AutoConnect,
        int ConfigSchemaVersion,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record RawEndpoint(
        string EndpointId,
        string InstanceId,
        PersistedTransportKind TransportKind,
        string PublicConfigJson,
        string? SecretReference,
        int Priority,
        bool Enabled,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
