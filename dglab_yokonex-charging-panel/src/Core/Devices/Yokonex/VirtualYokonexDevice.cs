using System;
using ChargingPanel.Core.Devices.DGLab;
using ChargingPanel.Core.Protocols;
using Serilog;

namespace ChargingPanel.Core.Devices.Yokonex;

/// <summary>
/// 役次元虚拟设备（按设备类型模拟）
/// 支持：电击器 / 灌肠器 / 跳蛋 / 飞机杯。
/// </summary>
public sealed class VirtualYokonexDevice :
    IDevice,
    IYokonexEmsDevice,
    IYokonexEnemaDevice,
    IYokonexToyDevice,
    IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<VirtualYokonexDevice>();

    private readonly object _sync = new();
    private System.Timers.Timer? _simulationTimer;
    private bool _disposed;
    private int _tickCount;

    private int _strengthA;
    private int _strengthB;
    private int _limitA;
    private int _limitB;
    private int _batteryLevel = 100;

    // EMS 状态
    private YokonexMotorState _motorState = YokonexMotorState.Off;
    private PedometerState _pedometerState = PedometerState.Off;
    private bool _angleSensorEnabled = true;
    private int _stepCount;
    private (float X, float Y, float Z) _currentAngle;
    private (bool ChannelA, bool ChannelB) _channelConnectionState = (true, true);
    private int _fixedMode = 1;
    private int _customFrequency = 50;
    private int _customPulseTime = 20;

    // 灌肠状态
    private bool _isInjecting;
    private int _injectionStrength;
    private int _vibrationStrength;

    // 跳蛋/飞机杯状态
    private int _toyMode = 1;
    private (int Motor1, int Motor2, int Motor3) _motorStrengths;

    public string Id { get; }
    public string Name { get; set; }
    public DeviceType Type => DeviceType.Yokonex;
    public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;
    public DeviceState State => GetState();
    public ConnectionConfig? Config { get; private set; }
    public YokonexDeviceType YokonexType { get; }

    public event EventHandler<DeviceStatus>? StatusChanged;
    public event EventHandler<StrengthInfo>? StrengthChanged;
    public event EventHandler<int>? BatteryChanged;
    public event EventHandler<Exception>? ErrorOccurred;

    public event EventHandler<YokonexMotorState>? MotorStateChanged;
    public event EventHandler<int>? StepCountChanged;
    public event EventHandler<(float X, float Y, float Z)>? AngleChanged;
    public event EventHandler<(bool ChannelA, bool ChannelB)>? ChannelConnectionChanged;
    public event EventHandler<bool>? InjectionStateChanged;
    public event EventHandler<(int Motor1, int Motor2, int Motor3)>? MotorStrengthChanged;

    public int StepCount => _stepCount;
    public (float X, float Y, float Z) CurrentAngle => _currentAngle;
    public (bool ChannelA, bool ChannelB) ChannelConnectionState => _channelConnectionState;
    public bool IsInjecting => _isInjecting;
    public int InjectionStrength => _injectionStrength;
    public int VibrationStrength => _vibrationStrength;
    public (int Motor1, int Motor2, int Motor3) MotorStrengths => _motorStrengths;

    public VirtualYokonexDevice(YokonexDeviceType yokonexType, string? name = null, string? id = null)
    {
        YokonexType = yokonexType switch
        {
            YokonexDeviceType.Enema => YokonexDeviceType.Enema,
            YokonexDeviceType.Vibrator => YokonexDeviceType.Vibrator,
            YokonexDeviceType.Cup => YokonexDeviceType.Cup,
            _ => YokonexDeviceType.Estim
        };

        (_limitA, _limitB) = YokonexType switch
        {
            YokonexDeviceType.Enema => (100, 100),
            YokonexDeviceType.Vibrator or YokonexDeviceType.Cup => (20, 20),
            _ => (276, 276)
        };

        Id = id ?? $"virtual_yokonex_{Guid.NewGuid():N}"[..20];
        Name = name ?? YokonexType switch
        {
            YokonexDeviceType.Enema => "虚拟役次元灌肠器",
            YokonexDeviceType.Vibrator => "虚拟役次元跳蛋",
            YokonexDeviceType.Cup => "虚拟役次元飞机杯",
            _ => "虚拟役次元电击器"
        };
    }

    public Task ConnectAsync(ConnectionConfig config, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Config = config;
        UpdateStatus(DeviceStatus.Connected);
        StartSimulation();
        Logger.Information("Virtual Yokonex connected: {Id}, type={Type}", Id, YokonexType);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        StopSimulation();
        _strengthA = 0;
        _strengthB = 0;
        _motorStrengths = (0, 0, 0);
        _isInjecting = false;
        UpdateStatus(DeviceStatus.Disconnected);
        RaiseStrengthChanged();
        return Task.CompletedTask;
    }

    public Task SetStrengthAsync(Channel channel, int value, StrengthMode mode = StrengthMode.Set)
    {
        ThrowIfDisposed();
        EnsureConnected();

        lock (_sync)
        {
            switch (YokonexType)
            {
                case YokonexDeviceType.Estim:
                    ApplyToChannels(
                        channel,
                        value,
                        mode,
                        _limitA,
                        _limitB,
                        out _strengthA,
                        out _strengthB,
                        _strengthA,
                        _strengthB);
                    break;
                case YokonexDeviceType.Enema:
                    _injectionStrength = ApplyMode(_injectionStrength, value, mode, _limitA);
                    _vibrationStrength = ApplyMode(_vibrationStrength, value, mode, _limitB);
                    _isInjecting = _injectionStrength > 0;
                    _strengthA = _injectionStrength;
                    _strengthB = _vibrationStrength;
                    InjectionStateChanged?.Invoke(this, _isInjecting);
                    break;
                case YokonexDeviceType.Vibrator:
                case YokonexDeviceType.Cup:
                    var mapped = Math.Clamp((int)Math.Round(value * 0.2), 0, 20);
                    var m1 = _motorStrengths.Motor1;
                    var m2 = _motorStrengths.Motor2;
                    var m3 = _motorStrengths.Motor3;
                    switch (channel)
                    {
                        case Channel.A:
                            m1 = ApplyMode(m1, mapped, mode, _limitA);
                            break;
                        case Channel.B:
                            m2 = ApplyMode(m2, mapped, mode, _limitB);
                            break;
                        default:
                            m1 = ApplyMode(m1, mapped, mode, _limitA);
                            m2 = ApplyMode(m2, mapped, mode, _limitB);
                            m3 = ApplyMode(m3, mapped, mode, _limitA);
                            break;
                    }

                    _motorStrengths = (m1, m2, m3);
                    _strengthA = m1;
                    _strengthB = m2;
                    MotorStrengthChanged?.Invoke(this, _motorStrengths);
                    break;
            }
        }

        RaiseStrengthChanged();
        return Task.CompletedTask;
    }

    public Task SendWaveformAsync(Channel channel, WaveformData waveform)
    {
        ThrowIfDisposed();
        EnsureConnected();

        if (YokonexType == YokonexDeviceType.Estim)
        {
            // 模拟：波形触发时保持当前强度并记录频率参数，便于联调规则链路。
            if (WaveformPresetExchangeService.TryParseFrequencyPulse(waveform.HexData, out var freq, out var pulse))
            {
                _customFrequency = freq;
                _customPulseTime = pulse;
            }
            else
            {
                _customFrequency = Math.Clamp(waveform.Frequency, 1, 100);
                _customPulseTime = Math.Clamp(waveform.Duration, 1, 100);
            }
        }

        return Task.CompletedTask;
    }

    public Task ClearWaveformQueueAsync(Channel channel)
    {
        ThrowIfDisposed();
        EnsureConnected();
        return Task.CompletedTask;
    }

    public Task SetLimitsAsync(int limitA, int limitB)
    {
        ThrowIfDisposed();
        var max = YokonexType switch
        {
            YokonexDeviceType.Vibrator or YokonexDeviceType.Cup => 20,
            YokonexDeviceType.Estim => 276,
            _ => 100
        };

        _limitA = Math.Clamp(limitA, 0, max);
        _limitB = Math.Clamp(limitB, 0, max);
        return Task.CompletedTask;
    }

    public Task SetMotorStateAsync(YokonexMotorState state)
    {
        _motorState = state;
        MotorStateChanged?.Invoke(this, state);
        return Task.CompletedTask;
    }

    public Task SetPedometerStateAsync(PedometerState state)
    {
        _pedometerState = state;
        return Task.CompletedTask;
    }

    public Task SetAngleSensorEnabledAsync(bool enabled)
    {
        _angleSensorEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task SetCustomWaveformAsync(Channel channel, int frequency, int pulseTime)
    {
        _customFrequency = Math.Clamp(frequency, 1, 100);
        _customPulseTime = Math.Clamp(pulseTime, 0, 100);
        return Task.CompletedTask;
    }

    public Task SetFixedModeAsync(Channel channel, int mode)
    {
        _fixedMode = Math.Clamp(mode, 1, 16);
        return Task.CompletedTask;
    }

    public Task SetInjectionStrengthAsync(int strength)
    {
        _injectionStrength = Math.Clamp(strength, 0, _limitA);
        _strengthA = _injectionStrength;
        RaiseStrengthChanged();
        return Task.CompletedTask;
    }

    public Task StartInjectionAsync()
    {
        _isInjecting = _injectionStrength > 0;
        InjectionStateChanged?.Invoke(this, _isInjecting);
        return Task.CompletedTask;
    }

    public Task StopInjectionAsync()
    {
        _isInjecting = false;
        _injectionStrength = 0;
        _strengthA = 0;
        InjectionStateChanged?.Invoke(this, _isInjecting);
        RaiseStrengthChanged();
        return Task.CompletedTask;
    }

    public Task SetVibrationStrengthAsync(int strength)
    {
        _vibrationStrength = Math.Clamp(strength, 0, _limitB);
        _strengthB = _vibrationStrength;
        RaiseStrengthChanged();
        return Task.CompletedTask;
    }

    public Task QueryDeviceInfoAsync()
    {
        return Task.CompletedTask;
    }

    public Task SetMotorStrengthAsync(int motor, int strength)
    {
        var safeStrength = Math.Clamp(strength, 0, 20);
        var current = _motorStrengths;
        _motorStrengths = motor switch
        {
            1 => (safeStrength, current.Motor2, current.Motor3),
            2 => (current.Motor1, safeStrength, current.Motor3),
            3 => (current.Motor1, current.Motor2, safeStrength),
            _ => current
        };

        _strengthA = _motorStrengths.Motor1;
        _strengthB = _motorStrengths.Motor2;
        MotorStrengthChanged?.Invoke(this, _motorStrengths);
        RaiseStrengthChanged();
        return Task.CompletedTask;
    }

    public Task SetAllMotorsAsync(int strength1, int strength2, int strength3)
    {
        _motorStrengths = (
            Math.Clamp(strength1, 0, 20),
            Math.Clamp(strength2, 0, 20),
            Math.Clamp(strength3, 0, 20));

        _strengthA = _motorStrengths.Motor1;
        _strengthB = _motorStrengths.Motor2;
        MotorStrengthChanged?.Invoke(this, _motorStrengths);
        RaiseStrengthChanged();
        return Task.CompletedTask;
    }

    public Task SetFixedModeAsync(int mode)
    {
        _toyMode = Math.Clamp(mode, 1, 8);
        return Task.CompletedTask;
    }

    public Task StopAllMotorsAsync()
    {
        _motorStrengths = (0, 0, 0);
        _strengthA = 0;
        _strengthB = 0;
        MotorStrengthChanged?.Invoke(this, _motorStrengths);
        RaiseStrengthChanged();
        return Task.CompletedTask;
    }

    private void StartSimulation()
    {
        StopSimulation();
        _simulationTimer = new System.Timers.Timer(1000);
        _simulationTimer.Elapsed += OnSimulationTick;
        _simulationTimer.AutoReset = true;
        _simulationTimer.Start();
    }

    private void StopSimulation()
    {
        if (_simulationTimer == null)
        {
            return;
        }

        _simulationTimer.Stop();
        _simulationTimer.Elapsed -= OnSimulationTick;
        _simulationTimer.Dispose();
        _simulationTimer = null;
    }

    private void OnSimulationTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (Status != DeviceStatus.Connected)
        {
            return;
        }

        _tickCount++;
        if (_tickCount % 30 == 0 && _batteryLevel > 1)
        {
            _batteryLevel--;
            BatteryChanged?.Invoke(this, _batteryLevel);
        }

        if (YokonexType == YokonexDeviceType.Estim)
        {
            if (_pedometerState == PedometerState.On)
            {
                _stepCount += Random.Shared.Next(0, 2);
                StepCountChanged?.Invoke(this, _stepCount);
            }

            if (_angleSensorEnabled)
            {
                _currentAngle = (
                    Random.Shared.NextSingle() * 30f - 15f,
                    Random.Shared.NextSingle() * 30f - 15f,
                    Random.Shared.NextSingle() * 20f - 10f);
                AngleChanged?.Invoke(this, _currentAngle);
            }

            // 每 20 秒模拟一次通道抖动后恢复。
            if (_tickCount % 20 == 0)
            {
                _channelConnectionState = (_channelConnectionState.ChannelA, !_channelConnectionState.ChannelB);
                ChannelConnectionChanged?.Invoke(this, _channelConnectionState);
            }
            else if (_tickCount % 20 == 2)
            {
                _channelConnectionState = (true, true);
                ChannelConnectionChanged?.Invoke(this, _channelConnectionState);
            }
        }
    }

    private static void ApplyToChannels(
        Channel channel,
        int value,
        StrengthMode mode,
        int limitA,
        int limitB,
        out int outA,
        out int outB,
        int currentA = 0,
        int currentB = 0)
    {
        outA = currentA;
        outB = currentB;
        var safe = Math.Clamp(value, 0, 1000);

        if (channel is Channel.A or Channel.AB)
        {
            outA = ApplyMode(currentA, safe, mode, limitA);
        }

        if (channel is Channel.B or Channel.AB)
        {
            outB = ApplyMode(currentB, safe, mode, limitB);
        }
    }

    private static int ApplyMode(int current, int value, StrengthMode mode, int limit)
    {
        var safe = Math.Clamp(value, 0, Math.Max(limit, value));
        return mode switch
        {
            StrengthMode.Increase => Math.Min(current + safe, limit),
            StrengthMode.Decrease => Math.Max(current - safe, 0),
            _ => Math.Min(safe, limit)
        };
    }

    private void RaiseStrengthChanged()
    {
        StrengthChanged?.Invoke(this, new StrengthInfo
        {
            ChannelA = _strengthA,
            ChannelB = _strengthB,
            LimitA = _limitA,
            LimitB = _limitB
        });
    }

    private void EnsureConnected()
    {
        if (Status != DeviceStatus.Connected)
        {
            throw new InvalidOperationException("Virtual Yokonex device is not connected.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VirtualYokonexDevice));
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

    private void UpdateStatus(DeviceStatus status)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopSimulation();
    }
}
