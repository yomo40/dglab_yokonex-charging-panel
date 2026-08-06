using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;

namespace ChargingPanel.Vendor.Yokonex.Common;

/// <summary>
/// 通用协议驱动工厂。每个 Profile 对应一个工厂，创建时注入具体驱动类型。
/// </summary>
public sealed class YokonexDriverFactory : IProtocolDriverFactory
{
    private readonly Func<IPluginContext, IProtocolDriver> _create;

    public YokonexDriverFactory(ProtocolProfileId profileId, Func<IPluginContext, IProtocolDriver> create)
    {
        _ = profileId.Value;
        ArgumentNullException.ThrowIfNull(create);
        ProfileId = profileId;
        _create = create;
    }

    public ProtocolProfileId ProfileId { get; }

    public IProtocolDriver CreateDriver(IPluginContext context) => _create(context);
}
