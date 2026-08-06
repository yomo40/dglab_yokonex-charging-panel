using System.Collections.ObjectModel;
using System.Text.Json;
using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Core.Devices.Discovery;

namespace ChargingPanel.Core.Devices.Catalog;

public sealed class DeviceCatalog : IDeviceCatalog
{
    private readonly Dictionary<string, PluginId> _pluginOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginId> _vendorOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CatalogDeviceProfile> _profiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CatalogDeviceProfile> _models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DiscoveryRuleEntry> _discoveryRules = new(StringComparer.Ordinal);
    private readonly List<CatalogDiagnostic> _diagnostics = [];

    public DeviceCatalog(IEnumerable<IDevicePlugin> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);

        foreach (var plugin in plugins)
        {
            RegisterContribution(plugin);
        }
    }

    public IReadOnlyList<DeviceModelDescriptor> GetModels(bool includeHiddenReserved = false)
    {
        var models = _models.Values
            .Select(entry => entry.Model)
            .Where(model => includeHiddenReserved || model.Availability != DeviceAvailability.HiddenReserved)
            .OrderBy(model => model.Id.Value, StringComparer.Ordinal)
            .ToArray();

        return new ReadOnlyCollection<DeviceModelDescriptor>(models);
    }

    public IReadOnlyList<CatalogDiagnostic> GetDiagnostics() =>
        new ReadOnlyCollection<CatalogDiagnostic>(_diagnostics.ToArray());

    public CatalogMatchResult Match(DiscoveryAdvertisement advertisement)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        var matches = _discoveryRules.Values
            .Where(entry => entry.Rule.TransportKind == advertisement.TransportKind)
            .Where(entry => Matches(entry.Rule.MatchSchema.Schema, advertisement))
            .ToArray();

        if (matches.Length == 0)
        {
            return new CatalogMatchResult(
                null,
                null,
                null,
                null,
                false,
                [new DiscoveryDiagnostic(DiscoveryDiagnosticCode.NoMatch, "未找到匹配的设备发现规则。")]);
        }

        if (matches.Length > 1)
        {
            return new CatalogMatchResult(
                null,
                null,
                null,
                null,
                false,
                [new DiscoveryDiagnostic(DiscoveryDiagnosticCode.AmbiguousMatch, "发现结果匹配了多个协议 Profile。")]);
        }

        var match = matches[0];
        var availability = match.DeviceProfile.Model.Availability;
        if (availability == DeviceAvailability.UpgradeDiagnostic)
        {
            return new CatalogMatchResult(
                availability,
                match.DeviceProfile.PluginId,
                match.DeviceProfile.Model.Id,
                match.DeviceProfile.Profile.Id,
                false,
                [new DiscoveryDiagnostic(DiscoveryDiagnosticCode.UpgradeRequired, "该设备需要升级适配后才能创建会话。")]);
        }

        return new CatalogMatchResult(
            availability,
            match.DeviceProfile.PluginId,
            match.DeviceProfile.Model.Id,
            match.DeviceProfile.Profile.Id,
            true,
            Array.Empty<DiscoveryDiagnostic>());
    }

    public bool TryGetDeviceProfile(
        PluginId pluginId,
        ModelId modelId,
        ProtocolProfileId profileId,
        out CatalogDeviceProfile? deviceProfile)
    {
        var pluginValue = pluginId.Value;
        var modelValue = modelId.Value;
        var profileValue = profileId.Value;
        if (_profiles.TryGetValue(profileValue, out var resolved) &&
            resolved.PluginId.Value == pluginValue &&
            resolved.Model.Id.Value == modelValue)
        {
            deviceProfile = resolved;
            return true;
        }

        deviceProfile = null;
        return false;
    }

    private void RegisterContribution(IDevicePlugin? plugin)
    {
        if (plugin is null)
        {
            throw new ArgumentException("目录插件不能为 null。", nameof(plugin));
        }

        var manifest = plugin.Manifest ?? throw new ArgumentException("目录插件必须提供清单。", nameof(plugin));
        var pluginId = manifest.PluginId;
        var pluginValue = pluginId.Value;
        if (_pluginOwners.ContainsKey(pluginValue))
        {
            AddDiagnostic(pluginId, CatalogDiagnosticCode.DuplicatePluginId, "插件稳定 ID 已被其他贡献占用。", pluginValue);
            return;
        }

        if (!manifest.HostApiRange.Supports(HostApi.CurrentVersion))
        {
            AddDiagnostic(pluginId, CatalogDiagnosticCode.IncompatibleHostApi, "插件不支持当前宿主 API 版本。", pluginValue);
            return;
        }

        var contribution = plugin.Describe() ?? throw new ArgumentException("目录插件必须提供贡献。", nameof(plugin));
        if (!TryValidateContribution(pluginId, contribution, out var diagnostic))
        {
            _diagnostics.Add(diagnostic);
            return;
        }

        _pluginOwners.Add(pluginValue, pluginId);
        foreach (var vendor in contribution.Vendors)
        {
            _vendorOwners.Add(vendor.Id.Value, pluginId);
        }

        var profilesById = contribution.ProtocolProfiles.ToDictionary(profile => profile.Id.Value, StringComparer.Ordinal);
        foreach (var model in contribution.DeviceModels)
        {
            var primaryProfile = profilesById[model.ProtocolProfileIds[0].Value];
            _models.Add(model.Id.Value, new CatalogDeviceProfile(pluginId, model, primaryProfile));
        }

        foreach (var profile in contribution.ProtocolProfiles)
        {
            var model = contribution.DeviceModels.Single(candidate => candidate.Id.Value == profile.ModelId.Value);
            _profiles.Add(profile.Id.Value, new CatalogDeviceProfile(pluginId, model, profile));
        }

        foreach (var rule in contribution.DiscoveryRules)
        {
            _discoveryRules.Add(
                rule.RuleId,
                new DiscoveryRuleEntry(rule, _profiles[rule.ProfileId.Value]));
        }
    }

    private bool TryValidateContribution(
        PluginId pluginId,
        DevicePluginContribution contribution,
        out CatalogDiagnostic diagnostic)
    {
        var pluginValue = pluginId.Value;
        var modelIds = contribution.DeviceModels.Select(model => model.Id.Value).ToHashSet(StringComparer.Ordinal);
        var profileIds = contribution.ProtocolProfiles.Select(profile => profile.Id.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var model in contribution.DeviceModels)
        {
            if (model.ProtocolProfileIds.Count == 0)
            {
                diagnostic = CreateDiagnostic(
                    pluginId,
                    CatalogDiagnosticCode.InvalidContribution,
                    "设备模型必须至少关联一个协议 Profile。",
                    model.Id.Value);
                return false;
            }

            foreach (var profileId in model.ProtocolProfileIds)
            {
                if (!profileIds.Contains(profileId.Value))
                {
                    diagnostic = CreateDiagnostic(
                        pluginId,
                        CatalogDiagnosticCode.InvalidContribution,
                        "设备模型引用了同一贡献中不存在的协议 Profile。",
                        profileId.Value);
                    return false;
                }
            }
        }

        foreach (var profile in contribution.ProtocolProfiles)
        {
            if (!modelIds.Contains(profile.ModelId.Value))
            {
                diagnostic = CreateDiagnostic(
                    pluginId,
                    CatalogDiagnosticCode.InvalidContribution,
                    "协议 Profile 引用了同一贡献中不存在的设备模型。",
                    profile.ModelId.Value);
                return false;
            }
        }

        foreach (var rule in contribution.DiscoveryRules)
        {
            if (!profileIds.Contains(rule.ProfileId.Value))
            {
                diagnostic = CreateDiagnostic(
                    pluginId,
                    CatalogDiagnosticCode.InvalidContribution,
                    "发现规则引用了同一贡献中不存在的协议 Profile。",
                    rule.ProfileId.Value);
                return false;
            }

            if (!TryValidateDiscoveryRuleSchema(rule, out var schemaError))
            {
                diagnostic = CreateDiagnostic(
                    pluginId,
                    CatalogDiagnosticCode.InvalidContribution,
                    schemaError,
                    rule.RuleId);
                return false;
            }
        }

        foreach (var factory in contribution.DriverFactories)
        {
            if (!profileIds.Contains(factory.ProfileId.Value))
            {
                diagnostic = CreateDiagnostic(
                    pluginId,
                    CatalogDiagnosticCode.InvalidContribution,
                    "协议驱动工厂引用了同一贡献中不存在的协议 Profile。",
                    factory.ProfileId.Value);
                return false;
            }
        }

        if (_pluginOwners.ContainsKey(pluginValue))
        {
            diagnostic = CreateDiagnostic(pluginId, CatalogDiagnosticCode.DuplicatePluginId, "插件稳定 ID 已被其他贡献占用。", pluginValue);
            return false;
        }

        foreach (var vendor in contribution.Vendors)
        {
            if (_vendorOwners.ContainsKey(vendor.Id.Value))
            {
                diagnostic = CreateDiagnostic(pluginId, CatalogDiagnosticCode.DuplicateVendorId, "厂商稳定 ID 已被其他贡献占用。", vendor.Id.Value);
                return false;
            }
        }

        foreach (var model in contribution.DeviceModels)
        {
            if (_models.ContainsKey(model.Id.Value))
            {
                diagnostic = CreateDiagnostic(pluginId, CatalogDiagnosticCode.DuplicateModelId, "设备模型稳定 ID 已被其他贡献占用。", model.Id.Value);
                return false;
            }
        }

        foreach (var profile in contribution.ProtocolProfiles)
        {
            if (_profiles.ContainsKey(profile.Id.Value))
            {
                diagnostic = CreateDiagnostic(pluginId, CatalogDiagnosticCode.DuplicateProfileId, "协议 Profile 稳定 ID 已被其他贡献占用。", profile.Id.Value);
                return false;
            }
        }

        foreach (var rule in contribution.DiscoveryRules)
        {
            if (_discoveryRules.ContainsKey(rule.RuleId))
            {
                diagnostic = CreateDiagnostic(pluginId, CatalogDiagnosticCode.DuplicateDiscoveryRuleId, "发现规则稳定 ID 已被其他贡献占用。", rule.RuleId);
                return false;
            }
        }

        diagnostic = null!;
        return true;
    }

    private void AddDiagnostic(PluginId pluginId, CatalogDiagnosticCode code, string message, string conflictingStableId) =>
        _diagnostics.Add(CreateDiagnostic(pluginId, code, message, conflictingStableId));

    private static CatalogDiagnostic CreateDiagnostic(
        PluginId pluginId,
        CatalogDiagnosticCode code,
        string message,
        string conflictingStableId) =>
        new(pluginId, code, message, conflictingStableId);

    private static bool Matches(JsonElement schema, DiscoveryAdvertisement advertisement)
    {
        if (!TryValidateMatchSchema(schema, out _))
        {
            return false;
        }

        if (schema.TryGetProperty("name", out var name) &&
            (name.ValueKind != JsonValueKind.String ||
             !string.Equals(name.GetString(), advertisement.DisplayName, StringComparison.Ordinal)))
        {
            return false;
        }

        if (!schema.TryGetProperty("properties", out var properties))
        {
            return true;
        }

        if (properties.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in properties.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String ||
                !advertisement.Properties.TryGetValue(property.Name, out var actual) ||
                !string.Equals(property.Value.GetString(), actual, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateDiscoveryRuleSchema(
        DiscoveryRuleDescriptor rule,
        out string error)
    {
        if (!string.Equals(rule.MatchSchema.Version, "1.0", StringComparison.Ordinal))
        {
            error = "发现规则仅支持 1.0 匹配 Schema 版本。";
            return false;
        }

        return TryValidateMatchSchema(rule.MatchSchema.Schema, out error);
    }

    private static bool TryValidateMatchSchema(JsonElement schema, out string error)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            error = "发现规则匹配 Schema 必须是 object。";
            return false;
        }

        var seenFields = new HashSet<string>(StringComparer.Ordinal);
        var hasCondition = false;
        foreach (var field in schema.EnumerateObject())
        {
            if (!seenFields.Add(field.Name))
            {
                error = "发现规则匹配 Schema 不能包含重复字段。";
                return false;
            }

            switch (field.Name)
            {
                case "name":
                    if (field.Value.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(field.Value.GetString()))
                    {
                        error = "发现规则 name 条件必须是非空字符串。";
                        return false;
                    }

                    hasCondition = true;
                    break;
                case "properties":
                    if (!TryValidatePropertyConditions(field.Value, out error))
                    {
                        return false;
                    }

                    hasCondition = true;
                    break;
                default:
                    error = "发现规则匹配 Schema 包含不支持的字段。";
                    return false;
            }
        }

        if (!hasCondition)
        {
            error = "发现规则匹配 Schema 至少需要一个有效条件。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidatePropertyConditions(JsonElement properties, out string error)
    {
        if (properties.ValueKind != JsonValueKind.Object)
        {
            error = "发现规则 properties 条件必须是 object。";
            return false;
        }

        var seenProperties = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var property in properties.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) ||
                !seenProperties.Add(property.Name) ||
                property.Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                error = "发现规则 properties 必须由唯一的非空字符串键和值组成。";
                return false;
            }

            count++;
        }

        if (count == 0)
        {
            error = "发现规则 properties 至少需要一个条件。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private sealed record DiscoveryRuleEntry(
        DiscoveryRuleDescriptor Rule,
        CatalogDeviceProfile DeviceProfile);
}
