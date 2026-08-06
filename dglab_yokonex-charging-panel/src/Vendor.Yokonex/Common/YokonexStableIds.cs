namespace ChargingPanel.Vendor.Yokonex.Common;

/// <summary>
/// YOKONEX 厂商插件的稳定标识符集中定义。
/// 稳定 ID 是小写点分段，一旦发布不得变更，否则持久化的设备实例会失效。
/// </summary>
public static class YokonexStableIds
{
    /// <summary>厂商稳定 ID。</summary>
    public const string Vendor = "yokonex";

    // 设备模型
    public const string ModelEmsGen1 = "yokonex.ems.gen1";
    public const string ModelEmsGen2 = "yokonex.ems.gen2";
    public const string ModelEnemaGen1 = "yokonex.enema.gen1";
    public const string ModelToyV11 = "yokonex.toy.v1_1";
    public const string ModelImTarget = "yokonex.im.target";
    public const string ModelApiBridge = "yokonex.api_bridge";

    // 协议 Profile
    public const string ProfileEmsV16 = "yokonex.ems.v1_6";
    public const string ProfileEmsV20 = "yokonex.ems.v2_0";
    public const string ProfileEnemaV10 = "yokonex.enema.v1_0";
    public const string ProfileToyV11 = "yokonex.toy.v1_1";
    public const string ProfileImTarget = "yokonex.im.target.v1";
    public const string ProfileApiBridge = "yokonex.api_bridge.v1";

    // 能力
    public const string CapEmsChannelOutput = "ems.channel.output";
    public const string CapEmsMotorControl = "ems.motor.control";
    public const string CapEmsStepControl = "ems.step.control";
    public const string CapEmsAngleControl = "ems.angle.control";
    public const string CapEmsTelemetry = "ems.telemetry";
    public const string CapEmsFrequencyList = "ems.frequency_list";

    public const string CapEnemaPumpControl = "enema.pump.control";
    public const string CapEnemaTelemetry = "enema.telemetry";

    public const string CapToyMotorControl = "toy.motor.control";
    public const string CapToySpeedControl = "toy.speed.control";
    public const string CapToyTelemetry = "toy.telemetry";

    public const string CapImCommandSend = "im.command.send";
    public const string CapImGlobalStop = "im.global.stop";
    public const string CapImCommandCustom = "im.command.custom";
    public const string CapImMetadataUpdate = "im.metadata.update";
    public const string CapImGameInfoLegacy = "im.game_info.legacy";

    public const string CapBridgeCommandSend = "bridge.command.send";
    public const string CapBridgeLogin = "bridge.login";
    public const string CapBridgeReinit = "bridge.reinit";
    public const string CapBridgeTelemetry = "bridge.telemetry";
}
