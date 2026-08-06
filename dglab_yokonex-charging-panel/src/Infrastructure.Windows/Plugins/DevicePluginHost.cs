using System.Reflection;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Core.Plugins;

namespace ChargingPanel.Infrastructure.Windows.Plugins;

/// <summary>插件宿主选项。</summary>
public sealed record PluginHostOptions
{
    /// <summary>第三方插件根目录；默认 <c>%LocalAppData%/ChargingPanel/plugins</c>。</summary>
    public string RootPath { get; init; } = DefaultRootPath();

    public bool SafeMode { get; init; }

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public static string DefaultRootPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChargingPanel",
            "plugins");
}

/// <summary>加载结果的诊断分类。信任模型 C 不宣称 ALC 能隔离恶意 DLL。</summary>
public enum PluginLoadCode
{
    Loaded,
    Untrusted,
    RequiresConfirmation,
    Disabled,
    SafeModeSkipped,
    ManifestInvalid,
    HostApiIncompatible,
    DuplicatePluginId,
    MissingDependency,
    LoadFailed,
    DescribeFailed
}

/// <summary>
/// 第三方设备插件宿主（信任模型 C）。
///
/// 只扫描 <c>%LocalAppData%/ChargingPanel/plugins/&lt;plugin-id&gt;/plugin.json</c>；
/// 内置插件由组合根静态注册，不经过本宿主。第三方插件每个使用独立的
/// <see cref="DevicePluginLoadContext"/>，Contracts 强制解析到宿主唯一程序集。
///
/// 安全边界：
/// 1) 首次启用与哈希变化必须经 <see cref="ConfirmTrust"/> 用户确认；
/// 2) 安全模式在读取清单之前就跳过第三方插件；
/// 3) 单个插件失败被隔离为 <see cref="PluginState"/>，不会阻止宿主启动；
/// 4) 插件只能拿到 <see cref="IPluginContext"/> 窄代理，拿不到根容器、SQLite、
///    DPAPI 明文或任意 UI 注入；
/// 5) ALC 不是沙箱：插件与本程序拥有相同权限，文档与代码均明确此点。
/// </summary>
public sealed class DevicePluginHost
{
    private static readonly string BundledContractsAssemblyName = typeof(IPluginContext).Assembly.GetName().Name!;

    private readonly PluginHostOptions _options;
    private readonly PluginManifestReader _reader;
    private readonly PluginTrustStore _trustStore;
    private readonly HashSet<string> _builtInPluginIds;
    private readonly object _gate = new();
    private readonly List<PluginState> _states = [];
    private readonly List<IDevicePlugin> _loadedPlugins = [];
    private readonly List<DevicePluginLoadContext> _loadContexts = [];

    public DevicePluginHost(
        PluginHostOptions options,
        IEnumerable<IDevicePlugin>? builtInPlugins = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _reader = new PluginManifestReader();
        _trustStore = new PluginTrustStore(options.RootPath);
        _builtInPluginIds = new HashSet<string>(
            (builtInPlugins ?? []).Select(plugin => plugin.Manifest.PluginId.Value),
            StringComparer.Ordinal);
        SafeMode = options.SafeMode;
    }

    /// <summary>安全模式：跳过第三方插件发现（在读取任何清单之前决定）。</summary>
    public bool SafeMode { get; set; }

    /// <summary>所有已发现插件的状态快照（顺序与发现顺序一致）。</summary>
    public IReadOnlyList<PluginState> States
    {
        get
        {
            lock (_gate)
            {
                return _states.ToArray();
            }
        }
    }

    /// <summary>成功加载且已信任、已启用的插件实例。仅供组合根合并进目录。</summary>
    public IReadOnlyList<IDevicePlugin> LoadedPlugins
    {
        get
        {
            lock (_gate)
            {
                return _loadedPlugins.ToArray();
            }
        }
    }

    public PluginState? GetState(PluginId pluginId)
    {
        _ = pluginId.Value;
        return States.FirstOrDefault(state => state.PluginId == pluginId);
    }

