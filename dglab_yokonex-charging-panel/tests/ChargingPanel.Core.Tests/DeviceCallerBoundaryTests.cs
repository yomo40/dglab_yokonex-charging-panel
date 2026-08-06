using System.Text.Json;
using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Telemetry;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Core.Application.Devices;
using ChargingPanel.Core.Automation;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Data.Entities;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.Catalog;
using ChargingPanel.Core.Devices.Registry;
using ChargingPanel.Core.Devices.Runtime;
using ChargingPanel.Core.Devices.Safety;
using ChargingPanel.Core.Events;
using ChargingPanel.Core.OCR;
using ChargingPanel.Core.Scripting;
using ChargingPanel.Core.Services;
using Xunit;

namespace ChargingPanel.Core.Tests;

/// <summary>
/// 业务边界测试：
/// 1) 扫描 Core 业务目录，拒绝 legacy 设备访问模式；
/// 2) 行为测试：事件/脚本/传感器规则/对战/房间命令最终都产生
///    <see cref="DeviceCommandEnvelope"/> 并路由通过 <see cref="CommandDispatcher"/>。
/// </summary>
public sealed class DeviceCallerBoundaryTests : IDisposable
{
    private readonly string _dbPath;

    public DeviceCallerBoundaryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"boundary_test_{Guid.NewGuid():N}.db");
        Database.Initialize(_dbPath);
    }

    // ---------- 边界扫描 ----------

    private static readonly string[] ScannedDirectories =
    {
        "Events", "Services", "Scripting", "Network", "Automation", "Protocols"
    };

    private static readonly (string Name, string Pattern)[] ForbiddenPatterns =
    {
        ("IDevice", @"\bIDevice\b"),
        ("GetDevice(", @"GetDevice\("),
        ("DGLabBluetoothAdapter", @"\bDGLabBluetoothAdapter\b"),
        ("DGLabWebSocketAdapter", @"\bDGLabWebSocketAdapter\b"),
        ("YokonexEmsBluetoothAdapter", @"\bYokonexEmsBluetoothAdapter\b"),
        ("YokonexEnemaBluetoothAdapter", @"\bYokonexEnemaBluetoothAdapter\b"),
        ("YokonexToyBluetoothAdapter", @"\bYokonexToyBluetoothAdapter\b"),
        ("YokonexIMAdapter", @"\bYokonexIMAdapter\b"),
        ("WindowsBluetoothTransport", @"\bWindowsBluetoothTransport\b"),
        (".SetStrengthAsync(", @"\.SetStrengthAsync\("),
        ("DeviceActionTranslatorRegistry", @"\bDeviceActionTranslatorRegistry\b"),
        ("DeviceManager", @"\bDeviceManager\b")
    };

    [Fact]
    public void BusinessDirectories_MustNotReferenceLegacyDeviceAccess()
    {
        var coreRoot = FindCoreRoot();
        var violations = new List<string>();

        foreach (var directory in ScannedDirectories)
        {
            var fullPath = Path.Combine(coreRoot, directory);
            if (!Directory.Exists(fullPath))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(fullPath, "*.cs", SearchOption.AllDirectories))
            {
                var content = File.ReadAllText(file);
                foreach (var (name, pattern) in ForbiddenPatterns)
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(content, pattern))
                    {
                        violations.Add($"{file}: {name}");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "业务目录包含 legacy 设备访问模式：\r\n" + string.Join("\r\n", violations));
    }

    // ---------- 行为测试：五类调用方都路由到 CommandDispatcher ----------

    [Fact]
    public async Task EventTrigger_WithActiveInstance_RoutesEnvelopeThroughCommandDispatcher()
    {
        using var harness = RuntimeHarness.CreateDglab(out var instanceId);
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);

        eventService.AddEvent(new EventRecord
        {
            EventId = "boundary-event-trigger",
            Name = "边界事件触发",
            Category = "custom",
            Channel = "A",
            Action = "set",
            ActionType = "set",
            Value = 50,
            Strength = 50,
            Duration = 0,
            Enabled = true,
            Priority = 1,
            TargetDeviceType = "All"
        });

        await eventService.TriggerEventAsync("boundary-event-trigger", instanceId.Value.ToString("D"));

        Assert.Contains(harness.Driver.Calls, call => call.StartsWith("set.level:"));
    }

    [Fact]
    public async Task ScriptAction_WithActiveInstance_RoutesEnvelopeThroughCommandDispatcher()
    {
        using var harness = RuntimeHarness.CreateDglab(out var instanceId);
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);
        using var scriptEngine = new ScriptEngine(harness.AppService, harness.Query, eventService, modBridgeService: null, mapper: harness.Mapper);

        var scriptId = $"boundary_script_{Guid.NewGuid():N}";
        var loaded = scriptEngine.LoadScript(new ScriptRecord
        {
            Id = scriptId,
            Name = "Boundary Script",
            Game = "UnitTest",
            Enabled = true,
            Code = $"device.setStrength('{instanceId.Value.ToString("D")}', 'A', 40);"
        });

        Assert.True(loaded);
        Assert.Contains(harness.Driver.Calls, call => call.StartsWith("set.level:"));
    }

    [Fact]
    public async Task SensorRule_WithActiveInstance_RoutesEnvelopeThroughCommandDispatcher()
    {
        using var harness = RuntimeHarness.CreateDglab(out var instanceId);
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);
        using var sensorRuleService = new SensorRuleService(
            harness.AppService, harness.Query, harness.Scheduler, eventService, harness.Mapper);
        sensorRuleService.Start();

        sensorRuleService.AddRule(new SensorRule
        {
            Name = "边界传感器规则",
            SensorType = SensorType.Pressure,
            TriggerType = SensorTriggerType.Threshold,
            Threshold = 5,
            TargetDeviceId = instanceId.Value.ToString("D"),
            TargetChannel = Channel.A,
            Action = SensorAction.Set,
            Value = 30,
            Duration = 0,
            CooldownMs = 0,
            Enabled = true
        });

        EventBus.Instance.PublishGameEvent(new GameEvent
        {
            Type = GameEventType.PressureChanged,
            EventId = "pressure-changed",
            Source = "BoundaryTest",
            TargetDeviceId = instanceId.Value.ToString("D"),
            NewValue = 10,
            Data = new Dictionary<string, object> { ["pressure"] = 10 }
        });

        await WaitUntilAsync(() => harness.Driver.Calls.Any(call => call.StartsWith("set.level:")));
    }

    [Fact]
    public async Task BattleAction_WithActiveInstance_RoutesEnvelopeThroughCommandDispatcher()
    {
        using var harness = RuntimeHarness.CreateDglab(out var instanceId);
        using var battle = new BattleModeService(
            harness.AppService, harness.Query, harness.Scheduler, EventBus.Instance, ocrService: null, mapper: harness.Mapper);

        var room = battle.CreateRoom("边界对战", new BattleSettings
        {
            BaseStrength = 25,
            MaxStrength = 100,
            PunishmentDuration = 500,
            TargetChannel = Channel.A
        });
        var player = new BattlePlayer
        {
            Id = "boundary_player",
            Name = "边界玩家",
            DeviceId = instanceId.Value.ToString("D"),
            IsLocal = true
        };
        Assert.True(battle.JoinRoom(room.Id, player));

        await battle.TriggerPlayerDeathAsync(player.Id);

        Assert.Contains(harness.Driver.Calls, call => call.StartsWith("set.level:"));
    }

    [Fact]
    public async Task RoomCommand_WithActiveInstance_RoutesEnvelopeThroughCommandDispatcher()
    {
        using var harness = RuntimeHarness.CreateDglab(out var instanceId);
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);
        using var processor = new EventProcessor(
            eventService, harness.AppService, harness.Query, harness.Scheduler, EventBus.Instance, harness.Mapper);

        EventBus.Instance.PublishRemoteSync(new RemoteSyncEvent
        {
            Type = RemoteSyncType.ControlCommand,
            SenderId = "remote-user",
            Payload = new DeviceControlEvent
            {
                DeviceId = instanceId.Value.ToString("D"),
                Channel = ChannelTarget.A,
                Action = EventAction.Set,
                Value = 42,
                Duration = 0,
                Source = "RoomCommandBoundary"
            }
        });

        await WaitUntilAsync(() => harness.Driver.Calls.Any(call => call.StartsWith("set.level:")));
    }

    // ---------- 工具 ----------

    private static string FindCoreRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Core");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("无法定位 src/Core 目录。");
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000, int intervalMs = 25)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(intervalMs);
        }

        throw new TimeoutException("Condition not reached in time.");
    }

    public void Dispose()
    {
        try
        {
            Database.Instance.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch
        {
            // ignore
        }
    }
}

