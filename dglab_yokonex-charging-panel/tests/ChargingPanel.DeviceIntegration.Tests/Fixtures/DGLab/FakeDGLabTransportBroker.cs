using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Core.Devices.Sessions;
using ChargingPanel.Core.Devices.Transport;
using ChargingPanel.Vendor.DGLab;
using ChargingPanel.Vendor.DGLab.Common;

namespace ChargingPanel.DeviceIntegration.Tests.Fixtures.DGLab;

/// <summary>
/// 测试用传输 Broker：握手时按 Profile 创建驱动并打开租约。
/// 生产组合根在 Broker 与 Driver 之间做同样的桥接，此处用假实现覆盖。
/// </summary>
public sealed class FakeDGLabTransportBroker : ITransportBroker
{
    private readonly DGLabPlugin _plugin;
    private readonly object _gate = new();
    private readonly Dictionary<EndpointId, FakeEndpointLease> _leases = [];
    private readonly Dictionary<EndpointId, IProtocolDriver> _drivers = [];
    private int _acquireCount;
    private int _releaseCount;

    public FakeDGLabTransportBroker(DGLabPlugin plugin)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
    }

    public bool FailAcquire { get; init; }

    public bool FailHandshake { get; init; }

    /// <summary>每个新租约创建后应用的回调，用于注入发送/读取行为。</summary>
    public Action<FakeEndpointLease>? ConfigureLease { get; set; }

    public int AcquireCount => Volatile.Read(ref _acquireCount);

    public int ReleaseCount => Volatile.Read(ref _releaseCount);

    public FakeEndpointLease GetLease(EndpointId endpointId)
    {
        lock (_gate)
        {
            return _leases[endpointId];
        }
    }

    public IProtocolDriver GetDriver(EndpointId endpointId)
    {
        lock (_gate)
        {
            return _drivers[endpointId];
        }
    }

    public ValueTask<TransportLeaseHandle> AcquireAsync(
        EndpointCandidate candidate,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _acquireCount);
        if (FailAcquire)
        {
            throw new IOException("合成租约获取失败。");
        }

        var lease = new FakeEndpointLease(candidate.ProfileId, candidate.TransportKind);
        ConfigureLease?.Invoke(lease);
        lock (_gate)
        {
            _leases[candidate.EndpointId] = lease;
        }

        return ValueTask.FromResult(new TransportLeaseHandle(candidate.EndpointId, candidate.ProfileId));
    }

    public async ValueTask<bool> HandshakeAsync(
        TransportLeaseHandle lease,
        CancellationToken cancellationToken)
    {
        if (FailHandshake)
        {
            return false;
        }

        var factory = _plugin.Describe().DriverFactories.Single(item => item.ProfileId == lease.ProfileId);
        var driver = factory.CreateDriver(new FakePluginContext());
        FakeEndpointLease endpointLease;
        lock (_gate)
        {
            endpointLease = _leases[lease.EndpointId];
        }

        try
        {
            await driver.OpenAsync(endpointLease, cancellationToken);
        }
        catch
        {
            return false;
        }

        lock (_gate)
        {
            _drivers[lease.EndpointId] = driver;
        }

        return true;
    }

    public ValueTask ReleaseAsync(TransportLeaseHandle lease, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _releaseCount);
        lock (_gate)
        {
            _leases.Remove(lease.EndpointId);
            _drivers.Remove(lease.EndpointId);
        }

        return ValueTask.CompletedTask;
    }
}