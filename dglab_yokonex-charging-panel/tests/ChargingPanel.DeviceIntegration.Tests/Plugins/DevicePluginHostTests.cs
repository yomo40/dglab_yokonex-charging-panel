using System.Text.Json;
using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Core.Devices.Catalog;
using ChargingPanel.Core.Plugins;
using ChargingPanel.Infrastructure.Windows.Plugins;
using ChargingPanel.Vendor.DGLab;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Plugins;

/// <summary>
/// 第三方设备插件宿主测试（信任模型 C）。
/// 夹具以编译后 DLL 路径加载（不直接引用夹具类型），宿主扫描临时插件根目录。
/// </summary>
public sealed class DevicePluginHostTests : IDisposable
{
    private readonly string _root;

    public DevicePluginHostTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cp-plugin-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // 已加载的 ALC 在本进程内保持存活（v1 无热卸载），会锁定夹具 DLL，
        // 因此删除临时目录可能失败；测试结束后由操作系统清理即可。
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    // ---------- 兼容加载 ----------

    [Fact]
    public void CompatiblePlugin_WhenTrustedAndEnabled_LoadsAndContributesToCatalog()
    {
        WritePlugin(
            Compatible.PluginId,
            BuildManifestJson(Compatible.PluginId, "兼容测试插件", "CompatibleDevicePlugin.dll"),
            "CompatibleDevicePlugin.dll");
        var host = CreateHost();

        // 未信任：首次启用前不能加载。
        host.Refresh();
        var untrusted = Assert.Single(host.States);
        Assert.Equal(PluginTrustState.Untrusted, untrusted.TrustState);
        Assert.Empty(host.LoadedPlugins);

        // 用户确认信任后加载。
        host.ConfirmTrust(Compatible.Id);

        var loaded = Assert.Single(host.States);
        Assert.Equal(PluginTrustState.Loaded, loaded.TrustState);
        var plugin = Assert.Single(host.LoadedPlugins);
        Assert.IsAssignableFrom<IDevicePlugin>(plugin);
        Assert.Equal(Compatible.PluginId, plugin.Manifest.PluginId.Value);

        // 贡献可合并进目录。
        var catalog = new DeviceCatalog(host.LoadedPlugins);
        Assert.Contains(catalog.GetModels(), model => model.Id.Value == Compatible.ModelId);
    }

    // ---------- Host API 不兼容 ----------

    [Fact]
    public void IncompatiblePlugin_WhenHostApiRangeMismatch_ReportsFailureWithoutLoading()
    {
        WritePlugin(
            Incompatible.PluginId,
            BuildManifestJson(
                Incompatible.PluginId,
                "Host API 不兼容插件",
                "IncompatibleDevicePlugin.dll",
                minApi: "2.0",
                maxApi: "3.0"),
            "IncompatibleDevicePlugin.dll");
        var host = CreateHost();

        host.ConfirmTrust(Incompatible.Id);

        var state = Assert.Single(host.States);
        Assert.Equal(PluginTrustState.Failed, state.TrustState);
        Assert.Contains("宿主 API", state.Message, StringComparison.Ordinal);
        Assert.Empty(host.LoadedPlugins);
    }

    // ---------- 重复 Model ID ----------

    [Fact]
    public void DuplicateModelPlugin_WhenMergedIntoCatalog_ReportsDuplicateModelId()
    {
        WritePlugin(
            DuplicateModel.PluginId,
            BuildManifestJson(DuplicateModel.PluginId, "重复模型 ID 插件", "DuplicateModelDevicePlugin.dll"),
            "DuplicateModelDevicePlugin.dll");
        var host = CreateHost();
        host.ConfirmTrust(DuplicateModel.Id);

        var loaded = Assert.Single(host.LoadedPlugins);
        var catalog = new DeviceCatalog(new IDevicePlugin[] { new DGLabPlugin(), loaded });

        var diagnostic = Assert.Single(catalog.GetDiagnostics());
        Assert.Equal(CatalogDiagnosticCode.DuplicateModelId, diagnostic.Code);
        Assert.Equal(DuplicateModel.ModelId, diagnostic.ConflictingStableId);
    }

    // ---------- 携带第二份 Contracts ----------

    [Fact]
    public void BundledContractsPlugin_WhenSecondContractsCopyPresent_LoadsWithHostContractsIdentity()
    {
        WritePlugin(
            BundledContracts.PluginId,
            BuildManifestJson(BundledContracts.PluginId, "携带第二份 Contracts 插件", "BundledContractsDevicePlugin.dll"),
            "BundledContractsDevicePlugin.dll",
            "ChargingPanel.Contracts.dll");
        var host = CreateHost();
        host.ConfirmTrust(BundledContracts.Id);

        var state = Assert.Single(host.States);
        Assert.Equal(PluginTrustState.Loaded, state.TrustState);
        Assert.Contains("Contracts", state.Message, StringComparison.Ordinal);

        // 强转宿主 IDevicePlugin 成功即证明类型身份与宿主统一（未使用打包副本）。
        var plugin = Assert.Single(host.LoadedPlugins);
        Assert.IsAssignableFrom<IDevicePlugin>(plugin);
        Assert.Equal(BundledContracts.PluginId, plugin.Manifest.PluginId.Value);
    }

