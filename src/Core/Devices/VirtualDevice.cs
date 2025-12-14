using System;
using System.Threading;
using System.Threading.Tasks;
using ChargingPanel.Core.Devices.DGLab;
using Serilog;

namespace ChargingPanel.Core.Devices;

/// <summary>
/// 虚拟设备（用于测试）
/// </summary>
public class VirtualDevice : IDevice
{
    public string Id { get; }
    public string Name { get; set; }
    public DeviceType Type { get; }
    public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;
    public DeviceState State => GetState();
    public ConnectionConfig? Config { get; private set; }

    public event EventHandler<DeviceStatus>? StatusChanged;
    public event EventHandler<StrengthInfo>? StrengthChanged;
    public event EventHandler<int>? BatteryChanged;
    public event EventHandler<Exception>? ErrorOccurred;

    private readonly ILogger _logger = Log.ForContext<VirtualDevice>();
    private int _strengthA = 0;
    private int _strengthB = 0;
    private int _limitA = 200;
    private int _limitB = 200;
    private int _batteryLevel = 100;

    public VirtualDevice(DeviceType type, string? name = null)
    {
        Id = $"virtual_{Guid.NewGuid():N}".Substring(0, 20);
        Type = type;
        Name = name ?? $"虚拟{(type == DeviceType.DGLab ? "郊狼" : "役次元")}";
    }

    public Task ConnectAsync(ConnectionConfig config, CancellationToken cancellationToken = default)
    {
        Config = config;
        UpdateStatus(DeviceStatus.Connected);
        _logger.Information("Virtual device {Name} connected", Name);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        UpdateStatus(DeviceStatus.Disconnected);
        _strengthA = 0;
        _strengthB = 0;
        _logger.Information("Virtual device {Name} disconnected", Name);
        return Task.CompletedTask;
    }

    public Task SetStrengthAsync(Channel channel, int value, StrengthMode mode = StrengthMode.Set)
    {
        EnsureConnected();

        var safeValue = Math.Clamp(value, 0, 200);

        switch (channel)
        {
            case Channel.A:
                _strengthA = ApplyMode(_strengthA, safeValue, mode, _limitA);
                break;
            case Channel.B:
                _strengthB = ApplyMode(_strengthB, safeValue, mode, _limitB);
                break;
        }

        _logger.Debug("Virtual device {Name}: Set strength channel={Channel}, value={Value}, mode={Mode}, resultA={A}, resultB={B}",
            Name, channel, safeValue, mode, _strengthA, _strengthB);

        StrengthChanged?.Invoke(this, new StrengthInfo
        {
            ChannelA = _strengthA,
            ChannelB = _strengthB,
            LimitA = _limitA,
            LimitB = _limitB
        });

        return Task.CompletedTask;
    }

    public Task SendWaveformAsync(Channel channel, WaveformData data)
    {
        EnsureConnected();
        _logger.Debug("Virtual device {Name}: Waveform sent to channel {Channel}, duration={Duration}ms",
            Name, channel, data.Duration);
        return Task.CompletedTask;
    }

    public Task ClearWaveformQueueAsync(Channel channel)
    {
        EnsureConnected();
        _logger.Debug("Virtual device {Name}: Waveform queue cleared for channel {Channel}", Name, channel);
        return Task.CompletedTask;
    }

    public Task SetLimitsAsync(int limitA, int limitB)
    {
        _limitA = Math.Clamp(limitA, 0, 200);
        _limitB = Math.Clamp(limitB, 0, 200);
        _logger.Debug("Virtual device {Name}: Limits set to A={LimitA}, B={LimitB}", Name, _limitA, _limitB);
        return Task.CompletedTask;
    }

    private int ApplyMode(int current, int value, StrengthMode mode, int limit)
    {
        return mode switch
        {
            StrengthMode.Increase => Math.Min(current + value, limit),
            StrengthMode.Decrease => Math.Max(current - value, 0),
            StrengthMode.Set => Math.Min(value, limit),
            _ => current
        };
    }

    private void UpdateStatus(DeviceStatus status)
    {
        if (Status != status)
        {
            Status = status;
            StatusChanged?.Invoke(this, status);
        }
    }

    private void EnsureConnected()
    {
        if (Status != DeviceStatus.Connected)
        {
            throw new InvalidOperationException("Virtual device is not connected");
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
            BatteryLevel = _batteryLevel,
            LastUpdate = DateTime.UtcNow
        };
    }
}
