namespace ChargingPanel.Contracts.Identifiers;

public readonly record struct PluginId
{
    private readonly string? _value;

    public PluginId(string value) => _value = StableStringId.Normalize(value, nameof(value));

    public string Value => _value ?? throw InvalidIdentifier();

    public static PluginId Parse(string value) => new(value);

    public override string ToString() => Value;

    internal void ThrowIfInvalid(string parameterName) =>
        StableStringId.ThrowIfDefault(_value, parameterName);

    private static InvalidOperationException InvalidIdentifier() =>
        new("不能使用 default PluginId。");
}

public readonly record struct VendorId
{
    private readonly string? _value;

    public VendorId(string value) => _value = StableStringId.Normalize(value, nameof(value));

    public string Value => _value ?? throw new InvalidOperationException("不能使用 default VendorId。");

    public static VendorId Parse(string value) => new(value);

    public override string ToString() => Value;

    internal void ThrowIfInvalid(string parameterName) =>
        StableStringId.ThrowIfDefault(_value, parameterName);
}

public readonly record struct ModelId
{
    private readonly string? _value;

    public ModelId(string value) => _value = StableStringId.Normalize(value, nameof(value));

    public string Value => _value ?? throw new InvalidOperationException("不能使用 default ModelId。");

    public static ModelId Parse(string value) => new(value);

    public override string ToString() => Value;

    internal void ThrowIfInvalid(string parameterName) =>
        StableStringId.ThrowIfDefault(_value, parameterName);
}

public readonly record struct ProtocolProfileId
{
    private readonly string? _value;

    public ProtocolProfileId(string value) => _value = StableStringId.Normalize(value, nameof(value));

    public string Value => _value ?? throw new InvalidOperationException("不能使用 default ProtocolProfileId。");

    public static ProtocolProfileId Parse(string value) => new(value);

    public override string ToString() => Value;

    internal void ThrowIfInvalid(string parameterName) =>
        StableStringId.ThrowIfDefault(_value, parameterName);
}

public readonly record struct CapabilityId
{
    private readonly string? _value;

    public CapabilityId(string value) => _value = StableStringId.Normalize(value, nameof(value));

    public string Value => _value ?? throw new InvalidOperationException("不能使用 default CapabilityId。");

    public static CapabilityId Parse(string value) => new(value);

    public override string ToString() => Value;

    internal void ThrowIfInvalid(string parameterName) =>
        StableStringId.ThrowIfDefault(_value, parameterName);
}

public readonly record struct CommandId
{
    private readonly Guid _value;

    public CommandId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        _value = value;
    }

    public Guid Value => _value != Guid.Empty
        ? _value
        : throw new InvalidOperationException("不能使用 default CommandId。");

    public static CommandId New() => new(Guid.NewGuid());

    public static CommandId Parse(string value) => new(Guid.Parse(value));

    public override string ToString() => Value.ToString("D");

    internal void ThrowIfInvalid(string parameterName) =>
        StrongGuidId.ThrowIfDefault(_value, parameterName);
}

public readonly record struct DeviceInstanceId
{
    private readonly Guid _value;

    public DeviceInstanceId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        _value = value;
    }

    public Guid Value => _value != Guid.Empty
        ? _value
        : throw new InvalidOperationException("不能使用 default DeviceInstanceId。");

    public static DeviceInstanceId New() => new(Guid.NewGuid());

    public static DeviceInstanceId Parse(string value) => new(Guid.Parse(value));

    public override string ToString() => Value.ToString("D");

    internal void ThrowIfInvalid(string parameterName) =>
        StrongGuidId.ThrowIfDefault(_value, parameterName);
}

public readonly record struct EndpointId
{
    private readonly Guid _value;

    public EndpointId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        _value = value;
    }

    public Guid Value => _value != Guid.Empty
        ? _value
        : throw new InvalidOperationException("不能使用 default EndpointId。");

    public static EndpointId New() => new(Guid.NewGuid());

    public static EndpointId Parse(string value) => new(Guid.Parse(value));

    public override string ToString() => Value.ToString("D");

    internal void ThrowIfInvalid(string parameterName) =>
        StrongGuidId.ThrowIfDefault(_value, parameterName);
}

internal static class StableStringId
{
    public static void ThrowIfDefault(string? value, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentException("稳定 ID 不能为 default。", parameterName);
        }
    }

    public static string Normalize(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (value.Length is 0 or > 128)
        {
            throw new ArgumentException("稳定 ID 长度必须为 1 到 128 个字符。", parameterName);
        }

        foreach (var segment in value.Split('.'))
        {
            if (segment.Length == 0 || !IsAsciiLetterOrDigit(segment[0]) || !IsAsciiLetterOrDigit(segment[^1]))
            {
                throw new ArgumentException("稳定 ID 必须由小写点分段组成。", parameterName);
            }

            if (segment.Any(character =>
                    !IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            {
                throw new ArgumentException("稳定 ID 只能包含 ASCII 小写字母、数字、连字符、下划线和点。", parameterName);
            }
        }

        return value;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';
}

internal static class StableContractToken
{
    public static string Validate(string value, string parameterName) =>
        StableStringId.Normalize(value, parameterName);
}

internal static class StrongGuidId
{
    public static void ThrowIfDefault(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("强类型 GUID 不能为 default。", parameterName);
        }
    }
}

internal static class ContractGuards
{
    public static void ThrowIfUndefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "枚举值未定义。");
        }
    }

    public static void ThrowIfNegative(long value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "数值不能为负数。");
        }
    }
}
