using System.Text.Json;
using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.Infrastructure.Windows.Plugins;

/// <summary>
/// 单个插件的信任记录。只保存非敏感数据：启用状态、已确认的入口程序集
/// SHA-256 与确认时间。不保存任何密钥、Token 或凭据。
/// </summary>
public sealed record PluginTrustRecord
{
    public bool Enabled { get; init; }

    public string? TrustedSha256 { get; init; }

    public DateTimeOffset? ConfirmedAt { get; init; }
}

/// <summary>
/// 插件信任存储。记录存放在 <c>&lt;root&gt;/.trust/&lt;pluginId&gt;.json</c>，
/// 独立于插件目录，插件升级替换目录后仍可检测哈希变化并触发重新确认。
/// </summary>
public sealed class PluginTrustStore
{
    private readonly string _rootPath;

    public PluginTrustStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = rootPath;
    }

    public PluginTrustRecord Load(PluginId pluginId)
    {
        _ = pluginId.Value;
        var path = GetFilePath(pluginId);
        if (!File.Exists(path))
        {
            return new PluginTrustRecord();
        }

        try
        {
            return JsonSerializer.Deserialize<PluginTrustRecord>(File.ReadAllText(path)) ?? new PluginTrustRecord();
        }
        catch (JsonException)
        {
            return new PluginTrustRecord();
        }
        catch (IOException)
        {
            return new PluginTrustRecord();
        }
    }

    public void Save(PluginId pluginId, PluginTrustRecord record)
    {
        _ = pluginId.Value;
        ArgumentNullException.ThrowIfNull(record);
        var directory = Path.Combine(_rootPath, ".trust");
        Directory.CreateDirectory(directory);
        File.WriteAllText(GetFilePath(pluginId), JsonSerializer.Serialize(record));
    }

    public void Delete(PluginId pluginId)
    {
        _ = pluginId.Value;
        var path = GetFilePath(pluginId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetFilePath(PluginId pluginId) =>
        Path.Combine(_rootPath, ".trust", pluginId.Value + ".json");
}
