using ChargingPanel.Infrastructure.Windows.Bluetooth;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Bluetooth;

/// <summary>
/// BluetoothConnectionBroker 生命周期测试。
///
/// Broker 是 Windows BLE 句柄的唯一拥有者：按规范化地址去重、
/// 共享物理连接但独立发放租约、引用计数归零才真正释放。
/// 它只做单次可取消 I/O，绝不自行重连（重连归 SessionCoordinator）。
/// </summary>
public sealed class BluetoothConnectionBrokerTests
{
    private static readonly Guid Service = Guid.Parse("0000180c-0000-1000-8000-00805f9b34fb");
    private static readonly Guid Characteristic = Guid.Parse("0000150a-0000-1000-8000-00805f9b34fb");

    [Fact]
    public async Task AcquireAsync_SameAddressDifferentCasing_SharesOnePhysicalConnection()
    {
        // 同一设备可能以不同大小写/分隔符形式出现；必须去重成一个物理连接，
        // 否则会对同一 GATT 句柄重复连接并互相踢掉。
        var backend = new FakeWindowsBluetoothBackend();
        await using var broker = new BluetoothConnectionBroker(backend, TimeProvider.System);

        await using var first = await broker.AcquireAsync("AA:BB:CC:DD:EE:01", CancellationToken.None);
        await using var second = await broker.AcquireAsync("aabbccddee01", CancellationToken.None);

        Assert.Equal(1, backend.ConnectCounts.Values.Sum());
        Assert.Single(backend.LiveConnections);
    }

    [Fact]
    public async Task DisposeAsync_NotLastLease_KeepsPhysicalConnectionAlive()
    {
        // 释放一个租约不得断开仍被其他租约使用的物理连接。
        var backend = new FakeWindowsBluetoothBackend();
        await using var broker = new BluetoothConnectionBroker(backend, TimeProvider.System);

        var first = await broker.AcquireAsync("AA:BB:CC:DD:EE:02", CancellationToken.None);
        await using var second = await broker.AcquireAsync("AA:BB:CC:DD:EE:02", CancellationToken.None);

        await first.DisposeAsync();

        Assert.Single(backend.LiveConnections);
        Assert.False(second.IsClosed);
    }

    [Fact]
    public async Task DisposeAsync_LastLease_ReleasesGattHandleAndUnsubscribes()
    {
        // 最后一个租约释放时必须取消通知订阅并释放 GATT 句柄，
        // 否则设备会被永久占用且通知继续投递到无人处理的回调。
        var backend = new FakeWindowsBluetoothBackend();
        await using var broker = new BluetoothConnectionBroker(backend, TimeProvider.System);

        var lease = await broker.AcquireAsync("AA:BB:CC:DD:EE:03", CancellationToken.None);
        var connection = backend.LiveConnections["aabbccddee03"];

        await lease.DisposeAsync();

        Assert.True(connection.Disposed);
        Assert.Equal(1, connection.UnsubscribeCount);
        Assert.Empty(backend.LiveConnections);
    }

    [Fact]
    public async Task AcquireAsync_AdapterUnavailable_FailsWithoutFakingSuccess()
    {
        // 蓝牙关闭时必须明确失败：绝不返回一个"看起来能用"的租约。
        var backend = new FakeWindowsBluetoothBackend { AdapterAvailable = false };
        await using var broker = new BluetoothConnectionBroker(backend, TimeProvider.System);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            broker.AcquireAsync("AA:BB:CC:DD:EE:04", CancellationToken.None).AsTask());

