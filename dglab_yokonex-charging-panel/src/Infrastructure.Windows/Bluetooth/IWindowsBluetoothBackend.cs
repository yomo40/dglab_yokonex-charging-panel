namespace ChargingPanel.Infrastructure.Windows.Bluetooth;

/// <summary>
/// 一条 BLE 广播。只承载平台原样上报的字段，不解释任何厂商协议内容。
/// </summary>
public sealed record BluetoothAdvertisement
{
    public BluetoothAdvertisement(
        string normalizedAddress,
        string? localName,
        int rssi,
        IReadOnlyList<Guid> serviceUuids)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedAddress);
        ArgumentNullException.ThrowIfNull(serviceUuids);
        NormalizedAddress = normalizedAddress;
        LocalName = localName;
        Rssi = rssi;
        ServiceUuids = serviceUuids;
    }

    /// <summary>规范化蓝牙地址，作为设备去重的唯一键。</summary>
    public string NormalizedAddress { get; }

    public string? LocalName { get; }

    public int Rssi { get; }

    public IReadOnlyList<Guid> ServiceUuids { get; }
}

/// <summary>
/// 单个物理 BLE 连接。每个方法只做一次可取消 I/O，
/// 不包含重连与语义重试——那是 SessionCoordinator 与 Dispatcher 的职责。
/// </summary>
public interface IWindowsBluetoothConnection : IAsyncDisposable
{
    /// <summary>
    /// 写入特征值。<paramref name="withResponse"/> 必须由调用方显式声明：
    /// WithoutResponse 失败时不得自动改成 WithResponse 重发同一物理命令，
    /// 否则一次电击或泵动作可能被执行两次。
    /// </summary>
    ValueTask WriteAsync(
        Guid serviceUuid,
        Guid characteristicUuid,
        ReadOnlyMemory<byte> payload,
        bool withResponse,
        CancellationToken cancellationToken);

    ValueTask SubscribeAsync(
        Guid serviceUuid,
        Guid characteristicUuid,
        Action<byte[]> onNotified,
        CancellationToken cancellationToken);

    /// <summary>取消全部通知订阅。释放物理连接前必须调用。</summary>
    ValueTask UnsubscribeAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Windows 平台后端。所有 WinRT/GATT 类型都止于该接口之下的实现，
/// 使 Broker 的租约与引用计数逻辑可在无蓝牙硬件的环境下测试。
/// </summary>
public interface IWindowsBluetoothBackend
{
    ValueTask<bool> IsAdapterAvailableAsync(CancellationToken cancellationToken);

    /// <summary>启动广播监听。释放返回值即停止监听。</summary>
    IDisposable StartWatcher(Action<BluetoothAdvertisement> onAdvertisement);

    /// <summary>建立一条物理连接。失败时抛出，绝不返回"看起来可用"的连接。</summary>
    ValueTask<IWindowsBluetoothConnection> ConnectAsync(
        string normalizedAddress,
        CancellationToken cancellationToken);
}
