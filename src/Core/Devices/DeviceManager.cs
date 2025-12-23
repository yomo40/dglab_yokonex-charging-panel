using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChargingPanel.Core.Bluetooth;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices.DGLab;
using ChargingPanel.Core.Devices.Yokonex;
using Serilog;

namespace ChargingPanel.Core.Devices;

/// <summary>
/// 连接模式
/// </summary>
public enum ConnectionMode
{
    /// <summary>WebSocket 中转</summary>
    WebSocket,
    /// <summary>蓝牙直连</summary>
    Bluetooth,
    /// <summary>腾讯 IM</summary>
    TencentIM
}

/// <summary>
/// 设备管理器 - 优化版
/// 使用异步操作、缓存和批量处理提升性能
/// </summary>
public class DeviceManager : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<DeviceManager>();

    private readonly ConcurrentDictionary<string, DeviceWrapper> _devices = new();
    private readonly ConnectionDiagnostics _diagnostics = new();
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private int _deviceCounter;
    private bool _disposed;
    
    // 设备信息缓存
    private List<DeviceInfo>? _deviceInfoCache;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private const int CacheExpiryMs = 500;

    /// <summary>连接诊断服务</summary>
    public ConnectionDiagnostics Diagnostics => _diagnostics;

    /// <summary>设备状态变化事件</summary>
    public event EventHandler<DeviceStatusChangedEventArgs>? DeviceStatusChanged;

    /// <summary>设备强度变化事件</summary>
    public event EventHandler<DeviceStrengthChangedEventArgs>? DeviceStrengthChanged;

    /// <summary>设备错误事件</summary>
    public event EventHandler<DeviceErrorEventArgs>? DeviceError;

    /// <summary>QR 码变化事件（用于 WebSocket 连接）</summary>
    public event EventHandler<QRCodeChangedEventArgs>? QRCodeChanged;



    #region Device Creation

    /// <summary>
    /// 创建设备适配器
    /// </summary>
    public IDevice CreateAdapter(DeviceType type, ConnectionMode mode, string? name = null,
        DGLabVersion? dglabVersion = null, YokonexDeviceType? yokonexType = null)
    {
        return type switch
        {
            DeviceType.DGLab => CreateDGLabAdapter(mode, dglabVersion, name),
            DeviceType.Yokonex => CreateYokonexAdapter(mode, yokonexType, name),
            DeviceType.Virtual => new VirtualDevice(type, name),
            _ => throw new ArgumentException($"未知设备类型: {type}")
        };
    }

    private IDevice CreateDGLabAdapter(ConnectionMode mode, DGLabVersion? version, string? name)
    {
        var v = version ?? DGLabVersion.V3;
        var deviceName = name ?? $"郊狼 {v}";

        return mode switch
        {
            ConnectionMode.WebSocket => new DGLabWebSocketAdapter(name: deviceName),
            ConnectionMode.Bluetooth => CreateDGLabBluetoothAdapter(v, deviceName),
            _ => new DGLabWebSocketAdapter(name: deviceName)
        };
    }

    private IDevice CreateDGLabBluetoothAdapter(DGLabVersion version, string name)
    {
        var adapter = new DGLabBluetoothAdapter(version, name: name);
        var transport = new WindowsBluetoothTransport();
        adapter.SetTransport(transport);
        return adapter;
    }

    private IDevice CreateYokonexAdapter(ConnectionMode mode, YokonexDeviceType? deviceType, string? name)
    {
        var type = deviceType ?? YokonexDeviceType.Estim;
        var deviceName = name ?? $"役次元 {GetYokonexTypeName(type)}";

        return mode switch
        {
            ConnectionMode.Bluetooth => CreateYokonexBluetoothAdapter(type, deviceName),
            ConnectionMode.TencentIM => new YokonexIMAdapter(type, name: deviceName),
            _ => new YokonexIMAdapter(type, name: deviceName)
        };
    }

    private IDevice CreateYokonexBluetoothAdapter(YokonexDeviceType type, string name)
    {
        var transport = new WindowsBluetoothTransport();
        return type switch
        {
            YokonexDeviceType.Estim => new YokonexEmsBluetoothAdapter(transport, name: name),
            YokonexDeviceType.Enema => new YokonexEnemaBluetoothAdapter(transport, name: name),
            YokonexDeviceType.Vibrator or YokonexDeviceType.Cup => new YokonexToyBluetoothAdapter(transport, name: name),
            _ => new YokonexEmsBluetoothAdapter(transport, name: name)
        };
    }

    private static string GetYokonexTypeName(YokonexDeviceType type) => type switch
    {
        YokonexDeviceType.Estim => "电击器",
        YokonexDeviceType.Enema => "灌肠器",
        YokonexDeviceType.Vibrator => "跳蛋",
        YokonexDeviceType.Cup => "飞机杯",
        _ => "设备"
    };

    #endregion

    #region Device Management

    /// <summary>
    /// 添加设备
    /// </summary>
    public async Task<string> AddDeviceAsync(DeviceType type, ConnectionConfig config, string? name = null,
        bool isVirtual = false, ConnectionMode mode = ConnectionMode.WebSocket,
        DGLabVersion? dglabVersion = null, YokonexDeviceType? yokonexType = null)
    {
        var deviceName = name ?? $"{(type == DeviceType.DGLab ? "郊狼" : "役次元")} 设备 {_deviceCounter + 1}";

        IDevice adapter;
        if (isVirtual)
        {
            adapter = new VirtualDevice(type, deviceName);
        }
        else
        {
            adapter = CreateAdapter(type, mode, deviceName, dglabVersion, yokonexType);
        }

        var id = adapter.Id;

        var wrapper = new DeviceWrapper
        {
            Id = id,
            Name = deviceName,
            Type = type,
            Status = DeviceStatus.Disconnected,
            Config = config,
            Device = adapter,
            IsVirtual = isVirtual,
            ConnectionMode = mode,
            DGLabVersion = dglabVersion,
            YokonexType = yokonexType
        };

        // 注册事件
        RegisterDeviceEvents(wrapper);

        _devices[id] = wrapper;
        Interlocked.Increment(ref _deviceCounter);
        Logger.Information("设备已添加: {Id} ({Name}), 模式={Mode}", id, deviceName, mode);

        // 保存到数据库
        SaveDeviceToDatabase(wrapper);

        return id;
    }

    private void RegisterDeviceEvents(DeviceWrapper wrapper)
    {
        var adapter = wrapper.Device;
        var id = wrapper.Id;

        adapter.StatusChanged += (s, status) =>
        {
            wrapper.Status = status;
            DeviceStatusChanged?.Invoke(this, new DeviceStatusChangedEventArgs(id, status));
            Logger.Information("设备 {Id} 状态变化: {Status}", id, status);
        };

        adapter.StrengthChanged += (s, strength) =>
        {
            DeviceStrengthChanged?.Invoke(this, new DeviceStrengthChangedEventArgs(id, strength));
        };

        adapter.ErrorOccurred += (s, error) =>
        {
            DeviceError?.Invoke(this, new DeviceErrorEventArgs(id, error));
            Logger.Error(error, "设备 {Id} 错误", id);
        };

        // WebSocket 适配器的 QR 码事件
        if (adapter is DGLabWebSocketAdapter wsAdapter)
        {
            wsAdapter.QRCodeChanged += (s, qrContent) =>
            {
                QRCodeChanged?.Invoke(this, new QRCodeChangedEventArgs(id, qrContent, wsAdapter.ClientId));
            };
        }
    }

    private void SaveDeviceToDatabase(DeviceWrapper wrapper)
    {
        try
        {
            Database.Instance.AddDevice(new DeviceRecord
            {
                Id = wrapper.Id,
                Name = wrapper.Name,
                Type = wrapper.Type == DeviceType.DGLab ? "dglab" : "yokonex",
                Config = System.Text.Json.JsonSerializer.Serialize(wrapper.Config),
                AutoConnect = wrapper.Config.AutoReconnect
            });
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "保存设备到数据库失败");
        }
    }

    /// <summary>
    /// 连接设备
    /// </summary>
    public async Task ConnectDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        var wrapper = GetDeviceWrapper(deviceId);
        Logger.Information("正在连接设备: {DeviceId}", deviceId);
        await wrapper.Device.ConnectAsync(wrapper.Config, ct);
    }

    /// <summary>
    /// 断开设备连接
    /// </summary>
    public async Task DisconnectDeviceAsync(string deviceId)
    {
        var wrapper = GetDeviceWrapper(deviceId);
        Logger.Information("正在断开设备: {DeviceId}", deviceId);
        await wrapper.Device.DisconnectAsync();
    }

    /// <summary>
    /// 移除设备
    /// </summary>
    public async Task RemoveDeviceAsync(string deviceId)
    {
        if (_devices.TryRemove(deviceId, out var wrapper))
        {
            if (wrapper.Status == DeviceStatus.Connected)
            {
                await wrapper.Device.DisconnectAsync();
            }

            if (wrapper.Device is IDisposable disposable)
            {
                disposable.Dispose();
            }

            // 从数据库删除
            try
            {
                Database.Instance.DeleteDevice(deviceId);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "从数据库删除设备失败");
            }

            Logger.Information("设备已移除: {DeviceId}", deviceId);
        }
    }

    /// <summary>
    /// 获取设备
    /// </summary>
    public IDevice GetDevice(string deviceId)
    {
        return GetDeviceWrapper(deviceId).Device;
    }

    /// <summary>
    /// 获取设备信息
    /// </summary>
    public DeviceInfo GetDeviceInfo(string deviceId)
    {
        var wrapper = GetDeviceWrapper(deviceId);
        return new DeviceInfo
        {
            Id = wrapper.Id,
            Name = wrapper.Name,
            Type = wrapper.Type,
            Status = wrapper.Status,
            State = wrapper.Device.State,
            IsVirtual = wrapper.IsVirtual,
            ConnectionMode = wrapper.ConnectionMode,
            DGLabVersion = wrapper.DGLabVersion,
            YokonexType = wrapper.YokonexType
        };
    }

    /// <summary>
    /// 获取所有设备信息（带缓存）
    /// </summary>
    public List<DeviceInfo> GetAllDevices()
    {
        // 使用缓存减少频繁创建对象
        if (_deviceInfoCache != null && DateTime.UtcNow < _cacheExpiry)
        {
            return _deviceInfoCache;
        }
        
        _deviceInfoCache = _devices.Values.Select(w => new DeviceInfo
        {
            Id = w.Id,
            Name = w.Name,
            Type = w.Type,
            Status = w.Status,
            State = w.Device.State,
            IsVirtual = w.IsVirtual,
            ConnectionMode = w.ConnectionMode,
            DGLabVersion = w.DGLabVersion,
            YokonexType = w.YokonexType
        }).ToList();
        
        _cacheExpiry = DateTime.UtcNow.AddMilliseconds(CacheExpiryMs);
        return _deviceInfoCache;
    }
    
    /// <summary>
    /// 使缓存失效
    /// </summary>
    private void InvalidateCache()
    {
        _deviceInfoCache = null;
        _cacheExpiry = DateTime.MinValue;
    }

    /// <summary>
    /// 获取已连接的设备
    /// </summary>
    public List<DeviceInfo> GetConnectedDevices()
    {
        return _devices.Values
            .Where(w => w.Status == DeviceStatus.Connected)
            .Select(w => new DeviceInfo
            {
                Id = w.Id,
                Name = w.Name,
                Type = w.Type,
                Status = w.Status,
                State = w.Device.State,
                IsVirtual = w.IsVirtual,
                ConnectionMode = w.ConnectionMode
            }).ToList();
    }

    #endregion

    #region Device Control

    /// <summary>
    /// 设置强度
    /// </summary>
    public async Task SetStrengthAsync(string deviceId, Channel channel, int value,
        StrengthMode mode = StrengthMode.Set, string? source = null)
    {
        var device = GetDevice(deviceId);
        await device.SetStrengthAsync(channel, value, mode);

        // 记录到数据库
        try
        {
            Database.Instance.AddLog("info", "DeviceManager",
                $"SetStrength: device={deviceId}, channel={channel}, value={value}, mode={mode}",
                System.Text.Json.JsonSerializer.Serialize(new { deviceId, channel = channel.ToString(), value, mode = mode.ToString(), source }));
        }
        catch { }
    }

    /// <summary>
    /// 发送波形
    /// </summary>
    public async Task SendWaveformAsync(string deviceId, Channel channel, WaveformData data)
    {
        var device = GetDevice(deviceId);
        await device.SendWaveformAsync(channel, data);
    }

    /// <summary>
    /// 清空波形队列
    /// </summary>
    public async Task ClearWaveformQueueAsync(string deviceId, Channel channel)
    {
        var device = GetDevice(deviceId);
        await device.ClearWaveformQueueAsync(channel);
    }

    /// <summary>
    /// 设置强度上限
    /// </summary>
    public async Task SetLimitsAsync(string deviceId, int limitA, int limitB)
    {
        var device = GetDevice(deviceId);
        await device.SetLimitsAsync(limitA, limitB);
        Logger.Information("设备 {DeviceId} 限制已设置: A={LimitA}, B={LimitB}", deviceId, limitA, limitB);
    }

    /// <summary>
    /// 紧急停止所有设备
    /// </summary>
    public async Task EmergencyStopAllAsync()
    {
        Logger.Warning("紧急停止所有设备!");

        var tasks = _devices.Values
            .Where(w => w.Status == DeviceStatus.Connected)
            .SelectMany(w => new[]
            {
                w.Device.SetStrengthAsync(Channel.A, 0, StrengthMode.Set),
                w.Device.SetStrengthAsync(Channel.B, 0, StrengthMode.Set)
            });

        await Task.WhenAll(tasks);
    }

    #endregion

    #region Database Operations

    /// <summary>
    /// 从数据库加载设备
    /// </summary>
    public async Task LoadDevicesFromDatabaseAsync()
    {
        try
        {
            var records = Database.Instance.GetAllDevices();
            foreach (var record in records)
            {
                var type = record.Type == "dglab" ? DeviceType.DGLab : DeviceType.Yokonex;
                var config = string.IsNullOrEmpty(record.Config)
                    ? new ConnectionConfig()
                    : System.Text.Json.JsonSerializer.Deserialize<ConnectionConfig>(record.Config) ?? new ConnectionConfig();

                // 根据配置确定连接模式
                var mode = DetermineConnectionMode(config);
                var adapter = CreateAdapter(type, mode, record.Name);

                var wrapper = new DeviceWrapper
                {
                    Id = record.Id,
                    Name = record.Name,
                    Type = type,
                    Status = DeviceStatus.Disconnected,
                    Config = config,
                    Device = adapter,
                    IsVirtual = false,
                    ConnectionMode = mode
                };

                RegisterDeviceEvents(wrapper);
                _devices[record.Id] = wrapper;

                // 自动连接
                if (record.AutoConnect)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(1000); // 延迟启动
                            await ConnectDeviceAsync(record.Id);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning(ex, "自动连接设备 {Id} 失败", record.Id);
                        }
                    });
                }
            }

            Logger.Information("从数据库加载了 {Count} 个设备", records.Count);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "从数据库加载设备失败");
        }
    }

    private static ConnectionMode DetermineConnectionMode(ConnectionConfig config)
    {
        if (!string.IsNullOrEmpty(config.Address))
            return ConnectionMode.Bluetooth;
        if (!string.IsNullOrEmpty(config.WebSocketUrl))
            return ConnectionMode.WebSocket;
        if (!string.IsNullOrEmpty(config.UserId) || !string.IsNullOrEmpty(config.Uid))
            return ConnectionMode.TencentIM;
        return ConnectionMode.WebSocket;
    }

    #endregion

    #region Helpers

    private DeviceWrapper GetDeviceWrapper(string deviceId)
    {
        if (!_devices.TryGetValue(deviceId, out var wrapper))
        {
            throw new KeyNotFoundException($"设备未找到: {deviceId}");
        }
        return wrapper;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var wrapper in _devices.Values)
        {
            if (wrapper.Device is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
        }
        _devices.Clear();

        GC.SuppressFinalize(this);
    }

    #endregion

    private class DeviceWrapper
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public DeviceType Type { get; set; }
        public DeviceStatus Status { get; set; }
        public ConnectionConfig Config { get; set; } = new();
        public IDevice Device { get; set; } = null!;
        public bool IsVirtual { get; set; }
        public ConnectionMode ConnectionMode { get; set; }
        public DGLabVersion? DGLabVersion { get; set; }
        public YokonexDeviceType? YokonexType { get; set; }
    }
}