/// <summary>
/// v2 运行时测试骨架：Safety + RuntimeRegistry + CommandDispatcher + 查询/应用服务 + 调度器 + 映射器，
/// 并登记一个带假 Driver 的活动实例。
/// </summary>
internal sealed class RuntimeHarness : IDisposable
{
    public SafetySupervisor Safety { get; }

    public DeviceRuntimeRegistry RuntimeRegistry { get; }

    public CommandDispatcher Dispatcher { get; }

    public DeviceQueryService Query { get; }

    public DeviceApplicationService AppService { get; }

    public DeviceCommandScheduler Scheduler { get; }

    public DeviceActionMapperRegistry Mapper { get; }

    public TestDriver Driver { get; }

    public IReadOnlyList<TestDriver> AllDrivers { get; }

    private readonly List<TestDriver> _drivers = [];

    private RuntimeHarness(
        SafetySupervisor safety,
        DeviceRuntimeRegistry runtimeRegistry,
        CommandDispatcher dispatcher,
        DeviceQueryService query,
        DeviceApplicationService appService,
        DeviceCommandScheduler scheduler,
        TestDriver driver)
    {
        Safety = safety;
        RuntimeRegistry = runtimeRegistry;
        Dispatcher = dispatcher;
        Query = query;
        AppService = appService;
        Scheduler = scheduler;
        Mapper = new DeviceActionMapperRegistry();
        Driver = driver;
        _drivers.Add(driver);
        AllDrivers = _drivers;
    }