    // ---------- 依赖缺失 ----------

    [Fact]
    public void Plugin_WhenDeclaredDependencyMissing_ReportsFailure()
    {
        WritePlugin(
            Compatible.PluginId,
            BuildManifestJson(
                Compatible.PluginId,
                "兼容测试插件",
                "CompatibleDevicePlugin.dll",
                dependencies: ["MissingSupport.dll"]),
            "CompatibleDevicePlugin.dll");
        var host = CreateHost();

        host.Refresh();

        var state = Assert.Single(host.States);
        Assert.Equal(PluginTrustState.Failed, state.TrustState);
        Assert.Contains("依赖", state.Message, StringComparison.Ordinal);
        Assert.Empty(host.LoadedPlugins);
    }

    // ---------- 构造/描述抛错且不阻塞宿主 ----------

    [Fact]
    public void ThrowingPlugin_WhenConstructorThrows_IsolatedAndDoesNotBlockOtherPlugins()
    {
        WritePlugin(
            Throwing.PluginId,
            BuildManifestJson(Throwing.PluginId, "构造抛错插件", "ThrowingDevicePlugin.dll"),
            "ThrowingDevicePlugin.dll");
        WritePlugin(
            Compatible.PluginId,
            BuildManifestJson(Compatible.PluginId, "兼容测试插件", "CompatibleDevicePlugin.dll"),
            "CompatibleDevicePlugin.dll");
        var host = CreateHost();

        host.ConfirmTrust(Throwing.Id);
        host.ConfirmTrust(Compatible.Id);

        var throwing = host.GetState(Throwing.Id);
        Assert.NotNull(throwing);
        Assert.Equal(PluginTrustState.Failed, throwing!.TrustState);
        Assert.Contains("构造或描述失败", throwing.Message, StringComparison.Ordinal);

        var compatible = host.GetState(Compatible.Id);
        Assert.NotNull(compatible);
        Assert.Equal(PluginTrustState.Loaded, compatible!.TrustState);

        var loaded = Assert.Single(host.LoadedPlugins);
        Assert.Equal(Compatible.PluginId, loaded.Manifest.PluginId.Value);
    }

    // ---------- 哈希变化 ----------

    [Fact]
    public void TrustedPlugin_WhenHashChanges_RequiresReconfirmationAndReloadsAfterConfirm()
    {
        WritePlugin(
            Compatible.PluginId,
            BuildManifestJson(Compatible.PluginId, "兼容测试插件", "CompatibleDevicePlugin.dll"),
            "CompatibleDevicePlugin.dll");

        // 模拟插件升级：信任记录中的哈希与当前文件哈希不一致。
        // （直接改信任记录可避免 ALC 锁定 DLL 导致的写文件失败。）
        new PluginTrustStore(_root).Save(
            Compatible.Id,
            new PluginTrustRecord { Enabled = true, TrustedSha256 = new string('0', 64) });
        var host = CreateHost();

        host.Refresh();
        var changed = Assert.Single(host.States);
        Assert.Equal(PluginTrustState.RequiresConfirmation, changed.TrustState);
        Assert.Contains("哈希已变化", changed.Message, StringComparison.Ordinal);
        Assert.Empty(host.LoadedPlugins);

        // 重新确认后再次加载。
        host.ConfirmTrust(Compatible.Id);
        Assert.Equal(PluginTrustState.Loaded, Assert.Single(host.States).TrustState);
        Assert.Single(host.LoadedPlugins);
    }

    // ---------- 禁用 ----------

    [Fact]
    public void DisabledPlugin_WhenDisabled_IsNotLoadedAndCanBeReEnabled()
    {
        WritePlugin(
            Compatible.PluginId,
            BuildManifestJson(Compatible.PluginId, "兼容测试插件", "CompatibleDevicePlugin.dll"),
            "CompatibleDevicePlugin.dll");
        var host = CreateHost();
        host.ConfirmTrust(Compatible.Id);
        Assert.Single(host.LoadedPlugins);

        host.SetEnabled(Compatible.Id, enabled: false);
        var disabled = Assert.Single(host.States);
        Assert.Equal(PluginTrustState.Disabled, disabled.TrustState);
        Assert.Empty(host.LoadedPlugins);

        host.SetEnabled(Compatible.Id, enabled: true);
        Assert.Equal(PluginTrustState.Loaded, Assert.Single(host.States).TrustState);
        Assert.Single(host.LoadedPlugins);
    }

    // ---------- 安全模式 ----------

