using ChargingPanel.Core.Data;
using ChargingPanel.Core.Data.Entities;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Events;
using ChargingPanel.Core.Protocols;
using ChargingPanel.Core.Devices.DGLab;
using ChargingPanel.Core.Devices.Yokonex;
using ChargingPanel.Core.Network;
using ChargingPanel.Core.OCR;
using ChargingPanel.Core.Scripting;
using ChargingPanel.Core.Services;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DeviceType = ChargingPanel.Core.Devices.DeviceType;
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
        using var deviceManager = new DeviceManager();
        var eventService = new EventService(deviceManager);
        var deviceId = await AddConnectedVirtualDeviceAsync(deviceManager);

        await eventService.TriggerEventAsync("lost-hp", deviceId);
        var infoAfterDefault = deviceManager.GetDeviceInfo(deviceId);
        Assert.True(infoAfterDefault.State.Strength.ChannelA > 0);

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

        await deviceManager.SetStrengthAsync(deviceId, Channel.A, 0, StrengthMode.Set);
        await eventService.TriggerEventAsync("lost-hp", deviceId);

        var infoAfterOverride = deviceManager.GetDeviceInfo(deviceId);
        Assert.Equal(66, infoAfterOverride.State.Strength.ChannelA);
    }

    [Fact]
    public async Task EventProcessor_ShouldBridgeEventBusToRuleEngine()
    {
        using var deviceManager = new DeviceManager();
        var eventService = new EventService(deviceManager);
        using var processor = new EventProcessor(deviceManager, eventService, EventBus.Instance);
        var deviceId = await AddConnectedVirtualDeviceAsync(deviceManager);

        EventBus.Instance.PublishGameEvent(new GameEvent
        {
            Type = GameEventType.HealthLost,
            EventId = "script_health_lost",
            Source = "UnitTest",
            OldValue = 100,
            NewValue = 80
        });

        await WaitUntilAsync(() => deviceManager.GetDeviceInfo(deviceId).State.Strength.ChannelA > 0);
        var info = deviceManager.GetDeviceInfo(deviceId);
        Assert.True(info.State.Strength.ChannelA >= 15);
    }

    [Fact]
    public async Task EventProcessor_ShouldRespectSkipFlag()
    {
        using var deviceManager = new DeviceManager();
        var eventService = new EventService(deviceManager);
        using var processor = new EventProcessor(deviceManager, eventService, EventBus.Instance);
        var deviceId = await AddConnectedVirtualDeviceAsync(deviceManager);

        await deviceManager.SetStrengthAsync(deviceId, Channel.A, 0, StrengthMode.Set);

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
        var info = deviceManager.GetDeviceInfo(deviceId);
        Assert.Equal(0, info.State.Strength.ChannelA);
    }

    [Fact]
    public void EventService_ShouldExcludeNonRuleDeviceTelemetryEvents()
    {
        using var deviceManager = new DeviceManager();
        var eventService = new EventService(deviceManager);

        Assert.NotNull(eventService.GetEvent("lost-hp"));
        Assert.Null(eventService.GetEvent("pressure-changed"));
        Assert.Null(eventService.GetEvent("device-battery-changed"));
        Assert.Null(eventService.GetEvent("query"));
        Assert.Null(eventService.GetEvent("new-credit"));
    }

    [Fact]
    public async Task EventProcessor_ShouldIgnoreNonRuleTelemetryEventMapping()
    {
        using var deviceManager = new DeviceManager();
        var eventService = new EventService(deviceManager);
        using var processor = new EventProcessor(deviceManager, eventService, EventBus.Instance);
        var deviceId = await AddConnectedVirtualDeviceAsync(deviceManager);

        await deviceManager.SetStrengthAsync(deviceId, Channel.A, 0, StrengthMode.Set);

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
        var info = deviceManager.GetDeviceInfo(deviceId);
        Assert.Equal(0, info.State.Strength.ChannelA);
    }

    [Fact]
    public async Task EventProcessor_ShouldHandleRemoteControlCommand()
    {
        using var deviceManager = new DeviceManager();
        var eventService = new EventService(deviceManager);
        using var processor = new EventProcessor(deviceManager, eventService, EventBus.Instance);
        var deviceId = await AddConnectedVirtualDeviceAsync(deviceManager);

        EventBus.Instance.PublishRemoteSync(new RemoteSyncEvent
        {
            Type = RemoteSyncType.ControlCommand,
            SenderId = "remote-user",
            Payload = new DeviceControlEvent
            {
                DeviceId = deviceId,
                Channel = ChannelTarget.A,
                Action = EventAction.Set,
                Value = 42,
                Duration = 0,
                Source = "UnitTestRemote"
            }
        });

        await WaitUntilAsync(() => deviceManager.GetDeviceInfo(deviceId).State.Strength.ChannelA == 42);
        var info = deviceManager.GetDeviceInfo(deviceId);
        Assert.Equal(42, info.State.Strength.ChannelA);
    }

    [Fact]
    public async Task EventService_ShouldRespectTargetDeviceTypeFilter()
    {
        using var deviceManager = new DeviceManager();
        var eventService = new EventService(deviceManager);

        var dglabId = await AddConnectedVirtualDeviceAsync(deviceManager, DeviceType.DGLab);
        var yokonexId = await AddConnectedVirtualDeviceAsync(deviceManager, DeviceType.Yokonex);

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

        var dglab = deviceManager.GetDeviceInfo(dglabId);
        var yokonex = deviceManager.GetDeviceInfo(yokonexId);

        Assert.Equal(110, dglab.State.Strength.ChannelA);
        Assert.Equal(0, yokonex.State.Strength.ChannelA);
    }

    [Fact]
    public async Task EventProcessor_ShouldPreferExplicitEventIdOverTypeMapping()
    {
        using var deviceManager = new DeviceManager();
        var eventService = new EventService(deviceManager);
        using var processor = new EventProcessor(deviceManager, eventService, EventBus.Instance);
        var deviceId = await AddConnectedVirtualDeviceAsync(deviceManager, DeviceType.DGLab);

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

        await WaitUntilAsync(() => deviceManager.GetDeviceInfo(deviceId).State.Strength.ChannelA == 154);
        var info = deviceManager.GetDeviceInfo(deviceId);
        Assert.Equal(154, info.State.Strength.ChannelA);
    }

    [Fact]
    public async Task DeviceActionTranslatorRegistry_ShouldRouteToyModeAction()
    {
        var registry = new DeviceActionTranslatorRegistry();
        var fakeToy = new FakeToyDevice();

        await registry.ExecuteAsync(fakeToy, new DeviceActionRequest
        {
            EventId = "toy-mode",
            ActionType = "vibrate_mode",
            Value = 0,
            RawValue = 6,
            DurationMs = 0,
            Channels = new[] { Channel.A }
        });

        Assert.Equal(6, fakeToy.LastMode);
    }

    [Fact]
    public async Task DeviceActionTranslatorRegistry_ShouldRouteToyMotorAction_AndMapStrength()
    {
        var registry = new DeviceActionTranslatorRegistry();
        var fakeToy = new FakeToyDevice();

        await registry.ExecuteAsync(fakeToy, new DeviceActionRequest
        {
            EventId = "toy-motor-2",
            ActionType = "toy_motor_2",
            Value = 60,
            RawValue = 60,
            DurationMs = 0,
            Channels = Array.Empty<Channel>()
        });

        Assert.Equal(2, fakeToy.LastMotorIndex);
        Assert.Equal(12, fakeToy.LastMotorStrength);
    }

    [Fact]
    public async Task DeviceActionTranslatorRegistry_ShouldRouteToyQueryDeviceInfoAction()
    {
        var registry = new DeviceActionTranslatorRegistry();
        var fakeToy = new FakeToyDevice();

        await registry.ExecuteAsync(fakeToy, new DeviceActionRequest
        {
            EventId = "toy-query-device-info",
            ActionType = "query_device_info",
            Value = 0,
            RawValue = 0,
            DurationMs = 0,
            Channels = Array.Empty<Channel>()
        });

        Assert.True(fakeToy.QueryDeviceInfoCalled);
    }

    [Fact]
    public async Task DeviceActionTranslatorRegistry_ShouldRouteEmsCustomWaveformAction()
    {
        var registry = new DeviceActionTranslatorRegistry();
        var fakeEms = new FakeEmsDevice();

        await registry.ExecuteAsync(fakeEms, new DeviceActionRequest
        {
            EventId = "ems-custom-wave",
            ActionType = "custom_waveform",
            Value = 50,
            RawValue = 50,
            DurationMs = 20,
            WaveformData = "50,20",
            Channels = new[] { Channel.A }
        });

        Assert.Equal(Channel.A, fakeEms.LastCustomWaveformChannel);
        Assert.Equal(50, fakeEms.LastCustomWaveformFrequency);
        Assert.Equal(20, fakeEms.LastCustomWaveformPulseTime);
    }

    [Fact]
    public async Task DeviceActionTranslatorRegistry_ShouldRouteGenericWaveToDeviceWaveformApi()
    {
        var registry = new DeviceActionTranslatorRegistry();
        var fakeEms = new FakeEmsDevice();

        await registry.ExecuteAsync(fakeEms, new DeviceActionRequest
        {
            EventId = "ems-wave-generic",
            ActionType = "wave",
            Value = 42,
            RawValue = 42,
            DurationMs = 1200,
            WaveformData = "pulse",
            Channels = new[] { Channel.B }
        });

        Assert.NotNull(fakeEms.LastWaveform);
        Assert.Equal(42, fakeEms.LastWaveform!.Strength);
        Assert.Equal(1200, fakeEms.LastWaveform.Duration);
        Assert.Equal(150, fakeEms.LastWaveform.Frequency);
    }

    [Fact]
    public async Task DeviceActionTranslatorRegistry_ShouldRouteSmartLockActions()
    {
        var registry = new DeviceActionTranslatorRegistry();
        var fakeLock = new FakeSmartLockDevice();

        await registry.ExecuteAsync(fakeLock, new DeviceActionRequest
        {
            EventId = "lock-open",
            ActionType = "smart_unlock",
            Value = 0,
            DurationMs = 0,
            Channels = Array.Empty<Channel>()
        });

        await registry.ExecuteAsync(fakeLock, new DeviceActionRequest
        {
            EventId = "lock-temp",
            ActionType = "smart_temp_unlock",
            Value = 15,
            DurationMs = 0,
            Channels = Array.Empty<Channel>()
        });

        Assert.True(fakeLock.UnlockCalled);
        Assert.Equal(15, fakeLock.LastTemporaryUnlockSeconds);
    }

    [Fact]
    public async Task EventService_ShouldSupportActionAliasNormalization()
    {
        using var deviceManager = new DeviceManager();
        var eventService = new EventService(deviceManager);
        var deviceId = await AddConnectedVirtualDeviceAsync(deviceManager, DeviceType.DGLab);

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
        var info = deviceManager.GetDeviceInfo(deviceId);
        Assert.Equal(24, info.State.Strength.ChannelA);
    }

    [Fact]
    public async Task EventService_ShouldBlockLowerPriorityEvent_DuringPriorityWindow()
    {
        using var deviceManager = new DeviceManager();
        var eventService = new EventService(deviceManager);
        var deviceId = await AddConnectedVirtualDeviceAsync(deviceManager, DeviceType.DGLab);

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

        var info = deviceManager.GetDeviceInfo(deviceId);
        Assert.Equal(160, info.State.Strength.ChannelA);
    }

    [Fact]
    public void OCRService_ShouldPersistArmorAndModelSettings()
    {
        using var deviceManager = new DeviceManager();
        var eventService = new EventService(deviceManager);
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
    public async Task DeviceManager_ShouldNormalizeLegacyIMConfigToConnectCode_WhenAddingDevice()
    {
        using var deviceManager = new DeviceManager();

        var deviceId = await deviceManager.AddDeviceAsync(
            DeviceType.Yokonex,
            new ConnectionConfig
            {
                UserId = "game_42",
                Token = "legacy_token_value",
                AutoReconnect = false
            },
            name: "Legacy-IM-Device",
            isVirtual: true,
            mode: ConnectionMode.TencentIM,
            yokonexType: YokonexDeviceType.Estim);

        var record = Database.Instance.GetAllDevices().Single(r => r.Id == deviceId);
        Assert.False(string.IsNullOrWhiteSpace(record.Config));
        var savedConfig = JsonSerializer.Deserialize<ConnectionConfig>(record.Config);

        Assert.NotNull(savedConfig);
        Assert.Equal("game_42 legacy_token_value", savedConfig!.ConnectCode);
        Assert.Equal(ConnectionMode.TencentIM, savedConfig.ConnectionMode);
    }

    [Fact]
    public async Task DeviceManager_ShouldRejectIMConnection_WhenConnectCodeMissing()
    {
        using var deviceManager = new DeviceManager();

        var deviceId = await deviceManager.AddDeviceAsync(
            DeviceType.Yokonex,
            new ConnectionConfig
            {
                AutoReconnect = false
            },
            name: "Invalid-IM-Device",
            isVirtual: true,
            mode: ConnectionMode.TencentIM,
            yokonexType: YokonexDeviceType.Estim);

        await Assert.ThrowsAsync<ArgumentException>(() => deviceManager.ConnectDeviceAsync(deviceId));
    }

    [Fact]
    public async Task DeviceManager_ShouldConnectVirtualYokonex_InBluetoothModeWithoutAddress()
    {
        using var deviceManager = new DeviceManager();

        var deviceId = await deviceManager.AddDeviceAsync(
            DeviceType.Yokonex,
            new ConnectionConfig
            {
                ConnectionMode = ConnectionMode.Bluetooth,
                AutoReconnect = false
            },
            name: "虚拟役次元蓝牙测试",
            isVirtual: true,
            mode: ConnectionMode.Bluetooth,
            yokonexType: ChargingPanel.Core.Devices.Yokonex.YokonexDeviceType.Estim);

        await deviceManager.ConnectDeviceAsync(deviceId);

        var info = deviceManager.GetDeviceInfo(deviceId);
        Assert.Equal(DeviceStatus.Connected, info.Status);
        Assert.True(info.IsVirtual);
        Assert.Equal(DeviceType.Yokonex, info.Type);
        Assert.Equal(ConnectionMode.Bluetooth, info.ConnectionMode);
    }

    [Fact]
    public async Task DeviceManager_ShouldMapLegacyApiBridgeModeToTencentIM()
    {
        using var deviceManager = new DeviceManager();
        const ConnectionMode legacyApiBridgeMode = (ConnectionMode)3;

        var deviceId = await deviceManager.AddDeviceAsync(
            DeviceType.Yokonex,
            new ConnectionConfig
            {
                ConnectionMode = legacyApiBridgeMode,
                ConnectCode = "game_42 token_value",
                AutoReconnect = false
            },
            name: "LegacyApiBridge-Device",
            isVirtual: false,
            mode: legacyApiBridgeMode,
            yokonexType: YokonexDeviceType.Estim);

        var info = deviceManager.GetDeviceInfo(deviceId);
        var adapter = deviceManager.GetDevice(deviceId);

        Assert.Equal(ConnectionMode.TencentIM, info.ConnectionMode);
        Assert.Equal(YokonexDeviceType.Estim, info.YokonexType);
        Assert.Equal(YokonexProtocolGeneration.IMEvent, info.YokonexProtocolGeneration);
        Assert.IsType<YokonexIMAdapter>(adapter);
    }

    [Fact]
    public async Task DeviceManager_ShouldApplyStrength_ForAllVirtualYokonexTypes()
    {
        using var deviceManager = new DeviceManager();

        async Task<string> AddAndConnectVirtualAsync(YokonexDeviceType type, string name)
        {
            var id = await deviceManager.AddDeviceAsync(
                DeviceType.Yokonex,
                new ConnectionConfig
                {
                    ConnectionMode = ConnectionMode.Bluetooth,
                    AutoReconnect = false
                },
                name: name,
                isVirtual: true,
                mode: ConnectionMode.Bluetooth,
                yokonexType: type);
            await deviceManager.ConnectDeviceAsync(id);
            return id;
        }

        var estimId = await AddAndConnectVirtualAsync(YokonexDeviceType.Estim, "virtual-estim");
        var enemaId = await AddAndConnectVirtualAsync(YokonexDeviceType.Enema, "virtual-enema");
        var vibratorId = await AddAndConnectVirtualAsync(YokonexDeviceType.Vibrator, "virtual-vibrator");
        var cupId = await AddAndConnectVirtualAsync(YokonexDeviceType.Cup, "virtual-cup");

        await deviceManager.SetStrengthAsync(estimId, Channel.AB, 60, StrengthMode.Set);
        await deviceManager.SetStrengthAsync(enemaId, Channel.AB, 60, StrengthMode.Set);
        await deviceManager.SetStrengthAsync(vibratorId, Channel.AB, 60, StrengthMode.Set);
        await deviceManager.SetStrengthAsync(cupId, Channel.AB, 60, StrengthMode.Set);

        Assert.True(deviceManager.GetDeviceInfo(estimId).State.Strength.ChannelA > 0);
        Assert.True(deviceManager.GetDeviceInfo(enemaId).State.Strength.ChannelA > 0);
        Assert.True(deviceManager.GetDeviceInfo(vibratorId).State.Strength.ChannelA > 0);
        Assert.True(deviceManager.GetDeviceInfo(cupId).State.Strength.ChannelA > 0);
    }

    [Fact]
    public async Task DeviceManager_ShouldCreateSmartLockPlaceholderAdapter()
    {
        using var deviceManager = new DeviceManager();

        var deviceId = await deviceManager.AddDeviceAsync(
            DeviceType.Yokonex,
            new ConnectionConfig
            {
                ConnectionMode = ConnectionMode.Bluetooth,
                Address = "00:00:00:00:00:00",
                AutoReconnect = false
            },
            name: "SmartLock-Placeholder",
            isVirtual: false,
            mode: ConnectionMode.Bluetooth,
            yokonexType: YokonexDeviceType.SmartLock);

        var info = deviceManager.GetDeviceInfo(deviceId);
        var adapter = deviceManager.GetDevice(deviceId);

        Assert.Equal(YokonexDeviceType.SmartLock, info.YokonexType);
        Assert.Equal(YokonexProtocolGeneration.SmartLockReserved, info.YokonexProtocolGeneration);
        Assert.IsType<YokonexSmartLockPlaceholderAdapter>(adapter);
    }

    [Fact]
    public async Task DeviceActionTranslatorRegistry_ShouldRouteGameCmdAction()
    {
        var registry = new DeviceActionTranslatorRegistry();
        var fakeCommandDevice = new FakeCommandDevice();

        await registry.ExecuteAsync(fakeCommandDevice, new DeviceActionRequest
        {
            EventId = "fallback_cmd",
            ActionType = "command",
            WaveformData = "player_hurt",
            Value = 0,
            DurationMs = 0,
            Channels = Array.Empty<Channel>()
        });

        Assert.Equal("player_hurt", fakeCommandDevice.LastCommandId);
    }

    [Fact]
    public async Task DeviceActionTranslatorRegistry_ShouldRouteStopAllToGlobalCommand()
    {
        var registry = new DeviceActionTranslatorRegistry();
        var fakeCommandDevice = new FakeCommandDevice();

        await registry.ExecuteAsync(fakeCommandDevice, new DeviceActionRequest
        {
            EventId = "global-stop",
            ActionType = "stop_all",
            Value = 0,
            DurationMs = 0,
            Channels = Array.Empty<Channel>()
        });

        Assert.Equal("_stop_all", fakeCommandDevice.LastCommandId);
    }

    [Fact]
    public async Task EventService_ShouldRegisterAndCleanupModSessionRules()
    {
        using var deviceManager = new DeviceManager();
        var eventService = new EventService(deviceManager);
        var deviceId = await AddConnectedVirtualDeviceAsync(deviceManager, DeviceType.DGLab);
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
        var info = deviceManager.GetDeviceInfo(deviceId);
        Assert.Equal(84, info.State.Strength.ChannelA);

        var removed = eventService.UnregisterModRulesForSession(sessionId);
        Assert.Equal(1, removed);
        Assert.Null(eventService.GetEvent("mod-player-hurt"));
    }

    [Fact]
    public async Task EventProcessor_ShouldRespectModRuleCustomTriggerCondition()
    {
        using var deviceManager = new DeviceManager();
        var eventService = new EventService(deviceManager);
        using var processor = new EventProcessor(deviceManager, eventService, EventBus.Instance);
        var deviceId = await AddConnectedVirtualDeviceAsync(deviceManager, DeviceType.DGLab);

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
        await deviceManager.SetStrengthAsync(deviceId, Channel.A, 0, StrengthMode.Set);

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
        Assert.Equal(0, deviceManager.GetDeviceInfo(deviceId).State.Strength.ChannelA);

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
        Assert.Equal(0, deviceManager.GetDeviceInfo(deviceId).State.Strength.ChannelA);

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

        await WaitUntilAsync(() => deviceManager.GetDeviceInfo(deviceId).State.Strength.ChannelA > 0, 1500, 50);
        Assert.Equal(60, deviceManager.GetDeviceInfo(deviceId).State.Strength.ChannelA);
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
        using var deviceManager = new DeviceManager();
        var eventService = new EventService(deviceManager);
        using var modBridge = new ModBridgeService(EventBus.Instance, eventService, deviceManager);
        await modBridge.StartAsync();
        using var scriptEngine = new ScriptEngine(deviceManager, eventService, modBridge);

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
        using var deviceManager = new DeviceManager();
        var eventService = new EventService(deviceManager);
        using var modBridge = new ModBridgeService(EventBus.Instance, eventService, deviceManager);
        await modBridge.StartAsync();
        using var scriptEngine = new ScriptEngine(deviceManager, eventService, modBridge);

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
        using var deviceManager = new DeviceManager();
        var eventService = new EventService(deviceManager);
        var deviceId = await AddConnectedVirtualDeviceAsync(deviceManager, DeviceType.DGLab);
        await deviceManager.SetLimitsAsync(deviceId, 200, 200);

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
        var info = deviceManager.GetDeviceInfo(deviceId);
        Assert.Equal(100, info.State.Strength.ChannelA);

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
        info = deviceManager.GetDeviceInfo(deviceId);
        Assert.Equal(200, info.State.Strength.ChannelA);
    }

    [Fact]
    public async Task ModBridgeService_UdpHandshakeTemplateAndEvent_ShouldWork()
    {
        using var deviceManager = new DeviceManager();
        var eventService = new EventService(deviceManager);
        using var modBridge = new ModBridgeService(EventBus.Instance, eventService, deviceManager);
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
        using var deviceManager = new DeviceManager();
        var eventService = new EventService(deviceManager);
        using var sensorRuleService = new SensorRuleService(deviceManager, eventService);
        sensorRuleService.Start();

        var deviceId = await AddConnectedVirtualDeviceAsync(deviceManager, DeviceType.DGLab);

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

        await WaitUntilAsync(() => deviceManager.GetDeviceInfo(deviceId).State.Strength.ChannelA == 23);
        var info = deviceManager.GetDeviceInfo(deviceId);
        Assert.Equal(23, info.State.Strength.ChannelA);
    }

    [Fact]
    public async Task SensorRuleService_ShouldTriggerExternalVoltageRule_AndAvoidDuplicateSubscription()
    {
        using var deviceManager = new DeviceManager();
        var eventService = new EventService(deviceManager);
        using var sensorRuleService = new SensorRuleService(deviceManager, eventService);
        sensorRuleService.Start();
        sensorRuleService.Stop();
        sensorRuleService.Start();

        var deviceId = await AddConnectedVirtualDeviceAsync(deviceManager, DeviceType.DGLab);

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

        await WaitUntilAsync(() => deviceManager.GetDeviceInfo(deviceId).State.Strength.ChannelA == 7);
        var info = deviceManager.GetDeviceInfo(deviceId);
        Assert.Equal(7, info.State.Strength.ChannelA);
    }

    private static async Task<string> AddConnectedVirtualDeviceAsync(DeviceManager deviceManager, DeviceType type = DeviceType.DGLab)
    {
        var id = await deviceManager.AddDeviceAsync(
            type,
            new ConnectionConfig
            {
                ConnectionMode = ConnectionMode.WebSocket,
                AutoReconnect = false
            },
            name: "虚拟测试设备",
            isVirtual: true,
            mode: ConnectionMode.WebSocket);

        await deviceManager.ConnectDeviceAsync(id);
        return id;
    }

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

    private sealed class FakeToyDevice : IYokonexToyDevice
    {
        public string Id { get; } = "fake_toy";
        public string Name { get; set; } = "FakeToy";
        public DeviceType Type => DeviceType.Yokonex;
        public DeviceStatus Status => DeviceStatus.Connected;
        public DeviceState State => new()
        {
            Status = DeviceStatus.Connected,
            Strength = new StrengthInfo()
        };
        public ConnectionConfig? Config => new();
        public YokonexDeviceType YokonexType => YokonexDeviceType.Vibrator;
        public (int Motor1, int Motor2, int Motor3) MotorStrengths => (0, 0, 0);
        public int LastMode { get; private set; }
        public int LastMotorIndex { get; private set; }
        public int LastMotorStrength { get; private set; }
        public (int m1, int m2, int m3) LastAllMotorStrengths { get; private set; }
        public bool StopAllCalled { get; private set; }
        public bool QueryDeviceInfoCalled { get; private set; }

#pragma warning disable CS0067
        public event EventHandler<DeviceStatus>? StatusChanged;
        public event EventHandler<StrengthInfo>? StrengthChanged;
        public event EventHandler<int>? BatteryChanged;
        public event EventHandler<Exception>? ErrorOccurred;
        public event EventHandler<(int Motor1, int Motor2, int Motor3)>? MotorStrengthChanged;
#pragma warning restore CS0067

        public Task ConnectAsync(ConnectionConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task SetStrengthAsync(Channel channel, int value, StrengthMode mode = StrengthMode.Set) => Task.CompletedTask;
        public Task SendWaveformAsync(Channel channel, WaveformData waveform) => Task.CompletedTask;
        public Task ClearWaveformQueueAsync(Channel channel) => Task.CompletedTask;
        public Task SetLimitsAsync(int limitA, int limitB) => Task.CompletedTask;

        public Task SetMotorStrengthAsync(int motor, int strength)
        {
            LastMotorIndex = motor;
            LastMotorStrength = strength;
            return Task.CompletedTask;
        }

        public Task SetAllMotorsAsync(int strength1, int strength2, int strength3)
        {
            LastAllMotorStrengths = (strength1, strength2, strength3);
            return Task.CompletedTask;
        }

        public Task QueryDeviceInfoAsync()
        {
            QueryDeviceInfoCalled = true;
            return Task.CompletedTask;
        }

        public Task SetFixedModeAsync(int mode)
        {
            LastMode = mode;
            return Task.CompletedTask;
        }

        public Task StopAllMotorsAsync()
        {
            StopAllCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEmsDevice : IYokonexEmsDevice
    {
        public string Id { get; } = "fake_ems";
        public string Name { get; set; } = "FakeEms";
        public DeviceType Type => DeviceType.Yokonex;
        public DeviceStatus Status => DeviceStatus.Connected;
        public DeviceState State => new()
        {
            Status = DeviceStatus.Connected,
            Strength = new StrengthInfo()
        };
        public ConnectionConfig? Config => new();
        public YokonexDeviceType YokonexType => YokonexDeviceType.Estim;
        public int StepCount => 0;
        public (float X, float Y, float Z) CurrentAngle => (0, 0, 0);
        public (bool ChannelA, bool ChannelB) ChannelConnectionState => (true, true);
        public YokonexMotorState LastMotorState { get; private set; } = YokonexMotorState.Off;
        public Channel LastFixedModeChannel { get; private set; } = Channel.A;
        public int LastFixedMode { get; private set; }
        public Channel LastCustomWaveformChannel { get; private set; } = Channel.A;
        public int LastCustomWaveformFrequency { get; private set; }
        public int LastCustomWaveformPulseTime { get; private set; }
        public WaveformData? LastWaveform { get; private set; }

#pragma warning disable CS0067
        public event EventHandler<DeviceStatus>? StatusChanged;
        public event EventHandler<StrengthInfo>? StrengthChanged;
        public event EventHandler<int>? BatteryChanged;
        public event EventHandler<Exception>? ErrorOccurred;
        public event EventHandler<YokonexMotorState>? MotorStateChanged;
        public event EventHandler<int>? StepCountChanged;
        public event EventHandler<(float X, float Y, float Z)>? AngleChanged;
        public event EventHandler<(bool ChannelA, bool ChannelB)>? ChannelConnectionChanged;
#pragma warning restore CS0067

        public Task ConnectAsync(ConnectionConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task SetStrengthAsync(Channel channel, int value, StrengthMode mode = StrengthMode.Set) => Task.CompletedTask;
        public Task SendWaveformAsync(Channel channel, WaveformData waveform)
        {
            LastWaveform = waveform;
            return Task.CompletedTask;
        }
        public Task ClearWaveformQueueAsync(Channel channel) => Task.CompletedTask;
        public Task SetLimitsAsync(int limitA, int limitB) => Task.CompletedTask;
        public Task SetMotorStateAsync(YokonexMotorState state)
        {
            LastMotorState = state;
            return Task.CompletedTask;
        }

        public Task SetPedometerStateAsync(PedometerState state) => Task.CompletedTask;
        public Task SetAngleSensorEnabledAsync(bool enabled) => Task.CompletedTask;

        public Task SetCustomWaveformAsync(Channel channel, int frequency, int pulseTime)
        {
            LastCustomWaveformChannel = channel;
            LastCustomWaveformFrequency = frequency;
            LastCustomWaveformPulseTime = pulseTime;
            return Task.CompletedTask;
        }

        public Task SetFixedModeAsync(Channel channel, int mode)
        {
            LastFixedModeChannel = channel;
            LastFixedMode = mode;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSmartLockDevice : IYokonexSmartLockDevice
    {
        public string Id { get; } = "fake_lock";
        public string Name { get; set; } = "FakeSmartLock";
        public DeviceType Type => DeviceType.Yokonex;
        public DeviceStatus Status => DeviceStatus.Connected;
        public DeviceState State => new()
        {
            Status = DeviceStatus.Connected,
            Strength = new StrengthInfo()
        };
        public ConnectionConfig? Config => new();
        public YokonexDeviceType YokonexType => YokonexDeviceType.SmartLock;
        public bool LockCalled { get; private set; }
        public bool UnlockCalled { get; private set; }
        public int LastTemporaryUnlockSeconds { get; private set; }

#pragma warning disable CS0067
        public event EventHandler<DeviceStatus>? StatusChanged;
        public event EventHandler<StrengthInfo>? StrengthChanged;
        public event EventHandler<int>? BatteryChanged;
        public event EventHandler<Exception>? ErrorOccurred;
        public event EventHandler<YokonexSmartLockState>? StateChanged;
#pragma warning restore CS0067

        public Task ConnectAsync(ConnectionConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task SetStrengthAsync(Channel channel, int value, StrengthMode mode = StrengthMode.Set) => Task.CompletedTask;
        public Task SendWaveformAsync(Channel channel, WaveformData waveform) => Task.CompletedTask;
        public Task ClearWaveformQueueAsync(Channel channel) => Task.CompletedTask;
        public Task SetLimitsAsync(int limitA, int limitB) => Task.CompletedTask;
        public Task<YokonexSmartLockState> QueryStateAsync() => Task.FromResult(new YokonexSmartLockState());
        public Task LockAsync()
        {
            LockCalled = true;
            return Task.CompletedTask;
        }

        public Task UnlockAsync()
        {
            UnlockCalled = true;
            return Task.CompletedTask;
        }

        public Task TemporaryUnlockAsync(int seconds)
        {
            LastTemporaryUnlockSeconds = seconds;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCommandDevice : IYokonexCommandDevice
    {
        public string Id { get; } = "fake_command";
        public string Name { get; set; } = "FakeCommand";
        public DeviceType Type => DeviceType.Yokonex;
        public DeviceStatus Status => DeviceStatus.Connected;
        public DeviceState State => new()
        {
            Status = DeviceStatus.Connected,
            Strength = new StrengthInfo()
        };
        public ConnectionConfig? Config => new();
        public YokonexDeviceType YokonexType => YokonexDeviceType.Estim;
        public string? LastCommandId { get; private set; }
        public int LastGameInfo { get; private set; } = -1;

#pragma warning disable CS0067
        public event EventHandler<DeviceStatus>? StatusChanged;
        public event EventHandler<StrengthInfo>? StrengthChanged;
        public event EventHandler<int>? BatteryChanged;
        public event EventHandler<Exception>? ErrorOccurred;
#pragma warning restore CS0067

        public Task ConnectAsync(ConnectionConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task SetStrengthAsync(Channel channel, int value, StrengthMode mode = StrengthMode.Set) => Task.CompletedTask;
        public Task SendWaveformAsync(Channel channel, WaveformData waveform) => Task.CompletedTask;
        public Task ClearWaveformQueueAsync(Channel channel) => Task.CompletedTask;
        public Task SetLimitsAsync(int limitA, int limitB) => Task.CompletedTask;

        public Task SendGameCommandAsync(string eventId)
        {
            LastCommandId = eventId;
            return Task.CompletedTask;
        }

        public Task SendGameInfoAsync(int data)
        {
            LastGameInfo = data;
            return Task.CompletedTask;
        }
    }
}
