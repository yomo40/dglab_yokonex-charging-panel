using System.Text.Json;
using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Telemetry;
using ChargingPanel.Contracts.Transport;

namespace ChargingPanel.Contracts.Plugins;

public static class HostApi
{
    public static Version CurrentVersion { get; } = new(1, 0);
}

public sealed record HostApiVersionRange
{
    public HostApiVersionRange(Version minimumInclusive, Version maximumInclusive)
    {
        ArgumentNullException.ThrowIfNull(minimumInclusive);
        ArgumentNullException.ThrowIfNull(maximumInclusive);
        if (minimumInclusive.CompareTo(maximumInclusive) > 0)
        {
            throw new ArgumentException("Host API 最低版本不能高于最高版本。");
        }

        MinimumInclusive = minimumInclusive;
        MaximumInclusive = maximumInclusive;
    }

    public Version MinimumInclusive { get; }

    public Version MaximumInclusive { get; }

    public bool Supports(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return MinimumInclusive.CompareTo(version) <= 0 && MaximumInclusive.CompareTo(version) >= 0;
    }
}

public sealed record PluginManifest
{
    public PluginManifest(
        PluginId pluginId,
        string displayName,
        Version pluginVersion,
        HostApiVersionRange hostApiRange)
    {
        pluginId.ThrowIfInvalid(nameof(pluginId));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(pluginVersion);
        ArgumentNullException.ThrowIfNull(hostApiRange);

        PluginId = pluginId;
        DisplayName = displayName.Trim();
        PluginVersion = pluginVersion;
        HostApiRange = hostApiRange;
    }

    public PluginId PluginId { get; }

    public string DisplayName { get; }

    public Version PluginVersion { get; }

    public HostApiVersionRange HostApiRange { get; }
}

public sealed record DevicePluginContribution
{
    public DevicePluginContribution(
        IEnumerable<VendorDescriptor> vendors,
        IEnumerable<DeviceModelDescriptor> deviceModels,
        IEnumerable<ProtocolProfileDescriptor> protocolProfiles,
        IEnumerable<DiscoveryRuleDescriptor> discoveryRules,
        IEnumerable<IProtocolDriverFactory> driverFactories)
    {
        Vendors = CopyUnique(vendors, vendor => vendor.Id.Value, nameof(vendors));
        DeviceModels = CopyUnique(deviceModels, model => model.Id.Value, nameof(deviceModels));
        ProtocolProfiles = CopyUnique(protocolProfiles, profile => profile.Id.Value, nameof(protocolProfiles));
        DiscoveryRules = CopyUnique(discoveryRules, rule => rule.RuleId, nameof(discoveryRules));
        ArgumentNullException.ThrowIfNull(driverFactories);
        var copiedDriverFactories = driverFactories.ToArray();
        foreach (var driverFactory in copiedDriverFactories)
        {
            ArgumentNullException.ThrowIfNull(driverFactory, nameof(driverFactories));
            driverFactory.ProfileId.ThrowIfInvalid(nameof(driverFactories));
        }

        DriverFactories = CopyUnique(
            copiedDriverFactories,
            factory => factory.ProfileId.Value,
            nameof(driverFactories));
    }

    public IReadOnlyList<VendorDescriptor> Vendors { get; }

    public IReadOnlyList<DeviceModelDescriptor> DeviceModels { get; }

    public IReadOnlyList<ProtocolProfileDescriptor> ProtocolProfiles { get; }

    public IReadOnlyList<DiscoveryRuleDescriptor> DiscoveryRules { get; }

    public IReadOnlyList<IProtocolDriverFactory> DriverFactories { get; }

    private static IReadOnlyList<T> CopyUnique<T>(
        IEnumerable<T> source,
        Func<T, string> keySelector,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        var items = source.ToArray();
        if (items.Select(keySelector).Distinct(StringComparer.Ordinal).Count() != items.Length)
        {
            throw new ArgumentException("插件贡献中存在重复 ID。", parameterName);
        }

        return Array.AsReadOnly(items);
    }
}

public interface IDevicePlugin
{
    PluginManifest Manifest { get; }

    DevicePluginContribution Describe();
}

public interface IProtocolDriverFactory
{
    ProtocolProfileId ProfileId { get; }

    IProtocolDriver CreateDriver(IPluginContext context);
}

public enum PluginLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

public interface IPluginLogger
{
    void Log(
        PluginLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, JsonElement>? properties = null);
}

public interface IPluginStorage
{
    ValueTask<JsonElement?> ReadAsync(string key, CancellationToken cancellationToken);

    ValueTask WriteAsync(string key, JsonElement value, CancellationToken cancellationToken);

    ValueTask DeleteAsync(string key, CancellationToken cancellationToken);
}

public interface IPluginContext
{
    IPluginLogger Logger { get; }

    TimeProvider TimeProvider { get; }

    IPluginStorage Storage { get; }

    IEndpointLeaseProvider Endpoints { get; }
}

public interface IProtocolDriver : IAsyncDisposable
{
    ProtocolProfileId ProfileId { get; }

    ValueTask OpenAsync(IEndpointLease lease, CancellationToken cancellationToken);

    /// <summary>
    /// 执行一条命令。宿主保证同一实例的 <see cref="ExecuteAsync"/> 串行调用。
    /// </summary>
    ValueTask<DeviceCommandResult> ExecuteAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken);

    /// <summary>
    /// 执行设备专属安全停止。
    /// 线程安全契约：急停刻意绕过命令队列，以免挂起的队列阻塞停止，
    /// 因此本方法可能与 <see cref="ExecuteAsync"/> 并发调用。
    /// 实现必须对这两个调用之间的共享状态自行加锁。
    /// </summary>
    ValueTask<SafeStopResult> SafeStopAsync(
        SafeStopContext context,
        CancellationToken cancellationToken);

    IAsyncEnumerable<DeviceTelemetryEnvelope> ReadTelemetryAsync(CancellationToken cancellationToken);
}