    [Fact]
    public void SafeMode_WhenEnabled_SkipsThirdPartyDiscoveryBeforeAssemblyLoading()
    {
        WritePlugin(
            Compatible.PluginId,
            BuildManifestJson(Compatible.PluginId, "兼容测试插件", "CompatibleDevicePlugin.dll"),
            "CompatibleDevicePlugin.dll");
        var host = CreateHost(safeMode: true);

        host.Refresh();

        var state = Assert.Single(host.States);
        Assert.Equal(PluginTrustState.SafeModeSkipped, state.TrustState);
        Assert.Empty(host.LoadedPlugins);
    }

    // ---------- 重复 Plugin ID（与内置冲突） ----------

    [Fact]
    public void Plugin_WhenPluginIdMatchesBuiltIn_IsRejected()
    {
        WritePlugin(
            Compatible.PluginId,
            BuildManifestJson(Compatible.PluginId, "兼容测试插件", "CompatibleDevicePlugin.dll"),
            "CompatibleDevicePlugin.dll");
        var host = CreateHost(builtIns: [new BuiltInPluginStub(Compatible.PluginId)]);

        host.ConfirmTrust(Compatible.Id);

        var state = Assert.Single(host.States);
        Assert.Equal(PluginTrustState.Failed, state.TrustState);
        Assert.Contains("与内置插件冲突", state.Message, StringComparison.Ordinal);
        Assert.Empty(host.LoadedPlugins);
    }

    // ---------- 工具 ----------

    private DevicePluginHost CreateHost(
        bool safeMode = false,
        IEnumerable<IDevicePlugin>? builtIns = null) =>
        new(
            new PluginHostOptions
            {
                RootPath = _root,
                SafeMode = safeMode,
                TimeProvider = TimeProvider.System
            },
            builtIns);

    private string WritePlugin(string pluginId, string json, params string[] dllNames)
    {
        var directory = Path.Combine(_root, pluginId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), json);
        foreach (var dll in dllNames)
        {
            var source = Path.Combine(AppContext.BaseDirectory, dll);
            Assert.True(File.Exists(source), $"夹具 DLL 缺失: {source}");
            File.Copy(source, Path.Combine(directory, dll), overwrite: true);
        }

        return directory;
    }

    private static string BuildManifestJson(
        string pluginId,
        string displayName,
        string entryAssembly,
        string publisher = "测试发布者",
        string version = "1.0.0",
        string minApi = "1.0",
        string maxApi = "1.0",
        IReadOnlyList<string>? dependencies = null)
    {
        return JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["pluginId"] = pluginId,
                ["displayName"] = displayName,
                ["publisher"] = publisher,
                ["pluginVersion"] = version,
                ["hostApiRange"] = new Dictionary<string, string>
                {
                    ["minimumInclusive"] = minApi,
                    ["maximumInclusive"] = maxApi
                },
                ["entryAssembly"] = entryAssembly,
                ["dependencies"] = dependencies ?? Array.Empty<string>()
            });
    }

    private static class Compatible
    {
        public const string PluginId = "fixtures.compatible";
        public const string ModelId = "fixtures.compatible.device";
        public static ChargingPanel.Contracts.Identifiers.PluginId Id => ChargingPanel.Contracts.Identifiers.PluginId.Parse(PluginId);
    }

    private static class Incompatible
    {
        public const string PluginId = "fixtures.incompatible";
        public static ChargingPanel.Contracts.Identifiers.PluginId Id => ChargingPanel.Contracts.Identifiers.PluginId.Parse(PluginId);
    }

    private static class DuplicateModel
    {
        public const string PluginId = "fixtures.duplicatemodel";
        public const string ModelId = "dglab.coyote.v3";
        public static ChargingPanel.Contracts.Identifiers.PluginId Id => ChargingPanel.Contracts.Identifiers.PluginId.Parse(PluginId);
    }

    private static class BundledContracts
    {
        public const string PluginId = "fixtures.bundledcontracts";
        public static ChargingPanel.Contracts.Identifiers.PluginId Id => ChargingPanel.Contracts.Identifiers.PluginId.Parse(PluginId);
    }

    private static class Throwing
    {
        public const string PluginId = "fixtures.throwing";
        public static ChargingPanel.Contracts.Identifiers.PluginId Id => ChargingPanel.Contracts.Identifiers.PluginId.Parse(PluginId);
    }

    private sealed class BuiltInPluginStub : IDevicePlugin
    {
        private readonly string _pluginId;

        public BuiltInPluginStub(string pluginId)
        {
            _pluginId = pluginId;
            Manifest = new PluginManifest(
                new PluginId(pluginId),
                "内置插件桩",
                new Version(1, 0),
                new HostApiVersionRange(new Version(1, 0), new Version(1, 0)));
        }

        public PluginManifest Manifest { get; }

        public DevicePluginContribution Describe() => new([], [], [], [], []);
    }
}