#region Event Args & Info Classes

/// <summary>
/// 设备信息
/// </summary>
public class DeviceInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DeviceType Type { get; set; }
    public DeviceStatus Status { get; set; }
    public DeviceState State { get; set; } = new();
    public bool IsVirtual { get; set; }
    public ConnectionMode ConnectionMode { get; set; }
    public DGLabVersion? DGLabVersion { get; set; }
    public YokonexDeviceType? YokonexType { get; set; }
}

/// <summary>
/// 设备状态变化事件参数
/// </summary>
public class DeviceStatusChangedEventArgs : EventArgs
{
    public string DeviceId { get; }
    public DeviceStatus Status { get; }

    public DeviceStatusChangedEventArgs(string deviceId, DeviceStatus status)
    {
        DeviceId = deviceId;
        Status = status;
    }
}

/// <summary>
/// 设备强度变化事件参数
/// </summary>
public class DeviceStrengthChangedEventArgs : EventArgs
{
    public string DeviceId { get; }
    public StrengthInfo Strength { get; }

    public DeviceStrengthChangedEventArgs(string deviceId, StrengthInfo strength)
    {
        DeviceId = deviceId;
        Strength = strength;
    }
}

/// <summary>
/// 设备错误事件参数
/// </summary>
public class DeviceErrorEventArgs : EventArgs
{
    public string DeviceId { get; }
    public Exception Error { get; }

    public DeviceErrorEventArgs(string deviceId, Exception error)
    {
        DeviceId = deviceId;
        Error = error;
    }
}

/// <summary>
/// QR 码变化事件参数
/// </summary>
public class QRCodeChangedEventArgs : EventArgs
{
    public string DeviceId { get; }
    public string QRCodeContent { get; }
    public string? ClientId { get; }

    public QRCodeChangedEventArgs(string deviceId, string qrCodeContent, string? clientId)
    {
        DeviceId = deviceId;
        QRCodeContent = qrCodeContent;
        ClientId = clientId;
    }
}

#endregion
