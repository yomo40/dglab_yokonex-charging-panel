using System.Text.Json;
using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Vendor.DGLab;
using ChargingPanel.Vendor.DGLab.Common;
using ChargingPanel.Core.Devices.Catalog;
using ChargingPanel.Core.Devices.Discovery;
using ChargingPanel.Core.Devices.Registry;
using ChargingPanel.Core.Devices.Runtime;
using ChargingPanel.Core.Devices.Safety;
using ChargingPanel.Core.Devices.Sessions;

namespace ChargingPanel.DeviceIntegration.Tests.Fixtures.DGLab;

/// <summary>
/// DG-LAB 运行时测试装置：装配 Catalog→Registry→Session→Dispatcher→Driver→Telemetry 全链路。
/// 生产组合根做同样的装配，此处用假租约与假传输 Broker 覆盖。
/// </summary>
public sealed class DGLabRuntimeHarness : IDisposable
{
    private readonly DGLabPlugin _plugin;
    private readonly TimeProvider _timeProvider;
    private readonly DeviceCatalog _catalog;
    private readonly DeviceRegistry _registry;
    private readonly DiscoveryCoordinator _discovery;
    private readonly FakeDGLabTransportBroker _broker;
    private readonly SessionCoordinator _sessions;
    private readonly DeviceRuntimeRegistry _runtime;
    private readonly SafetySupervisor _safety;
    private readonly CommandDispatcher _dispatcher;
    private readonly Dictionary<DeviceInstanceId, IProtocolDriver> _drivers = [];

    public DGLabRuntimeHarness(DGLabPlugin? plugin = null, TimeProvider? timeProvider = null)
    {
        _plugin = plugin ?? new DGLabPlugin();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _catalog = new DeviceCatalog([_plugin]);
        _registry = new DeviceRegistry(_catalog);
        _discovery = new DiscoveryCoordinator(_catalog, _registry);
        _broker = new FakeDGLabTransportBroker(_plugin);
        var selector = new EndpointSelector(_timeProvider);
        _sessions = new SessionCoordinator(
            _broker,
            selector,
            _timeProvider,
            new SessionRecoveryPolicy(3, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(20)));
        _runtime = new DeviceRuntimeRegistry();
        _safety = new SafetySupervisor(_timeProvider);
        _safety.AttachRuntime(_runtime);
        _dispatcher = new CommandDispatcher(_safety, _runtime, _timeProvider);
    }

    public DGLabPlugin Plugin => _plugin;

    public DeviceCatalog Catalog => _catalog;

    public DeviceRegistry Registry => _registry;

    public DiscoveryCoordinator Discovery => _discovery;

    public FakeDGLabTransportBroker Broker => _broker;

    public SessionCoordinator Sessions => _sessions;

    public DeviceRuntimeRegistry Runtime => _runtime;

    public SafetySupervisor Safety => _safety;

    public CommandDispatcher Dispatcher => _dispatcher;

    /// <summary>观察发现广播并连接；返回注册的设备实例。</summary>
    public async Task<DeviceInstance> ConnectAdvertisementAsync(
        DiscoveryAdvertisement advertisement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        var result = _discovery.Observe(advertisement);
        if (result.DeviceInstance is null)
        {
            throw new InvalidOperationException("发现结果未注册设备实例。");
        }

        var connected = await TryConnectInstanceAsync(result.DeviceInstance, cancellationToken);
        if (!connected)
        {
            throw new InvalidOperationException("会话连接失败。");
        }

        return result.DeviceInstance;
    }

    /// <summary>连接已注册实例并刷新运行时绑定。</summary>
    public async Task<bool> TryConnectInstanceAsync(
        DeviceInstance instance,
        CancellationToken cancellationToken = default)
    {
        var candidates = instance.Endpoints
            .Select(endpoint => new EndpointCandidate(endpoint.Id, endpoint.ProfileId, endpoint.TransportKind, 0, true))
            .ToArray();
        var connect = await _sessions.ConnectAsync(instance.Id, candidates, cancellationToken);
        if (!connect.IsConnected)
        {
            return false;
        }

        RefreshBinding(instance);
        return true;
    }

    /// <summary>按当前会话快照刷新运行时绑定（重连后必须调用）。</summary>
    public void RefreshBinding(DeviceInstance instance)
    {
        var session = _sessions.GetSession(instance.Id);
        if (session is null || session.EndpointId is null || session.State != SessionState.Ready)
        {
            return;
        }

        var driver = _broker.GetDriver(session.EndpointId.Value);
        (driver as DGLabDriverBase)?.Configure(instance.Id, session.SessionEpoch);
        if (!_catalog.TryGetDeviceProfile(instance.PluginId, instance.ModelId, instance.ProfileId, out var profile) ||
            profile is null)
        {
            return;
        }

        _runtime.Register(new DeviceRuntimeBinding(
            instance.Id,
            driver,
            profile.Profile.Capabilities,
            session.SessionEpoch));
        _drivers[instance.Id] = driver;
    }

    /// <summary>取实例当前绑定的驱动。</summary>
    public T GetDriver<T>(DeviceInstance instance)
        where T : class =>
        _drivers.TryGetValue(instance.Id, out var driver)
            ? driver as T ?? throw new InvalidOperationException($"驱动类型不匹配，期望 {typeof(T).Name}。")
            : throw new InvalidOperationException("实例尚未绑定驱动。");

    /// <summary>构造命令信封。epoch 自动取自当前会话与安全锁。</summary>
    public DeviceCommandEnvelope BuildCommand(
        DeviceInstance instance,
        CapabilityId capability,
        string operation,
        string payloadJson,
        CommandDelivery delivery = CommandDelivery.Idempotent,
        DateTimeOffset? expiresAt = null)
    {
        var session = _sessions.GetSession(instance.Id)
            ?? throw new InvalidOperationException("实例没有活动会话。");
        using var document = JsonDocument.Parse(payloadJson);
        var now = _timeProvider.GetUtcNow();
        return new DeviceCommandEnvelope(
            CommandId.New(),
            instance.Id,
            capability,
            operation,
            document.RootElement.Clone(),
            new CommandSource(CommandSourceKind.UserInterface, "test"),
            delivery,
            now,
            expiresAt ?? now + TimeSpan.FromSeconds(5),
            _safety.Current.SafetyEpoch,
            session.SessionEpoch);
    }

    public void Dispose()
    {
        _dispatcher.Dispose();
    }
}