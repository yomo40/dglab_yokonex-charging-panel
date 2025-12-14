using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using Windows.Storage.Streams;
using Serilog;

namespace ChargingPanel.Core.Bluetooth;

/// <summary>
/// Windows BLE 传输层实现
/// 使用 Windows.Devices.Bluetooth API (Windows 10 1703+)
/// </summary>
public class WindowsBluetoothTransport : IBluetoothTransport
{
    private static readonly ILogger Logger = Log.ForContext<WindowsBluetoothTransport>();

    private BleConnectionState _state = BleConnectionState.Disconnected;
    private string _deviceName = string.Empty;
    private string _macAddress = string.Empty;
    private bool _isScanning;
    private bool _disposed;

    private BluetoothLEDevice? _device;
    private BluetoothLEAdvertisementWatcher? _watcher;
    private CancellationTokenSource? _scanCts;

    private readonly ConcurrentDictionary<string, BleDeviceInfo> _discoveredDevices = new();
    private readonly ConcurrentDictionary<Guid, GattDeviceService> _services = new();
    private readonly ConcurrentDictionary<(Guid, Guid), GattCharacteristic> _characteristics = new();
    private readonly object _lock = new();

    public BleConnectionState State => _state;
    public string DeviceName => _deviceName;
    public string MacAddress => _macAddress;

    public event EventHandler<BleConnectionState>? StateChanged;
    public event EventHandler<BleDataReceivedEventArgs>? DataReceived;
    public event EventHandler<BleScanResultEventArgs>? DeviceDiscovered;

