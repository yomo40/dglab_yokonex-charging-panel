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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceOperationLocks = new();
    private readonly ConnectionDiagnostics _diagnostics = new();
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
        DGLabVersion? dglabVersion = null, YokonexDeviceType? yokonexType = null,
        YokonexProtocolGeneration? yokonexProtocolGeneration = null, string? id = null)
    {
        return type switch
        {
            DeviceType.DGLab => CreateDGLabAdapter(mode, dglabVersion, name, id),
            DeviceType.Yokonex => CreateYokonexAdapter(mode, yokonexType, yokonexProtocolGeneration, name, id),
            DeviceType.Virtual => new VirtualDevice(type, name, id),
            _ => throw new ArgumentException($"未知设备类型: {type}")
        };
    }

    private IDevice CreateDGLabAdapter(ConnectionMode mode, DGLabVersion? version, string? name, string? id)
    {
        var v = version ?? DGLabVersion.V3;
        var deviceName = name ?? $"郊狼 {v}";
        if (v == DGLabVersion.V3WirelessSensor)
        {
            return new DGLabAccessoryPlaceholderAdapter(DGLabAccessoryType.WirelessSensor47L120100, id: id, name: deviceName);
        }
        if (v == DGLabVersion.PawPrints)
        {
            return new DGLabAccessoryPlaceholderAdapter(DGLabAccessoryType.PawPrintsExternalVoltage, id: id, name: deviceName);
        }

        return mode switch
        {
            ConnectionMode.WebSocket => new DGLabWebSocketAdapter(id: id, name: deviceName),
            ConnectionMode.Bluetooth => CreateDGLabBluetoothAdapter(v, deviceName, id),
            _ => new DGLabWebSocketAdapter(id: id, name: deviceName)
        };
    }

    private IDevice CreateDGLabBluetoothAdapter(DGLabVersion version, string name, string? id)
    {
        if (version == DGLabVersion.V3WirelessSensor)
        {
            return new DGLabAccessoryPlaceholderAdapter(DGLabAccessoryType.WirelessSensor47L120100, id: id, name: name);
        }
        if (version == DGLabVersion.PawPrints)
        {
            return new DGLabAccessoryPlaceholderAdapter(DGLabAccessoryType.PawPrintsExternalVoltage, id: id, name: name);
        }

        var adapter = new DGLabBluetoothAdapter(version, id: id, name: name);
        var transport = new WindowsBluetoothTransport();
        adapter.SetTransport(transport);
        return adapter;
    }

    private IDevice CreateYokonexAdapter(ConnectionMode mode, YokonexDeviceType? deviceType,
        YokonexProtocolGeneration? protocolGeneration, string? name, string? id)
    {
        var type = deviceType ?? YokonexDeviceType.Estim;
        var deviceName = name ?? $"役次元 {GetYokonexTypeName(type)}";
        if (type == YokonexDeviceType.SmartLock)
        {
            return new YokonexSmartLockPlaceholderAdapter(id: id, name: deviceName);
        }

        return mode switch
        {
            ConnectionMode.Bluetooth => CreateYokonexBluetoothAdapter(type, protocolGeneration, deviceName, id),
            ConnectionMode.TencentIM => new YokonexIMAdapter(type, id: id, name: deviceName),
            ConnectionMode.WebSocket => new YokonexWebSocketPlaceholderAdapter(type, id: id, name: deviceName),
            _ => new YokonexIMAdapter(type, id: id, name: deviceName)
        };
    }

    private IDevice CreateYokonexBluetoothAdapter(
        YokonexDeviceType type,
        YokonexProtocolGeneration? protocolGeneration,
        string name,
        string? id)
    {
        var transport = new WindowsBluetoothTransport();
        return type switch
        {
            YokonexDeviceType.Estim => new YokonexEmsBluetoothAdapter(
                transport,
                generation: protocolGeneration ?? YokonexProtocolGeneration.EmsV1_6,
                id: id,
                name: name),
            YokonexDeviceType.Enema => new YokonexEnemaBluetoothAdapter(
                transport,
                generation: protocolGeneration ?? YokonexProtocolGeneration.EnemaV1_0,
                id: id,
                name: name),
            YokonexDeviceType.Vibrator or YokonexDeviceType.Cup => new YokonexToyBluetoothAdapter(
                transport,
                deviceType: type,
                generation: protocolGeneration ?? YokonexProtocolGeneration.ToyV1_1,
                id: id,
                name: name),
            YokonexDeviceType.SmartLock => new YokonexSmartLockPlaceholderAdapter(id: id, name: name),
            _ => new YokonexEmsBluetoothAdapter(
                transport,
                generation: protocolGeneration ?? YokonexProtocolGeneration.EmsV1_6,
                id: id,
                name: name)
        };
    }

    private static string GetYokonexTypeName(YokonexDeviceType type) => type switch
    {
        YokonexDeviceType.Estim => "电击器",
        YokonexDeviceType.Enema => "灌肠器",
        YokonexDeviceType.Vibrator => "跳蛋",
        YokonexDeviceType.Cup => "飞机杯",
        YokonexDeviceType.SmartLock => "智能锁",
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

        var requestedMode = config.ConnectionMode ?? mode;
        var effectiveMode = NormalizeConnectionMode(requestedMode);
        var normalizedInputConfig = NormalizeConnectionConfig(config, type, effectiveMode);
        DGLabVersion? effectiveDglabVersion = type == DeviceType.DGLab
            ? dglabVersion ?? normalizedInputConfig.DGLabVersion ?? DGLabVersion.V3
            : null;
        YokonexDeviceType? effectiveYokonexType = type == DeviceType.Yokonex
            ? yokonexType ?? normalizedInputConfig.YokonexType ?? YokonexDeviceType.Estim
            : null;
        YokonexProtocolGeneration? effectiveYokonexProtocolGeneration = type == DeviceType.Yokonex
            ? ResolveYokonexProtocolGeneration(
                normalizedInputConfig.YokonexProtocolGeneration,
                effectiveMode,
                effectiveYokonexType ?? YokonexDeviceType.Estim)
            : null;

        var normalizedConfig = normalizedInputConfig with
        {
            ConnectionMode = effectiveMode,
            DGLabVersion = effectiveDglabVersion,
            YokonexType = effectiveYokonexType,
            YokonexProtocolGeneration = effectiveYokonexProtocolGeneration
        };

        IDevice adapter;
        if (isVirtual)
        {
            adapter = type == DeviceType.Yokonex
                ? new VirtualYokonexDevice(effectiveYokonexType ?? YokonexDeviceType.Estim, deviceName)
                : new VirtualDevice(type, deviceName);
        }
        else
        {
            adapter = CreateAdapter(
                type,
                effectiveMode,
                deviceName,
                effectiveDglabVersion,
                effectiveYokonexType,
                effectiveYokonexProtocolGeneration);
        }

        var id = adapter.Id;

        var wrapper = new DeviceWrapper
        {
            Id = id,
            Name = deviceName,
            Type = type,
            Status = DeviceStatus.Disconnected,
            Config = normalizedConfig,
            Device = adapter,
            IsVirtual = isVirtual,
            ConnectionMode = effectiveMode,
            DGLabVersion = effectiveDglabVersion,
            YokonexType = effectiveYokonexType,
            YokonexProtocolGeneration = effectiveYokonexProtocolGeneration
        };

        // 注册事件
        RegisterDeviceEvents(wrapper);

        _devices[id] = wrapper;
        Interlocked.Increment(ref _deviceCounter);
        InvalidateCache();
        Logger.Information("设备已添加: {Id} ({Name}), 模式={Mode}", id, deviceName, effectiveMode);

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
            if (wrapper.Status == status)
            {
                return;
            }

            wrapper.Status = status;
            InvalidateCache();
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
        var deviceLock = GetDeviceOperationLock(deviceId);

        await deviceLock.WaitAsync(ct);
        try
        {
            if (wrapper.Status == DeviceStatus.Connected)
            {
                Logger.Information("设备已连接，跳过重复连接: {DeviceId}", deviceId);
                return;
            }

            ValidateConnectionConfig(wrapper);

            var maxAttempts = wrapper.Config.AutoReconnect ? 3 : 1;
            var retryDelayMs = Math.Clamp(wrapper.Config.ReconnectInterval, 500, 15000);
            Exception? lastException = null;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    Logger.Information("正在连接设备: {DeviceId}, attempt={Attempt}/{Max}", deviceId, attempt, maxAttempts);
                    await wrapper.Device.ConnectAsync(wrapper.Config, ct);
                    Logger.Information("设备连接成功: {DeviceId}", deviceId);
                    return;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    Logger.Warning(ex, "设备连接失败: {DeviceId}, attempt={Attempt}/{Max}", deviceId, attempt, maxAttempts);

                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(retryDelayMs, ct);
                    }
                }
            }

            throw new InvalidOperationException($"设备连接失败: {deviceId}", lastException);
        }
        finally
        {
            deviceLock.Release();
        }
    }

    /// <summary>
    /// 断开设备连接
    /// </summary>
    public async Task DisconnectDeviceAsync(string deviceId)
    {
        var wrapper = GetDeviceWrapper(deviceId);
        var deviceLock = GetDeviceOperationLock(deviceId);
        await deviceLock.WaitAsync();
        try
        {
            Logger.Information("正在断开设备: {DeviceId}", deviceId);
            await wrapper.Device.DisconnectAsync();
        }
        finally
        {
            deviceLock.Release();
        }
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
            InvalidateCache();

            _deviceOperationLocks.TryRemove(deviceId, out _);
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
        var state = CaptureDeviceState(wrapper.Device, wrapper.Id);
        return new DeviceInfo
        {
            Id = wrapper.Id,
            Name = wrapper.Name,
            Type = wrapper.Type,
            Status = wrapper.Status,
            State = state,
            IsVirtual = wrapper.IsVirtual,
            ConnectionMode = wrapper.ConnectionMode,
            DGLabVersion = wrapper.DGLabVersion,
            YokonexType = wrapper.YokonexType,
            YokonexProtocolGeneration = wrapper.YokonexProtocolGeneration
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
            State = CaptureDeviceState(w.Device, w.Id),
            IsVirtual = w.IsVirtual,
            ConnectionMode = w.ConnectionMode,
            DGLabVersion = w.DGLabVersion,
            YokonexType = w.YokonexType,
            YokonexProtocolGeneration = w.YokonexProtocolGeneration
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
                State = CaptureDeviceState(w.Device, w.Id),
                IsVirtual = w.IsVirtual,
                ConnectionMode = w.ConnectionMode,
                DGLabVersion = w.DGLabVersion,
                YokonexType = w.YokonexType,
                YokonexProtocolGeneration = w.YokonexProtocolGeneration
            }).ToList();
    }

    #endregion

    #region Device Control

    /// <summary>
    /// 记录设备动作日志（统一动作历史入口）
    /// </summary>
    public void RecordDeviceAction(string deviceId, string action, object? data = null, string? source = null)
    {
        try
        {
            var deviceName = _devices.TryGetValue(deviceId, out var wrapper) ? wrapper.Name : "unknown";
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                deviceId,
                deviceName,
                action,
                source,
                data,
                at = DateTime.UtcNow.ToString("o")
            });

            Database.Instance.AddLog(
                "info",
                "DeviceAction",
                $"{action}: device={deviceName}({deviceId})",
                payload);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "记录设备动作日志失败: {DeviceId} {Action}", deviceId, action);
        }
    }

    /// <summary>
    /// 设置强度
    /// </summary>
    public async Task SetStrengthAsync(string deviceId, Channel channel, int value,
        StrengthMode mode = StrengthMode.Set, string? source = null)
    {
        var device = GetDevice(deviceId);
        await device.SetStrengthAsync(channel, value, mode);

        RecordDeviceAction(
            deviceId,
            "设置强度",
            new { channel = channel.ToString(), value, mode = mode.ToString() },
            source ?? "DeviceManager");
    }

    /// <summary>
    /// 发送波形
    /// </summary>
    public async Task SendWaveformAsync(string deviceId, Channel channel, WaveformData data)
    {
        var device = GetDevice(deviceId);
        await device.SendWaveformAsync(channel, data);
        RecordDeviceAction(
            deviceId,
            "发送波形",
            new
            {
                channel = channel.ToString(),
                frequency = data.Frequency,
                strength = data.Strength,
                duration = data.Duration,
                hex = data.HexData
            },
            "DeviceManager");
    }

    /// <summary>
    /// 清空波形队列
    /// </summary>
    public async Task ClearWaveformQueueAsync(string deviceId, Channel channel)
    {
        var device = GetDevice(deviceId);
        await device.ClearWaveformQueueAsync(channel);
        RecordDeviceAction(
            deviceId,
            "清空波形队列",
            new { channel = channel.ToString() },
            "DeviceManager");
    }

    /// <summary>
    /// 设置强度上限
    /// </summary>
    public async Task SetLimitsAsync(string deviceId, int limitA, int limitB)
    {
        var device = GetDevice(deviceId);
        await device.SetLimitsAsync(limitA, limitB);
        Logger.Information("设备 {DeviceId} 限制已设置: A={LimitA}, B={LimitB}", deviceId, limitA, limitB);
        RecordDeviceAction(
            deviceId,
            "设置上限",
            new { limitA, limitB },
            "DeviceManager");
    }

    /// <summary>
    /// 紧急停止所有设备
    /// </summary>
    public async Task EmergencyStopAllAsync(int repeatCount = 5, int intervalMs = 80)
    {
        var connectedWrappers = _devices.Values
            .Where(w => w.Status == DeviceStatus.Connected)
            .ToList();
        if (connectedWrappers.Count == 0)
        {
            return;
        }

        var safeRepeatCount = Math.Clamp(repeatCount, 1, 10);
        var safeIntervalMs = Math.Clamp(intervalMs, 0, 1000);

        Logger.Warning("紧急停止所有设备: count={Count}, repeat={Repeat}", connectedWrappers.Count, safeRepeatCount);

        for (var attempt = 0; attempt < safeRepeatCount; attempt++)
        {
            var tasks = connectedWrappers
                .SelectMany(w => new[]
                {
                    TrySetStrengthZeroAsync(w.Id, w.Device, Channel.A),
                    TrySetStrengthZeroAsync(w.Id, w.Device, Channel.B)
                });
            await Task.WhenAll(tasks);

            if (safeIntervalMs > 0 && attempt < safeRepeatCount - 1)
            {
                await Task.Delay(safeIntervalMs);
            }
        }

        foreach (var wrapper in connectedWrappers)
        {
            RecordDeviceAction(
                wrapper.Id,
                "紧急停止归零",
                new { repeat = safeRepeatCount },
                "EmergencyStop");
        }
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

                // 优先使用显式元数据，兼容老记录时再回退推断。
                var mode = DetermineConnectionMode(config, type);
                config = NormalizeConnectionConfig(config, type, mode);
                DGLabVersion? dglabVersion = type == DeviceType.DGLab
                    ? config.DGLabVersion ?? DGLabVersion.V3
                    : null;
                YokonexDeviceType? yokonexType = type == DeviceType.Yokonex
                    ? config.YokonexType ?? YokonexDeviceType.Estim
                    : null;
                YokonexProtocolGeneration? yokonexProtocolGeneration = type == DeviceType.Yokonex
                    ? ResolveYokonexProtocolGeneration(
                        config.YokonexProtocolGeneration,
                        mode,
                        yokonexType ?? YokonexDeviceType.Estim)
                    : null;
                var normalizedConfig = config with
                {
                    ConnectionMode = mode,
                    DGLabVersion = dglabVersion,
                    YokonexType = yokonexType,
                    YokonexProtocolGeneration = yokonexProtocolGeneration
                };

                var adapter = CreateAdapter(
                    type,
                    mode,
                    record.Name,
                    dglabVersion,
                    yokonexType,
                    yokonexProtocolGeneration,
                    id: record.Id);

                var wrapper = new DeviceWrapper
                {
                    Id = record.Id,
                    Name = record.Name,
                    Type = type,
                    Status = DeviceStatus.Disconnected,
                    Config = normalizedConfig,
                    Device = adapter,
                    IsVirtual = false,
                    ConnectionMode = mode,
                    DGLabVersion = dglabVersion,
                    YokonexType = yokonexType,
                    YokonexProtocolGeneration = yokonexProtocolGeneration
                };

                RegisterDeviceEvents(wrapper);
                _devices[record.Id] = wrapper;

                // 兼容迁移: 回写标准化后的连接元数据
                try
                {
                    var normalizedJson = System.Text.Json.JsonSerializer.Serialize(normalizedConfig);
                    if (!string.Equals(record.Config, normalizedJson, StringComparison.Ordinal))
                    {
                        Database.Instance.UpdateDevice(record.Id, new DeviceRecord
                        {
                            Id = record.Id,
                            Name = record.Name,
                            Type = record.Type,
                            Config = normalizedJson,
                            AutoConnect = record.AutoConnect
                        });
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "设备配置迁移回写失败: {Id}", record.Id);
                }

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

            InvalidateCache();
            Logger.Information("从数据库加载了 {Count} 个设备", records.Count);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "从数据库加载设备失败");
        }
    }

    private static ConnectionMode DetermineConnectionMode(ConnectionConfig config, DeviceType? type = null)
    {
        if (config.ConnectionMode.HasValue)
            return NormalizeConnectionMode(config.ConnectionMode.Value);
        if (!string.IsNullOrWhiteSpace(config.ConnectCode))
            return ConnectionMode.TencentIM;
        if (!string.IsNullOrEmpty(config.Address))
            return ConnectionMode.Bluetooth;
        if (!string.IsNullOrEmpty(config.WebSocketUrl))
            return ConnectionMode.WebSocket;
        if (!string.IsNullOrEmpty(config.UserId) || !string.IsNullOrEmpty(config.Uid))
            return ConnectionMode.TencentIM;

        if (type == DeviceType.Yokonex)
            return ConnectionMode.TencentIM;

        return ConnectionMode.WebSocket;
    }

    private static YokonexProtocolGeneration ResolveYokonexProtocolGeneration(
        YokonexProtocolGeneration? configuredGeneration,
        ConnectionMode mode,
        YokonexDeviceType yokonexType)
    {
        if (configuredGeneration.HasValue && configuredGeneration.Value != YokonexProtocolGeneration.Auto)
            return configuredGeneration.Value;

        if (mode == ConnectionMode.TencentIM)
            return YokonexProtocolGeneration.IMEvent;

        if (mode == ConnectionMode.WebSocket)
            return YokonexProtocolGeneration.WebSocketReserved;

        return yokonexType switch
        {
            YokonexDeviceType.Estim => YokonexProtocolGeneration.EmsV1_6,
            YokonexDeviceType.Enema => YokonexProtocolGeneration.EnemaV1_0,
            YokonexDeviceType.Vibrator => YokonexProtocolGeneration.ToyV1_1,
            YokonexDeviceType.Cup => YokonexProtocolGeneration.ToyV1_1,
            YokonexDeviceType.SmartLock => YokonexProtocolGeneration.SmartLockReserved,
            _ => YokonexProtocolGeneration.EmsV1_6
        };
    }

    #endregion

    #region Helpers

    private async Task TrySetStrengthZeroAsync(string deviceId, IDevice device, Channel channel)
    {
        try
        {
            await device.SetStrengthAsync(channel, 0, StrengthMode.Set);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "紧急停止归零失败: {DeviceId}, channel={Channel}", deviceId, channel);
        }
    }

    private DeviceState CaptureDeviceState(IDevice device, string deviceId)
    {
        try
        {
            return device.State ?? new DeviceState
            {
                Status = device.Status,
                LastUpdate = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "读取设备状态失败，使用兜底状态: {DeviceId}", deviceId);
            return new DeviceState
            {
                Status = device.Status,
                LastUpdate = DateTime.UtcNow
            };
        }
    }

    private DeviceWrapper GetDeviceWrapper(string deviceId)
    {
        if (!_devices.TryGetValue(deviceId, out var wrapper))
        {
            throw new KeyNotFoundException($"设备未找到: {deviceId}");
        }
        return wrapper;
    }

    private SemaphoreSlim GetDeviceOperationLock(string deviceId)
    {
        return _deviceOperationLocks.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));
    }

    private static void ValidateConnectionConfig(DeviceWrapper wrapper)
    {
        if (wrapper.IsVirtual && wrapper.ConnectionMode == ConnectionMode.Bluetooth)
        {
            return;
        }

        switch (wrapper.ConnectionMode)
        {
            case ConnectionMode.Bluetooth:
                if (string.IsNullOrWhiteSpace(wrapper.Config.Address))
                {
                    throw new ArgumentException($"设备 {wrapper.Id} 的蓝牙地址为空");
                }
                break;

            case ConnectionMode.TencentIM:
                if (!HasValidConnectCode(wrapper.Config))
                {
                    throw new ArgumentException($"设备 {wrapper.Id} 的 IM 鉴权配置不完整（需要连接码 connect_code）");
                }
                break;

            case ConnectionMode.WebSocket:
                // WebSocket URL 允许为空，适配器内部会使用默认地址。
                break;
        }
    }

    private static ConnectionConfig NormalizeConnectionConfig(ConnectionConfig config, DeviceType type, ConnectionMode mode)
    {
        if (type != DeviceType.Yokonex || mode != ConnectionMode.TencentIM)
        {
            return config;
        }

        var normalizedConnectCode = NormalizeConnectCode(config.ConnectCode);
        if (string.IsNullOrWhiteSpace(normalizedConnectCode) && TryBuildConnectCodeFromLegacyFields(config, out var fallbackConnectCode))
        {
            normalizedConnectCode = NormalizeConnectCode(fallbackConnectCode);
            Logger.Warning("检测到旧版 IM 配置（uid/token），已自动迁移为连接码 connect_code。");
        }

        if (!string.Equals(normalizedConnectCode, config.ConnectCode, StringComparison.Ordinal))
        {
            return config with
            {
                ConnectCode = string.IsNullOrWhiteSpace(normalizedConnectCode) ? null : normalizedConnectCode
            };
        }

        return config;
    }

    private static ConnectionMode NormalizeConnectionMode(ConnectionMode mode)
    {
        // 兼容历史配置：旧版 ApiBridge 会以枚举值 3 持久化，统一迁移为 TencentIM。
        return (int)mode == 3 ? ConnectionMode.TencentIM : mode;
    }

    private static bool HasValidConnectCode(ConnectionConfig config)
    {
        if (!string.IsNullOrWhiteSpace(NormalizeConnectCode(config.ConnectCode)))
        {
            return true;
        }

        return TryBuildConnectCodeFromLegacyFields(config, out _);
    }

    private static string NormalizeConnectCode(string? connectCode)
    {
        if (string.IsNullOrWhiteSpace(connectCode))
        {
            return string.Empty;
        }

        var segments = connectCode
            .Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", segments);
    }

    private static bool TryBuildConnectCodeFromLegacyFields(ConnectionConfig config, out string connectCode)
    {
        connectCode = string.Empty;
        var uid = config.UserId ?? config.Uid;
        var token = config.Token;
        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        connectCode = $"{uid.Trim()} {token.Trim()}";
        return true;
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

        foreach (var deviceLock in _deviceOperationLocks.Values)
        {
            deviceLock.Dispose();
        }
        _deviceOperationLocks.Clear();

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
        public YokonexProtocolGeneration? YokonexProtocolGeneration { get; set; }
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
    public YokonexProtocolGeneration? YokonexProtocolGeneration { get; set; }
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
