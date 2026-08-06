using System.Reflection;
using System.Text.Json;
using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Catalog;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Telemetry;
using ChargingPanel.Contracts.Transport;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Contracts;

public sealed class ContractValidationTests
{
    // 捕获合法小写点分段稳定 ID 无法往返的问题。
    [Theory]
    [InlineData("dglab.coyote.v3")]
    [InlineData("yokonex.enema.gen1")]
    [InlineData("dglab.pawprints.v1_1")]
    public void ModelId_ValidStableId_RoundTrips(string value)
    {
        Assert.Equal(value, ModelId.Parse(value).Value);
    }

    // 捕获空段、非 ASCII 字符或路径字符进入跨程序集稳定 ID 的问题。
    [Theory]
    [InlineData("")]
    [InlineData("dglab..v3")]
    [InlineData(".dglab")]
    [InlineData("dglab/v3")]
    [InlineData("dglab.设备")]
    public void StableStringId_InvalidFormat_IsRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => CapabilityId.Parse(value));
    }

    // 捕获大小写或边缘空白被静默规范化，导致签名/持久化输入与实际 ID 不同的问题。
    [Theory]
    [InlineData("DGLAB.coyote.v3")]
    [InlineData(" dglab.coyote.v3")]
    [InlineData("dglab.coyote.v3 ")]
    public void StableStringId_NonCanonicalForm_IsRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => ModelId.Parse(value));
    }

    // 捕获实例 ID 与 Endpoint ID 退化为可互换裸字符串的问题。
    [Fact]
    public void HostGeneratedIdentifiers_GuidValues_RoundTripThroughStrongTypes()
    {
        var deviceValue = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var endpointValue = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var deviceId = new DeviceInstanceId(deviceValue);
        var endpointId = new EndpointId(endpointValue);

        Assert.Equal(deviceValue, deviceId.Value);
        Assert.Equal(endpointValue, endpointId.Value);
        Assert.NotEqual(deviceId.ToString(), endpointId.ToString());
    }

    // 捕获 default 结构体绕过构造校验并以 null/Guid.Empty 静默进入持久化键的问题。
    [Fact]
    public void DeviceIdentifiers_DefaultValue_CannotBeReadSilently()
    {
        Assert.Throws<InvalidOperationException>(() => _ = default(PluginId).Value);
        Assert.Throws<InvalidOperationException>(() => _ = default(VendorId).Value);
        Assert.Throws<InvalidOperationException>(() => _ = default(ModelId).Value);
        Assert.Throws<InvalidOperationException>(() => _ = default(ProtocolProfileId).Value);
        Assert.Throws<InvalidOperationException>(() => _ = default(CapabilityId).Value);
        Assert.Throws<InvalidOperationException>(() => _ = default(CommandId).Value);
        Assert.Throws<InvalidOperationException>(() => _ = default(DeviceInstanceId).Value);
        Assert.Throws<InvalidOperationException>(() => _ = default(EndpointId).Value);
    }

    // 捕获关键 DTO 接收 default 强类型 ID 后把无效标识传播到插件/调度/遥测边界的问题。
    [Fact]
    public void ContractDtos_DefaultIdentifiers_AreRejectedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new VendorDescriptor(default, "DG-LAB"));
        Assert.Throws<ArgumentException>(() => new ProtocolProfileDescriptor(
            default,
            ModelId.Parse("dglab.coyote.v3"),
            "爪印 V1.1",
            [TransportKind.BluetoothLowEnergy],
            [CreateCapability("intensity.output", "actuator-a")]));
        Assert.Throws<ArgumentException>(() => new PluginManifest(
            default,
            "测试插件",
            new Version(1, 0),
            new HostApiVersionRange(new Version(1, 0), new Version(1, 0))));
        Assert.Throws<ArgumentException>(() => new EndpointLeaseRequest(
            default,
            ProtocolProfileId.Parse("dglab.pawprints.v1_1")));
        Assert.Throws<ArgumentException>(() => new DeviceTelemetryEnvelope(
            default,
            ProtocolProfileId.Parse("dglab.pawprints.v1_1"),
            CapabilityId.Parse("battery.telemetry"),
            "1.0",
            Json("{\"battery\":80}"),
            DateTimeOffset.UnixEpoch,
            0,
            null,
            TelemetryQuality.Good));
    }

    // 捕获目录引入未冻结的第四种可见性，令创建/诊断语义变得含糊的问题。
    [Fact]
    public void DeviceAvailability_PublicContract_HasOnlyFrozenValues()
    {
        Assert.Equal(
            [DeviceAvailability.Public, DeviceAvailability.HiddenReserved, DeviceAvailability.UpgradeDiagnostic],
            Enum.GetValues<DeviceAvailability>());
    }

    // 捕获截止时刻已到仍被调度的边界错误。
    [Fact]
    public void DeviceCommandEnvelope_ExpiredAtCurrentTime_IsNotDispatchable()
    {
        var now = DateTimeOffset.Parse("2026-08-01T10:00:00+08:00");
        var command = CreateCommand(expiresAt: now);

        Assert.True(command.IsExpired(new FixedTimeProvider(now)));
    }

    // 捕获无截止时间的命令被误判为过期的问题。
    [Fact]
    public void DeviceCommandEnvelope_WithoutExpiry_RemainsDispatchable()
    {
        var command = CreateCommand(expiresAt: null);

        Assert.False(command.IsExpired(new FixedTimeProvider(DateTimeOffset.MaxValue)));
    }

    // 捕获命令信封借用 JsonDocument 所有权，文档释放后调度读取 payload 崩溃的问题。
    [Fact]
    public void DeviceCommandEnvelope_ConstructedFromDocument_OwnsPayload()
    {
        DeviceCommandEnvelope command;
        using (var document = JsonDocument.Parse("{\"value\":10}"))
        {
            command = CreateCommand(document.RootElement);
        }

        Assert.Equal(10, command.Payload.GetProperty("value").GetInt32());
    }

    // 捕获位置 record 的 init/with 在构造后把信封改成空操作或未定义 payload 的问题。
    [Fact]
    public void DeviceCommandEnvelope_PublicShape_HasFixedConstructorAndElevenGetOnlyProperties()
    {
        var expectedProperties = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["CommandId"] = typeof(CommandId),
            ["DeviceInstanceId"] = typeof(DeviceInstanceId),
            ["CapabilityId"] = typeof(CapabilityId),
            ["OperationId"] = typeof(string),
            ["Payload"] = typeof(JsonElement),
            ["Source"] = typeof(CommandSource),
            ["Delivery"] = typeof(CommandDelivery),
            ["CreatedAt"] = typeof(DateTimeOffset),
            ["ExpiresAt"] = typeof(DateTimeOffset?),
            ["SafetyEpoch"] = typeof(long),
            ["SessionEpoch"] = typeof(long)
        };
        var properties = typeof(DeviceCommandEnvelope)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        var constructor = typeof(DeviceCommandEnvelope).GetConstructor(
            [
                typeof(CommandId),
                typeof(DeviceInstanceId),
                typeof(CapabilityId),
                typeof(string),
                typeof(JsonElement),
                typeof(CommandSource),
                typeof(CommandDelivery),
                typeof(DateTimeOffset),
                typeof(DateTimeOffset?),
                typeof(long),
                typeof(long)
            ]);

        Assert.NotNull(constructor);
        Assert.Equal(expectedProperties.Count, properties.Length);
        Assert.All(properties, property =>
        {
            Assert.True(expectedProperties.TryGetValue(property.Name, out var expectedType));
            Assert.Equal(expectedType, property.PropertyType);
            Assert.Null(property.SetMethod);
        });
    }

    // 捕获空操作、空来源、无效 payload 或 default ID 在命令边界绕过构造校验的问题。
    [Fact]
    public void DeviceCommandEnvelope_InvalidRequiredState_IsRejected()
    {
        var commandId = new CommandId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var deviceInstanceId = new DeviceInstanceId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var capabilityId = CapabilityId.Parse("intensity.output");
        var source = new CommandSource(CommandSourceKind.UserInterface, "local-user");

        DeviceCommandEnvelope Construct(
            CommandId selectedCommandId,
            DeviceInstanceId selectedDeviceInstanceId,
            CapabilityId selectedCapabilityId,
            string operationId,
            JsonElement payload,
            CommandSource selectedSource) => new(
                selectedCommandId,
                selectedDeviceInstanceId,
                selectedCapabilityId,
                operationId,
                payload,
                selectedSource,
                CommandDelivery.Idempotent,
                DateTimeOffset.UnixEpoch,
                null,
                0,
                0);

        Assert.Throws<ArgumentException>(() =>
            Construct(commandId, deviceInstanceId, capabilityId, "", Json("{}"), source));
        Assert.Throws<ArgumentNullException>(() =>
            Construct(commandId, deviceInstanceId, capabilityId, "set", Json("{}"), null!));
        Assert.Throws<ArgumentException>(() =>
            Construct(commandId, deviceInstanceId, capabilityId, "set", default, source));
        Assert.Throws<ArgumentException>(() =>
            Construct(commandId, deviceInstanceId, capabilityId, "set", Json("null"), source));
        Assert.Throws<ArgumentException>(() =>
            Construct(default, deviceInstanceId, capabilityId, "set", Json("{}"), source));
        Assert.Throws<ArgumentException>(() =>
            Construct(commandId, default, capabilityId, "set", Json("{}"), source));
        Assert.Throws<ArgumentException>(() =>
            Construct(commandId, deviceInstanceId, default, "set", Json("{}"), source));
    }

    // 捕获 OutcomeUnknown 后盲目重放非幂等或流式命令的问题。
    [Theory]
    [InlineData(CommandDelivery.AtMostOnce, false)]
    [InlineData(CommandDelivery.Idempotent, true)]
    [InlineData(CommandDelivery.StreamingFrame, false)]
    public void CommandDelivery_OutcomeUnknown_AllowsRetryOnlyForIdempotent(
        CommandDelivery delivery,
        bool expected)
    {
        Assert.Equal(expected, delivery.CanRetryAfterOutcomeUnknown());
    }

    // 捕获传输已接受与六种终态中的任一状态被合并或删除的问题。
    [Fact]
    public void DeviceCommandStatus_PublicContract_RepresentsEveryRequiredOutcome()
    {
        Assert.Equal(
            [
                DeviceCommandStatus.AcceptedByTransport,
                DeviceCommandStatus.Executed,
                DeviceCommandStatus.Rejected,
                DeviceCommandStatus.Expired,
                DeviceCommandStatus.Superseded,
                DeviceCommandStatus.TransportFailed,
                DeviceCommandStatus.OutcomeUnknown
            ],
            Enum.GetValues<DeviceCommandStatus>());
    }

    // 捕获任意公开 DTO 接受未定义 enum 数值并把未知语义传播到调度/传输边界的问题。
    [Fact]
    public void PublicContractConstructors_UndefinedEnumValues_AreRejected()
    {
        var capability = CreateCapability("intensity.output", "actuator-a");
        var command = CreateCommand(Json("{}"));
        var endpointId = new EndpointId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var profileId = ProtocolProfileId.Parse("dglab.pawprints.v1_1");
        var payload = EndpointPayload.FromJson(Json("{}"));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CommandSource((CommandSourceKind)999, "local-user"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceModelDescriptor(
            ModelId.Parse("dglab.coyote.v3"),
            VendorId.Parse("dglab"),
            "郊狼 3.0",
            (DeviceAvailability)999,
            [profileId]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProtocolProfileDescriptor(
            profileId,
            ModelId.Parse("dglab.coyote.v3"),
            "爪印 V1.1",
            [(TransportKind)999],
            [capability]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiscoveryRuleDescriptor(
            "dglab.scan",
            profileId,
            (TransportKind)999,
            new VersionedJsonSchema("1.0", Json("{}"))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapabilityCommandPolicy(
            (CommandDelivery)999,
            CommandAcknowledgement.DeviceAcknowledgement,
            10,
            TimeSpan.FromSeconds(2),
            true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapabilityCommandPolicy(
            CommandDelivery.Idempotent,
            (CommandAcknowledgement)999,
            10,
            TimeSpan.FromSeconds(2),
            true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapabilityDescriptor(
            capability.Id,
            capability.DisplayName,
            (CapabilityStability)999,
            capability.Actuators,
            capability.CommandSchema,
            capability.TelemetrySchema,
            capability.CommandPolicy,
            capability.SafeStopPlan,
            capability.UiSchema));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceCommandEnvelope(
            command.CommandId,
            command.DeviceInstanceId,
            command.CapabilityId,
            command.OperationId,
            command.Payload,
            command.Source,
            (CommandDelivery)999,
            command.CreatedAt,
            command.ExpiresAt,
            command.SafetyEpoch,
            command.SessionEpoch));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DeviceCommandResult(command.CommandId, (DeviceCommandStatus)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SafeStopResult((SafeStopStatus)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceTelemetryEnvelope(
            command.DeviceInstanceId,
            profileId,
            CapabilityId.Parse("battery.telemetry"),
            "1.0",
            Json("{}"),
            DateTimeOffset.UnixEpoch,
            0,
            null,
            (TelemetryQuality)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectionEndpointDescriptor(
            endpointId,
            profileId,
            (TransportKind)999,
            "测试端点",
            new Dictionary<string, string>()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EndpointRequest("write", payload, (EndpointResponseMode)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EndpointSendResult((EndpointOperationStatus)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EndpointReadResult((EndpointOperationStatus)999));
    }

    // 捕获负 Safety/SessionEpoch 或序列号绕过新旧会话与去重边界的问题。
    [Fact]
    public void EpochAndSequenceContracts_NegativeValues_AreRejected()
    {
        var command = CreateCommand(Json("{}"));
        var profileId = ProtocolProfileId.Parse("dglab.pawprints.v1_1");
        var capabilityId = CapabilityId.Parse("battery.telemetry");

        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceCommandEnvelope(
            command.CommandId,
            command.DeviceInstanceId,
            command.CapabilityId,
            command.OperationId,
            command.Payload,
            command.Source,
            command.Delivery,
            command.CreatedAt,
            command.ExpiresAt,
            -1,
            0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceCommandEnvelope(
            command.CommandId,
            command.DeviceInstanceId,
            command.CapabilityId,
            command.OperationId,
            command.Payload,
            command.Source,
            command.Delivery,
            command.CreatedAt,
            command.ExpiresAt,
            0,
            -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SafeStopContext(command.DeviceInstanceId, -1, 0, DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceTelemetryEnvelope(
            command.DeviceInstanceId,
            profileId,
            capabilityId,
            "1.0",
            Json("{}"),
            DateTimeOffset.UnixEpoch,
            -1,
            null,
            TelemetryQuality.Good));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceTelemetryEnvelope(
            command.DeviceInstanceId,
            profileId,
            capabilityId,
            "1.0",
            Json("{}"),
            DateTimeOffset.UnixEpoch,
            0,
            -1,
            TelemetryQuality.Good));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EndpointMessage("notify", EndpointPayload.FromJson(Json("{}")), DateTimeOffset.UnixEpoch, -1));
    }

    // 捕获成功结果携带错误或失败结果没有稳定诊断，令 Dispatcher 无法安全决策的问题。
    [Fact]
    public void DeviceCommandResult_ContradictoryStatusAndDiagnostic_IsRejected()
    {
        var commandId = new CommandId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        Assert.Throws<ArgumentException>(() =>
            new DeviceCommandResult(commandId, DeviceCommandStatus.Executed, "device.failed"));
        Assert.Throws<ArgumentException>(() =>
            new DeviceCommandResult(commandId, DeviceCommandStatus.Rejected));
        Assert.Throws<ArgumentException>(() =>
            new DeviceCommandResult(commandId, DeviceCommandStatus.TransportFailed, "Transport.Failed"));

        var rejected = new DeviceCommandResult(
            commandId,
            DeviceCommandStatus.Rejected,
            "command.rejected");
        Assert.Equal("command.rejected", rejected.ReasonCode);
    }

    // 捕获安全停止成功携带错误或失败没有诊断，及三态集合发生漂移的问题。
    [Fact]
    public void SafeStopResult_StatusAndDiagnostic_AreConsistent()
    {
        Assert.Equal(
            [SafeStopStatus.ConfirmedStopped, SafeStopStatus.Failed, SafeStopStatus.OutcomeUnknown],
            Enum.GetValues<SafeStopStatus>());
        Assert.Throws<ArgumentException>(() =>
            new SafeStopResult(SafeStopStatus.ConfirmedStopped, "stop.failed"));
        Assert.Throws<ArgumentException>(() => new SafeStopResult(SafeStopStatus.Failed));

        var failed = new SafeStopResult(SafeStopStatus.Failed, "stop.failed");
        Assert.Equal("stop.failed", failed.ReasonCode);
    }

    // 捕获 Schema 版本丢失或 JsonDocument 释放后合同持有悬空 JsonElement 的问题。
    [Fact]
    public void VersionedJsonSchema_ConstructedFromDocument_ClonesSchemaAndKeepsVersion()
    {
        VersionedJsonSchema schema;
        using (var document = JsonDocument.Parse("{\"type\":\"object\"}"))
        {
            schema = new VersionedJsonSchema("1.0", document.RootElement);
        }

        Assert.Equal("1.0", schema.Version);
        Assert.Equal("object", schema.Schema.GetProperty("type").GetString());
    }

    // 捕获 null、标量或数组伪装成 JSON Schema，令下游校验器收到错误根类型的问题。
    [Fact]
    public void VersionedJsonSchema_NonObjectSchema_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new VersionedJsonSchema("1.0", default));
        Assert.Throws<ArgumentException>(() => new VersionedJsonSchema("1.0", Json("null")));
        Assert.Throws<ArgumentException>(() => new VersionedJsonSchema("1.0", Json("42")));
        Assert.Throws<ArgumentException>(() => new VersionedJsonSchema("1.0", Json("[]")));
        Assert.Throws<ArgumentException>(() => new VersionedJsonSchema("1.0", Json("\"schema\"")));
    }

    // 捕获同一协议 Profile 内重复 CapabilityId 造成命令路由不确定的问题。
    [Fact]
    public void ProtocolProfile_DuplicateCapabilityId_IsRejected()
    {
        var first = CreateCapability("intensity.output", "actuator-a");
        var duplicate = CreateCapability("intensity.output", "actuator-b");

        Assert.Throws<ArgumentException>(() => new ProtocolProfileDescriptor(
            ProtocolProfileId.Parse("dglab.pawprints.v1_1"),
            ModelId.Parse("dglab.coyote.v3"),
            "爪印 V1.1",
            [TransportKind.BluetoothLowEnergy],
            [first, duplicate]));
    }

    // 捕获把标准 CapabilityId 错误地提升为插件全局唯一而拒绝合法复用的问题。
    [Fact]
    public void PluginContribution_SameCapabilityAcrossProfiles_IsAllowed()
    {
        var firstProfile = CreateProfile("dglab.pawprints.v1_1", "dglab.coyote.v3");
        var secondProfile = CreateProfile("yokonex.im.v1", "yokonex.enema.gen1");

        var contribution = new DevicePluginContribution(
            [new VendorDescriptor(VendorId.Parse("dglab"), "DG-LAB")],
            [],
            [firstProfile, secondProfile],
            [],
            []);

        Assert.Equal(2, contribution.ProtocolProfiles.Count);
        Assert.All(
            contribution.ProtocolProfiles,
            profile => Assert.Equal("intensity.output", Assert.Single(profile.Capabilities).Id.Value));
    }

    // 捕获调用方在 Describe 后修改源列表、静默改变插件贡献的问题。
    [Fact]
    public void PluginContribution_SourceCollectionMutated_RemainsUnchanged()
    {
        var vendors = new List<VendorDescriptor>
        {
            new(VendorId.Parse("dglab"), "DG-LAB")
        };
        var contribution = new DevicePluginContribution(vendors, [], [], [], []);

        vendors.Clear();

        Assert.Single(contribution.Vendors);
    }

    // 捕获第三方 Factory 通过属性返回 default ProfileId，绕过宿主贡献验证的问题。
    [Fact]
    public void PluginContribution_FactoryWithDefaultProfileId_IsRejectedAsInvalidContract()
    {
        Assert.Throws<ArgumentException>(() => new DevicePluginContribution(
            [],
            [],
            [],
            [],
            [new DefaultProfileDriverFactory()]));
    }

    // 捕获 SafeStopPlan 未覆盖能力中的全部执行器，导致部分物理输出无法停止的问题。
    [Fact]
    public void CapabilityDescriptor_SafeStopMissesActuator_IsRejected()
    {
        var actuator = CreateActuator("actuator-a");
        var plan = new SafeStopPlanDescriptor("stop", Json("{}"), []);

        Assert.Throws<ArgumentException>(() => CreateCapability(
            "intensity.output",
            [actuator],
            plan));
    }

    // 捕获能力声明没有任何执行器/目标却仍被目录视为可执行能力的问题。
    [Fact]
    public void CapabilityDescriptor_EmptyActuatorCollection_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => CreateCapability(
            "intensity.output",
            [],
            new SafeStopPlanDescriptor("stop", Json("{}"), [])));
    }

    // 捕获执行器没有通道/target，导致命令无法映射到具体输出的问题。
    [Fact]
    public void CapabilityActuatorDescriptor_EmptyChannelCollection_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new CapabilityActuatorDescriptor(
            "actuator-a",
            "执行器 A",
            "percent",
            new NumericRange(0, 100),
            new NumericRange(0, 30),
            []));
    }

    // 捕获通道 token 因大小写或边缘空白产生 Ordinal 唯一性漂移的问题。
    [Fact]
    public void CapabilityChannelDescriptor_NonCanonicalToken_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new CapabilityChannelDescriptor(" Channel-A ", "通道 A"));
    }

    // 捕获执行器 token 与 SafeStop 覆盖列表使用不同规范的问题。
    [Fact]
    public void CapabilityActuatorDescriptor_NonCanonicalToken_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new CapabilityActuatorDescriptor(
            "Actuator-A",
            "执行器 A",
            "percent",
            new NumericRange(0, 100),
            new NumericRange(0, 30),
            [new CapabilityChannelDescriptor("channel-a", "通道 A")]));
    }

    // 捕获安全停止 operation 与普通命令 operation 采用不同 token 规则的问题。
    [Fact]
    public void SafeStopPlan_NonCanonicalOperation_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new SafeStopPlanDescriptor(" Stop ", Json("{}"), ["actuator-a"]));
    }

    // 捕获发现规则 ID 无法作为稳定 Ordinal 键的问题。
    [Fact]
    public void DiscoveryRuleDescriptor_NonCanonicalRuleId_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new DiscoveryRuleDescriptor(
            "DGLAB.Scan",
            ProtocolProfileId.Parse("dglab.pawprints.v1_1"),
            TransportKind.BluetoothLowEnergy,
            new VersionedJsonSchema("1.0", Json("{}"))));
    }

    // 捕获命令 OperationId 接受大小写/空白变体而绕过能力 Schema 路由的问题。
    [Fact]
    public void DeviceCommandEnvelope_NonCanonicalOperation_IsRejected()
    {
        var command = CreateCommand(Json("{}"));

        Assert.Throws<ArgumentException>(() => new DeviceCommandEnvelope(
            command.CommandId,
            command.DeviceInstanceId,
            command.CapabilityId,
            " Set ",
            command.Payload,
            command.Source,
            command.Delivery,
            command.CreatedAt,
            command.ExpiresAt,
            command.SafetyEpoch,
            command.SessionEpoch));
    }

    // 捕获 Host API 兼容范围上下界倒置或边界被错误排除的问题。
    [Fact]
    public void HostApiVersionRange_InclusiveRange_ValidatesAndMatchesBoundaries()
    {
        var range = new HostApiVersionRange(new Version(1, 0), new Version(2, 0));

        Assert.True(range.Supports(new Version(1, 0)));
        Assert.True(range.Supports(new Version(2, 0)));
        Assert.False(range.Supports(new Version(2, 1)));
        Assert.Throws<ArgumentException>(() =>
            new HostApiVersionRange(new Version(2, 0), new Version(1, 0)));
    }

    // 捕获 API review 后宿主版本未固化为 1.0 的问题。
    [Fact]
    public void HostApi_CurrentVersion_IsFrozenAtVersionOne()
    {
        Assert.Equal(new Version(1, 0), HostApi.CurrentVersion);
    }

    // 捕获插件上下文新增根容器、数据库或平台对象，扩大第三方插件权限的问题。
    [Fact]
    public void PluginContext_PublicSurface_ContainsOnlyFourNarrowProxies()
    {
        var properties = typeof(IPluginContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => (property.Name, property.PropertyType))
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                ("Endpoints", typeof(IEndpointLeaseProvider)),
                ("Logger", typeof(IPluginLogger)),
                ("Storage", typeof(IPluginStorage)),
                ("TimeProvider", typeof(TimeProvider))
            ],
            properties);
        Assert.DoesNotContain(typeof(IPluginContext).GetMethods(), method => !method.IsSpecialName);
    }

    // 捕获 Contracts 通过公开签名或程序集引用泄漏 Avalonia、DI、WinRT、SQLite 或厂商实现的问题。
    [Fact]
    public void ContractsAssembly_PublicApiAndReferences_AreBclOnly()
    {
        var assembly = typeof(PluginId).Assembly;
        var forbiddenFragments = new[]
        {
            "Avalonia",
            "DependencyInjection",
            "WinRT",
            "SQLite",
            "ChargingPanel.Core",
            "ChargingPanel.Desktop",
            "ChargingPanel.Vendor"
        };
        var publicSignatureTypeNames = assembly.ExportedTypes
            .SelectMany(GetPublicSignatureTypes)
            .Select(type => type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
        var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty);

        Assert.DoesNotContain(
            publicSignatureTypeNames.Concat(references),
            value => forbiddenFragments.Any(fragment =>
                value.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    // 捕获遥测 Schema 版本、会话纪元或质量信息在跨插件边界时丢失的问题。
    [Fact]
    public void DeviceTelemetryEnvelope_VersionedPayload_PreservesRoutingAndQuality()
    {
        DeviceTelemetryEnvelope telemetry;
        using (var document = JsonDocument.Parse("{\"battery\":80}"))
        {
            telemetry = new DeviceTelemetryEnvelope(
                new DeviceInstanceId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                ProtocolProfileId.Parse("dglab.pawprints.v1_1"),
                CapabilityId.Parse("battery.telemetry"),
                "1.0",
                document.RootElement,
                DateTimeOffset.Parse("2026-08-01T10:00:00+08:00"),
                7,
                42,
                TelemetryQuality.Good);
        }

        Assert.Equal(7, telemetry.SessionEpoch);
        Assert.Equal(42, telemetry.SequenceNumber);
        Assert.Equal(TelemetryQuality.Good, telemetry.Quality);
        Assert.Equal(80, telemetry.Payload.GetProperty("battery").GetInt32());
    }

    // 捕获 Endpoint payload 在 JsonDocument 释放后失效或混淆二进制/JSON 两种载荷的问题。
    [Fact]
    public void EndpointPayload_JsonAndBinary_AreTransportNeutralAndOwned()
    {
        EndpointPayload jsonPayload;
        using (var document = JsonDocument.Parse("{\"action\":\"stop\"}"))
        {
            jsonPayload = EndpointPayload.FromJson(document.RootElement);
        }

        var bytes = new byte[] { 1, 2, 3 };
        var binaryPayload = EndpointPayload.FromBinary(bytes);
        bytes[0] = 9;

        Assert.Equal(EndpointPayloadKind.Json, jsonPayload.Kind);
        Assert.Equal("stop", jsonPayload.Json!.Value.GetProperty("action").GetString());
        Assert.Equal(EndpointPayloadKind.Binary, binaryPayload.Kind);
        Assert.Equal(new byte[] { 1, 2, 3 }, binaryPayload.Binary.ToArray());
    }

    // 捕获空路由、null payload 或 JSON null 进入一次 I/O 边界的问题。
    [Fact]
    public void EndpointContracts_EmptyRouteOrPayload_IsRejected()
    {
        var payload = EndpointPayload.FromJson(Json("{}"));

        Assert.Throws<ArgumentException>(() => EndpointPayload.FromJson(Json("null")));
        Assert.Throws<ArgumentException>(() => new EndpointRequest("", payload, EndpointResponseMode.WithResponse));
        Assert.Throws<ArgumentNullException>(() =>
            new EndpointRequest("write", null!, EndpointResponseMode.WithResponse));
        Assert.Throws<ArgumentException>(() => new EndpointReadRequest(" "));
        Assert.Throws<ArgumentException>(() =>
            new EndpointMessage("", payload, DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentNullException>(() =>
            new EndpointMessage("notify", null!, DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() =>
            new SafeStopPlanDescriptor("stop", Json("null"), ["actuator-a"]));
    }

    // 捕获 Endpoint 成功结果携带错误或失败结果不带 ErrorCode 的矛盾状态。
    [Fact]
    public void EndpointOperationResult_ContradictoryStatusAndError_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new EndpointSendResult(EndpointOperationStatus.Completed, null, "transport.failed"));
        Assert.Throws<ArgumentException>(() =>
            new EndpointSendResult(EndpointOperationStatus.Failed));
        Assert.Throws<ArgumentException>(() =>
            new EndpointReadResult(EndpointOperationStatus.Completed, null, "transport.failed"));
        Assert.Throws<ArgumentException>(() =>
            new EndpointReadResult(EndpointOperationStatus.OutcomeUnknown));
        Assert.Throws<ArgumentException>(() =>
            new EndpointReadResult(EndpointOperationStatus.Failed, null, "Transport.Failed"));

        var failed = new EndpointSendResult(
            EndpointOperationStatus.Failed,
            null,
            "transport.failed");
        Assert.Equal("transport.failed", failed.ErrorCode);
    }

    // 捕获 Driver 核心接口被直接增员或签名漂移，破坏已发布插件二进制兼容的问题。
    [Fact]
    public void ProtocolDriver_PublicSurface_MatchesFrozenV1Contract()
    {
        var properties = typeof(IProtocolDriver)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(property => (property.Name, property.PropertyType))
            .ToArray();
        var methods = typeof(IProtocolDriver)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([("ProfileId", typeof(ProtocolProfileId))], properties);
        Assert.Equal(["ExecuteAsync", "OpenAsync", "ReadTelemetryAsync", "SafeStopAsync"], methods);
        AssertMethod(
            typeof(IProtocolDriver),
            "OpenAsync",
            typeof(ValueTask),
            typeof(IEndpointLease),
            typeof(CancellationToken));
        AssertMethod(
            typeof(IProtocolDriver),
            "ExecuteAsync",
            typeof(ValueTask<DeviceCommandResult>),
            typeof(DeviceCommandEnvelope),
            typeof(CancellationToken));
        AssertMethod(
            typeof(IProtocolDriver),
            "SafeStopAsync",
            typeof(ValueTask<SafeStopResult>),
            typeof(SafeStopContext),
            typeof(CancellationToken));
        AssertMethod(
            typeof(IProtocolDriver),
            "ReadTelemetryAsync",
            typeof(IAsyncEnumerable<DeviceTelemetryEnvelope>),
            typeof(CancellationToken));
        Assert.Contains(typeof(IAsyncDisposable), typeof(IProtocolDriver).GetInterfaces());
    }

    // 捕获插件入口或工厂被直接增员、改为异步构造或绕过 IPluginContext 的二进制破坏。
    [Fact]
    public void DevicePluginAndFactory_PublicSurface_MatchesFrozenV1Contract()
    {
        Assert.Equal(
            [("Manifest", typeof(PluginManifest))],
            typeof(IDevicePlugin)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(property => (property.Name, property.PropertyType))
                .ToArray());
        Assert.Equal(
            ["Describe"],
            GetDeclaredOperationNames(typeof(IDevicePlugin)));
        AssertMethod(typeof(IDevicePlugin), "Describe", typeof(DevicePluginContribution));

        Assert.Equal(
            [("ProfileId", typeof(ProtocolProfileId))],
            typeof(IProtocolDriverFactory)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(property => (property.Name, property.PropertyType))
                .ToArray());
        Assert.Equal(
            ["CreateDriver"],
            GetDeclaredOperationNames(typeof(IProtocolDriverFactory)));
        AssertMethod(
            typeof(IProtocolDriverFactory),
            "CreateDriver",
            typeof(IProtocolDriver),
            typeof(IPluginContext));
    }

    // 捕获 Endpoint Lease 引入隐式重试参数或连接生命周期方法，突破一次 I/O 边界的问题。
    [Fact]
    public void EndpointLease_PublicSurface_ExposesOnlySingleIoOperations()
    {
        Assert.Equal(
            ["ReadAsync", "SendAsync"],
            GetDeclaredOperationNames(typeof(IEndpointLease)));
        AssertMethod(
            typeof(IEndpointLease),
            "SendAsync",
            typeof(ValueTask<EndpointSendResult>),
            typeof(EndpointRequest),
            typeof(CancellationToken));
        AssertMethod(
            typeof(IEndpointLease),
            "ReadAsync",
            typeof(ValueTask<EndpointReadResult>),
            typeof(EndpointReadRequest),
            typeof(CancellationToken));
    }

    // 捕获持续通知流再次被塞回基础 Lease，迫使所有一次 I/O 传输实现无关能力的问题。
    [Fact]
    public void EndpointMessageSource_PublicSurface_ProvidesOptionalMessageStream()
    {
        Assert.Equal(
            ["ReadMessagesAsync"],
            GetDeclaredOperationNames(typeof(IEndpointMessageSource)));
        AssertMethod(
            typeof(IEndpointMessageSource),
            "ReadMessagesAsync",
            typeof(IAsyncEnumerable<EndpointMessage>),
            typeof(CancellationToken));
    }

    private static DeviceCommandEnvelope CreateCommand(DateTimeOffset? expiresAt)
    {
        return CreateCommand(Json("{\"value\":10}"), expiresAt: expiresAt);
    }

    private static DeviceCommandEnvelope CreateCommand(JsonElement payload, DateTimeOffset? expiresAt = null)
    {
        return new DeviceCommandEnvelope(
            new CommandId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            new DeviceInstanceId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            CapabilityId.Parse("intensity.output"),
            "set",
            payload,
            new CommandSource(CommandSourceKind.UserInterface, "local-user"),
            CommandDelivery.Idempotent,
            DateTimeOffset.Parse("2026-08-01T09:59:00+08:00"),
            expiresAt,
            1,
            1);
    }

    private static ProtocolProfileDescriptor CreateProfile(string profileId, string modelId)
    {
        return new ProtocolProfileDescriptor(
            ProtocolProfileId.Parse(profileId),
            ModelId.Parse(modelId),
            profileId,
            [TransportKind.Virtual],
            [CreateCapability("intensity.output", "actuator-a")]);
    }

    private static CapabilityDescriptor CreateCapability(string id, string actuatorId)
    {
        var actuator = CreateActuator(actuatorId);
        return CreateCapability(
            id,
            [actuator],
            new SafeStopPlanDescriptor("stop", Json("{}"), [actuatorId]));
    }

    private static CapabilityDescriptor CreateCapability(
        string id,
        IReadOnlyList<CapabilityActuatorDescriptor> actuators,
        SafeStopPlanDescriptor safeStopPlan)
    {
        return new CapabilityDescriptor(
            CapabilityId.Parse(id),
            id,
            CapabilityStability.Stable,
            actuators,
            new VersionedJsonSchema("1.0", Json("{\"type\":\"object\"}")),
            new VersionedJsonSchema("1.0", Json("{\"type\":\"object\"}")),
            new CapabilityCommandPolicy(
                CommandDelivery.Idempotent,
                CommandAcknowledgement.DeviceAcknowledgement,
                10,
                TimeSpan.FromSeconds(2),
                true),
            safeStopPlan,
            new VersionedJsonSchema("1.0", Json("{\"type\":\"object\"}")));
    }

    private static CapabilityActuatorDescriptor CreateActuator(string id)
    {
        return new CapabilityActuatorDescriptor(
            id,
            id,
            "percent",
            new NumericRange(0, 100),
            new NumericRange(0, 30),
            [new CapabilityChannelDescriptor("channel-a", "通道 A")]);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static IEnumerable<Type> GetPublicSignatureTypes(Type type)
    {
        yield return type;

        foreach (var constructor in type.GetConstructors())
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var property in type.GetProperties())
        {
            yield return property.PropertyType;
        }

        foreach (var method in type.GetMethods().Where(method => !method.IsSpecialName))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
    }

    private static string[] GetDeclaredOperationNames(Type interfaceType)
    {
        return interfaceType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AssertMethod(
        Type interfaceType,
        string methodName,
        Type returnType,
        params Type[] parameterTypes)
    {
        var method = interfaceType.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            parameterTypes);

        Assert.NotNull(method);
        Assert.Equal(returnType, method.ReturnType);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class DefaultProfileDriverFactory : IProtocolDriverFactory
    {
        public ProtocolProfileId ProfileId => default;

        public IProtocolDriver CreateDriver(IPluginContext context) =>
            throw new NotSupportedException();
    }
}
