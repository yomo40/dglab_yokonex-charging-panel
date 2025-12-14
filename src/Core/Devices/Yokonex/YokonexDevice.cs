using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ChargingPanel.Core.Devices.Yokonex;

/// <summary>
/// Yokonex (役次元) 设备适配器
/// TODO: 需要实现腾讯 IM SDK 集成和蓝牙协议
/// </summary>
public class YokonexDevice : IDevice, IDisposable
{
    public string Id { get; }
    public string Name { get; set; }
    public DeviceType Type => DeviceType.Yokonex;
    public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;
    public DeviceState State => GetState();
    public ConnectionConfig? Config { get; private set; }

    public event EventHandler<DeviceStatus>? StatusChanged;
    public event EventHandler<StrengthInfo>? StrengthChanged;
    public event EventHandler<int>? BatteryChanged;
    public event EventHandler<Exception>? ErrorOccurred;

    private readonly ILogger _logger = Log.ForContext<YokonexDevice>();
    
    private int _strengthA = 0;
    private int _strengthB = 0;
    private int _limitA = 276; // Yokonex 最大 276 级
    private int _limitB = 276;

    public YokonexDevice(string? id = null, string? name = null)
    {
        Id = id ?? $"yokonex_{Guid.NewGuid():N}".Substring(0, 20);
        Name = name ?? "役次元设备";
    }

    public Task ConnectAsync(ConnectionConfig config, CancellationToken cancellationToken = default)
    {
        Config = config;
        
        // TODO: 实现腾讯 IM SDK 连接或蓝牙连接
        _logger.Warning("Yokonex device connection not yet implemented");
        
        UpdateStatus(DeviceStatus.Error);
        throw new NotImplementedException("Yokonex adapter is not yet implemented. Please use DG-LAB or Virtual device for now.");
    }

    public Task DisconnectAsync()
    {
        UpdateStatus(DeviceStatus.Disconnected);
        return Task.CompletedTask;
    }

    public Task SetStrengthAsync(Channel channel, int value, StrengthMode mode = StrengthMode.Set)
    {
        throw new NotImplementedException("Yokonex adapter is not yet implemented");
    }

    public Task SendWaveformAsync(Channel channel, DGLab.WaveformData data)
    {
        throw new NotImplementedException("Yokonex adapter is not yet implemented");
    }

    public Task ClearWaveformQueueAsync(Channel channel)
    {
        throw new NotImplementedException("Yokonex adapter is not yet implemented");
    }

    public Task SetLimitsAsync(int limitA, int limitB)
    {
        _limitA = Math.Clamp(limitA, 0, 276);
        _limitB = Math.Clamp(limitB, 0, 276);
        return Task.CompletedTask;
    }

    private void UpdateStatus(DeviceStatus status)
    {
        if (Status != status)
        {
            Status = status;
            StatusChanged?.Invoke(this, status);
        }
    }

    private DeviceState GetState()
    {
        return new DeviceState
        {
            Status = Status,
            Strength = new StrengthInfo
            {
                ChannelA = _strengthA,
                ChannelB = _strengthB,
                LimitA = _limitA,
                LimitB = _limitB
            },
            LastUpdate = DateTime.UtcNow
        };
    }

    public void Dispose()
    {
        // Cleanup
    }
}
