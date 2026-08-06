using System.Collections.ObjectModel;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Plugins;
using ChargingPanel.Infrastructure.Windows.Plugins;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChargingPanel.Desktop.ViewModels.Plugins;

/// <summary>
/// 第三方设备插件管理视图模型。只管理信任、启用/禁用与安全模式，
/// 不暴露插件加载的实现细节。信任模型 C 的警告文案在这里呈现。
/// </summary>
public sealed partial class DevicePluginManagerViewModel : ObservableObject
{
    private readonly DevicePluginHost _host;
    private PluginId? _pendingTrustPluginId;

    public DevicePluginManagerViewModel(DevicePluginHost? host = null)
    {
        _host = host ?? CreateDefaultHost();
        SafeMode = _host.SafeMode;
    }

    public ObservableCollection<DevicePluginItemViewModel> Plugins { get; } = new();

    [ObservableProperty]
    private bool _safeMode;

    [ObservableProperty]
    private bool _isTrustWarningVisible;

    [ObservableProperty]
    private string _trustWarningTitle = "警告：插件拥有与本程序相同权限";

    [ObservableProperty]
    private string _trustWarningText =
        "插件与本程序拥有相同权限。插件不是沙箱，无法隔离恶意代码；" +
        "请只安装可信来源的插件，并在确认哈希无误后再启用。";

    [ObservableProperty]
    private string _trustWarningPublisher = string.Empty;

    [ObservableProperty]
    private string _trustWarningPath = string.Empty;

    [ObservableProperty]
    private string _trustWarningHash = string.Empty;

    [ObservableProperty]
    private string _lastError = string.Empty;

    partial void OnSafeModeChanged(bool value)
    {
        _host.SafeMode = value;
        Refresh();
    }

    /// <summary>重新扫描插件目录并刷新列表。</summary>
    public void Refresh()
    {
        _host.Refresh();
        Plugins.Clear();
        foreach (var state in _host.States)
        {
            Plugins.Add(new DevicePluginItemViewModel(state));
        }
    }

    public void Enable(DevicePluginItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.RequiresTrustConfirmation)
        {
            ShowTrustWarning(item);
            return;
        }

        try
        {
            _host.SetEnabled(item.PluginId, enabled: true);
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }

        Refresh();
    }

    public void Disable(DevicePluginItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            _host.SetEnabled(item.PluginId, enabled: false);
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }

        Refresh();
    }

    public void ConfirmTrust()
    {
        if (_pendingTrustPluginId is { } pluginId)
        {
            try
            {
                _host.ConfirmTrust(pluginId);
                LastError = string.Empty;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        ClearTrustWarning();
        Refresh();
    }

    public void CancelTrust() => ClearTrustWarning();

    private void ShowTrustWarning(DevicePluginItemViewModel item)
    {
        _pendingTrustPluginId = item.PluginId;
        TrustWarningPublisher = $"发布者: {item.Publisher}";
        TrustWarningPath = $"路径: {item.EntryAssemblyPath}";
        TrustWarningHash = $"SHA-256: {item.Sha256}";
        IsTrustWarningVisible = true;
    }

    private void ClearTrustWarning()
    {
        _pendingTrustPluginId = null;
        IsTrustWarningVisible = false;
    }

    private static DevicePluginHost CreateDefaultHost() =>
        new(
            new PluginHostOptions(),
            builtInPlugins:
            [
                new ChargingPanel.Vendor.DGLab.DGLabPlugin(),
                new ChargingPanel.Vendor.Yokonex.YokonexPlugin()
            ]);
}

/// <summary>单个第三方插件行的展示模型。</summary>
public sealed class DevicePluginItemViewModel
{
    public DevicePluginItemViewModel(PluginState state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    public PluginState State { get; }

    public PluginId PluginId => State.PluginId;

    public string PluginIdText => State.PluginId.Value;

    public string DisplayName => State.DisplayName;

    public string VersionText => State.PluginVersion?.ToString(3) ?? "--";

    public string Publisher => State.Publisher;

    public string EntryAssemblyPath => State.EntryAssemblyPath;

    public string Sha256 => State.Sha256;

    public string StatusText => State.TrustState switch
    {
        PluginTrustState.Loaded => "已加载",
        PluginTrustState.Untrusted => "未信任",
        PluginTrustState.RequiresConfirmation => "需要重新确认",
        PluginTrustState.Disabled => "已禁用",
        PluginTrustState.Failed => "加载失败",
        PluginTrustState.SafeModeSkipped => "安全模式跳过",
        _ => State.TrustState.ToString()
    };

    public string? Message => State.Message;

    public bool RequiresTrustConfirmation => State.RequiresTrustConfirmation;

    public bool IsFailed => State.IsFailed;

    public bool IsLoaded => State.IsLoaded;
}
