using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.Core.Plugins;

/// <summary>
/// 第三方插件的信任状态。信任模型 C：插件与本程序拥有相同权限，
/// 宿主不提供沙箱，只负责在用户确认信任后才加载。
/// </summary>
public enum PluginTrustState
{
    /// <summary>已信任且已成功加载。</summary>
    Loaded,

    /// <summary>尚未确认信任，首次启用需要用户确认。</summary>
    Untrusted,

    /// <summary>插件文件哈希已变化，需要用户重新确认信任。</summary>
    RequiresConfirmation,

    /// <summary>用户已禁用。</summary>
    Disabled,

    /// <summary>清单校验、依赖解析、加载或构造/描述失败。</summary>
    Failed,

    /// <summary>安全模式下跳过第三方插件发现。</summary>
    SafeModeSkipped
}

/// <summary>
/// 单个第三方插件在宿主中的状态快照。只承载展示与管理所需字段，
/// 不含任何运行时对象，跨程序集边界安全。
/// </summary>
public sealed record PluginState
{
    public PluginState(
        PluginId pluginId,
        string displayName,
        Version? pluginVersion,
        string publisher,
        string entryAssemblyPath,
        string sha256,
        PluginTrustState trustState,
        string? message = null)
    {
        _ = pluginId.Value;

        PluginId = pluginId;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? pluginId.Value : displayName.Trim();
        PluginVersion = pluginVersion;
        Publisher = string.IsNullOrWhiteSpace(publisher) ? string.Empty : publisher.Trim();
        EntryAssemblyPath = entryAssemblyPath ?? string.Empty;
        Sha256 = sha256 ?? string.Empty;
        TrustState = trustState;
        Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
    }

    public PluginId PluginId { get; }

    public string DisplayName { get; }

    public Version? PluginVersion { get; }

    public string Publisher { get; }

    public string EntryAssemblyPath { get; }

    public string Sha256 { get; }

    public PluginTrustState TrustState { get; }

    public string? Message { get; }

    /// <summary>首次启用或哈希变化时都需要先让用户确认信任。</summary>
    public bool RequiresTrustConfirmation =>
        TrustState is PluginTrustState.Untrusted or PluginTrustState.RequiresConfirmation;

    public bool IsLoaded => TrustState == PluginTrustState.Loaded;

    public bool IsFailed => TrustState == PluginTrustState.Failed;
}
