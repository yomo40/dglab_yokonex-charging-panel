namespace ChargingPanel.Vendor.DGLab.Common;

/// <summary>
/// DG-LAB 厂商插件的稳定标识符集中定义。
/// 稳定 ID 是小写点分段，一旦发布不得变更，否则持久化的设备实例会失效。
/// </summary>
public static class DGLabStableIds
{
    /// <summary>厂商稳定 ID。</summary>
    public const string Vendor = "dglab";

    // 设备模型
    public const string ModelCoyoteV3 = "dglab.coyote.v3";
    public const string ModelCoyoteV2 = "dglab.coyote.v2";
    public const string ModelPawPrintsV11 = "dglab.pawprints.v1.1";
    public const string ModelPawPrintsV10 = "dglab.pawprints.v1.0";
    public const string ModelCivetV1 = "dglab.civet.v1";
    public const string ModelOpossumV1 = "dglab.opossum.v1";

    // 协议 Profile
    public const string ProfileCoyoteV3Ble = "dglab.coyote.v3.ble";
    public const string ProfileCoyoteV3WebSocket = "dglab.coyote.v3.ws";
    public const string ProfileCoyoteV2Ble = "dglab.coyote.v2.ble";
    public const string ProfilePawPrintsV11Ble = "dglab.pawprints.v1.1.ble";
    public const string ProfilePawPrintsV10Ble = "dglab.pawprints.v1.0.ble";
    public const string ProfileCivetV1Ble = "dglab.civet.v1.ble";
    public const string ProfileOpossumV1Ble = "dglab.opossum.v1.ble";

    // 能力
    public const string CapIntensityOutput = "intensity.output";
    public const string CapWaveformStream = "waveform.stream";
    public const string CapWaveformClear = "waveform.clear";
    public const string CapBatteryStatus = "battery.status";
    public const string CapSafetyLimits = "safety.limits";
    public const string CapLampColor = "lamp.color";
    public const string CapShoulderLamp = "shoulder.lamp";
    public const string CapTriggerMode = "trigger.mode";
    public const string CapTriggerParamReset = "trigger.param.reset";
    public const string CapTriggerAutoDetect = "trigger.auto.detect";
    public const string CapPoseAcceleration = "pose.acceleration";
    public const string CapExternalVoltage = "external.voltage";
    public const string CapButtonStatus = "button.status";
    public const string CapPressureReporting = "pressure.reporting";
    public const string CapPressureStatus = "pressure.status";
    public const string CapPressureZero = "pressure.zero";
    public const string CapScreenSync = "screen.sync";
}
