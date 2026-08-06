using System.Collections.ObjectModel;
using System.Text;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Core.Devices.Discovery;

namespace ChargingPanel.Core.Devices.Registry;

public sealed record ConnectionEndpoint
{
    public ConnectionEndpoint(
        EndpointId id,
        ProtocolProfileId profileId,
        TransportKind transportKind,
        string fingerprint,
        string displayName,
        IReadOnlyDictionary<string, string> properties,
        string? manufacturerStableUid)
    {
        _ = id.Value;
        _ = profileId.Value;
        if (!Enum.IsDefined(transportKind))
        {
            throw new ArgumentOutOfRangeException(nameof(transportKind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(properties);
        Id = id;
        ProfileId = profileId;
        TransportKind = transportKind;
        Fingerprint = fingerprint.Trim();
        DisplayName = displayName.Trim();
        Properties = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(properties, StringComparer.Ordinal));
        ManufacturerStableUid = string.IsNullOrWhiteSpace(manufacturerStableUid)
            ? null
            : manufacturerStableUid.Trim();
    }

    public EndpointId Id { get; }

    public ProtocolProfileId ProfileId { get; }

    public TransportKind TransportKind { get; }

    public string Fingerprint { get; }

    public string DisplayName { get; }

    public IReadOnlyDictionary<string, string> Properties { get; }

    public string? ManufacturerStableUid { get; }

    internal bool HasCanonicalFingerprint() =>
        string.Equals(
            Fingerprint,
            CreateFingerprint(TransportKind, ProfileId, Properties),
            StringComparison.Ordinal);

    public static ConnectionEndpoint FromAdvertisement(
        DiscoveryAdvertisement advertisement,
        ProtocolProfileId profileId)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        var fingerprint = CreateFingerprint(advertisement, profileId);
        return new ConnectionEndpoint(
            EndpointId.New(),
            profileId,
            advertisement.TransportKind,
            fingerprint,
            advertisement.DisplayName,
            advertisement.Properties,
            advertisement.ManufacturerStableUid);
    }

    public static string CreateFingerprint(DiscoveryAdvertisement advertisement, ProtocolProfileId profileId)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        return CreateFingerprint(advertisement.TransportKind, profileId, advertisement.Properties);
    }

    private static string CreateFingerprint(
        TransportKind transportKind,
        ProtocolProfileId profileId,
        IReadOnlyDictionary<string, string> properties) =>
        transportKind switch
        {
            TransportKind.BluetoothLowEnergy => EncodeFingerprint(
                "ble",
                profileId.Value,
                NormalizeBluetoothAddress(properties),
                NormalizeServiceUuids(properties)),
            TransportKind.InstantMessaging => EncodeFingerprint(
                "im",
                GetRequiredProperty(properties, "accountPrincipal"),
                GetRequiredProperty(properties, "target")),
            TransportKind.WebSocket or TransportKind.Http => EncodeFingerprint(
                "bridge",
                GetRequiredProperty(properties, "serviceIdentity"),
                GetRequiredProperty(properties, "target")),
            TransportKind.Virtual => EncodeFingerprint(
                "virtual",
                profileId.Value,
                GetRequiredProperty(properties, "virtualId")),
            _ => throw new ArgumentOutOfRangeException(nameof(transportKind), "不支持的发现传输类型。")
        };

    private static string EncodeFingerprint(string kind, params string[] parts)
    {
        var builder = new StringBuilder(kind);
        foreach (var part in parts)
        {
            builder.Append('|').Append(part.Length).Append(':').Append(part);
        }

        return builder.ToString();
    }

    private static string NormalizeBluetoothAddress(IReadOnlyDictionary<string, string> properties)
    {
        var address = GetRequiredProperty(properties, "address");
        var normalized = new string(address.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        if (normalized.Length != 12 || address.Any(character => !Uri.IsHexDigit(character) && character is not ':' and not '-'))
        {
            throw new ArgumentException("BLE 地址必须是规范可识别的六字节地址。", nameof(properties));
        }

        return normalized;
    }

    private static string NormalizeServiceUuids(IReadOnlyDictionary<string, string> properties)
    {
        var raw = GetRequiredProperty(properties, "serviceUuids");
        var values = raw.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Guid.TryParse(value, out var uuid)
                ? uuid.ToString("D").ToLowerInvariant()
                : throw new ArgumentException("BLE 服务 UUID 格式无效。", nameof(properties)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (values.Length == 0)
        {
            throw new ArgumentException("BLE 发现必须包含至少一个服务 UUID。", nameof(properties));
        }

        return string.Join(',', values);
    }

    private static string GetRequiredProperty(IReadOnlyDictionary<string, string> properties, string key)
    {
        if (!properties.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"发现属性缺少必需字段 {key}。", nameof(properties));
        }

        return value.Trim();
    }
}
