namespace ChargingPanel.Infrastructure.Windows.Bluetooth;

/// <summary>
/// Windows 蓝牙连接协调器。整个进程中 BLE 扫描与 GATT 句柄的唯一拥有者。
///
/// 关键约束：
/// - 按规范化地址去重，同一设备只存在一条物理连接；
/// - 多个调用方各持独立租约，引用计数归零才真正释放句柄；
/// - GATT 写按连接串行化，避免 WinRT 上的并发写失败与乱序；
/// - 只做单次可取消 I/O，绝不自行重连（重连归 SessionCoordinator）；
/// - 适配器不可用时明确失败，绝不返回"看起来可用"的租约。
/// </summary>
public sealed class BluetoothConnectionBroker : IAsyncDisposable
{
    private readonly IWindowsBluetoothBackend _backend;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ConnectionSlot> _connections = new(StringComparer.Ordinal);
    private readonly List<Action<BluetoothAdvertisement>> _scanSubscribers = [];
    private IDisposable? _watcher;
    private bool _disposed;

    public BluetoothConnectionBroker(IWindowsBluetoothBackend backend, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _backend = backend;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 获取设备连接租约。同一地址的多次获取共享一条物理连接。
    /// </summary>
    public async ValueTask<BluetoothConnectionLease> AcquireAsync(
        string address,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalized = BluetoothAddress.Normalize(address);

        if (!await _backend.IsAdapterAvailableAsync(cancellationToken))
        {
            throw new InvalidOperationException("蓝牙适配器不可用，无法建立连接。");
        }

        await _gate.WaitAsync(cancellationToken);
        ConnectionSlot slot;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_connections.TryGetValue(normalized, out var existing))
            {
                existing = new ConnectionSlot(normalized);
                _connections[normalized] = existing;
            }

            slot = existing;
            // 先登记引用再建立连接：否则并发获取会各自建连接。
            slot.LeaseCount++;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await slot.EnsureConnectedAsync(_backend, cancellationToken);
            return new BluetoothConnectionLease(this, slot);
        }
        catch
        {
            // 建连失败或被取消：必须退还引用并清理空 slot，
            // 否则该地址会永久残留一个无人持有的计数，之后再也连不上。
            await ReleaseAsync(slot);
            throw;
        }
    }

    /// <summary>
    /// 获取扫描租约。多个订阅共享同一个平台 watcher；
    /// 最后一个租约释放时才停止监听。
    /// </summary>
    public IDisposable AcquireScanLease(Action<BluetoothAdvertisement> onAdvertisement)
    {
        ArgumentNullException.ThrowIfNull(onAdvertisement);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _gate.Wait();
        try
        {
            _scanSubscribers.Add(onAdvertisement);
            try
            {
                _watcher ??= _backend.StartWatcher(DispatchAdvertisement);
            }
            catch
            {
                // StartWatcher 失败：必须退订，否则订阅者永远留在列表里，
                // 之后最后一个订阅者释放时无法触发 watcher 停止。
                _scanSubscribers.Remove(onAdvertisement);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }

        return new BluetoothScanLease(this, onAdvertisement);
    }

    private void DispatchAdvertisement(BluetoothAdvertisement advertisement)
    {
        Action<BluetoothAdvertisement>[] subscribers;
        _gate.Wait();
        try
        {
            subscribers = _scanSubscribers.ToArray();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var subscriber in subscribers)
        {
            try
            {
                subscriber(advertisement);
            }
            catch
            {
                // 单个订阅者抛出不得影响其他订阅者接收广播。
            }
        }
    }

    internal void ReleaseScanSubscriber(Action<BluetoothAdvertisement> subscriber)
    {
        IDisposable? watcherToStop = null;
        _gate.Wait();
        try
        {
            _scanSubscribers.Remove(subscriber);
            if (_scanSubscribers.Count == 0)
            {
                watcherToStop = _watcher;
                _watcher = null;
            }
        }
        finally
        {
            _gate.Release();
        }

        watcherToStop?.Dispose();
    }

    internal async ValueTask ReleaseAsync(ConnectionSlot slot)
    {
        IWindowsBluetoothConnection? connectionToRelease = null;
        await _gate.WaitAsync();
        try
        {
            slot.LeaseCount--;
            if (slot.LeaseCount > 0)
            {
                return;
            }

            _connections.Remove(slot.NormalizedAddress);
            connectionToRelease = slot.TakeConnection();
        }
        finally
        {
            _gate.Release();
        }

        if (connectionToRelease is null)
        {
            return;
        }

        // 在锁外释放：取消订阅与断开都是 I/O。
        try
        {
            await connectionToRelease.UnsubscribeAllAsync(CancellationToken.None);
        }
        catch
        {
            // 取消订阅失败不得阻止释放句柄，否则设备被永久占用。
        }

        await connectionToRelease.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // 在触碰 _gate 之前先检查：DI 容器会在关闭参与者释放后再次释放单例，
        // 此时 _gate 已被 Dispose，直接 WaitAsync 会抛 ObjectDisposedException。
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        ConnectionSlot[] slots;
        IDisposable? watcher;
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            slots = _connections.Values.ToArray();
            _connections.Clear();
            _scanSubscribers.Clear();
            watcher = _watcher;
            _watcher = null;
        }
        finally
        {
            _gate.Release();
        }

        watcher?.Dispose();

        // 宿主关闭时必须释放全部平台句柄，不能依赖调用方逐个归还。
        foreach (var slot in slots)
        {
            var connection = slot.TakeConnection();
            if (connection is null)
            {
                continue;
            }

            try
            {
                await connection.UnsubscribeAllAsync(CancellationToken.None);
            }
            catch
            {
                // 同上：不阻止释放。
            }

            await connection.DisposeAsync();
        }

        _gate.Dispose();
    }

    /// <summary>
    /// 一个地址对应的物理连接槽位。引用计数与写串行化都在此。
    /// </summary>
    internal sealed class ConnectionSlot
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly SemaphoreSlim _connectGate = new(1, 1);
        private IWindowsBluetoothConnection? _connection;

        public ConnectionSlot(string normalizedAddress)
        {
            NormalizedAddress = normalizedAddress;
        }

        public string NormalizedAddress { get; }

        public int LeaseCount { get; set; }

        public async ValueTask EnsureConnectedAsync(
            IWindowsBluetoothBackend backend,
            CancellationToken cancellationToken)
        {
            if (_connection is not null)
            {
                return;
            }

            await _connectGate.WaitAsync(cancellationToken);
            try
            {
                // 双检：并发获取同一地址时只建立一条物理连接。
                _connection ??= await backend.ConnectAsync(NormalizedAddress, cancellationToken);
            }
            finally
            {
                _connectGate.Release();
            }
        }

        public IWindowsBluetoothConnection? TakeConnection()
        {
            var connection = _connection;
            _connection = null;
            return connection;
        }

        /// <summary>
        /// 串行化 GATT 写。WinRT 上并发写同一句柄会失败或乱序。
        /// </summary>
        public async ValueTask WriteAsync(
            Guid serviceUuid,
            Guid characteristicUuid,
            ReadOnlyMemory<byte> payload,
            bool withResponse,
            CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                var connection = _connection
                    ?? throw new InvalidOperationException("连接已释放，无法写入。");
                await connection.WriteAsync(
                    serviceUuid,
                    characteristicUuid,
                    payload,
                    withResponse,
                    cancellationToken);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public async ValueTask SubscribeAsync(
            Guid serviceUuid,
            Guid characteristicUuid,
            Action<byte[]> onNotified,
            CancellationToken cancellationToken)
        {
            var connection = _connection
                ?? throw new InvalidOperationException("连接已释放，无法订阅。");
            await connection.SubscribeAsync(serviceUuid, characteristicUuid, onNotified, cancellationToken);
        }
    }
}