    /// <summary>
    /// 确定性发现与加载。可重复调用；每次重新扫描并重建状态快照。
    /// 已加载的 ALC 因 v1 无热卸载而保持存活。
    /// </summary>
    public void Refresh()
    {
        lock (_gate)
        {
            RefreshCore();
        }
    }

    /// <summary>用户确认信任并启用插件（记录当前入口程序集 SHA-256）。</summary>
    public void ConfirmTrust(PluginId pluginId)
    {
        _ = pluginId.Value;
        lock (_gate)
        {
            var directory = Path.Combine(_options.RootPath, pluginId.Value);
            var manifest = _reader.Read(directory);
            _trustStore.Save(
                pluginId,
                new PluginTrustRecord
                {
                    Enabled = true,
                    TrustedSha256 = manifest.Sha256,
                    ConfirmedAt = _options.TimeProvider.GetUtcNow()
                });
            RefreshCore();
        }
    }

    /// <summary>启用或禁用已信任插件。未信任插件不能直接启用。</summary>
    public void SetEnabled(PluginId pluginId, bool enabled)
    {
        _ = pluginId.Value;
        lock (_gate)
        {
            var trust = _trustStore.Load(pluginId);
            if (enabled && trust.TrustedSha256 is null)
            {
                throw new InvalidOperationException("插件尚未信任，请先确认信任。");
            }

            _trustStore.Save(pluginId, trust with { Enabled = enabled });
            RefreshCore();
        }
    }

    /// <summary>为已加载插件创建窄代理上下文。v1 不创建驱动实例，仅提供可用代理。</summary>
    public IPluginContext CreatePluginContext(PluginId pluginId)
    {
        _ = pluginId.Value;
        var dataDirectory = Path.Combine(_options.RootPath, ".plugin-data", pluginId.Value);
        return new PluginContext(pluginId, dataDirectory, new DebugPluginLogger(), _options.TimeProvider);
    }

