using System.Collections.Concurrent;
using ChargingPanel.Infrastructure.Windows.Bluetooth;

namespace ChargingPanel.DeviceIntegration.Tests.Bluetooth;

/// <summary>
/// 合成 Windows 蓝牙后端。只记录调用与注入故障，绝不伪造连接成功：
/// 适配器关闭时抛出，取消时抛出 OperationCanceledException。
/// </summary>
internal sealed class FakeWindowsBluetoothBackend : IWindowsBluetoothBackend
{
    private readonly ConcurrentDictionary<string, int> _connectCounts = new(StringComparer.Ordinal);
    private int _watcherStarts;
    private int _watcherStops;

    /// <summary>适配器是否可用。false 时任何获取都必须失败而非降级。</summary>
    public bool AdapterAvailable { get; set; } = true;

    /// <summary>连接前的闸门；用于制造"连接进行中"的确定性时序。</summary>
    public TaskCompletionSource? ConnectGate { get; set; }

    public int WatcherStarts => Volatile.Read(ref _watcherStarts);

    public int WatcherStops => Volatile.Read(ref _watcherStops);

    public IReadOnlyDictionary<string, int> ConnectCounts => _connectCounts;

    /// <summary>已创建但尚未释放的物理连接。</summary>
    public ConcurrentDictionary<string, FakeConnection> LiveConnections { get; } = new(StringComparer.Ordinal);

    public ValueTask<bool> IsAdapterAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(AdapterAvailable);
    }

    /// <summary>
    /// 模拟断电/移除设备/睡眠恢复后连接失效：令所有现存连接进入 Dead 状态。
    /// 失效连接的写入与订阅必须明确失败，绝不返回"看起来可用"的结果。
    /// </summary>
    public void PowerOff()
    {
        foreach (var connection in LiveConnections.Values)
        {
            connection.Dead = true;
        }
    }

    public IDisposable StartWatcher(Action<BluetoothAdvertisement> onAdvertisement)
    {
        ArgumentNullException.ThrowIfNull(onAdvertisement);
        Interlocked.Increment(ref _watcherStarts);
        return new WatcherHandle(this);
    }

    public async ValueTask<IWindowsBluetoothConnection> ConnectAsync(
        string normalizedAddress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AdapterAvailable)
        {
            throw new InvalidOperationException("合成适配器不可用。");
        }

        if (ConnectGate is not null)
        {
            await ConnectGate.Task.WaitAsync(cancellationToken);
        }

        _connectCounts.AddOrUpdate(normalizedAddress, 1, static (_, count) => count + 1);
        var connection = new FakeConnection(this, normalizedAddress);
        LiveConnections[normalizedAddress] = connection;
        return connection;
    }

    private sealed class WatcherHandle : IDisposable
    {
        private readonly FakeWindowsBluetoothBackend _owner;
        private bool _disposed;

        public WatcherHandle(FakeWindowsBluetoothBackend owner) => _owner = owner;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Interlocked.Increment(ref _owner._watcherStops);
        }
    }

    internal sealed class FakeConnection : IWindowsBluetoothConnection
    {
        private readonly FakeWindowsBluetoothBackend _owner;
        private readonly string _address;

        public FakeConnection(FakeWindowsBluetoothBackend owner, string address)
        {
            _owner = owner;
            _address = address;
        }

        public List<(Guid Service, Guid Characteristic, byte[] Payload, bool WithResponse)> Writes { get; } = [];

        /// <summary>令写入失败，用于验证失败不会被静默改写方式重发。</summary>
        public Exception? WriteException { get; set; }

        /// <summary>连接是否已失效（断电/移除/睡眠后）。失效后写入与订阅必须失败。</summary>
        public bool Dead { get; set; }

        /// <summary>
        /// 写入闸门；设置后写入等待该闸门直到取消（模拟 GATT 操作挂起/超时）。
        /// 闸门永不完成时，取消令牌即成为唯一出口。
        /// </summary>
        public TaskCompletionSource? WriteGate { get; set; }

        /// <summary>设备主动上报的载荷，按到达顺序记录。</summary>
        public List<byte[]> NotifiedPayloads { get; } = [];

        private Action<byte[]>? _notificationCallback;

        public int SubscribeCount { get; private set; }

        public int UnsubscribeCount { get; private set; }

        public bool Disposed { get; private set; }

        public ValueTask WriteAsync(
            Guid serviceUuid,
            Guid characteristicUuid,
            ReadOnlyMemory<byte> payload,
            bool withResponse,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (Dead)
            {
                // 断电/移除后绝不重试或降级：设备已不在，任何写都是无效动作。
                throw new InvalidOperationException("连接已失效（设备断电或移除）。");
            }

            // 先记录再抛出/等待：调用方是否用另一种写方式重发，必须能从记录中看出来。
            Writes.Add((serviceUuid, characteristicUuid, payload.ToArray(), withResponse));
            if (WriteException is not null)
            {
                throw WriteException;
            }

            if (WriteGate is not null)
            {
                // GATT 操作挂起：只有取消令牌能结束这次调用，Broker 不得自行重试。
                return new ValueTask(WriteGate.Task.WaitAsync(cancellationToken));
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask SubscribeAsync(
            Guid serviceUuid,
            Guid characteristicUuid,
            Action<byte[]> onNotified,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (Dead)
            {
                throw new InvalidOperationException("连接已失效（设备断电或移除）。");
            }

            SubscribeCount++;
            _notificationCallback = onNotified;
            return ValueTask.CompletedTask;
        }

        /// <summary>模拟设备主动上报：载荷按到达顺序投递到订阅者，业务去重不属于传输层。</summary>
        public void FireNotification(byte[] payload)
        {
            NotifiedPayloads.Add(payload);
            _notificationCallback?.Invoke(payload);
        }

        public ValueTask UnsubscribeAllAsync(CancellationToken cancellationToken)
        {
            UnsubscribeCount++;
            _notificationCallback = null;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _owner.LiveConnections.TryRemove(_address, out _);
            return ValueTask.CompletedTask;
        }
    }
}
