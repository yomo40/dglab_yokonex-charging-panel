using System.Text.Json;
using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.Vendor.DGLab.Common;

/// <summary>
/// DG-LAB 能力声明构建工具。
/// </summary>
internal static class DGLabCapabilities
{
    private static readonly JsonDocument ObjectSchemaDocument = JsonDocument.Parse("{\"type\":\"object\"}");

    /// <summary>构造各厂商共用的通用对象 Schema。</summary>
    public static VersionedJsonSchema ObjectSchema() => new("1.0", ObjectSchemaDocument.RootElement.Clone());

    /// <summary>郊狼 V3 全能力清单。</summary>
    public static IReadOnlyList<CapabilityDescriptor> CoyoteV3Capabilities() =>
        [IntensityOutput(), WaveformStream(), WaveformClear(), SafetyLimits(), BatteryStatus()];

    /// <summary>郊狼 V2 全能力清单（无 BF 软上限指令）。</summary>
    public static IReadOnlyList<CapabilityDescriptor> CoyoteV2Capabilities() =>
        [IntensityOutput(), WaveformStream(), WaveformClear(), BatteryStatus()];

    /// <summary>强度输出能力（0-200 显示尺度，Profile 负责映射原始尺度）。</summary>
    public static CapabilityDescriptor IntensityOutput() =>
        BuildActuatorCapability(
            new CapabilityId(DGLabStableIds.CapIntensityOutput),
            "双通道强度输出",
            Actuator("main", "主输出", "level", 0, 200, 0, 200, "a", "A 通道", "b", "B 通道"),
            CommandPolicy(CommandDelivery.Idempotent, CommandAcknowledgement.DeviceAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("set.level", "{\"channelA\":0,\"channelB\":0}", "main"));

    /// <summary>波形流能力（4×25ms 帧，单帧强度 0-100）。</summary>
    public static CapabilityDescriptor WaveformStream() =>
        BuildActuatorCapability(
            new CapabilityId(DGLabStableIds.CapWaveformStream),
            "双通道波形流",
            Actuator("main", "主输出", "level", 0, 100, 0, 100, "a", "A 通道", "b", "B 通道"),
            CommandPolicy(CommandDelivery.StreamingFrame, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromMilliseconds(500), canReturnOutcomeUnknown: true),
            SafeStop("stop.frame", "{\"channelA\":{\"frequencies\":[0,0,0,0],\"strengths\":[0,0,0,0]},\"channelB\":{\"frequencies\":[0,0,0,0],\"strengths\":[0,0,0,0]}}", "main"));

    /// <summary>清空波形队列能力。</summary>
    public static CapabilityDescriptor WaveformClear() =>
        BuildActuatorCapability(
            new CapabilityId(DGLabStableIds.CapWaveformClear),
            "清空波形队列",
            Actuator("main", "主输出", "level", 0, 100, 0, 100, "a", "A 通道", "b", "B 通道"),
            CommandPolicy(CommandDelivery.Idempotent, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("clear.queue", "{}", "main"));

    /// <summary>安全软上限与平衡参数能力（V3 BF 指令）。</summary>
    public static CapabilityDescriptor SafetyLimits() =>
        BuildActuatorCapability(
            new CapabilityId(DGLabStableIds.CapSafetyLimits),
            "通道强度软上限与平衡参数",
            Actuator("limits", "软上限", "level", 0, 200, 0, 200, "a", "A 通道", "b", "B 通道"),
            CommandPolicy(CommandDelivery.Idempotent, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("set.soft.limit", "{\"limitA\":200,\"limitB\":200}", "limits"));

    /// <summary>爪印 V1.1 全能力清单。</summary>
    public static IReadOnlyList<CapabilityDescriptor> PawPrintsV11Capabilities() =>
        [LampColor(), ShoulderLamp(), TriggerModeConfig(), TriggerParamReset(), TriggerAutoDetect(), BatteryStatus(), ButtonStatus(), PoseAcceleration(), ExternalVoltage()];

    /// <summary>主指示灯颜色能力。</summary>
    public static CapabilityDescriptor LampColor() =>
        BuildActuatorCapability(
            new CapabilityId(DGLabStableIds.CapLampColor),
            "主指示灯颜色",
            Actuator("lamp", "主指示灯", "color", 0, 255, 0, 255, "color", "颜色"),
            CommandPolicy(CommandDelivery.Idempotent, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("set.color", "{}", "lamp"));

    /// <summary>肩灯颜色能力。</summary>
    public static CapabilityDescriptor ShoulderLamp() =>
        BuildActuatorCapability(
            new CapabilityId(DGLabStableIds.CapShoulderLamp),
            "肩灯",
            Actuator("shoulder", "肩灯", "color", 0, 255, 0, 255, "color", "颜色"),
            CommandPolicy(CommandDelivery.Idempotent, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("set.shoulder", "{}", "shoulder"));

    /// <summary>触发模式配置能力。</summary>
    public static CapabilityDescriptor TriggerModeConfig() =>
        BuildActuatorCapability(
            new CapabilityId(DGLabStableIds.CapTriggerMode),
            "触发模式配置",
            Actuator("trigger", "触发", "mode", 0, 255, 0, 255, "mode", "模式"),
            CommandPolicy(CommandDelivery.Idempotent, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("configure.trigger", "{}", "trigger"));

    /// <summary>触发参数清零能力。</summary>
    public static CapabilityDescriptor TriggerParamReset() =>
        BuildActuatorCapability(
            new CapabilityId(DGLabStableIds.CapTriggerParamReset),
            "触发参数清零",
            Actuator("trigger", "触发", "param", 0, 255, 0, 255, "param", "参数"),
            CommandPolicy(CommandDelivery.Idempotent, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("reset.param", "{}", "trigger"));

    /// <summary>角度自动检测能力。</summary>
    public static CapabilityDescriptor TriggerAutoDetect() =>
        BuildActuatorCapability(
            new CapabilityId(DGLabStableIds.CapTriggerAutoDetect),
            "角度自动检测",
            Actuator("trigger", "触发", "detect", 0, 255, 0, 255, "detect", "检测"),
            CommandPolicy(CommandDelivery.Idempotent, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("auto.detect", "{}", "trigger"));

    /// <summary>按键状态上报能力（遥测）。</summary>
    public static CapabilityDescriptor ButtonStatus() =>
        BuildActuatorCapability(
            new CapabilityId(DGLabStableIds.CapButtonStatus),
            "按键状态",
            Actuator("button", "按键", "state", 0, 255, 0, 255, "state", "状态"),
            TelemetryOnlyPolicy(),
            SafeStop("none", "{}", "button"));

    /// <summary>姿态/加速度遥测能力。</summary>
    public static CapabilityDescriptor PoseAcceleration() =>
        BuildActuatorCapability(
            new CapabilityId(DGLabStableIds.CapPoseAcceleration),
            "姿态与加速度",
            Actuator("sensor", "传感器", "value", 0, 255, 0, 255, "accel", "加速度", "angle", "角度"),
            TelemetryOnlyPolicy(),
            SafeStop("none", "{}", "sensor"));

    /// <summary>外部电压遥测能力。</summary>
    public static CapabilityDescriptor ExternalVoltage() =>
        BuildActuatorCapability(
            new CapabilityId(DGLabStableIds.CapExternalVoltage),
            "外部电压",
            Actuator("voltage", "外部电压", "level", 0, 210, 0, 210, "level", "电压"),
            TelemetryOnlyPolicy(),
            SafeStop("none", "{}", "voltage"));

    /// <summary>灵猫 V1 全能力清单。</summary>
    public static IReadOnlyList<CapabilityDescriptor> CivetV1Capabilities() =>
        [PressureReporting(), PressureZero(), BatteryStatus(), PressureStatus()];

    /// <summary>负鼠 V1 全能力清单。</summary>
    public static IReadOnlyList<CapabilityDescriptor> OpossumV1Capabilities() =>
        [LampColor(), ButtonStatus(), WaveformStream(), WaveformClear(), IntensityOutput(), ScreenSync(), BatteryStatus()];

    /// <summary>气压主动上报开关能力。</summary>
    public static CapabilityDescriptor PressureReporting() =>
        BuildActuatorCapability(
            new CapabilityId(DGLabStableIds.CapPressureReporting),
            "气压主动上报",
            Actuator("pressure", "气压", "state", 0, 1, 0, 1, "reporting", "上报"),
            CommandPolicy(CommandDelivery.Idempotent, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("stop", "{}", "pressure"));

    /// <summary>气压归零与屏幕方向能力。</summary>
    public static CapabilityDescriptor PressureZero() =>
        BuildActuatorCapability(
            new CapabilityId(DGLabStableIds.CapPressureZero),
            "气压归零与屏幕方向",
            Actuator("pressure", "气压", "value", 0, 255, 0, 255, "zero", "归零"),
            CommandPolicy(CommandDelivery.Idempotent, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("reset", "{}", "pressure"));

    /// <summary>气压遥测能力。</summary>
    public static CapabilityDescriptor PressureStatus() =>
        BuildActuatorCapability(
            new CapabilityId(DGLabStableIds.CapPressureStatus),
            "气压状态",
            Actuator("pressure", "气压", "kpa", -1000, 1000, -1000, 1000, "kpa", "气压"),
            TelemetryOnlyPolicy(),
            SafeStop("none", "{}", "pressure"));

    /// <summary>屏幕显示同步能力。</summary>
    public static CapabilityDescriptor ScreenSync() =>
        BuildActuatorCapability(
            new CapabilityId(DGLabStableIds.CapScreenSync),
            "屏幕显示同步",
            Actuator("display", "屏幕", "level", 0, 200, 0, 200, "a", "A 通道", "b", "B 通道"),
            CommandPolicy(CommandDelivery.Idempotent, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("sync.display", "{\"channelA\":0,\"channelB\":0}", "display"));

    /// <summary>纯遥测能力的命令策略：无实际命令。</summary>
    public static CapabilityCommandPolicy TelemetryOnlyPolicy() =>
        CommandPolicy(CommandDelivery.Idempotent, CommandAcknowledgement.None, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: false);

    /// <summary>电量状态能力。</summary>
    public static CapabilityDescriptor BatteryStatus() =>
        BuildActuatorCapability(
            new CapabilityId(DGLabStableIds.CapBatteryStatus),
            "电量状态",
            Actuator("battery", "电池电量", "percent", 0, 100, 0, 100, "level", "电量"),
            CommandPolicy(CommandDelivery.Idempotent, CommandAcknowledgement.None, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: false),
            SafeStop("read.battery", "{}", "battery"));

    private static CapabilityDescriptor BuildActuatorCapability(
        CapabilityId id,
        string displayName,
        CapabilityActuatorDescriptor actuator,
        CapabilityCommandPolicy policy,
        SafeStopPlanDescriptor safeStopPlan)
    {
        var schema = ObjectSchema();
        return new CapabilityDescriptor(
            id,
            displayName,
            CapabilityStability.Stable,
            [actuator],
            schema,
            schema,
            policy,
            safeStopPlan,
            schema);
    }

    private static CapabilityActuatorDescriptor Actuator(
        string actuatorId,
        string displayName,
        string unit,
        double validMin,
        double validMax,
        double safetyMin,
        double safetyMax,
        params string[] channelPairs)
    {
        var channels = new List<CapabilityChannelDescriptor>();
        for (var i = 0; i < channelPairs.Length; i += 2)
        {
            channels.Add(new CapabilityChannelDescriptor(channelPairs[i], channelPairs[i + 1]));
        }

        return new CapabilityActuatorDescriptor(
            actuatorId,
            displayName,
            unit,
            new NumericRange(validMin, validMax),
            new NumericRange(safetyMin, safetyMax),
            channels);
    }

    private static CapabilityCommandPolicy CommandPolicy(
        CommandDelivery delivery,
        CommandAcknowledgement acknowledgement,
        TimeSpan timeout,
        bool canReturnOutcomeUnknown) =>
        new(delivery, acknowledgement, null, timeout, canReturnOutcomeUnknown);

    private static SafeStopPlanDescriptor SafeStop(string operationId, string payloadJson, string actuatorId)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return new SafeStopPlanDescriptor(operationId, document.RootElement.Clone(), [actuatorId]);
    }
}