/// <summary>
/// 设备连接租约。释放即归还引用；只有最后一个租约释放才断开物理连接。
/// </summary>
public sealed class BluetoothConnectionLease : IAsyncDisposable
{
    private readonly BluetoothConnectionBroker _broker;
    private readonly BluetoothConnectionBroker.ConnectionSlot _slot;
    private bool _closed;

    internal BluetoothConnectionLease(
        BluetoothConnectionBroker broker,
        BluetoothConnectionBroker.ConnectionSlot slot)
    {
        _broker = broker;
        _slot = slot;
    }

    /// <summary>租约是否已归还。</summary>
    public bool IsClosed => _closed;

    public string NormalizedAddress => _slot.NormalizedAddress;

    public ValueTask WriteAsync(
        Guid serviceUuid,
        Guid characteristicUuid,
        ReadOnlyMemory<byte> payload,
        bool withResponse,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        return _slot.WriteAsync(serviceUuid, characteristicUuid, payload, withResponse, cancellationToken);
    }

    public ValueTask SubscribeAsync(
        Guid serviceUuid,
        Guid characteristicUuid,
        Action<byte[]> onNotified,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        return _slot.SubscribeAsync(serviceUuid, characteristicUuid, onNotified, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        await _broker.ReleaseAsync(_slot);
    }
}

/// <summary>扫描租约。释放即退订；最后一个释放时停止平台 watcher。</summary>
internal sealed class BluetoothScanLease : IDisposable
{
    private readonly BluetoothConnectionBroker _broker;
    private readonly Action<BluetoothAdvertisement> _subscriber;
    private bool _disposed;

    public BluetoothScanLease(
        BluetoothConnectionBroker broker,
        Action<BluetoothAdvertisement> subscriber)
    {
        _broker = broker;
        _subscriber = subscriber;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _broker.ReleaseScanSubscriber(_subscriber);
    }
}
