using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using ChargingPanel.Core.Devices.DGLab;
using Serilog;

namespace ChargingPanel.Core.Devices;

/// <summary>
/// 虚拟设备（用于测试）
/// 模拟真实设备的所有功能，包括波形队列、电量变化等
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
    
    // 波形队列
    private readonly ConcurrentQueue<(Channel Channel, WaveformData Data)> _waveformQueueA = new();
    private readonly ConcurrentQueue<(Channel Channel, WaveformData Data)> _waveformQueueB = new();
    private System.Timers.Timer? _waveformTimer;
    private System.Timers.Timer? _batteryTimer;
    private CancellationTokenSource? _waveformCts;

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
        
        // 启动波形处理定时器
        _waveformCts = new CancellationTokenSource();
        _waveformTimer = new System.Timers.Timer(100); // 100ms 处理一次波形
        _waveformTimer.Elapsed += ProcessWaveformQueue;
        _waveformTimer.AutoReset = true;
        _waveformTimer.Start();
        
        // 启动电量模拟定时器 (每30秒减少1%电量)
        _batteryTimer = new System.Timers.Timer(30000);
        _batteryTimer.Elapsed += SimulateBatteryDrain;
        _batteryTimer.AutoReset = true;
        _batteryTimer.Start();
        
        _logger.Information("Virtual device {Name} connected", Name);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        // 停止定时器
        _waveformTimer?.Stop();
        _waveformTimer?.Dispose();
        _waveformTimer = null;
        
        _batteryTimer?.Stop();
        _batteryTimer?.Dispose();
        _batteryTimer = null;
        
        _waveformCts?.Cancel();
        _waveformCts?.Dispose();
        _waveformCts = null;
        
        // 清空波形队列
        while (_waveformQueueA.TryDequeue(out _)) { }
        while (_waveformQueueB.TryDequeue(out _)) { }
        
        UpdateStatus(DeviceStatus.Disconnected);
        _strengthA = 0;
        _strengthB = 0;
        _logger.Information("Virtual device {Name} disconnected", Name);
        return Task.CompletedTask;
    }
    
    private void ProcessWaveformQueue(object? sender, ElapsedEventArgs e)
    {
        // 处理 A 通道波形
        if (_waveformQueueA.TryPeek(out var waveA))
        {
            // 模拟波形效果：根据波形强度临时调整通道强度
            var tempStrength = Math.Min(waveA.Data.Strength, _limitA);
            if (_strengthA != tempStrength)
            {
                _strengthA = tempStrength;
                NotifyStrengthChanged();
            }
            
            // 波形持续时间结束后移除
            if (waveA.Data.Duration <= 100)
            {
                _waveformQueueA.TryDequeue(out _);
                _strengthA = 0;
                NotifyStrengthChanged();
            }
            else
            {
                // 减少剩余时间
                waveA.Data.Duration -= 100;
            }
        }
        
        // 处理 B 通道波形
        if (_waveformQueueB.TryPeek(out var waveB))
        {
            var tempStrength = Math.Min(waveB.Data.Strength, _limitB);
            if (_strengthB != tempStrength)
            {
                _strengthB = tempStrength;
                NotifyStrengthChanged();
            }
            
            if (waveB.Data.Duration <= 100)
            {
                _waveformQueueB.TryDequeue(out _);
                _strengthB = 0;
                NotifyStrengthChanged();
            }
            else
            {
                waveB.Data.Duration -= 100;
            }
        }
    }
    
    private void SimulateBatteryDrain(object? sender, ElapsedEventArgs e)
    {
        if (_batteryLevel > 0)
        {
            _batteryLevel = Math.Max(0, _batteryLevel - 1);
            BatteryChanged?.Invoke(this, _batteryLevel);
            _logger.Debug("Virtual device {Name} battery: {Level}%", Name, _batteryLevel);
        }
    }
    
    private void NotifyStrengthChanged()
    {
        StrengthChanged?.Invoke(this, new StrengthInfo
        {
            ChannelA = _strengthA,
            ChannelB = _strengthB,
            LimitA = _limitA,
            LimitB = _limitB
        });
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
            case Channel.AB:
                _strengthA = ApplyMode(_strengthA, safeValue, mode, _limitA);
                _strengthB = ApplyMode(_strengthB, safeValue, mode, _limitB);
                break;
        }

        _logger.Debug("Virtual device {Name}: Set strength channel={Channel}, value={Value}, mode={Mode}, resultA={A}, resultB={B}",
            Name, channel, safeValue, mode, _strengthA, _strengthB);

        NotifyStrengthChanged();
        return Task.CompletedTask;
    }

    public Task SendWaveformAsync(Channel channel, WaveformData data)
    {
        EnsureConnected();
        
        // 克隆波形数据以避免修改原始数据
        var waveformCopy = new WaveformData
        {
            Frequency = data.Frequency,
            Strength = data.Strength,
            Duration = data.Duration
        };
        
        // 添加到对应通道的波形队列
        switch (channel)
        {
            case Channel.A:
                _waveformQueueA.Enqueue((channel, waveformCopy));
                break;
            case Channel.B:
                _waveformQueueB.Enqueue((channel, waveformCopy));
                break;
            case Channel.AB:
                _waveformQueueA.Enqueue((Channel.A, waveformCopy));
                _waveformQueueB.Enqueue((Channel.B, new WaveformData
                {
                    Frequency = data.Frequency,
                    Strength = data.Strength,
                    Duration = data.Duration
                }));
                break;
        }
        
        _logger.Debug("Virtual device {Name}: Waveform queued to channel {Channel}, duration={Duration}ms, strength={Strength}",
            Name, channel, data.Duration, data.Strength);
        return Task.CompletedTask;
    }

    public Task ClearWaveformQueueAsync(Channel channel)
    {
        EnsureConnected();
        
        switch (channel)
        {
            case Channel.A:
                while (_waveformQueueA.TryDequeue(out _)) { }
                _strengthA = 0;
                break;
            case Channel.B:
                while (_waveformQueueB.TryDequeue(out _)) { }
                _strengthB = 0;
                break;
            case Channel.AB:
                while (_waveformQueueA.TryDequeue(out _)) { }
                while (_waveformQueueB.TryDequeue(out _)) { }
                _strengthA = 0;
                _strengthB = 0;
                break;
        }
        
        NotifyStrengthChanged();
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
