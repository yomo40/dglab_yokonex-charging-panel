using System.Text.Json;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;

namespace ChargingPanel.Infrastructure.Windows.Plugins;

/// <summary>
/// 第三方插件的窄代理上下文。只提供结构化日志、<see cref="TimeProvider"/>、
/// 命名空间存储和 Endpoint Lease 请求；绝不暴露根 DI、SQLite、
/// DPAPI 明文或任意 UI 注入。
/// </summary>
public sealed class PluginContext : IPluginContext
{
    public PluginContext(
        PluginId pluginId,
        string dataDirectory,
        IPluginLogger logger,
        TimeProvider timeProvider)
    {
        _ = pluginId.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        Logger = logger;
        TimeProvider = timeProvider;
        Storage = new PluginNamespacedStorage(dataDirectory);
    }

    public IPluginLogger Logger { get; }

    public TimeProvider TimeProvider { get; }

    public IPluginStorage Storage { get; }

    public IEndpointLeaseProvider Endpoints { get; } = new UnavailableEndpointLeaseProvider();
}

/// <summary>
/// 插件命名空间存储。每个插件的数据写入独立目录
/// （<c>&lt;root&gt;/.plugin-data/&lt;pluginId&gt;/&lt;key&gt;.json</c>），
/// 键必须是不含路径分隔符的简单键，杜绝目录穿越。
/// </summary>
internal sealed class PluginNamespacedStorage : IPluginStorage
{
    private readonly string _dataDirectory;

    public PluginNamespacedStorage(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = dataDirectory;
    }

    public async ValueTask<JsonElement?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = ResolvePath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    public async ValueTask WriteAsync(string key, JsonElement value, CancellationToken cancellationToken)
    {
        var path = ResolvePath(key);
        Directory.CreateDirectory(_dataDirectory);
        await File.WriteAllTextAsync(path, value.GetRawText(), cancellationToken);
    }

    public ValueTask DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var path = ResolvePath(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return ValueTask.CompletedTask;
    }

    private string ResolvePath(string key)
    {
        PluginStorageKey.Validate(key);
        return Path.Combine(_dataDirectory, key + ".json");
    }
}

/// <summary>
/// v1 宿主不提供第三方插件端点租约。获取 Endpoint 是显式失败而非伪造成功，
/// 未来由宿主传输层（SessionCoordinator）按需分配。
/// </summary>
internal sealed class UnavailableEndpointLeaseProvider : IEndpointLeaseProvider
{
    public ValueTask<IEndpointLease> AcquireAsync(
        EndpointLeaseRequest request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("v1 宿主不提供第三方插件端点租约；Endpoint 由宿主传输层分配。");
}

/// <summary>默认插件日志器：输出到调试器，不落普通业务日志。</summary>
internal sealed class DebugPluginLogger : IPluginLogger
{
    public void Log(
        PluginLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, JsonElement>? properties = null)
    {
        var propertyText = properties is null
            ? string.Empty
            : string.Join(", ", properties.Select(pair => $"{pair.Key}={pair.Value.GetRawText()}"));
        System.Diagnostics.Debug.WriteLine($"[plugin:{level}:{eventName}] {message} {propertyText}".TrimEnd());
    }
}

internal static class PluginStorageKey
{
    public static void Validate(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 128)
        {
            throw new ArgumentException("存储键长度不能超过 128。", nameof(key));
        }

        if (key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            key.Contains('/') ||
            key.Contains('\\'))
        {
            throw new ArgumentException("存储键必须是简单键，不能包含路径分隔符。", nameof(key));
        }
    }
}
