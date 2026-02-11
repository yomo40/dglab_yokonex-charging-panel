using System;
using System.Threading;
using System.Threading.Tasks;
using ChargingPanel.Core.Devices.DGLab;
using Serilog;

namespace ChargingPanel.Core.Devices.Yokonex;

/// <summary>
/// 役次元智能锁预留适配器
/// 当前产品尚未上市，先提供统一接口和连接生命周期占位。
/// </summary>
public sealed class YokonexSmartLockPlaceholderAdapter : IDevice, IYokonexSmartLockDevice
{
    private static readonly ILogger Logger = Log.ForContext<YokonexSmartLockPlaceholderAdapter>();

    private YokonexSmartLockState _state = new();
    private ConnectionConfig? _config;

    public string Id { get; }
    public string Name { get; set; }
    public DeviceType Type => DeviceType.Yokonex;
    public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;
    public DeviceState State => new()
    {
        Status = Status,
        Strength = new StrengthInfo { ChannelA = 0, ChannelB = 0, LimitA = 0, LimitB = 0 },
        BatteryLevel = _state.BatteryLevel,
        LastUpdate = DateTime.UtcNow
    };
    public ConnectionConfig? Config => _config;
    public YokonexDeviceType YokonexType => YokonexDeviceType.SmartLock;

#pragma warning disable CS0067 // 部分事件为接口保留，目前占位适配器未触发
    public event EventHandler<DeviceStatus>? StatusChanged;
    public event EventHandler<StrengthInfo>? StrengthChanged;
    public event EventHandler<int>? BatteryChanged;
    public event EventHandler<Exception>? ErrorOccurred;
    public event EventHandler<YokonexSmartLockState>? StateChanged;
#pragma warning restore CS0067

    public YokonexSmartLockPlaceholderAdapter(string? id = null, string? name = null)
    {
        Id = id ?? $"yc_lock_{Guid.NewGuid():N}"[..20];
        Name = name ?? "役次元智能锁（预留）";
    }

    public Task ConnectAsync(ConnectionConfig config, CancellationToken cancellationToken = default)
    {
        _config = config;
        UpdateStatus(DeviceStatus.Connected);
        Logger.Warning("智能锁为预留接口，当前连接为占位模式: {Id}", Id);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        UpdateStatus(DeviceStatus.Disconnected);
        return Task.CompletedTask;
    }

    public Task SetStrengthAsync(Channel channel, int value, StrengthMode mode = StrengthMode.Set)
    {
        // 智能锁无需强度控制，保持 IDevice 接口兼容。
        return Task.CompletedTask;
    }

    public Task SendWaveformAsync(Channel channel, WaveformData waveform)
    {
        // 智能锁无需波形控制，保持 IDevice 接口兼容。
        return Task.CompletedTask;
    }

    public Task ClearWaveformQueueAsync(Channel channel)
    {
        return Task.CompletedTask;
    }

    public Task SetLimitsAsync(int limitA, int limitB)
    {
        return Task.CompletedTask;
    }

    public Task<YokonexSmartLockState> QueryStateAsync()
    {
        return Task.FromResult(_state);
    }

    public Task LockAsync()
    {
        _state = _state with { IsLocked = true, LastUpdate = DateTime.UtcNow };
        StateChanged?.Invoke(this, _state);
        return Task.CompletedTask;
    }

    public Task UnlockAsync()
    {
        _state = _state with { IsLocked = false, LastUpdate = DateTime.UtcNow };
        StateChanged?.Invoke(this, _state);
        return Task.CompletedTask;
    }

    public Task TemporaryUnlockAsync(int seconds)
    {
        var safeSeconds = Math.Clamp(seconds, 1, 3600);
        _state = _state with { IsLocked = false, LastUpdate = DateTime.UtcNow };
        StateChanged?.Invoke(this, _state);
        Logger.Information("智能锁临时解锁（预留）: {Seconds}s", safeSeconds);
        return Task.CompletedTask;
    }

    private void UpdateStatus(DeviceStatus status)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}