    /// <summary>
    /// 检查蓝牙适配器状态
    /// </summary>
    public static async Task<BluetoothAdapterStatus> CheckAdapterStatusAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter == null)
            {
                return new BluetoothAdapterStatus
                {
                    IsAvailable = false,
                    IsEnabled = false,
                    ErrorMessage = "未找到蓝牙适配器。请确保您的电脑有蓝牙功能。"
                };
            }

            var radio = await adapter.GetRadioAsync();
            if (radio == null)
            {
                return new BluetoothAdapterStatus
                {
                    IsAvailable = true,
                    IsEnabled = false,
                    ErrorMessage = "无法获取蓝牙无线电状态。请检查蓝牙驱动。"
                };
            }

            var isEnabled = radio.State == RadioState.On;

            return new BluetoothAdapterStatus
            {
                IsAvailable = true,
                IsEnabled = isEnabled,
                AdapterName = adapter.DeviceId,
                SupportsBle = adapter.IsLowEnergySupported,
                ErrorMessage = isEnabled ? null : "蓝牙已关闭。请在 Windows 设置中开启蓝牙。"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "检查蓝牙适配器状态失败");
            return new BluetoothAdapterStatus
            {
                IsAvailable = false,
                IsEnabled = false,
                ErrorMessage = $"检查蓝牙状态失败: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// 扫描 BLE 设备
    /// </summary>
    public async Task<BleDeviceInfo[]> ScanAsync(Guid? serviceFilter = null, string? namePrefix = null, 
        int timeoutMs = 10000, CancellationToken ct = default)
    {
        if (_isScanning)
        {
            Logger.Warning("扫描已在进行中");
            return _discoveredDevices.Values.ToArray();
        }

        // 检查蓝牙状态
        var status = await CheckAdapterStatusAsync();
        if (!status.IsAvailable)
            throw new InvalidOperationException(status.ErrorMessage ?? "蓝牙不可用");
        if (!status.IsEnabled)
            throw new InvalidOperationException(status.ErrorMessage ?? "蓝牙已关闭");

        _isScanning = true;
        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _discoveredDevices.Clear();

        try
        {
            Logger.Information("开始 BLE 扫描, timeout={Timeout}ms, namePrefix={Prefix}, serviceFilter={Filter}",
                timeoutMs, namePrefix, serviceFilter);

            _watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            // 设置服务过滤器
            if (serviceFilter.HasValue)
            {
                _watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(serviceFilter.Value);
            }

            var tcs = new TaskCompletionSource<bool>();

            _watcher.Received += (sender, args) =>
            {
                try
                {
                    ProcessAdvertisement(args, namePrefix);
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "处理广播数据失败");
                }
            };

            _watcher.Stopped += (sender, args) =>
            {
                Logger.Debug("BLE 扫描停止, error={Error}", args.Error);
                tcs.TrySetResult(true);
            };

            _watcher.Start();

            // 等待超时或取消
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_scanCts.Token, timeoutCts.Token);

            try
            {
                await Task.Delay(timeoutMs, linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                Logger.Debug("BLE 扫描被取消或超时");
            }
        }
        finally
        {
            StopScan();
        }

        var result = _discoveredDevices.Values.ToArray();
        Logger.Information("BLE 扫描完成, 发现 {Count} 个设备", result.Length);
        return result;
    }

    private void ProcessAdvertisement(BluetoothLEAdvertisementReceivedEventArgs args, string? namePrefix)
    {
        var deviceName = args.Advertisement.LocalName;

        // 名称前缀过滤
        if (!string.IsNullOrEmpty(namePrefix) &&
            (string.IsNullOrEmpty(deviceName) || !deviceName.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var macAddress = FormatMacAddress(args.BluetoothAddress);
        var deviceId = args.BluetoothAddress.ToString("X12");

        var device = new BleDeviceInfo
        {
            Id = deviceId,
            Name = string.IsNullOrEmpty(deviceName) ? $"Unknown ({macAddress})" : deviceName,
            MacAddress = macAddress,
            Rssi = args.RawSignalStrengthInDBm,
            ServiceUuids = args.Advertisement.ServiceUuids.ToArray(),
            IsConnectable = true // 默认可连接
        };

        var isNew = !_discoveredDevices.ContainsKey(deviceId);
        _discoveredDevices[deviceId] = device;

        if (isNew)
        {
            Logger.Debug("发现设备: {Name} ({Mac}), RSSI={Rssi}, Connectable={Connectable}",
                device.Name, macAddress, device.Rssi, device.IsConnectable);
            DeviceDiscovered?.Invoke(this, new BleScanResultEventArgs { Device = device });
        }
    }

    /// <summary>
    /// 停止扫描
    /// </summary>
    public void StopScan()
    {
        lock (_lock)
        {
            _scanCts?.Cancel();
            if (_watcher != null)
            {
                try
                {
                    _watcher.Stop();
                }
                catch { }
                _watcher = null;
            }
            _isScanning = false;
        }
    }

    /// <summary>
    /// 连接到 BLE 设备
    /// </summary>
    public async Task ConnectAsync(string deviceId, CancellationToken ct = default)
    {
        if (_state == BleConnectionState.Connected)
        {
            await DisconnectAsync();
        }

        SetState(BleConnectionState.Connecting);
        Logger.Information("正在连接 BLE 设备: {DeviceId}", deviceId);

        try
        {
            // 解析设备地址
            ulong address = ParseDeviceId(deviceId);

            // 使用 FromBluetoothAddressAsync 连接
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);

            if (_device == null)
            {
                throw new Exception($"无法连接到设备: {deviceId}。请确保设备已开启并在范围内。");
            }

            _device.ConnectionStatusChanged += OnConnectionStatusChanged;
            _deviceName = _device.Name ?? "Unknown";
            _macAddress = FormatMacAddress(address);

            // 获取 GATT 服务来触发实际连接
            var servicesResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success)
            {
                throw new Exception($"获取服务失败: {servicesResult.Status}。请确保设备未被其他应用占用。");
            }

            // 缓存服务
            _services.Clear();
            foreach (var service in servicesResult.Services)
            {
                _services[service.Uuid] = service;
                Logger.Debug("发现服务: {Uuid}", service.Uuid);
            }

            SetState(BleConnectionState.Connected);
            Logger.Information("已连接到 BLE 设备: {Name} ({Mac}), 发现 {Count} 个服务",
                _deviceName, _macAddress, _services.Count);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "连接 BLE 设备失败");
            SetState(BleConnectionState.Disconnected);
            throw new Exception($"蓝牙连接失败: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 断开连接
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_state == BleConnectionState.Disconnected)
            return;

        SetState(BleConnectionState.Disconnecting);
        Logger.Information("正在断开 BLE 设备连接");

        try
        {
            // 取消所有订阅
            foreach (var kvp in _characteristics)
            {
                try
                {
                    var characteristic = kvp.Value;
                    if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) ||
                        characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
                    {
                        characteristic.ValueChanged -= OnCharacteristicValueChanged;
                        await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.None);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "取消订阅失败");
                }
            }

            _characteristics.Clear();

            // 释放服务
            foreach (var service in _services.Values)
            {
                try { service.Dispose(); } catch { }
            }
            _services.Clear();

            // 释放设备
            if (_device != null)
            {
                _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                _device.Dispose();
                _device = null;
            }

            _deviceName = string.Empty;
            _macAddress = string.Empty;

            SetState(BleConnectionState.Disconnected);
            Logger.Information("已断开 BLE 设备连接");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "断开连接时出错");
            SetState(BleConnectionState.Disconnected);
        }
    }

    /// <summary>
    /// 发现服务（连接时已自动完成）
    /// </summary>
    public Task DiscoverServicesAsync()
    {
        // 服务在 ConnectAsync 中已经发现
        return Task.CompletedTask;
    }

    /// <summary>
    /// 订阅特性通知
    /// </summary>
    public async Task SubscribeAsync(Guid serviceUuid, Guid characteristicUuid)
    {
        var characteristic = await GetCharacteristicAsync(serviceUuid, characteristicUuid);

        if (!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) &&
            !characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
        {
            throw new Exception($"特性 {characteristicUuid} 不支持通知");
        }

        characteristic.ValueChanged += OnCharacteristicValueChanged;

        var descriptorValue = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;

        var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(descriptorValue);
        if (status != GattCommunicationStatus.Success)
        {
            characteristic.ValueChanged -= OnCharacteristicValueChanged;
            throw new Exception($"订阅失败: {status}");
        }

        Logger.Information("已订阅特性通知: {Uuid}", characteristicUuid);
    }

    /// <summary>
    /// 取消订阅
    /// </summary>
    public async Task UnsubscribeAsync(Guid serviceUuid, Guid characteristicUuid)
    {
        var key = (serviceUuid, characteristicUuid);
        if (_characteristics.TryGetValue(key, out var characteristic))
        {
            characteristic.ValueChanged -= OnCharacteristicValueChanged;
            await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.None);
            Logger.Information("已取消订阅特性通知: {Uuid}", characteristicUuid);
        }
    }

    /// <summary>
    /// 写入数据（带响应）
    /// </summary>
    public async Task WriteAsync(Guid serviceUuid, Guid characteristicUuid, byte[] data)
    {
        var characteristic = await GetCharacteristicAsync(serviceUuid, characteristicUuid);

        var writer = new DataWriter();
        writer.WriteBytes(data);
        var status = await characteristic.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse);

        if (status != GattCommunicationStatus.Success)
        {
            throw new Exception($"写入失败: {status}");
        }

        Logger.Debug("BLE Write (with response): {Data}", BitConverter.ToString(data));
    }

    /// <summary>
    /// 写入数据（无响应）
    /// </summary>
    public async Task WriteWithoutResponseAsync(Guid serviceUuid, Guid characteristicUuid, byte[] data)
    {
        var characteristic = await GetCharacteristicAsync(serviceUuid, characteristicUuid);

        var writer = new DataWriter();
        writer.WriteBytes(data);
        var status = await characteristic.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);

        if (status != GattCommunicationStatus.Success)
        {
            throw new Exception($"写入失败: {status}");
        }

        Logger.Debug("BLE Write (without response): {Data}", BitConverter.ToString(data));
    }

    /// <summary>
    /// 读取数据
    /// </summary>
    public async Task<byte[]> ReadAsync(Guid serviceUuid, Guid characteristicUuid)
    {
        var characteristic = await GetCharacteristicAsync(serviceUuid, characteristicUuid);

        var result = await characteristic.ReadValueAsync();
        if (result.Status != GattCommunicationStatus.Success)
        {
            throw new Exception($"读取失败: {result.Status}");
        }

        var reader = DataReader.FromBuffer(result.Value);
        var data = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(data);

        Logger.Debug("BLE Read: {Data}", BitConverter.ToString(data));
        return data;
    }

    #region Private Methods

    private async Task<GattCharacteristic> GetCharacteristicAsync(Guid serviceUuid, Guid characteristicUuid)
    {
        var key = (serviceUuid, characteristicUuid);

        if (_characteristics.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (!_services.TryGetValue(serviceUuid, out var service))
        {
            throw new Exception($"未找到服务: {serviceUuid}");
        }

        var charResult = await service.GetCharacteristicsForUuidAsync(characteristicUuid);
        if (charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
        {
            throw new Exception($"未找到特性: {characteristicUuid}");
        }

        var characteristic = charResult.Characteristics[0];
        _characteristics[key] = characteristic;
        return characteristic;
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        Logger.Information("BLE 连接状态变化: {Status}", sender.ConnectionStatus);

        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            SetState(BleConnectionState.Disconnected);
        }
    }

    private void OnCharacteristicValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var data = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(data);

            var serviceUuid = sender.Service.Uuid;

            DataReceived?.Invoke(this, new BleDataReceivedEventArgs
            {
                ServiceUuid = serviceUuid,
                CharacteristicUuid = sender.Uuid,
                Data = data
            });
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "处理特性值变化失败");
        }
    }

    private void SetState(BleConnectionState newState)
    {
        if (_state != newState)
        {
            _state = newState;
            StateChanged?.Invoke(this, newState);
        }
    }

    private static ulong ParseDeviceId(string deviceId)
    {
        // 支持多种格式: 十六进制字符串、MAC 地址
        if (deviceId.Contains(':'))
        {
            // MAC 地址格式: AA:BB:CC:DD:EE:FF
            var parts = deviceId.Split(':');
            if (parts.Length != 6)
                throw new ArgumentException($"无效的 MAC 地址格式: {deviceId}");

            var bytes = parts.Select(p => Convert.ToByte(p, 16)).Reverse().ToArray();
            var result = new byte[8];
            Array.Copy(bytes, result, 6);
            return BitConverter.ToUInt64(result, 0);
        }
        else if (ulong.TryParse(deviceId, System.Globalization.NumberStyles.HexNumber, null, out var address))
        {
            return address;
        }
        else
        {
            throw new ArgumentException($"无效的设备 ID 格式: {deviceId}");
        }
    }

    private static string FormatMacAddress(ulong address)
    {
        var bytes = BitConverter.GetBytes(address);
        return string.Join(":", bytes.Take(6).Reverse().Select(b => b.ToString("X2")));
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopScan();
        Task.Run(async () =>
        {
            try { await DisconnectAsync(); }
            catch { }
        }).Wait(TimeSpan.FromSeconds(2));

        GC.SuppressFinalize(this);
    }
}
