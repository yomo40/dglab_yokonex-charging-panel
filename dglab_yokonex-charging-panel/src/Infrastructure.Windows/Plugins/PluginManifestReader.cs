using System.Security.Cryptography;
using System.Text.Json;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;

namespace ChargingPanel.Infrastructure.Windows.Plugins;

/// <summary>插件清单解析或校验失败。消息使用中文。</summary>
public class PluginManifestException : Exception
{
    public PluginManifestException(string message) : base(message)
    {
    }

    public PluginManifestException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>声明依赖缺失，单独分类以便宿主保留结构化诊断。</summary>
public sealed class PluginMissingDependencyException : PluginManifestException
{
    public PluginMissingDependencyException(string dependencyName, string message)
        : base(message)
    {
        DependencyName = dependencyName;
    }

    public string DependencyName { get; }
}

/// <summary>解析后的插件清单信息。</summary>
public sealed record PluginManifestInfo
{
    public PluginManifestInfo(
        PluginId pluginId,
        string displayName,
        string publisher,
        Version pluginVersion,
        HostApiVersionRange hostApiRange,
        string entryAssemblyName,
        string entryAssemblyPath,
        string sha256,
        IReadOnlyList<string> dependencies,
        bool bundlesContracts)
    {
        _ = pluginId.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(pluginVersion);
        ArgumentNullException.ThrowIfNull(hostApiRange);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryAssemblyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryAssemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentNullException.ThrowIfNull(dependencies);

        PluginId = pluginId;
        DisplayName = displayName.Trim();
        Publisher = string.IsNullOrWhiteSpace(publisher) ? string.Empty : publisher.Trim();
        PluginVersion = pluginVersion;
        HostApiRange = hostApiRange;
        EntryAssemblyName = entryAssemblyName;
        EntryAssemblyPath = entryAssemblyPath;
        Sha256 = sha256;
        Dependencies = Array.AsReadOnly(dependencies.ToArray());
        BundlesContracts = bundlesContracts;
    }

    public PluginId PluginId { get; }

    public string DisplayName { get; }

    public string Publisher { get; }

    public Version PluginVersion { get; }

    public HostApiVersionRange HostApiRange { get; }

    public string EntryAssemblyName { get; }

    public string EntryAssemblyPath { get; }

    public string Sha256 { get; }

    public IReadOnlyList<string> Dependencies { get; }

    /// <summary>插件目录是否打包了第二份宿主 Contracts 程序集。</summary>
    public bool BundlesContracts { get; }
}

/// <summary>
/// 确定性插件清单读取器。只扫描固定形态：
/// <c>%LocalAppData%/ChargingPanel/plugins/&lt;plugin-id&gt;/plugin.json</c>。
/// 校验 PluginId、版本、Host API 范围、入口程序集、SHA-256 与依赖规则。
/// </summary>
public sealed class PluginManifestReader
{
    public PluginManifestInfo Read(string pluginDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        var pluginJsonPath = Path.Combine(pluginDirectory, "plugin.json");
        if (!File.Exists(pluginJsonPath))
        {
            throw new PluginManifestException($"缺少 plugin.json: {pluginJsonPath}");
        }

        using var document = ParseDocument(pluginJsonPath);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new PluginManifestException("plugin.json 根节点必须是对象。");
        }

        var pluginId = new PluginId(GetRequiredString(root, "pluginId"));
        var directoryName = Path.GetFileName(pluginDirectory);
        if (!string.Equals(directoryName, pluginId.Value, StringComparison.Ordinal))
        {
            throw new PluginManifestException(
                $"插件目录名称必须等于 pluginId（期望 {pluginId.Value}，实际 {directoryName}）。");
        }

        var displayName = GetRequiredString(root, "displayName");
        var publisher = GetOptionalString(root, "publisher");
        var pluginVersion = ParseVersion(GetRequiredString(root, "pluginVersion"), "pluginVersion");
        var hostApiRange = ReadHostApiRange(root);

        var entryAssemblyName = GetRequiredString(root, "entryAssembly");
        ValidateFileName(entryAssemblyName, "entryAssembly");
        var entryAssemblyPath = Path.Combine(pluginDirectory, entryAssemblyName);
        if (!File.Exists(entryAssemblyPath))
        {
            throw new PluginManifestException($"入口程序集不存在: {entryAssemblyPath}");
        }

        var sha256 = ComputeSha256(entryAssemblyPath);
        var dependencies = ReadDependencies(root);
        foreach (var dependency in dependencies)
        {
            var dependencyPath = Path.Combine(pluginDirectory, dependency);
            if (!File.Exists(dependencyPath))
            {
                throw new PluginMissingDependencyException(
                    dependency,
                    $"插件声明依赖缺失: {dependencyPath}");
            }
        }

        var bundlesContracts = File.Exists(Path.Combine(pluginDirectory, "ChargingPanel.Contracts.dll"));

        return new PluginManifestInfo(
            pluginId,
            displayName,
            publisher,
            pluginVersion,
            hostApiRange,
            entryAssemblyName,
            entryAssemblyPath,
            sha256,
            dependencies,
            bundlesContracts);
    }

    private static JsonDocument ParseDocument(string path)
    {
        try
        {
            return JsonDocument.Parse(
                File.ReadAllBytes(path),
                new JsonDocumentOptions { AllowTrailingCommas = false });
        }
        catch (JsonException ex)
        {
            throw new PluginManifestException("plugin.json 不是有效 JSON。", ex);
        }
    }

    private static HostApiVersionRange ReadHostApiRange(JsonElement root)
    {
        if (!root.TryGetProperty("hostApiRange", out var range) || range.ValueKind != JsonValueKind.Object)
        {
            throw new PluginManifestException("缺少 hostApiRange 对象。");
        }

        var minimum = ParseVersion(GetRequiredString(range, "minimumInclusive"), "hostApiRange.minimumInclusive");
        var maximum = ParseVersion(GetRequiredString(range, "maximumInclusive"), "hostApiRange.maximumInclusive");
        try
        {
            return new HostApiVersionRange(minimum, maximum);
        }
        catch (ArgumentException ex)
        {
            throw new PluginManifestException("hostApiRange 无效。", ex);
        }
    }

    private static IReadOnlyList<string> ReadDependencies(JsonElement root)
    {
        if (!root.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind == JsonValueKind.Undefined)
        {
            return Array.Empty<string>();
        }

        if (dependencies.ValueKind != JsonValueKind.Array)
        {
            throw new PluginManifestException("dependencies 必须是字符串数组。");
        }

        var result = new List<string>();
        foreach (var item in dependencies.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new PluginManifestException("dependencies 数组项必须是字符串。");
            }

            var name = item.GetString()!;
            ValidateFileName(name, "dependencies");
            result.Add(name);
        }

        return result;
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            throw new PluginManifestException($"缺少 {propertyName}。");
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new PluginManifestException($"{propertyName} 必须是非空字符串。");
        }

        return value.GetString()!;
    }

    private static string GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new PluginManifestException($"{propertyName} 必须是字符串。");
        }

        return value.GetString() ?? string.Empty;
    }

    private static Version ParseVersion(string value, string fieldName)
    {
        if (!Version.TryParse(value, out var version))
        {
            throw new PluginManifestException($"{fieldName} 不是有效版本号。");
        }

        return version;
    }

    private static void ValidateFileName(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PluginManifestException($"{fieldName} 不能为空。");
        }

        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains('\\') ||
            value.Contains('/') ||
            string.Equals(value, "..", StringComparison.Ordinal))
        {
            throw new PluginManifestException($"{fieldName} 必须是简单文件名。");
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
