using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Storage.Streams;
using Serilog;

namespace ChargingPanel.Core.Bluetooth;

/// <summary>
/// Windows 下的 BLE 传输封装。
/// 负责扫描、连接、订阅、读写，以及断线后的自动恢复。
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
    private CancellationTokenSource? _recoverCts;

    private readonly ConcurrentDictionary<string, BleDeviceInfo> _discoveredDevices = new();
    private readonly ConcurrentDictionary<Guid, GattDeviceService> _services = new();
    private readonly ConcurrentDictionary<(Guid, Guid), GattCharacteristic> _characteristics = new();
    private readonly ConcurrentDictionary<(Guid, Guid), byte> _subscriptionIntents = new();
    private readonly List<GattSession> _sessions = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _gattOperationLock = new(1, 1);
    private string? _lastDeviceId;
    private int _recovering;
    private bool _manualDisconnect;
    private const int ConnectRetryCount = 4;
    private const int SubscribeRetryCount = 3;
    private const int WriteRetryCount = 3;
    private const int RetryDelayMs = 350;
    private const int ConnectRetryDelayMs = 700;
    private const int ConnectAttemptTimeoutMs = 15000;
    private const int GattOperationTimeoutMs = 5000;
    private const int RecoverRetryCount = 3;

    public BleConnectionState State => _state;
    public string DeviceName => _deviceName;
    public string MacAddress => _macAddress;

    public event EventHandler<BleConnectionState>? StateChanged;
    public event EventHandler<BleDataReceivedEventArgs>? DataReceived;
    public event EventHandler<BleScanResultEventArgs>? DeviceDiscovered;

    /// <summary>
    /// 读取系统蓝牙适配器状态（连接前的快速自检）。
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
    /// 扫描 BLE 设备。
    /// </summary>
    public async Task<BleDeviceInfo[]> ScanAsync(Guid? serviceFilter = null, string? namePrefix = null, 
        int timeoutMs = 10000, CancellationToken ct = default)
    {
        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            try
            {
                ProcessAdvertisement(args, namePrefix);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "处理广播数据失败");
            }
        }

        void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            Logger.Debug("BLE 扫描停止, error={Error}", args.Error);
        }

        BluetoothLEAdvertisementWatcher? watcher = null;
        CancellationTokenSource? scanCts = null;

        await _connectionLock.WaitAsync(ct);
        try
        {
            if (_isScanning)
            {
                Logger.Warning("扫描已在进行中");
                return _discoveredDevices.Values.ToArray();
            }

            // 先确认系统蓝牙可用，再进入扫描流程。
            var status = await CheckAdapterStatusAsync();
            if (!status.IsAvailable)
                throw new InvalidOperationException(status.ErrorMessage ?? "蓝牙不可用");
            if (!status.IsEnabled)
                throw new InvalidOperationException(status.ErrorMessage ?? "蓝牙已关闭");

            _isScanning = true;
            _scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            scanCts = _scanCts;
            _discoveredDevices.Clear();

            Logger.Information("开始 BLE 扫描, timeout={Timeout}ms, namePrefix={Prefix}, serviceFilter={Filter}",
                timeoutMs, namePrefix, serviceFilter);

            watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };
            _watcher = watcher;

            // 指定服务 UUID 时只扫描目标设备，减少噪音。
            if (serviceFilter.HasValue)
            {
                watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(serviceFilter.Value);
            }

            watcher.Received += OnReceived;
            watcher.Stopped += OnStopped;
            watcher.Start();
        }
        finally
        {
            _connectionLock.Release();
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(scanCts?.Token ?? ct, timeoutCts.Token);

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
            await _connectionLock.WaitAsync();
            try
            {
                if (watcher != null)
                {
                    try
                    {
                        watcher.Stop();
                    }
                    catch
                    {
                        // 清理阶段忽略 Stop 异常，避免影响主流程。
                    }

                    watcher.Received -= OnReceived;
                    watcher.Stopped -= OnStopped;
                }

                if (ReferenceEquals(_watcher, watcher))
                {
                    _watcher = null;
                }

                if (ReferenceEquals(_scanCts, scanCts))
                {
                    _scanCts?.Cancel();
                    _scanCts?.Dispose();
                    _scanCts = null;
                }

                _isScanning = false;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        var result = _discoveredDevices.Values.ToArray();
        Logger.Information("BLE 扫描完成, 发现 {Count} 个设备", result.Length);
        return result;
    }

    /// <summary>
    /// 连接 BLE 设备（包含服务发现和订阅恢复）。
    /// </summary>
    public async Task ConnectAsync(string deviceId, CancellationToken ct = default)
    {
        // 连接前先停扫，避免扫描和连接争用蓝牙栈导致首连失败或 UI 卡顿。
        StopScan();

        await _connectionLock.WaitAsync(ct);
        try
        {
            _manualDisconnect = false;
            StopRecoveryLoop();
            _lastDeviceId = deviceId;

            if (_state == BleConnectionState.Connected)
            {
                await DisconnectCoreAsync();
            }

            await ConnectCoreAsync(deviceId, ct);
            await RestoreSubscriptionsAsync();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private void ProcessAdvertisement(BluetoothLEAdvertisementReceivedEventArgs args, string? namePrefix)
    {
        var deviceName = args.Advertisement.LocalName;

        // 按名称前缀过滤，避免无关设备混入结果。
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
            IsConnectable = true // 扫描阶段先按可连接处理，实际以连接结果为准
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
    /// 主动停止扫描。
    /// </summary>
    public void StopScan()
    {
        lock (_lock)
        {
            _scanCts?.Cancel();
            _scanCts?.Dispose();
            _scanCts = null;
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
    /// 断开当前连接。
    /// </summary>
    public async Task DisconnectAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            _manualDisconnect = true;
            StopRecoveryLoop();
            await DisconnectCoreAsync();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// 发现服务（已在 ConnectAsync 内完成，这里仅做兼容）。
    /// </summary>
    public Task DiscoverServicesAsync()
    {
        // 服务发现已在 ConnectAsync 执行，这里保留接口语义。
        return Task.CompletedTask;
    }

    /// <summary>
    /// 订阅特征通知。
    /// </summary>
    public async Task SubscribeAsync(Guid serviceUuid, Guid characteristicUuid)
    {
        await _gattOperationLock.WaitAsync();
        try
        {
            EnsureConnected();
            var characteristic = await GetCharacteristicAsync(serviceUuid, characteristicUuid);

            if (!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) &&
                !characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
            {
                throw new Exception($"特性 {characteristicUuid} 不支持通知");
            }

            var descriptorValue = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
                ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                : GattClientCharacteristicConfigurationDescriptorValue.Indicate;

            Exception? lastException = null;
            for (var attempt = 1; attempt <= SubscribeRetryCount; attempt++)
            {
                try
                {
                    characteristic.ValueChanged -= OnCharacteristicValueChanged;
                    characteristic.ValueChanged += OnCharacteristicValueChanged;

                    var status = await characteristic
                        .WriteClientCharacteristicConfigurationDescriptorAsync(descriptorValue)
                        .AsTask()
                        .WaitAsync(TimeSpan.FromMilliseconds(GattOperationTimeoutMs));
                    if (status == GattCommunicationStatus.Success)
                    {
                        _subscriptionIntents[(serviceUuid, characteristicUuid)] = 1;
                        Logger.Information("已订阅特性通知: {Uuid}", characteristicUuid);
                        return;
                    }

                    throw new Exception($"订阅失败: {status}");
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    characteristic.ValueChanged -= OnCharacteristicValueChanged;
                    Logger.Warning(ex, "订阅特性通知失败，尝试 {Attempt}/{Max}", attempt, SubscribeRetryCount);

                    if (attempt < SubscribeRetryCount)
                    {
                        await Task.Delay(RetryDelayMs);
                    }
                }
            }

            throw new Exception($"订阅失败: {characteristicUuid}", lastException);
        }
        finally
        {
            _gattOperationLock.Release();
        }
    }

    /// <summary>
    /// 取消特征通知订阅。
    /// </summary>
    public async Task UnsubscribeAsync(Guid serviceUuid, Guid characteristicUuid)
    {
        await _gattOperationLock.WaitAsync();
        try
        {
            EnsureConnected();
            var key = (serviceUuid, characteristicUuid);
            if (_characteristics.TryGetValue(key, out var characteristic))
            {
                characteristic.ValueChanged -= OnCharacteristicValueChanged;
                await characteristic
                    .WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(GattOperationTimeoutMs));
                _subscriptionIntents.TryRemove(key, out _);
                Logger.Information("已取消订阅特性通知: {Uuid}", characteristicUuid);
            }
        }
        finally
        {
            _gattOperationLock.Release();
        }
    }

    /// <summary>
    /// 写入数据（有响应）。
    /// </summary>
    public async Task WriteAsync(Guid serviceUuid, Guid characteristicUuid, byte[] data)
    {
        await _gattOperationLock.WaitAsync();
        try
        {
            EnsureConnected();
            var characteristic = await GetCharacteristicAsync(serviceUuid, characteristicUuid);
            await WriteWithRetryLockedAsync(characteristic, data, GattWriteOption.WriteWithResponse);
            Logger.Debug("BLE Write (with response): {Data}", BitConverter.ToString(data));
        }
        finally
        {
            _gattOperationLock.Release();
        }
    }

    /// <summary>
    /// 写入数据（无响应）。
    /// </summary>
    public async Task WriteWithoutResponseAsync(Guid serviceUuid, Guid characteristicUuid, byte[] data)
    {
        await _gattOperationLock.WaitAsync();
        try
        {
            EnsureConnected();
            var characteristic = await GetCharacteristicAsync(serviceUuid, characteristicUuid);

            // 先走无响应写，失败再回退到有响应写。
            var status = await characteristic
                .WriteValueAsync(data.AsBuffer(), GattWriteOption.WriteWithoutResponse)
                .AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(GattOperationTimeoutMs));
            if (status != GattCommunicationStatus.Success)
            {
                Logger.Warning("WriteWithoutResponse 失败: {Status}，回退 WriteWithResponse", status);
                await WriteWithRetryLockedAsync(characteristic, data, GattWriteOption.WriteWithResponse);
            }

            Logger.Debug("BLE Write (without response): {Data}", BitConverter.ToString(data));
        }
        finally
        {
            _gattOperationLock.Release();
        }
    }

    /// <summary>
    /// 读取特征值。
    /// </summary>
    public async Task<byte[]> ReadAsync(Guid serviceUuid, Guid characteristicUuid)
    {
        await _gattOperationLock.WaitAsync();
        try
        {
            EnsureConnected();
            var characteristic = await GetCharacteristicAsync(serviceUuid, characteristicUuid);

            var result = await characteristic
                .ReadValueAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(GattOperationTimeoutMs));
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
        finally
        {
            _gattOperationLock.Release();
        }
    }

    #region Private Methods

    private void EnsureConnected()
    {
        if (_state != BleConnectionState.Connected || _device == null)
        {
            throw new InvalidOperationException("BLE 设备未连接");
        }
    }

    private async Task ConnectCoreAsync(string deviceId, CancellationToken ct)
    {
        var adapterStatus = await CheckAdapterStatusAsync();
        if (!adapterStatus.IsAvailable)
            throw new InvalidOperationException(adapterStatus.ErrorMessage ?? "蓝牙不可用");
        if (!adapterStatus.IsEnabled)
            throw new InvalidOperationException(adapterStatus.ErrorMessage ?? "蓝牙已关闭");

        if (!IsProcessElevated())
        {
            Logger.Warning("当前进程未以管理员权限运行。建议管理员权限运行以获得最稳定的系统蓝牙连接权限。");
        }

        SetState(BleConnectionState.Connecting);
        Logger.Information("正在连接 BLE 设备: {DeviceId}", deviceId);

        Exception? lastException = null;
        ulong address = ParseDeviceId(deviceId);
        var attemptTimeout = TimeSpan.FromMilliseconds(ConnectAttemptTimeoutMs);

        for (var attempt = 1; attempt <= ConnectRetryCount; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var deviceInfo = await ResolveSystemDeviceInfoAsync(address);
                if (deviceInfo != null)
                {
                    await TryPairWithSystemAsync(deviceInfo);
                    await Task.Delay(200, ct);
                }

                _device = await CreateDeviceHandleAsync(address, deviceInfo, ct, attemptTimeout);
                if (_device == null)
                {
                    deviceInfo = await ResolveSystemDeviceInfoAsync(address);
                    _device = await CreateDeviceHandleAsync(address, deviceInfo, ct, attemptTimeout);
                }
                if (_device == null)
                {
                    throw new Exception($"无法连接到设备: {deviceId}。请确保设备已开启并在范围内。");
                }

                await EnsureDeviceAccessAsync(_device);
                await Task.Delay(150, ct);
                _device.ConnectionStatusChanged += OnConnectionStatusChanged;
                _deviceName = _device.Name ?? "Unknown";
                _macAddress = FormatMacAddress(address);

                // Win10/11 兼容策略：先 Uncached，失败再回退 Cached。
                var servicesResult = await _device
                    .GetGattServicesAsync(BluetoothCacheMode.Uncached)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(ConnectAttemptTimeoutMs), ct);
                if (servicesResult.Status != GattCommunicationStatus.Success)
                {
                    Logger.Warning("Uncached 服务发现失败: {Status}，回退 Cached 重试", servicesResult.Status);
                    servicesResult = await _device
                        .GetGattServicesAsync(BluetoothCacheMode.Cached)
                        .AsTask()
                        .WaitAsync(TimeSpan.FromMilliseconds(ConnectAttemptTimeoutMs), ct);
                }

                if (servicesResult.Status != GattCommunicationStatus.Success)
                {
                    throw new Exception($"获取服务失败: {servicesResult.Status}。请确保设备未被其他应用占用。");
                }

                _services.Clear();
                foreach (var service in servicesResult.Services)
                {
                    _services[service.Uuid] = service;
                    Logger.Debug("发现服务: {Uuid}", service.Uuid);
                }
                await EnableMaintainConnectionAsync(servicesResult.Services);

                SetState(BleConnectionState.Connected);
                Logger.Information("已连接到 BLE 设备: {Name} ({Mac}), 发现 {Count} 个服务",
                    _deviceName, _macAddress, _services.Count);
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
                Logger.Warning(ex, "连接 BLE 设备失败，尝试 {Attempt}/{Max}", attempt, ConnectRetryCount);
                CleanupPartialConnection();

                if (attempt < ConnectRetryCount)
                {
                    var retryDelay = TimeSpan.FromMilliseconds(ConnectRetryDelayMs * attempt);
                    await Task.Delay(retryDelay, ct);
                }
            }
        }

        SetState(BleConnectionState.Disconnected);
        throw new Exception($"蓝牙连接失败: {lastException?.Message}", lastException);
    }

    private async Task DisconnectCoreAsync()
    {
        if (_state == BleConnectionState.Disconnected)
        {
            return;
        }

        SetState(BleConnectionState.Disconnecting);
        Logger.Information("正在断开 BLE 设备连接");

        await _gattOperationLock.WaitAsync();
        try
        {
            foreach (var kvp in _characteristics)
            {
                try
                {
                    var characteristic = kvp.Value;
                    if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) ||
                        characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
                    {
                        characteristic.ValueChanged -= OnCharacteristicValueChanged;
                        await characteristic
                            .WriteClientCharacteristicConfigurationDescriptorAsync(
                                GattClientCharacteristicConfigurationDescriptorValue.None)
                            .AsTask()
                            .WaitAsync(TimeSpan.FromMilliseconds(GattOperationTimeoutMs));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "取消订阅失败");
                }
            }

            _characteristics.Clear();

            foreach (var service in _services.Values)
            {
                try { service.Dispose(); } catch { }
            }
            _services.Clear();
            ClearSessions();

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
        finally
        {
            _gattOperationLock.Release();
        }
    }

    private async Task RestoreSubscriptionsAsync()
    {
        if (_subscriptionIntents.IsEmpty)
        {
            return;
        }

        var subscriptions = _subscriptionIntents.Keys.ToArray();
        foreach (var (serviceUuid, characteristicUuid) in subscriptions)
        {
            try
            {
                await SubscribeAsync(serviceUuid, characteristicUuid);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "恢复订阅失败: {Service}/{Characteristic}", serviceUuid, characteristicUuid);
            }
        }
    }

    private void StartRecoveryLoop()
    {
        if (_manualDisconnect || _disposed || string.IsNullOrWhiteSpace(_lastDeviceId))
        {
            return;
        }

        if (Interlocked.Exchange(ref _recovering, 1) == 1)
        {
            return;
        }

        StopRecoveryLoop();
        _recoverCts = new CancellationTokenSource();
        var token = _recoverCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 1; attempt <= RecoverRetryCount && !token.IsCancellationRequested; attempt++)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(RetryDelayMs * attempt), token);
                        await _connectionLock.WaitAsync(token);
                        try
                        {
                            if (_state == BleConnectionState.Connected)
                            {
                                return;
                            }

                            await ConnectCoreAsync(_lastDeviceId!, token);
                            await RestoreSubscriptionsAsync();
                            Logger.Information("BLE 自动恢复连接成功");
                            return;
                        }
                        finally
                        {
                            _connectionLock.Release();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(ex, "BLE 自动恢复失败，尝试 {Attempt}/{Max}", attempt, RecoverRetryCount);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _recovering, 0);
            }
        }, token);
    }

    private void StopRecoveryLoop()
    {
        _recoverCts?.Cancel();
        _recoverCts?.Dispose();
        _recoverCts = null;
        Interlocked.Exchange(ref _recovering, 0);
    }

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

        var charResult = await service
            .GetCharacteristicsForUuidAsync(characteristicUuid)
            .AsTask()
            .WaitAsync(TimeSpan.FromMilliseconds(GattOperationTimeoutMs));
        if (charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
        {
            throw new Exception($"未找到特性: {characteristicUuid}");
        }

        var characteristic = charResult.Characteristics[0];
        _characteristics[key] = characteristic;
        return characteristic;
    }

    private async Task WriteWithRetryLockedAsync(
        GattCharacteristic characteristic,
        byte[] data,
        GattWriteOption writeOption)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= WriteRetryCount; attempt++)
        {
            try
            {
                var status = await characteristic
                    .WriteValueAsync(data.AsBuffer(), writeOption)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(GattOperationTimeoutMs));
                if (status == GattCommunicationStatus.Success)
                {
                    return;
                }

                lastException = new Exception($"写入失败: {status}");
            }
            catch (Exception ex)
            {
                lastException = ex;
            }

            if (attempt < WriteRetryCount)
            {
                await Task.Delay(RetryDelayMs);
            }
        }

        throw new Exception("写入失败", lastException);
    }

    private void CleanupPartialConnection()
    {
        _characteristics.Clear();

        foreach (var service in _services.Values)
        {
            try { service.Dispose(); } catch { }
        }
        _services.Clear();
        ClearSessions();

        if (_device != null)
        {
            try
            {
                _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                _device.Dispose();
            }
            catch { }
            _device = null;
        }

        _deviceName = string.Empty;
        _macAddress = string.Empty;
    }

    private async Task EnableMaintainConnectionAsync(IReadOnlyList<GattDeviceService> services)
    {
        ClearSessions();
        foreach (var service in services)
        {
            try
            {
                var bluetoothDeviceId = BluetoothDeviceId.FromId(service.DeviceId);
                var session = await GattSession.FromDeviceIdAsync(bluetoothDeviceId);
                if (session == null)
                {
                    continue;
                }

                session.MaintainConnection = true;
                _sessions.Add(session);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "启用 GattSession.MaintainConnection 失败: {ServiceUuid}", service.Uuid);
            }
        }
    }

    private void ClearSessions()
    {
        foreach (var session in _sessions)
        {
            try
            {
                session.Dispose();
            }
            catch
            {
                // 清理阶段不抛错，保证断链流程继续执行。
            }
        }

        _sessions.Clear();
    }

    private static async Task<DeviceInformation?> ResolveSystemDeviceInfoAsync(ulong address)
    {
        var selector = BluetoothLEDevice.GetDeviceSelectorFromBluetoothAddress(address);
        var devices = await DeviceInformation.FindAllAsync(selector);
        return devices.FirstOrDefault();
    }

    private static async Task<BluetoothLEDevice?> CreateDeviceHandleAsync(
        ulong address,
        DeviceInformation? deviceInfo,
        CancellationToken ct,
        TimeSpan timeout)
    {
        if (deviceInfo != null)
        {
            var byId = await BluetoothLEDevice
                .FromIdAsync(deviceInfo.Id)
                .AsTask()
                .WaitAsync(timeout, ct);
            if (byId != null)
            {
                return byId;
            }
        }

        return await BluetoothLEDevice
            .FromBluetoothAddressAsync(address)
            .AsTask()
            .WaitAsync(timeout, ct);
    }

    private static async Task EnsureDeviceAccessAsync(BluetoothLEDevice device)
    {
        var accessInfo = DeviceAccessInformation.CreateFromId(device.DeviceId);
        var status = accessInfo.CurrentStatus;

        if (status == DeviceAccessStatus.Unspecified)
        {
            status = await device.RequestAccessAsync();
        }

        if (status is DeviceAccessStatus.DeniedBySystem or DeviceAccessStatus.DeniedByUser)
        {
            throw new UnauthorizedAccessException(
                $"系统蓝牙权限不足（{status}）。请以管理员身份运行并在 Windows 设置中允许蓝牙访问。");
        }
    }

    private static async Task TryPairWithSystemAsync(DeviceInformation deviceInfo)
    {
        try
        {
            if (!deviceInfo.Pairing.IsPaired && deviceInfo.Pairing.CanPair)
            {
                var pairTask = deviceInfo.Pairing.PairAsync(DevicePairingProtectionLevel.None).AsTask();
                var pairResult = await pairTask.WaitAsync(TimeSpan.FromSeconds(4));
                var pairTask = deviceInfo.Pairing.PairAsync(DevicePairingProtectionLevel.None).AsTask();
                var pairResult = await pairTask.WaitAsync(TimeSpan.FromSeconds(4));
                Logger.Information("系统蓝牙配对结果: {Status}", pairResult.Status);
            }
        }
        catch (TimeoutException)
        {
            // 某些 Win10/11 机型配对调用会很慢，这里不阻塞直连流程。
            Logger.Warning("系统蓝牙配对超时，将继续直接连接");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "尝试系统蓝牙配对失败，将继续直接连接");
        }
    }

    private static bool IsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        Logger.Information("BLE 连接状态变化: {Status}", sender.ConnectionStatus);

        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            SetState(BleConnectionState.Disconnected);
            if (!_manualDisconnect && !_disposed)
            {
                StartRecoveryLoop();
            }
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
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("设备 ID 不能为空");
        }

        deviceId = deviceId.Trim();

        // 兼容多种地址写法：十六进制字符串或 MAC。
        if (deviceId.Contains(':') || deviceId.Contains('-'))
        {
            // MAC 地址格式：AA:BB:CC:DD:EE:FF 或 AA-BB-CC-DD-EE-FF
            var parts = deviceId.Replace('-', ':').Split(':');
            if (parts.Length != 6)
                throw new ArgumentException($"无效的 MAC 地址格式: {deviceId}");

            var bytes = parts.Select(p => Convert.ToByte(p, 16)).Reverse().ToArray();
            var result = new byte[8];
            Array.Copy(bytes, result, 6);
            return BitConverter.ToUInt64(result, 0);
        }

        // 12 位十六进制（无分隔符）或普通十六进制字符串。
        var normalized = deviceId.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        if (ulong.TryParse(normalized, System.Globalization.NumberStyles.HexNumber, null, out var address))
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

        StopRecoveryLoop();
        StopScan();
        try
        {
            foreach (var characteristic in _characteristics.Values)
            {
                try
                {
                    characteristic.ValueChanged -= OnCharacteristicValueChanged;
                }
                catch
                {
                    // 清理阶段忽略异常。
                }
            }

            _subscriptionIntents.Clear();
            CleanupPartialConnection();
            SetState(BleConnectionState.Disconnected);
        }
        catch
        {
            // 清理阶段忽略异常。
        }
        _connectionLock.Dispose();
        _gattOperationLock.Dispose();

        GC.SuppressFinalize(this);
    }
}
