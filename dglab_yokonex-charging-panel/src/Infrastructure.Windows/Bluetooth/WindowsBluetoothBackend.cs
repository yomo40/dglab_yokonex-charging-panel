using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Foundation;

namespace ChargingPanel.Infrastructure.Windows.Bluetooth;

/// <summary>
/// Windows 平台 BLE 后端。所有 WinRT 类型都止于该实现之下，
/// 使 Broker 的租约与引用计数逻辑可在无蓝牙硬件的环境下测试。
///
/// 只做单次可取消 I/O：适配器枚举、广播监听、建连与服务发现各一次，
/// 绝不自行重连、不执行语义重试、不伪造连接成功。
/// </summary>
public sealed class WindowsBluetoothBackend : IWindowsBluetoothBackend
{
    private static readonly TimeSpan WinRtOperationTimeout = TimeSpan.FromMilliseconds(15000);

    public async ValueTask<bool> IsAdapterAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var adapter = await BluetoothAdapter
            .GetDefaultAsync()
            .AsTask()
            .WaitAsync(WinRtOperationTimeout, cancellationToken);
        if (adapter is null)
        {
            return false;
        }

        var radio = await adapter
            .GetRadioAsync()
            .AsTask()
            .WaitAsync(WinRtOperationTimeout, cancellationToken);
        return radio is not null && radio.State == RadioState.On;
    }

    public IDisposable StartWatcher(Action<BluetoothAdvertisement> onAdvertisement)
    {
        ArgumentNullException.ThrowIfNull(onAdvertisement);

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> received =
            (_, args) => OnReceived(args, onAdvertisement);
        watcher.Received += received;
        watcher.Start();
        return new WatcherHandle(watcher, received);
    }

    public async ValueTask<IWindowsBluetoothConnection> ConnectAsync(
        string normalizedAddress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedAddress);
        cancellationToken.ThrowIfCancellationRequested();

        var address = ParseAddress(normalizedAddress);
        var device = await BluetoothLEDevice
            .FromBluetoothAddressAsync(address)
            .AsTask()
            .WaitAsync(WinRtOperationTimeout, cancellationToken);
        if (device is null)
        {
            throw new InvalidOperationException("无法建立 BLE 设备句柄。");
        }

        Dictionary<Guid, GattDeviceService>? services = null;
        GattSession? session = null;
        try
        {
            await EnsureDeviceAccessAsync(device, cancellationToken);

            var servicesResult = await device
                .GetGattServicesAsync(BluetoothCacheMode.Uncached)
                .AsTask()
                .WaitAsync(WinRtOperationTimeout, cancellationToken);
            if (servicesResult.Status != GattCommunicationStatus.Success)
            {
                throw new InvalidOperationException($"发现 GATT 服务失败（{servicesResult.Status}）。");
            }

            services = new Dictionary<Guid, GattDeviceService>();
            foreach (var service in servicesResult.Services)
            {
                services[service.Uuid] = service;
            }

            // MaintainConnection 让连接在无人访问特征时也不被系统回收；
            // 它是"建立物理连接"的一部分，不是重连逻辑。
            session = await GattSession
                .FromDeviceIdAsync(BluetoothDeviceId.FromId(device.DeviceId))
                .AsTask()
                .WaitAsync(WinRtOperationTimeout, cancellationToken);
            if (session is null)
            {
                throw new InvalidOperationException("无法创建 GattSession。");
            }

            session.MaintainConnection = true;
            return new WindowsBluetoothConnection(device, services, session);
        }
        catch
        {
            // 建连失败（含取消）必须释放所有已创建的 WinRT 句柄：
            // 服务、会话与设备各归各释放，否则失败路径会留下无人持有的
            // COM 对象，在反复连接同一台不稳定设备时非确定性地累积。
            if (session is not null)
            {
                try
                {
                    session.Dispose();
                }
                catch
                {
                    // 释放失败不掩盖原始连接异常。
                }
            }

            if (services is not null)
            {
                foreach (var service in services.Values)
                {
                    try
                    {
                        service.Dispose();
                    }
                    catch
                    {
                        // 单个服务释放失败不阻止其余释放。
                    }
                }
            }

            device.Dispose();
            throw;
        }
    }

    private static void OnReceived(
        BluetoothLEAdvertisementReceivedEventArgs args,
        Action<BluetoothAdvertisement> onAdvertisement)
    {
        var advertisement = new BluetoothAdvertisement(
            BluetoothAddress.FromUInt64(args.BluetoothAddress),
            args.Advertisement.LocalName,
            args.RawSignalStrengthInDBm,
            args.Advertisement.ServiceUuids.ToArray());
        onAdvertisement(advertisement);
    }

    private static ulong ParseAddress(string normalizedAddress) =>
        ulong.Parse(normalizedAddress, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static async Task EnsureDeviceAccessAsync(BluetoothLEDevice device, CancellationToken cancellationToken)
    {
        var access = DeviceAccessInformation.CreateFromId(device.DeviceId);
        var status = access.CurrentStatus;
        if (status == DeviceAccessStatus.Unspecified)
        {
            status = await device
                .RequestAccessAsync()
                .AsTask()
                .WaitAsync(WinRtOperationTimeout, cancellationToken);
        }

        if (status is DeviceAccessStatus.DeniedBySystem or DeviceAccessStatus.DeniedByUser)
        {
            throw new UnauthorizedAccessException($"系统蓝牙权限不足（{status}）。");
        }
    }

    /// <summary>watcher 句柄。释放即停止监听并摘除事件处理器，绝不自动重启。</summary>
    private sealed class WatcherHandle : IDisposable
    {
        private readonly BluetoothLEAdvertisementWatcher _watcher;
        private readonly TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> _received;
        private bool _disposed;

        public WatcherHandle(
            BluetoothLEAdvertisementWatcher watcher,
            TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> received)
        {
            _watcher = watcher;
            _received = received;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _watcher.Stop();
                _watcher.Received -= _received;
            }
            catch
            {
                // 停止/摘除失败不掩盖调用方的释放流程。
            }
        }
    }
}
