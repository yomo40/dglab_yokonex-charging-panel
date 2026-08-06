using System.Text.Json;
using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.Vendor.Yokonex.Common;

/// <summary>
/// YOKONEX 能力声明构建工具。所有文案使用中文。
/// </summary>
internal static class YokonexCapabilities
{
    private static readonly JsonDocument ObjectSchemaDocument = JsonDocument.Parse("{\"type\":\"object\"}");

    public static VersionedJsonSchema ObjectSchema() => new("1.0", ObjectSchemaDocument.RootElement.Clone());

    // ---------- 电击器 ----------

    public static IReadOnlyList<CapabilityDescriptor> EmsV16Capabilities() =>
        [EmsChannelOutput(), EmsMotorControl(), EmsStepControl(), EmsAngleControl(), EmsTelemetry()];

    public static IReadOnlyList<CapabilityDescriptor> EmsV20Capabilities() =>
        [EmsChannelOutput(), EmsMotorControl(), EmsStepControl(), EmsAngleControl(), EmsTelemetry(), EmsFrequencyList()];

    public static CapabilityDescriptor EmsChannelOutput() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapEmsChannelOutput),
            "电击通道输出",
            Actuator("output", "电击输出", "level", 0, 276, 0, 276, "a", "A 通道", "b", "B 通道"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("close.output", "{\"channel\":\"AB\",\"on\":false}", "output"));

    public static CapabilityDescriptor EmsMotorControl() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapEmsMotorControl),
            "电击器马达控制",
            Actuator("motor", "震动马达", "level", 0, 1, 0, 1, "state", "开关/预设"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("motor.off", "{\"state\":\"off\"}", "motor"));

    public static CapabilityDescriptor EmsStepControl() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapEmsStepControl),
            "计步器控制",
            Actuator("step", "计步器", "level", 0, 1, 0, 1, "command", "开关/清零/暂停/恢复"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("step.off", "{\"command\":\"off\"}", "step"));

    public static CapabilityDescriptor EmsAngleControl() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapEmsAngleControl),
            "角度传感器控制",
            Actuator("angle", "角度传感器", "level", 0, 1, 0, 1, "command", "开关"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("angle.off", "{\"command\":\"off\"}", "angle"));

    public static CapabilityDescriptor EmsTelemetry() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapEmsTelemetry),
            "电击器状态查询与遥测",
            Actuator("sensor", "状态传感器", "level", 0, 1, 0, 1, "query", "查询"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("query.none", "{}", "sensor"));

    public static CapabilityDescriptor EmsFrequencyList() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapEmsFrequencyList),
            "频率列表模式（二代）",
            Actuator("output", "电击输出", "level", 0, 276, 0, 276, "a", "A 通道", "b", "B 通道"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("close.output", "{\"channel\":\"AB\",\"on\":false}", "output"));

    // ---------- 灌肠机 ----------

    public static IReadOnlyList<CapabilityDescriptor> EnemaV10Capabilities() =>
        [EnemaPumpControl(), EnemaTelemetry()];

    public static CapabilityDescriptor EnemaPumpControl() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapEnemaPumpControl),
            "灌肠机双泵控制",
            Actuator("pump", "水泵", "level", 0, 1, 0, 1, "peristaltic", "蠕动泵", "suction", "抽水泵"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("pause.work", "{\"pump\":\"all\",\"state\":\"stop\"}", "pump"));

    public static CapabilityDescriptor EnemaTelemetry() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapEnemaTelemetry),
            "灌肠机状态查询与遥测",
            Actuator("sensor", "状态传感器", "level", 0, 1, 0, 1, "query", "查询"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("query.none", "{}", "sensor"));

    // ---------- 跳蛋/飞机杯 ----------

    public static IReadOnlyList<CapabilityDescriptor> ToyV11Capabilities() =>
        [ToyMotorControl(), ToySpeedControl(), ToyTelemetry()];

    public static CapabilityDescriptor ToyMotorControl() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapToyMotorControl),
            "玩具固定模式控制",
            Actuator("motor", "马达", "mode", 0, 20, 0, 20, "motor-a", "A 马达", "motor-b", "B 马达", "motor-c", "C 马达"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("motor.stop", "{\"motors\":\"none\",\"mode\":0}", "motor"));

    public static CapabilityDescriptor ToySpeedControl() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapToySpeedControl),
            "玩具实时力度控制",
            Actuator("motor", "马达", "level", 0, 20, 0, 20, "motor-a", "A 马达", "motor-b", "B 马达", "motor-c", "C 马达"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("speed.zero", "{\"a\":0,\"b\":0,\"c\":0}", "motor"));

    public static CapabilityDescriptor ToyTelemetry() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapToyTelemetry),
            "玩具设备信息与电量遥测",
            Actuator("sensor", "状态传感器", "level", 0, 1, 0, 1, "info", "设备信息"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("query.none", "{}", "sensor"));

    // ---------- IM 目标 ----------

    public static IReadOnlyList<CapabilityDescriptor> ImTargetCapabilities() =>
        [ImCommandSend(), ImGlobalStop(), ImCommandCustom(), ImMetadataUpdate(), ImGameInfoLegacy()];

    public static CapabilityDescriptor ImCommandSend() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapImCommandSend),
            "IM 指令发送（game_cmd）",
            Actuator("target", "IM 目标", "level", 0, 1, 0, 1, "command", "指令"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("stop.all", "{\"commandId\":\"_stop_all\"}", "target"));

    public static CapabilityDescriptor ImGlobalStop() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapImGlobalStop),
            "IM 全局停止指令（开发中）",
            Actuator("target", "IM 目标", "level", 0, 1, 0, 1, "command", "指令"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("stop.all", "{\"commandId\":\"_stop_all\"}", "target"),
            CapabilityStability.Experimental);

    public static CapabilityDescriptor ImCommandCustom() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapImCommandCustom),
            "IM 自定义指令参数（开发中）",
            Actuator("target", "IM 目标", "level", 0, 1, 0, 1, "command", "指令"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("stop.all", "{\"commandId\":\"_stop_all\"}", "target"),
            CapabilityStability.Experimental);

    public static CapabilityDescriptor ImMetadataUpdate() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapImMetadataUpdate),
            "IM 游戏事件定义更新（暂缓开发）",
            Actuator("target", "IM 目标", "level", 0, 1, 0, 1, "metadata", "元数据"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("stop.all", "{\"commandId\":\"_stop_all\"}", "target"),
            CapabilityStability.Experimental);

    public static CapabilityDescriptor ImGameInfoLegacy() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapImGameInfoLegacy),
            "IM 旧版 game_info 兼容（即将废弃）",
            Actuator("target", "IM 目标", "level", 0, 1, 0, 1, "data", "指令编号"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("stop.all", "{\"commandId\":\"_stop_all\"}", "target"),
            CapabilityStability.Experimental);

    // ---------- API Bridge ----------

    public static IReadOnlyList<CapabilityDescriptor> ApiBridgeCapabilities() =>
        [BridgeCommandSend(), BridgeLogin(), BridgeReinit(), BridgeTelemetry()];

    public static CapabilityDescriptor BridgeCommandSend() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapBridgeCommandSend),
            "API Bridge 指令发送",
            Actuator("target", "桥接目标", "level", 0, 1, 0, 1, "command", "指令"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("stop.all", "{\"commandId\":\"_stop_all\"}", "target"));

    public static CapabilityDescriptor BridgeLogin() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapBridgeLogin),
            "API Bridge 凭证登录",
            Actuator("target", "桥接目标", "level", 0, 1, 0, 1, "login", "登录"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("stop.all", "{\"commandId\":\"_stop_all\"}", "target"));

    public static CapabilityDescriptor BridgeReinit() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapBridgeReinit),
            "API Bridge 重新初始化",
            Actuator("target", "桥接目标", "level", 0, 1, 0, 1, "reinit", "重初始化"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("stop.all", "{\"commandId\":\"_stop_all\"}", "target"));

    public static CapabilityDescriptor BridgeTelemetry() =>
        BuildActuatorCapability(
            new CapabilityId(YokonexStableIds.CapBridgeTelemetry),
            "API Bridge 状态与消息推送",
            Actuator("sensor", "桥接状态", "level", 0, 1, 0, 1, "status", "状态"),
            CommandPolicy(CommandDelivery.AtMostOnce, CommandAcknowledgement.TransportAcknowledgement, TimeSpan.FromSeconds(2), canReturnOutcomeUnknown: true),
            SafeStop("query.none", "{}", "sensor"));

    // ---------- 构建工具 ----------

    private static CapabilityDescriptor BuildActuatorCapability(
        CapabilityId id,
        string displayName,
        CapabilityActuatorDescriptor actuator,
        CapabilityCommandPolicy policy,
        SafeStopPlanDescriptor safeStopPlan,
        CapabilityStability stability = CapabilityStability.Stable)
    {
        var schema = ObjectSchema();
        return new CapabilityDescriptor(
            id,
            displayName,
            stability,
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

    private static SafeStopPlanDescriptor SafeStop(string operationId, string payloadJson, params string[] actuatorIds)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return new SafeStopPlanDescriptor(operationId, document.RootElement.Clone(), actuatorIds);
    }
}