    public static RuntimeHarness CreateDglab(out DeviceInstanceId instanceId)
    {
        instanceId = DeviceInstanceId.New();
        var capability = Capabilities.BuildDglabIntensity();
        return Create(instanceId, capability, 1);
    }

    public static RuntimeHarness Create(
        DeviceInstanceId instanceId,
        CapabilityDescriptor capability,
        long sessionEpoch = 1)
    {
        var driver = new TestDriver();
        var safety = new SafetySupervisor(TimeProvider.System);
        var runtimeRegistry = new DeviceRuntimeRegistry();
        safety.AttachRuntime(runtimeRegistry);
        var dispatcher = new CommandDispatcher(safety, runtimeRegistry, TimeProvider.System);
        var catalog = new DeviceCatalog(Array.Empty<IDevicePlugin>());
        var registry = new DeviceRegistry(catalog);
        var query = new DeviceQueryService(catalog, registry, runtimeRegistry);
        var appService = new DeviceApplicationService(dispatcher, safety, TimeProvider.System);
        var scheduler = new DeviceCommandScheduler(dispatcher, TimeProvider.System);

        runtimeRegistry.Register(new DeviceRuntimeBinding(instanceId, driver, [capability], sessionEpoch));

        return new RuntimeHarness(safety, runtimeRegistry, dispatcher, query, appService, scheduler, driver);
    }

    /// <summary>以一个新的假 Driver 登记实例，并返回该 Driver 供断言。</summary>
    public TestDriver RegisterWithNewDriver(DeviceInstanceId instanceId, CapabilityDescriptor capability, long sessionEpoch = 1)
    {
        var driver = new TestDriver();
        RuntimeRegistry.Register(new DeviceRuntimeBinding(instanceId, driver, [capability], sessionEpoch));
        _drivers.Add(driver);
        return driver;
    }

    public void Register(DeviceInstanceId instanceId, CapabilityDescriptor capability, long sessionEpoch = 1)
    {
        RuntimeRegistry.Register(new DeviceRuntimeBinding(instanceId, Driver, [capability], sessionEpoch));
    }

    public void Dispose()
    {
        Scheduler.Dispose();
        Dispatcher.Dispose();
    }
}

/// <summary>
/// 记录命令 operation + 载荷的假 Driver。行为测试用它断言命令已到达驱动层。
/// </summary>
internal sealed class TestDriver : IProtocolDriver
{
    private readonly object _gate = new();
    private readonly List<string> _calls = [];

    public ProtocolProfileId ProfileId { get; } = new("test.profile.v1");

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    public void ClearCalls()
    {
        lock (_gate)
        {
            _calls.Clear();
        }
    }

    public ValueTask OpenAsync(IEndpointLease lease, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask<DeviceCommandResult> ExecuteAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add($"{command.OperationId}:{command.Payload.GetRawText()}");
        }

        return ValueTask.FromResult(new DeviceCommandResult(command.CommandId, DeviceCommandStatus.Executed));
    }

    public ValueTask<SafeStopResult> SafeStopAsync(SafeStopContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new SafeStopResult(SafeStopStatus.ConfirmedStopped));

    public async IAsyncEnumerable<DeviceTelemetryEnvelope> ReadTelemetryAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// 能力构造工具：厂商中立的能力描述，供测试骨架登记实例。
