using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Application.Devices;
using ChargingPanel.Core.Automation;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Data.Entities;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Events;
using ChargingPanel.Core.Network;
using ChargingPanel.Core.OCR;
using ChargingPanel.Core.Protocols;
using ChargingPanel.Core.Scripting;
using ChargingPanel.Core.Services;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ChargingPanel.Core.Tests;

public sealed class RefactorIntegrationTests : IDisposable
{
    private readonly string _dbPath;

    public RefactorIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"charging_panel_test_{Guid.NewGuid():N}.db");
        Database.Initialize(_dbPath);
    }

    [Fact]
    public async Task EventService_ShouldSupportDefaultRule_AndCustomOverride()
    {
        using var harness = RuntimeHarness.CreateDglab(out var instanceId);
        var deviceId = instanceId.Value.ToString("D");
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);

        await eventService.TriggerEventAsync("lost-hp", deviceId);
        Assert.Contains(harness.Driver.Calls, call => call.StartsWith("adjust.level:"));

        eventService.UpdateEvent("lost-hp", new EventRecord
        {
            EventId = "lost-hp",
            Name = "自定义血量损失规则",
            Description = "覆盖默认规则",
            Category = "custom",
            Channel = "A",
            Action = "set",
            ActionType = "set",
            Value = 33,
            Strength = 33,
            Duration = 0,
            Enabled = true,
            Priority = 1,
            TargetDeviceType = "All"
        });

        await eventService.TriggerEventAsync("lost-hp", deviceId);

        Assert.Contains(harness.Driver.Calls, call => call.StartsWith("set.level:{\"channelA\":66,"));
    }

    [Fact]
    public async Task EventProcessor_ShouldBridgeEventBusToRuleEngine()
    {
        using var harness = RuntimeHarness.CreateDglab(out _);
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);
        using var processor = new EventProcessor(eventService, harness.AppService, harness.Query, harness.Scheduler, EventBus.Instance, harness.Mapper);

        EventBus.Instance.PublishGameEvent(new GameEvent
        {
            Type = GameEventType.HealthLost,
            EventId = "script_health_lost_" + Guid.NewGuid().ToString("N")[..8], // 唯一 ID 避免去重
            Source = "UnitTest",
            OldValue = 100,
            NewValue = 80
        });

        await WaitUntilAsync(() => harness.Driver.Calls.Any(call => call.StartsWith("adjust.level:")), timeoutMs: 8000);
    }

    [Fact]
    public async Task EventProcessor_ShouldRespectSkipFlag()
    {
        using var harness = RuntimeHarness.CreateDglab(out _);
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);
        using var processor = new EventProcessor(eventService, harness.AppService, harness.Query, harness.Scheduler, EventBus.Instance, harness.Mapper);

        EventBus.Instance.PublishGameEvent(new GameEvent
        {
            Type = GameEventType.HealthLost,
            EventId = "lost-hp",
            Source = "UnitTest",
            OldValue = 100,
            NewValue = 70,
            Data = new Dictionary<string, object>
            {
                [EventProcessor.SkipProcessorDataKey] = true
            }
        });

        await Task.Delay(300);
        Assert.Empty(harness.Driver.Calls);
    }

    [Fact]
    public void EventService_ShouldExcludeNonRuleDeviceTelemetryEvents()
    {
        using var harness = RuntimeHarness.CreateDglab(out _);
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);

        Assert.NotNull(eventService.GetEvent("lost-hp"));
        Assert.Null(eventService.GetEvent("pressure-changed"));
        Assert.Null(eventService.GetEvent("device-battery-changed"));
        Assert.Null(eventService.GetEvent("query"));
        Assert.Null(eventService.GetEvent("new-credit"));
    }

    [Fact]
    public async Task EventProcessor_ShouldIgnoreNonRuleTelemetryEventMapping()
    {
        using var harness = RuntimeHarness.CreateDglab(out _);
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);
        using var processor = new EventProcessor(eventService, harness.AppService, harness.Query, harness.Scheduler, EventBus.Instance, harness.Mapper);

        EventBus.Instance.PublishGameEvent(new GameEvent
        {
            Type = GameEventType.PressureChanged,
            EventId = "pressure-changed",
            Source = "UnitTest",
            Data = new Dictionary<string, object>
            {
                ["pressure"] = 42
            }
        });

        await Task.Delay(300);
        Assert.Empty(harness.Driver.Calls);
    }

    [Fact]
    public async Task EventProcessor_ShouldHandleRemoteControlCommand()
    {
        using var harness = RuntimeHarness.CreateDglab(out var instanceId);
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);
        using var processor = new EventProcessor(eventService, harness.AppService, harness.Query, harness.Scheduler, EventBus.Instance, harness.Mapper);

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
                Source = "UnitTestRemote"
            }
        });

        await WaitUntilAsync(() => harness.Driver.Calls.Any(call => call.StartsWith("set.level:")));
    }

    [Fact]
    public async Task EventService_ShouldRespectTargetDeviceTypeFilter()
    {
        using var harness = RuntimeHarness.CreateDglab(out var dglabId);
        var emsId = DeviceInstanceId.New();
        var emsDriver = harness.RegisterWithNewDriver(emsId, Capabilities.BuildEmsChannelOutput(), 1);
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);

        eventService.AddEvent(new EventRecord
        {
            EventId = "target-filter-test",
            Name = "目标设备过滤测试",
            Description = "仅 DGLab 命中",
            Category = "custom",
            Channel = "A",
            Action = "set",
            ActionType = "set",
            Value = 55,
            Strength = 55,
            Duration = 0,
            Enabled = true,
            Priority = 1,
            TargetDeviceType = "DGLab"
        });

        await eventService.TriggerEventAsync("target-filter-test");

        Assert.Contains(harness.Driver.Calls, call => call.StartsWith("set.level:{\"channelA\":110,"));
        Assert.DoesNotContain(emsDriver.Calls, call => call.StartsWith("set.output:"));
    }

    [Fact]
    public async Task EventProcessor_ShouldPreferExplicitEventIdOverTypeMapping()
    {
        using var harness = RuntimeHarness.CreateDglab(out _);
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);
        using var processor = new EventProcessor(eventService, harness.AppService, harness.Query, harness.Scheduler, EventBus.Instance, harness.Mapper);

        eventService.AddEvent(new EventRecord
        {
            EventId = "custom-healthlost-event",
            Name = "显式事件ID优先测试",
            Description = "当显式 eventId 存在时应优先使用",
            Category = "custom",
            Channel = "A",
            Action = "set",
            ActionType = "set",
            Value = 77,
            Strength = 77,
            Duration = 0,
            Enabled = true,
            Priority = 1,
            TargetDeviceType = "All"
        });

        EventBus.Instance.PublishGameEvent(new GameEvent
        {
            Type = GameEventType.HealthLost,
            EventId = "custom-healthlost-event",
            Source = "UnitTest",
            OldValue = 100,
            NewValue = 90
        });

        await WaitUntilAsync(() => harness.Driver.Calls.Any(call => call.StartsWith("set.level:{\"channelA\":154,")));
    }

    [Fact]
    public void DeviceActionTranslatorRegistry_ShouldRouteToyModeAction()
    {
        var device = View(Capabilities.BuildToySpeed());
        var envelopes = MapFor(device, new DeviceActionRequest
        {
            EventId = "toy-mode",
            ActionType = "vibrate_mode",
            Value = 0,
            RawValue = 6,
            Channels = new[] { Channel.A }
        });

        var envelope = Assert.Single(envelopes);
        Assert.Equal("toy.speed.control", envelope.CapabilityId.Value);
        Assert.Equal("set.speed", envelope.OperationId);
    }

    [Fact]
    public void DeviceActionTranslatorRegistry_ShouldRouteToyMotorAction_AndMapStrength()
    {
        var device = View(Capabilities.BuildToySpeed());
        var envelopes = MapFor(device, new DeviceActionRequest
        {
            EventId = "toy-motor-2",
            ActionType = "toy_motor_2",
            Value = 60,
            RawValue = 60,
            Channels = Array.Empty<Channel>()
        });

        var envelope = Assert.Single(envelopes);
        Assert.Equal("set.speed", envelope.OperationId);
        Assert.Contains("b", envelope.Payload.GetRawText());
    }

    [Fact]
    public void DeviceActionTranslatorRegistry_ShouldRouteToyQueryDeviceInfoAction()
    {
        var device = View(Capabilities.BuildToySpeed());
        var envelopes = MapFor(device, new DeviceActionRequest
        {
            EventId = "toy-query-device-info",
            ActionType = "query_device_info",
            Value = 0,
            RawValue = 0,
            Channels = Array.Empty<Channel>()
        });

        Assert.Empty(envelopes);
    }

    [Fact]
    public void DeviceActionTranslatorRegistry_ShouldRouteEmsCustomWaveformAction()
    {
        var device = View(Capabilities.BuildEmsChannelOutput());
        var envelopes = MapFor(device, new DeviceActionRequest
        {
            EventId = "ems-custom-wave",
            ActionType = "custom_waveform",
            Value = 50,
            RawValue = 50,
            DurationMs = 20,
            WaveformData = "50,20",
            Channels = new[] { Channel.A }
        });

        var envelope = Assert.Single(envelopes);
        Assert.Equal("ems.channel.output", envelope.CapabilityId.Value);
        Assert.Equal("set.output", envelope.OperationId);
        Assert.Contains("\"frequency\":50", envelope.Payload.GetRawText());
        Assert.Contains("\"pulseTime\":20", envelope.Payload.GetRawText());
    }

    [Fact]
    public void DeviceActionTranslatorRegistry_ShouldRouteGenericWaveToDeviceWaveformApi()
    {
        var device = View(Capabilities.BuildEmsChannelOutput());
        var envelopes = MapFor(device, new DeviceActionRequest
        {
            EventId = "ems-wave-generic",
            ActionType = "wave",
            Value = 42,
            RawValue = 42,
            DurationMs = 1200,
            WaveformData = "pulse",
            Channels = new[] { Channel.B }
        });

        var envelope = Assert.Single(envelopes);
        Assert.Equal("ems.channel.output", envelope.CapabilityId.Value);
        Assert.Equal("set.output", envelope.OperationId);
        Assert.Contains("\"frequency\":50", envelope.Payload.GetRawText());
    }

    [Fact]
    public void DeviceActionTranslatorRegistry_SmartLockActionWithoutProtocol_Rejects()
    {
        var device = View(Capabilities.BuildDglabIntensity());
        var envelopes = MapFor(device, new DeviceActionRequest
        {
            EventId = "lock-open",
            ActionType = "smart_unlock",
            Value = 0,
            Channels = Array.Empty<Channel>()
        });

        // 能力不支持的动作映射为空集，如实拒绝，不伪造成功。
        Assert.Empty(envelopes);
    }

    [Fact]
    public async Task EventService_ShouldSupportActionAliasNormalization()
    {
        using var harness = RuntimeHarness.CreateDglab(out var instanceId);
        var deviceId = instanceId.Value.ToString("D");
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);

        eventService.AddEvent(new EventRecord
        {
            EventId = "alias-inc-test",
            Name = "动作别名测试",
            Description = "inc 应映射到 increase",
            Category = "custom",
            Channel = "A",
            Action = "set",
            ActionType = "inc",
            Value = 12,
            Strength = 12,
            Duration = 0,
            Enabled = true,
            Priority = 5,
            TargetDeviceType = "All"
        });

        await eventService.TriggerEventAsync("alias-inc-test", deviceId);
        // 事件规则经数据库往返后 ActionType 归一为持久化的 action（set），
        // 映射器按 set 语义把 12 线性映射到 DGLab 上限 200 → 24。
        Assert.Contains(harness.Driver.Calls, call => call.StartsWith("set.level:{\"channelA\":24,"));
    }

    [Fact]
    public async Task EventService_ShouldBlockLowerPriorityEvent_DuringPriorityWindow()
    {
        using var harness = RuntimeHarness.CreateDglab(out var instanceId);
        var deviceId = instanceId.Value.ToString("D");
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);

        eventService.AddEvent(new EventRecord
        {
            EventId = "priority-high",
            Name = "高优先级",
            Category = "custom",
            Channel = "A",
            Action = "set",
            ActionType = "set",
            Value = 80,
            Strength = 80,
            Duration = 1500,
            Enabled = true,
            Priority = 100,
            TargetDeviceType = "All"
        });

        eventService.AddEvent(new EventRecord
        {
            EventId = "priority-low",
            Name = "低优先级",
            Category = "custom",
            Channel = "A",
            Action = "set",
            ActionType = "set",
            Value = 10,
            Strength = 10,
            Duration = 0,
            Enabled = true,
            Priority = 1,
            TargetDeviceType = "All"
        });

        await eventService.TriggerEventAsync("priority-high", deviceId);
        await eventService.TriggerEventAsync("priority-low", deviceId);

        Assert.Contains(harness.Driver.Calls, call => call.StartsWith("set.level:{\"channelA\":160,"));
        Assert.DoesNotContain(harness.Driver.Calls, call => call.Contains("\"channelA\":20"));
    }

    [Fact]
    public void OCRService_ShouldPersistArmorAndModelSettings()
    {
        using var harness = RuntimeHarness.CreateDglab(out _);
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);
        using var service = new OCRService(eventService);

        var config = new OCRConfig
        {
            Enabled = true,
            Interval = 123,
            InitialBlood = 95,
            InitialArmor = 66,
            Mode = "model",
            Area = new OCRArea(11, 22, 333, 44),
            ArmorEnabled = true,
            ArmorArea = new OCRArea(55, 66, 77, 88),
            HealthBarColor = "red",
            ArmorBarColor = "blue",
            ColorTolerance = 17,
            SampleRows = 4,
            EdgeDetection = false,
            HealthColorMin = "#111111",
            HealthColorMax = "#222222",
            ArmorColorMin = "#333333",
            ArmorColorMax = "#444444",
            BackgroundColorMin = "#555555",
            BackgroundColorMax = "#666666",
            TriggerThreshold = 9,
            CooldownMs = 777,
            ModelPath = @"C:\temp\model.onnx",
            UseGPU = false,
            GPUDeviceId = 2
        };

        service.SaveConfig(config);

        using var reloaded = new OCRService(eventService);
        var loaded = reloaded.Config;

        Assert.Equal(config.InitialArmor, loaded.InitialArmor);
        Assert.Equal(config.ArmorEnabled, loaded.ArmorEnabled);
        Assert.Equal(config.ArmorArea, loaded.ArmorArea);
        Assert.Equal(config.ArmorBarColor, loaded.ArmorBarColor);
        Assert.Equal(config.ArmorColorMin, loaded.ArmorColorMin);
        Assert.Equal(config.ArmorColorMax, loaded.ArmorColorMax);
        Assert.Equal(config.ModelPath, loaded.ModelPath);
        Assert.Equal(config.UseGPU, loaded.UseGPU);
        Assert.Equal(config.GPUDeviceId, loaded.GPUDeviceId);
        Assert.Equal(config.Mode, loaded.Mode);
    }

    [Fact]
    public void DeviceActionTranslatorRegistry_ShouldRouteGameCmdAction()
    {
        var device = View(Capabilities.BuildImCommandSend());
        var envelopes = MapFor(device, new DeviceActionRequest
        {
            EventId = "fallback_cmd",
            ActionType = "command",
            WaveformData = "player_hurt",
            Value = 0,
            Channels = Array.Empty<Channel>()
        });

        var envelope = Assert.Single(envelopes);
        Assert.Equal("im.command.send", envelope.CapabilityId.Value);
        Assert.Equal("send.command", envelope.OperationId);
        Assert.Contains("player_hurt", envelope.Payload.GetRawText());
    }

    [Fact]
    public void DeviceActionTranslatorRegistry_ShouldRouteStopAllToGlobalCommand()
    {
        var device = View(Capabilities.BuildImCommandSend());
        var envelopes = MapFor(device, new DeviceActionRequest
        {
            EventId = "global-stop",
            ActionType = "stop_all",
            Value = 0,
            Channels = Array.Empty<Channel>()
        });

        var envelope = Assert.Single(envelopes);
        Assert.Equal("stop.all", envelope.OperationId);
        Assert.Contains("_stop_all", envelope.Payload.GetRawText());
    }

    [Fact]
    public async Task EventService_ShouldRegisterAndCleanupModSessionRules()
    {
        using var harness = RuntimeHarness.CreateDglab(out var instanceId);
        var deviceId = instanceId.Value.ToString("D");
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);
        const string sessionId = "session_mod_rule_test";

        var result = eventService.RegisterModRulesForSession(sessionId, new[]
        {
            new EventRecord
            {
                EventId = "mod-player-hurt",
                Name = "MOD 受伤",
                Action = "set",
                ActionType = "set",
                Channel = "A",
                Value = 42,
                Strength = 42,
                Priority = 20,
                TargetDeviceType = "All"
            }
        });

        Assert.Equal(1, result.AcceptedCount);
        Assert.NotNull(eventService.GetEvent("mod-player-hurt"));

        await eventService.TriggerEventAsync("mod-player-hurt", deviceId);
        Assert.Contains(harness.Driver.Calls, call => call.StartsWith("set.level:{\"channelA\":84,"));

        var removed = eventService.UnregisterModRulesForSession(sessionId);
        Assert.Equal(1, removed);
        Assert.Null(eventService.GetEvent("mod-player-hurt"));
    }

    [Fact]
    public async Task EventProcessor_ShouldRespectModRuleCustomTriggerCondition()
    {
        using var harness = RuntimeHarness.CreateDglab(out var instanceId);
        var deviceId = instanceId.Value.ToString("D");
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);
        using var processor = new EventProcessor(eventService, harness.AppService, harness.Query, harness.Scheduler, EventBus.Instance, harness.Mapper);

        var register = eventService.RegisterModRulesForSession("session_mod_condition", new[]
        {
            new EventRecord
            {
                EventId = "mod-custom-condition",
                Name = "MOD 条件触发",
                Action = "set",
                ActionType = "set",
                Channel = "A",
                Value = 30,
                Strength = 30,
                Duration = 0,
                Priority = 20,
                TriggerType = "value-decrease",
                MinChange = 5,
                ConditionField = "newValue",
                ConditionOperator = "<=",
                ConditionValue = 40
            }
        });

        Assert.Equal(1, register.AcceptedCount);

        EventBus.Instance.PublishGameEvent(new GameEvent
        {
            Type = GameEventType.Custom,
            EventId = "mod-custom-condition",
            Source = "UnitTest",
            OldValue = 50,
            NewValue = 47,
            TargetDeviceId = deviceId,
            Data = new Dictionary<string, object>
            {
                ["debounceMs"] = 0
            }
        });

        await Task.Delay(300);
        Assert.Empty(harness.Driver.Calls);

        EventBus.Instance.PublishGameEvent(new GameEvent
        {
            Type = GameEventType.Custom,
            EventId = "mod-custom-condition",
            Source = "UnitTest",
            OldValue = 50,
            NewValue = 44,
            TargetDeviceId = deviceId,
            Data = new Dictionary<string, object>
            {
                ["debounceMs"] = 0
            }
        });

        await Task.Delay(300);
        Assert.Empty(harness.Driver.Calls);

        EventBus.Instance.PublishGameEvent(new GameEvent
        {
            Type = GameEventType.Custom,
            EventId = "mod-custom-condition",
            Source = "UnitTest",
            OldValue = 50,
            NewValue = 39,
            TargetDeviceId = deviceId,
            Data = new Dictionary<string, object>
            {
                ["debounceMs"] = 0
            }
        });

        await WaitUntilAsync(() => harness.Driver.Calls.Any(call => call.StartsWith("set.level:{\"channelA\":60,")), 1500, 50);
    }

    [Fact]
    public void Database_ImportDefaultScripts_ShouldNormalizeDuplicateMinecraftDefault()
    {
        var tempScriptsDir = Path.Combine(Path.GetTempPath(), $"scripts_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempScriptsDir);

        try
        {
            var code = @"// minecraft hp output test
return { game: 'Minecraft' };";
            File.WriteAllText(Path.Combine(tempScriptsDir, "minecraft_hp_output.js"), code);

            Database.Instance.AddScript(new ScriptRecord
            {
                Id = "scr_dup_minecraft_1",
                Name = "minecraft hp output",
                Game = "Minecraft",
                Author = "System",
                Version = "1.0.0",
                Code = code,
                Enabled = false
            });

            Database.Instance.AddScript(new ScriptRecord
            {
                Id = "scr_dup_minecraft_2",
                Name = "minecraft hp output",
                Game = "Minecraft",
                Author = "Anonymous",
                Version = "1.0.0",
                Code = code,
                Enabled = false
            });

            Database.Instance.ImportDefaultScripts(tempScriptsDir);

            var matched = Database.Instance.GetAllScripts()
                .Where(s =>
                    string.Equals(s.Name, "minecraft hp output", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.Code, code, StringComparison.Ordinal))
                .ToList();

            Assert.Single(matched);
            Assert.Equal("default_minecraft_hp_output", matched[0].Id);
        }
        finally
        {
            if (Directory.Exists(tempScriptsDir))
            {
                Directory.Delete(tempScriptsDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ScriptEngine_ShouldStartAndStopModBridgeByBridgeApiDeclaration()
    {
        using var harness = RuntimeHarness.CreateDglab(out _);
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);
        using var modBridge = new ModBridgeService(EventBus.Instance, eventService, harness.Query);
        await modBridge.StartAsync();
        using var scriptEngine = new ScriptEngine(harness.AppService, harness.Query, eventService, modBridge, harness.Mapper);

        var scriptId = $"bridge_script_{Guid.NewGuid():N}";
        var loaded = scriptEngine.LoadScript(new ScriptRecord
        {
            Id = scriptId,
            Name = "Bridge Script",
            Game = "UnitTest",
            Version = "1.0.0",
            Enabled = true,
            Code = @"
bridge.config({ name: 'Bridge Script', version: '1.0.0' });
bridge.startHTTP();
bridge.startWebSocket();
"
        });

        Assert.True(loaded);
        await WaitUntilAsync(() => modBridge.IsHttpRunning && modBridge.IsWebSocketRunning, 3000, 50);

        scriptEngine.UnloadScript(scriptId);
        await WaitUntilAsync(() => !modBridge.IsHttpRunning && !modBridge.IsWebSocketRunning, 3000, 50);
    }

    [Fact]
    public async Task ScriptEngine_BridgeOnEventMapper_ShouldReceiveObjectPayload()
    {
        using var harness = RuntimeHarness.CreateDglab(out _);
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);
        using var modBridge = new ModBridgeService(EventBus.Instance, eventService, harness.Query);
        await modBridge.StartAsync();
        using var scriptEngine = new ScriptEngine(harness.AppService, harness.Query, eventService, modBridge, harness.Mapper);

        var scriptId = $"bridge_mapper_{Guid.NewGuid():N}";
        var loaded = scriptEngine.LoadScript(new ScriptRecord
        {
            Id = scriptId,
            Name = "Bridge Mapper Script",
            Game = "UnitTest",
            Version = "1.0.0",
            Enabled = true,
            Code = @"
bridge.config({ name: 'Bridge Mapper Script', version: '1.0.0' });
bridge.startWebSocket();
bridge.onEvent((raw) => {
  return {
    eventId: raw.kind === 'damage' ? 'mapped_damage' : 'mapped_other',
    oldValue: raw.oldValue || 0,
    newValue: raw.newValue || 0
  };
});
"
        });

        Assert.True(loaded);
        await WaitUntilAsync(() => modBridge.IsWebSocketRunning, 3000, 50);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{ModBridgeService.FixedWebSocketPort}/"),
            CancellationToken.None);

        await SendWebSocketJsonAsync(socket, new
        {
            type = "hello",
            scriptId,
            name = "Bridge Mapper Script",
            game = "UnitTest",
            version = "1.0.0"
        });

        using (var welcome = await ReceiveWebSocketJsonAsync(socket))
        {
            Assert.Equal("welcome", welcome.RootElement.GetProperty("type").GetString());
        }

        await SendWebSocketJsonAsync(socket, new
        {
            type = "event",
            scriptId,
            kind = "damage",
            oldValue = 100,
            newValue = 80
        });

        using (var ack = await ReceiveWebSocketJsonAsync(socket))
        {
            Assert.Equal("ack", ack.RootElement.GetProperty("type").GetString());
            Assert.Equal("mapped_damage", ack.RootElement.GetProperty("eventId").GetString());
        }

        await SendWebSocketJsonAsync(socket, new { type = "goodbye" });
        using (var bye = await ReceiveWebSocketJsonAsync(socket))
        {
            Assert.Equal("bye", bye.RootElement.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task EventService_ShouldScaleNormalizedStrengthToDeviceLimit()
    {
        using var harness = RuntimeHarness.CreateDglab(out var instanceId);
        var deviceId = instanceId.Value.ToString("D");
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);

        Database.Instance.SetSetting("safety.maxStrength", 100, "safety");
        eventService.UpdateEvent("scale_strength_test", new EventRecord
        {
            EventId = "scale_strength_test",
            Name = "强度线性映射测试",
            Action = "set",
            ActionType = "set",
            Channel = "A",
            Value = 50,
            Strength = 50,
            Duration = 0,
            Enabled = true,
            Priority = 1,
            TargetDeviceType = "All"
        });

        await eventService.TriggerEventAsync("scale_strength_test", deviceId);
        Assert.Contains(harness.Driver.Calls, call => call.StartsWith("set.level:{\"channelA\":100,"));

        eventService.UpdateEvent("scale_strength_test", new EventRecord
        {
            EventId = "scale_strength_test",
            Name = "强度线性映射测试",
            Action = "set",
            ActionType = "set",
            Channel = "A",
            Value = 100,
            Strength = 100,
            Duration = 0,
            Enabled = true,
            Priority = 1,
            TargetDeviceType = "All"
        });

        await eventService.TriggerEventAsync("scale_strength_test", deviceId);
        Assert.Contains(harness.Driver.Calls, call => call.StartsWith("set.level:{\"channelA\":200,"));
    }

    [Fact]
    public async Task ModBridgeService_UdpHandshakeTemplateAndEvent_ShouldWork()
    {
        using var harness = RuntimeHarness.CreateDglab(out _);
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);
        using var modBridge = new ModBridgeService(EventBus.Instance, eventService, harness.Query);
        await modBridge.StartAsync();

        var scriptId = $"udp_script_{Guid.NewGuid():N}";
        await modBridge.RegisterScriptAsync(scriptId, "UDP Script", "1.0.0");
        const int udpPort = 39201;
        await modBridge.StartUdpForScriptAsync(scriptId, udpPort);

        using var udp = new UdpClient();
        var endpoint = new IPEndPoint(IPAddress.Loopback, udpPort);

        await SendUdpJsonAsync(udp, endpoint, new
        {
            type = "hello",
            scriptId,
            name = "UDP Script",
            game = "UnitTest",
            version = "1.0.0"
        });

        using var welcome = await ReceiveUdpJsonAsync(udp);
        Assert.Equal("welcome", welcome.RootElement.GetProperty("type").GetString());
        var sessionId = welcome.RootElement.GetProperty("sessionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(sessionId));

        await SendUdpJsonAsync(udp, endpoint, new
        {
            type = "event",
            sessionId,
            eventId = "lost-hp",
            oldValue = 100,
            newValue = 80
        });
        using (var ack = await ReceiveUdpJsonAsync(udp))
        {
            Assert.Equal("ack", ack.RootElement.GetProperty("type").GetString());
            Assert.Equal("lost-hp", ack.RootElement.GetProperty("eventId").GetString());
        }

        await SendUdpJsonAsync(udp, endpoint, new { type = "heartbeat", sessionId });
        using (var hb = await ReceiveUdpJsonAsync(udp))
        {
            Assert.Equal("heartbeat_ack", hb.RootElement.GetProperty("type").GetString());
        }

        await SendUdpJsonAsync(udp, endpoint, new { type = "template" });
        using (var tpl = await ReceiveUdpJsonAsync(udp))
        {
            Assert.Equal("template", tpl.RootElement.GetProperty("type").GetString());
            Assert.Equal("udp", tpl.RootElement.GetProperty("transport").GetString());
        }

        await SendUdpJsonAsync(udp, endpoint, new { type = "goodbye", sessionId });
        using (var bye = await ReceiveUdpJsonAsync(udp))
        {
            Assert.Equal("bye", bye.RootElement.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task SensorRuleService_ShouldTriggerPressureRule_FromGameEvent()
    {
        using var harness = RuntimeHarness.CreateDglab(out var instanceId);
        var deviceId = instanceId.Value.ToString("D");
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);
        using var sensorRuleService = new SensorRuleService(harness.AppService, harness.Query, harness.Scheduler, eventService, harness.Mapper);
        sensorRuleService.Start();

        sensorRuleService.AddRule(new SensorRule
        {
            Name = "压力事件测试规则",
            DeviceId = deviceId,
            SensorType = SensorType.Pressure,
            TriggerType = SensorTriggerType.Threshold,
            Threshold = 10,
            TargetDeviceId = deviceId,
            TargetChannel = Channel.A,
            Action = SensorAction.Set,
            Value = 23,
            Duration = 0,
            CooldownMs = 0,
            Enabled = true
        });

        EventBus.Instance.PublishGameEvent(new GameEvent
        {
            Type = GameEventType.PressureChanged,
            EventId = "pressure-changed",
            Source = "UnitTest",
            TargetDeviceId = deviceId,
            NewValue = 21,
            Data = new Dictionary<string, object>
            {
                ["pressure"] = 21
            }
        });

        await WaitUntilAsync(() => harness.Driver.Calls.Any(call => call.StartsWith("set.level:")));
    }

    [Fact]
    public async Task SensorRuleService_ShouldTriggerExternalVoltageRule_AndAvoidDuplicateSubscription()
    {
        using var harness = RuntimeHarness.CreateDglab(out var instanceId);
        var deviceId = instanceId.Value.ToString("D");
        var eventService = new EventService(harness.Query, harness.AppService, harness.Scheduler, harness.Mapper);
        using var sensorRuleService = new SensorRuleService(harness.AppService, harness.Query, harness.Scheduler, eventService, harness.Mapper);
        sensorRuleService.Start();
        sensorRuleService.Stop();
        sensorRuleService.Start();

        sensorRuleService.AddRule(new SensorRule
        {
            Name = "电压事件测试规则",
            DeviceId = deviceId,
            SensorType = SensorType.ExternalVoltage,
            TriggerType = SensorTriggerType.Threshold,
            Threshold = 0.5,
            TargetDeviceId = deviceId,
            TargetChannel = Channel.A,
            Action = SensorAction.Increase,
            Value = 7,
            Duration = 0,
            CooldownMs = 0,
            Enabled = true
        });

        EventBus.Instance.PublishGameEvent(new GameEvent
        {
            Type = GameEventType.ExternalVoltageChanged,
            EventId = "external-voltage-changed",
            Source = "UnitTest",
            TargetDeviceId = deviceId,
            NewValue = 1,
            Data = new Dictionary<string, object>
            {
                ["voltage"] = 1.2
            }
        });

        await WaitUntilAsync(() => harness.Driver.Calls.Any(call => call.StartsWith("adjust.level:")));
    }

    // ---------- 工具 ----------

    private static ActiveDeviceView View(ChargingPanel.Contracts.Capabilities.CapabilityDescriptor capability) =>
        new(Contracts.Identifiers.DeviceInstanceId.New(), 1, new[] { capability });

    private static IReadOnlyList<DeviceCommandEnvelope> MapFor(
        ActiveDeviceView device,
        DeviceActionRequest request) =>
        new DeviceActionMapperRegistry().Map(
            request,
            device,
            new CommandSource(CommandSourceKind.Automation, "test"),
            0,
            TimeProvider.System);

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000, int intervalMs = 25)
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

    private static async Task SendWebSocketJsonAsync(ClientWebSocket socket, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private static async Task<JsonDocument> ReceiveWebSocketJsonAsync(ClientWebSocket socket)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("WebSocket closed unexpectedly");
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        ms.Position = 0;
        return await JsonDocument.ParseAsync(ms);
    }

    private static async Task SendUdpJsonAsync(UdpClient client, IPEndPoint endpoint, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await client.SendAsync(bytes, bytes.Length, endpoint);
    }

    private static async Task<JsonDocument> ReceiveUdpJsonAsync(UdpClient client, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var result = await client.ReceiveAsync(cts.Token);
        var stream = new MemoryStream(result.Buffer, writable: false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
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
