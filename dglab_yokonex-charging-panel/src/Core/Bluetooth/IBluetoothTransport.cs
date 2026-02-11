using System;
using System.Threading;
using System.Threading.Tasks;

namespace ChargingPanel.Core.Bluetooth;

/// <summary>
/// BLE 设备连接状态
/// </summary>
public enum BleConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting
}

/// <summary>
/// BLE 设备信息
/// </summary>
public class BleDeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public int Rssi { get; set; }
    public Guid[] ServiceUuids { get; set; } = Array.Empty<Guid>();
    public bool IsConnectable { get; set; } = true;
}

/// <summary>
/// BLE 扫描结果事件参数
/// </summary>
public class BleScanResultEventArgs : EventArgs
{
    public BleDeviceInfo Device { get; set; } = new();
}

/// <summary>
/// BLE 数据接收事件参数
/// </summary>
public class BleDataReceivedEventArgs : EventArgs
{
    public Guid ServiceUuid { get; set; }
    public Guid CharacteristicUuid { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// 蓝牙传输层接口
/// </summary>
public interface IBluetoothTransport : IDisposable
{
    /// <summary>当前连接状态</summary>
    BleConnectionState State { get; }

    /// <summary>设备名称</summary>
    string DeviceName { get; }

    /// <summary>设备 MAC 地址</summary>
    string MacAddress { get; }

    /// <summary>连接状态变化事件</summary>
    event EventHandler<BleConnectionState>? StateChanged;

    /// <summary>数据接收事件</summary>
    event EventHandler<BleDataReceivedEventArgs>? DataReceived;

    /// <summary>扫描发现设备事件</summary>
    event EventHandler<BleScanResultEventArgs>? DeviceDiscovered;

    /// <summary>开始扫描 BLE 设备</summary>
    Task<BleDeviceInfo[]> ScanAsync(Guid? serviceFilter = null, string? namePrefix = null, int timeoutMs = 10000, CancellationToken ct = default);

    /// <summary>停止扫描</summary>
    void StopScan();

    /// <summary>连接到 BLE 设备</summary>
    Task ConnectAsync(string deviceId, CancellationToken ct = default);

    /// <summary>断开连接</summary>
    Task DisconnectAsync();

    /// <summary>发现服务和特性</summary>
    Task DiscoverServicesAsync();

    /// <summary>订阅特性通知</summary>
    Task SubscribeAsync(Guid serviceUuid, Guid characteristicUuid);

    /// <summary>取消订阅</summary>
    Task UnsubscribeAsync(Guid serviceUuid, Guid characteristicUuid);

    /// <summary>写入数据（带响应）</summary>
    Task WriteAsync(Guid serviceUuid, Guid characteristicUuid, byte[] data);

    /// <summary>写入数据（无响应）</summary>
    Task WriteWithoutResponseAsync(Guid serviceUuid, Guid characteristicUuid, byte[] data);

    /// <summary>读取数据</summary>
    Task<byte[]> ReadAsync(Guid serviceUuid, Guid characteristicUuid);
}

/// <summary>
/// 蓝牙适配器状态
/// </summary>
public class BluetoothAdapterStatus
{
    public bool IsAvailable { get; set; }
    public bool IsEnabled { get; set; }
    public string? AdapterName { get; set; }
    public bool SupportsBle { get; set; }
    public string? ErrorMessage { get; set; }
}
