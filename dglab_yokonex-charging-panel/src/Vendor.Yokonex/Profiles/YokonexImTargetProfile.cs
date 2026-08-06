using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Vendor.Yokonex.Common;

namespace ChargingPanel.Vendor.Yokonex.Profiles;

/// <summary>
/// 役次元 IM 目标协议 Profile。即时通讯传输。
/// 稳定指令面为 game_cmd + 字符串 id；开发中能力标为 Experimental，默认禁用。
/// </summary>
public static class YokonexImTargetProfile
{
    public static ProtocolProfileDescriptor Create() =>
        new(
            new ProtocolProfileId(YokonexStableIds.ProfileImTarget),
            new ModelId(YokonexStableIds.ModelImTarget),
            "役次元 IM 目标",
            [TransportKind.InstantMessaging],
            YokonexCapabilities.ImTargetCapabilities());
}