/// </summary>
internal static class Capabilities
{
    public static CapabilityDescriptor BuildDglabIntensity()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        using var safeStopPayload = JsonDocument.Parse("{\"channelA\":0,\"channelB\":0}");
        return new CapabilityDescriptor(
            new CapabilityId("intensity.output"),
            "双通道强度输出",
            CapabilityStability.Stable,
            [
                new CapabilityActuatorDescriptor(
                    "main",
                    "主输出",
                    "level",
                    new NumericRange(0, 200),
                    new NumericRange(0, 200),
                    [new CapabilityChannelDescriptor("a", "A 通道"), new CapabilityChannelDescriptor("b", "B 通道")])
            ],
            new VersionedJsonSchema("1.0", schema.RootElement.Clone()),
            new VersionedJsonSchema("1.0", schema.RootElement.Clone()),
            new CapabilityCommandPolicy(
                CommandDelivery.Idempotent,
                CommandAcknowledgement.DeviceAcknowledgement,
                null,
                TimeSpan.FromSeconds(2),
                true),
            new SafeStopPlanDescriptor("set.level", safeStopPayload.RootElement.Clone(), ["main"]),
            new VersionedJsonSchema("1.0", schema.RootElement.Clone()));
    }

    public static CapabilityDescriptor BuildEmsChannelOutput()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        using var safeStopPayload = JsonDocument.Parse("{\"channel\":\"AB\",\"on\":false}");
        return new CapabilityDescriptor(
            new CapabilityId("ems.channel.output"),
            "电击通道输出",
            CapabilityStability.Stable,
            [
                new CapabilityActuatorDescriptor(
                    "output",
                    "电击输出",
                    "level",
                    new NumericRange(0, 276),
                    new NumericRange(0, 276),
                    [new CapabilityChannelDescriptor("a", "A 通道"), new CapabilityChannelDescriptor("b", "B 通道")])
            ],
            new VersionedJsonSchema("1.0", schema.RootElement.Clone()),
            new VersionedJsonSchema("1.0", schema.RootElement.Clone()),
            new CapabilityCommandPolicy(
                CommandDelivery.AtMostOnce,
                CommandAcknowledgement.TransportAcknowledgement,
                null,
                TimeSpan.FromSeconds(2),
                true),
            new SafeStopPlanDescriptor("close.output", safeStopPayload.RootElement.Clone(), ["output"]),
            new VersionedJsonSchema("1.0", schema.RootElement.Clone()));
    }

    public static CapabilityDescriptor BuildToySpeed()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        using var safeStopPayload = JsonDocument.Parse("{\"a\":0,\"b\":0,\"c\":0}");
        return new CapabilityDescriptor(
            new CapabilityId("toy.speed.control"),
            "玩具实时力度控制",
            CapabilityStability.Stable,
            [
                new CapabilityActuatorDescriptor(
                    "motor",
                    "马达",
                    "level",
                    new NumericRange(0, 20),
                    new NumericRange(0, 20),
                    [new CapabilityChannelDescriptor("motor-a", "A 马达")])
            ],
            new VersionedJsonSchema("1.0", schema.RootElement.Clone()),
            new VersionedJsonSchema("1.0", schema.RootElement.Clone()),
            new CapabilityCommandPolicy(
                CommandDelivery.AtMostOnce,
                CommandAcknowledgement.TransportAcknowledgement,
                null,
                TimeSpan.FromSeconds(2),
                true),
            new SafeStopPlanDescriptor("speed.zero", safeStopPayload.RootElement.Clone(), ["motor"]),
            new VersionedJsonSchema("1.0", schema.RootElement.Clone()));
    }

    public static CapabilityDescriptor BuildImCommandSend()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        using var safeStopPayload = JsonDocument.Parse("{\"commandId\":\"_stop_all\"}");
        return new CapabilityDescriptor(
            new CapabilityId("im.command.send"),
            "IM 指令发送",
            CapabilityStability.Stable,
            [
                new CapabilityActuatorDescriptor(
                    "target",
                    "IM 目标",
                    "level",
                    new NumericRange(0, 1),
                    new NumericRange(0, 1),
                    [new CapabilityChannelDescriptor("command", "指令")])
            ],
            new VersionedJsonSchema("1.0", schema.RootElement.Clone()),
            new VersionedJsonSchema("1.0", schema.RootElement.Clone()),
            new CapabilityCommandPolicy(
                CommandDelivery.AtMostOnce,
                CommandAcknowledgement.TransportAcknowledgement,
                null,
                TimeSpan.FromSeconds(2),
                true),
            new SafeStopPlanDescriptor("stop.all", safeStopPayload.RootElement.Clone(), ["target"]),
            new VersionedJsonSchema("1.0", schema.RootElement.Clone()));
    }
}