        Assert.Empty(backend.LiveConnections);
    }

    [Fact]
    public async Task AcquireAsync_CancelledDuringConnect_DoesNotLeakConnection()
    {
        // 取消发生在连接建立过程中：不得留下无主的物理连接。
        var backend = new FakeWindowsBluetoothBackend
        {
            ConnectGate = new TaskCompletionSource()
        };
        await using var broker = new BluetoothConnectionBroker(backend, TimeProvider.System);
        using var cts = new CancellationTokenSource();

        var pending = broker.AcquireAsync("AA:BB:CC:DD:EE:05", cts.Token).AsTask();
        await cts.CancelAsync();
        backend.ConnectGate.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(backend.LiveConnections);
    }

    [Fact]
    public async Task WriteAsync_ConcurrentCallers_AreSerializedPerConnection()
    {
        // GATT 写必须按连接串行化：并发写同一句柄在 WinRT 上会失败或乱序。
        var backend = new FakeWindowsBluetoothBackend();
        await using var broker = new BluetoothConnectionBroker(backend, TimeProvider.System);
        await using var lease = await broker.AcquireAsync("AA:BB:CC:DD:EE:06", CancellationToken.None);

        var payloads = Enumerable.Range(0, 32).Select(i => new byte[] { (byte)i }).ToArray();
        await Task.WhenAll(payloads.Select(payload => Task.Run(() =>
            lease.WriteAsync(Service, Characteristic, payload, withResponse: true, CancellationToken.None).AsTask())));

        var connection = backend.LiveConnections["aabbccddee06"];
        Assert.Equal(payloads.Length, connection.Writes.Count);
        Assert.Equal(
            payloads.Select(payload => payload[0]).OrderBy(value => value),
            connection.Writes.Select(write => write.Payload[0]).OrderBy(value => value));
    }

    [Fact]
    public async Task WriteAsync_WithoutResponseFailure_IsNotSilentlyRetriedWithResponse()
    {
        // WithoutResponse 失败不得自动改成 WithResponse 重发同一物理命令：
        // 那会让一次电击/泵动作被执行两次。
        var backend = new FakeWindowsBluetoothBackend();
        await using var broker = new BluetoothConnectionBroker(backend, TimeProvider.System);
        await using var lease = await broker.AcquireAsync("AA:BB:CC:DD:EE:07", CancellationToken.None);

        var connection = backend.LiveConnections["aabbccddee07"];
        connection.WriteException = new InvalidOperationException("合成 GATT 写失败");

        // 失败必须原样上抛，由 Dispatcher 依据交付语义决定是否重试。
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease
            .WriteAsync(Service, Characteristic, new byte[] { 0x01 }, withResponse: false, CancellationToken.None)
            .AsTask());

        // 恰好一次写入，且方式仍是 WithoutResponse：
        // 若 Broker 私自改用 WithResponse 重发，这里会看到第二条记录。
        var write = Assert.Single(connection.Writes);
        Assert.False(write.WithResponse);
    }

    [Fact]
    public async Task ScanAsync_MultipleSubscribers_ShareOneWatcher()
    {
        // 多个扫描订阅必须共享同一个 watcher；每个订阅各起一个会耗尽平台资源。
        var backend = new FakeWindowsBluetoothBackend();
        await using var broker = new BluetoothConnectionBroker(backend, TimeProvider.System);

        var first = broker.AcquireScanLease(_ => { });
        var second = broker.AcquireScanLease(_ => { });

        Assert.Equal(1, backend.WatcherStarts);

        first.Dispose();
        Assert.Equal(0, backend.WatcherStops);

        second.Dispose();
        Assert.Equal(1, backend.WatcherStops);
    }

    [Fact]
    public async Task DisposeAsync_Broker_ReleasesAllConnectionsAndWatchers()
    {
        // 宿主关闭时 Broker 必须释放全部平台句柄，不依赖调用方逐个归还。
        var backend = new FakeWindowsBluetoothBackend();
        var broker = new BluetoothConnectionBroker(backend, TimeProvider.System);
        _ = await broker.AcquireAsync("AA:BB:CC:DD:EE:08", CancellationToken.None);
        _ = broker.AcquireScanLease(_ => { });

        await broker.DisposeAsync();

        Assert.Empty(backend.LiveConnections);
        Assert.Equal(backend.WatcherStarts, backend.WatcherStops);
    }

    [Fact]
    public async Task DevicePowerOff_WriteFailsHonestlyAndLastReleaseCleansUp()
    {
        // 断电后连接失效：写入必须明确失败（绝不伪造成功），
        // 最后一个租约释放仍要清理槽位与句柄，不能留下无主残留。
        var backend = new FakeWindowsBluetoothBackend();
        await using var broker = new BluetoothConnectionBroker(backend, TimeProvider.System);
        await using var lease = await broker.AcquireAsync("AA:BB:CC:DD:EE:0A", CancellationToken.None);

        backend.PowerOff();

        await Assert.ThrowsAsync<InvalidOperationException>(() => lease
            .WriteAsync(Service, Characteristic, new byte[] { 0x01 }, withResponse: true, CancellationToken.None)
            .AsTask());

        await lease.DisposeAsync();

        Assert.Empty(backend.LiveConnections);
    }

    [Fact]
    public async Task DeviceRemoved_ReAcquireGetsFreshConnectionNotStaleHandle()
    {
        // 移除设备后：旧连接如实失败；释放后再获取同一地址必须得到新连接，
        // 绝不能把已失效的 GATT 句柄当作"仍然可用"复用。
        var backend = new FakeWindowsBluetoothBackend();
        await using var broker = new BluetoothConnectionBroker(backend, TimeProvider.System);
        var lease = await broker.AcquireAsync("AA:BB:CC:DD:EE:0B", CancellationToken.None);

        backend.PowerOff();

        await Assert.ThrowsAsync<InvalidOperationException>(() => lease
            .WriteAsync(Service, Characteristic, new byte[] { 0x01 }, withResponse: false, CancellationToken.None)
            .AsTask());

        await lease.DisposeAsync();
        Assert.Empty(backend.LiveConnections);

        await using var fresh = await broker.AcquireAsync("AA:BB:CC:DD:EE:0B", CancellationToken.None);
        Assert.Equal(2, backend.ConnectCounts["aabbccddee0b"]);
        Assert.False(backend.LiveConnections["aabbccddee0b"].Dead);
    }

    [Fact]
    public async Task WriteAsync_HangingGattOperation_PropagatesCancellationWithoutRetry()
    {
        // GATT 写入挂起（超时）时：取消必须原样上抛，且只允许恰好一次写入尝试——
        // Broker 不得静默改方式或重发，是否重试由 Dispatcher 按交付语义决定。
        var backend = new FakeWindowsBluetoothBackend();
        await using var broker = new BluetoothConnectionBroker(backend, TimeProvider.System);
        await using var lease = await broker.AcquireAsync("AA:BB:CC:DD:EE:0C", CancellationToken.None);

        var connection = backend.LiveConnections["aabbccddee0c"];
        connection.WriteGate = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();

        var write = lease
            .WriteAsync(Service, Characteristic, new byte[] { 0x01 }, withResponse: false, cts.Token)
            .AsTask();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);

        var recorded = Assert.Single(connection.Writes);
        Assert.False(recorded.WithResponse);
    }

    [Fact]
    public async Task SubscribeAsync_DuplicateNotifications_AreDeliveredInOrder()
    {
        // 重复通知按到达顺序原样投递到订阅者；业务去重是协议层职责，不属于传输层。
        var backend = new FakeWindowsBluetoothBackend();
        await using var broker = new BluetoothConnectionBroker(backend, TimeProvider.System);
        await using var lease = await broker.AcquireAsync("AA:BB:CC:DD:EE:0D", CancellationToken.None);

        var received = new List<byte[]>();
        await lease.SubscribeAsync(Service, Characteristic, received.Add, CancellationToken.None);
        var connection = backend.LiveConnections["aabbccddee0d"];

        connection.FireNotification([0x10, 0x01]);
        connection.FireNotification([0x10, 0x02]);
        connection.FireNotification([0x10, 0x02]);

        Assert.Equal(3, received.Count);
        Assert.Equal([0x10, 0x01], received[0]);
        Assert.Equal([0x10, 0x02], received[1]);
        Assert.Equal([0x10, 0x02], received[2]);
        Assert.Equal(3, connection.NotifiedPayloads.Count);
    }

    [Fact]
    public async Task SleepWake_StaleConnectionFailsAndFreshLeaseRestoresWrites()
    {
        // 睡眠恢复后旧连接成为陈旧句柄：写入如实失败；释放并重新获取后
        // 得到全新连接并可正常写入，绝不自动重连也不复用失效句柄。
        var backend = new FakeWindowsBluetoothBackend();
        await using var broker = new BluetoothConnectionBroker(backend, TimeProvider.System);
        var lease = await broker.AcquireAsync("AA:BB:CC:DD:EE:0E", CancellationToken.None);

        backend.PowerOff();

        await Assert.ThrowsAsync<InvalidOperationException>(() => lease
            .WriteAsync(Service, Characteristic, new byte[] { 0x01 }, withResponse: false, CancellationToken.None)
            .AsTask());

        await lease.DisposeAsync();
        Assert.Empty(backend.LiveConnections);

        await using var restored = await broker.AcquireAsync("AA:BB:CC:DD:EE:0E", CancellationToken.None);
        await restored.WriteAsync(Service, Characteristic, new byte[] { 0x02 }, withResponse: false, CancellationToken.None);

        var fresh = backend.LiveConnections["aabbccddee0e"];
        Assert.False(fresh.Dead);
        Assert.Equal([0x02], Assert.Single(fresh.Writes).Payload);
    }
}