    private void RefreshCore()
    {
        _states.Clear();
        _loadedPlugins.Clear();

        if (!Directory.Exists(_options.RootPath))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(_options.RootPath))
        {
            RefreshPlugin(directory);
        }
    }

    private void RefreshPlugin(string directory)
    {
        var directoryName = Path.GetFileName(directory);
        if (string.IsNullOrWhiteSpace(directoryName))
        {
            return;
        }

        PluginId pluginId;
        try
        {
            pluginId = new PluginId(directoryName);
        }
        catch (ArgumentException)
        {
            // 目录名不是合法稳定 ID，不是第三方插件目录。
            return;
        }

        if (SafeMode)
        {
            AddState(new PluginState(
                pluginId,
                displayName: pluginId.Value,
                pluginVersion: null,
                publisher: string.Empty,
                entryAssemblyPath: string.Empty,
                sha256: string.Empty,
                PluginTrustState.SafeModeSkipped,
                "安全模式已启用，跳过第三方插件发现。"));
            return;
        }

        PluginManifestInfo manifest;
        try
        {
            manifest = _reader.Read(directory);
        }
        catch (PluginMissingDependencyException ex)
        {
            AddState(FailedState(pluginId, PluginTrustState.Failed, ex.Message));
            return;
        }
        catch (PluginManifestException ex)
        {
            AddState(FailedState(pluginId, PluginTrustState.Failed, ex.Message));
            return;
        }

        if (_builtInPluginIds.Contains(manifest.PluginId.Value))
        {
            AddState(FailedState(
                manifest.PluginId,
                PluginTrustState.Failed,
                "插件稳定 ID 与内置插件冲突，禁止加载。"));
            return;
        }

        if (!manifest.HostApiRange.Supports(HostApi.CurrentVersion))
        {
            AddState(FailedState(
                manifest.PluginId,
                PluginTrustState.Failed,
                $"插件不支持当前宿主 API 版本 {HostApi.CurrentVersion}。"));
            return;
        }

        var trust = _trustStore.Load(manifest.PluginId);
        if (!trust.Enabled)
        {
            AddState(new PluginState(
                manifest.PluginId,
                manifest.DisplayName,
                manifest.PluginVersion,
                manifest.Publisher,
                manifest.EntryAssemblyPath,
                manifest.Sha256,
                trust.TrustedSha256 is null ? PluginTrustState.Untrusted : PluginTrustState.Disabled,
                trust.TrustedSha256 is null ? "尚未确认信任，首次启用需要用户确认。" : "插件已被禁用。"));
            return;
        }

        if (trust.TrustedSha256 is null)
        {
            AddState(new PluginState(
                manifest.PluginId,
                manifest.DisplayName,
                manifest.PluginVersion,
                manifest.Publisher,
                manifest.EntryAssemblyPath,
                manifest.Sha256,
                PluginTrustState.Untrusted,
                "尚未确认信任，首次启用需要用户确认。"));
            return;
        }

        if (!string.Equals(trust.TrustedSha256, manifest.Sha256, StringComparison.Ordinal))
        {
            AddState(new PluginState(
                manifest.PluginId,
                manifest.DisplayName,
                manifest.PluginVersion,
                manifest.Publisher,
                manifest.EntryAssemblyPath,
                manifest.Sha256,
                PluginTrustState.RequiresConfirmation,
                "插件入口程序集哈希已变化，需要重新确认信任。"));
            return;
        }

        LoadTrustedPlugin(manifest);
    }

    private void LoadTrustedPlugin(PluginManifestInfo manifest)
    {
        Assembly? assembly;
        Type pluginType;
        try
        {
            var pluginDirectory = Path.GetDirectoryName(manifest.EntryAssemblyPath)
                ?? throw new PluginLoadException("无法解析插件目录。");
            var loadContext = new DevicePluginLoadContext(pluginDirectory);
            assembly = loadContext.LoadFromAssemblyPath(manifest.EntryAssemblyPath);
            if (assembly is null)
            {
                AddState(FailedState(manifest.PluginId, PluginTrustState.Failed, "入口程序集加载失败。"));
                return;
            }

            pluginType = assembly.GetTypes()
                .FirstOrDefault(type =>
                    !type.IsAbstract && type.IsClass && typeof(IDevicePlugin).IsAssignableFrom(type))
                ?? throw new PluginLoadException("入口程序集未找到 IDevicePlugin 实现。");
            _loadContexts.Add(loadContext);
        }
        catch (Exception ex) when (ex is PluginLoadException or FileNotFoundException or BadImageFormatException or ReflectionTypeLoadException or IOException)
        {
            AddState(FailedState(manifest.PluginId, PluginTrustState.Failed, $"加载失败: {ex.Message}"));
            return;
        }

        try
        {
            var instance = (IDevicePlugin)Activator.CreateInstance(pluginType)!;
            var instanceManifest = instance.Manifest;
            _ = instance.Describe();

            if (!string.Equals(instanceManifest.PluginId.Value, manifest.PluginId.Value, StringComparison.Ordinal))
            {
                AddState(FailedState(
                    manifest.PluginId,
                    PluginTrustState.Failed,
                    "程序集清单与 plugin.json 的插件 ID 不一致。"));
                return;
            }

            _loadedPlugins.Add(instance);
            var message = manifest.BundlesContracts
                ? $"检测到插件目录包含宿主 {BundledContractsAssemblyName} 程序集，已强制解析到宿主版本。"
                : null;
            AddState(new PluginState(
                manifest.PluginId,
                manifest.DisplayName,
                manifest.PluginVersion,
                manifest.Publisher,
                manifest.EntryAssemblyPath,
                manifest.Sha256,
                PluginTrustState.Loaded,
                message));
        }
        catch (Exception ex)
        {
            AddState(FailedState(
                manifest.PluginId,
                PluginTrustState.Failed,
                $"插件构造或描述失败: {ex.Message}"));
        }
    }

    private static PluginState FailedState(PluginId pluginId, PluginTrustState trustState, string message) =>
        new(pluginId, displayName: pluginId.Value, pluginVersion: null, publisher: string.Empty,
            entryAssemblyPath: string.Empty, sha256: string.Empty, trustState, message);

    private void AddState(PluginState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _states.Add(state);
    }

    private sealed class PluginLoadException : Exception
    {
        public PluginLoadException(string message) : base(message)
        {
        }
    }
}
