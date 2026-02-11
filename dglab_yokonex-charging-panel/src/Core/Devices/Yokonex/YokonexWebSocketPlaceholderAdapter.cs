using System;
using System.Threading;
using System.Threading.Tasks;
using ChargingPanel.Core.Devices.DGLab;
using Serilog;

namespace ChargingPanel.Core.Devices.Yokonex;

/// <summary>
/// 役次元 WebSocket 连接预留适配器。
/// 厂商尚未公开 WebSocket 直连协议，当前用于统一连接层占位与后续扩展。
/// </summary>
public sealed class YokonexWebSocketPlaceholderAdapter : IDevice, IYokonexDevice
{
    private static readonly ILogger Logger = Log.ForContext<YokonexWebSocketPlaceholderAdapter>();

    private ConnectionConfig? _config;
    private int _strengthA;
    private int _strengthB;
    private int _limitA = 100;
    private int _limitB = 100;

    public string Id { get; }
    public string Name { get; set; }
    public DeviceType Type => DeviceType.Yokonex;
    public YokonexDeviceType YokonexType { get; }
    public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;
    public ConnectionConfig? Config => _config;

    public DeviceState State => new()
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

    public event EventHandler<DeviceStatus>? StatusChanged;
    public event EventHandler<StrengthInfo>? StrengthChanged;
    public event EventHandler<int>? BatteryChanged;
    public event EventHandler<Exception>? ErrorOccurred;

    public YokonexWebSocketPlaceholderAdapter(
        YokonexDeviceType type = YokonexDeviceType.Estim,
        string? id = null,
        string? name = null)
    {
        YokonexType = type;
        Id = id ?? $"yc_ws_{Guid.NewGuid():N}"[..20];
        Name = name ?? $"役次元{GetTypeName(type)}(WebSocket预留)";
    }

    public Task ConnectAsync(ConnectionConfig config, CancellationToken cancellationToken = default)
    {
        _config = config;
        UpdateStatus(DeviceStatus.Connected);
        Logger.Warning("Yokonex WebSocket adapter is a reserved placeholder. Device={DeviceId}, Type={Type}", Id, YokonexType);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        UpdateStatus(DeviceStatus.Disconnected);
        return Task.CompletedTask;
    }

    public Task SetStrengthAsync(Channel channel, int value, StrengthMode mode = StrengthMode.Set)
    {
        var (nextA, nextB) = (_strengthA, _strengthB);
        switch (channel)
        {
            case Channel.A:
                nextA = ApplyMode(_strengthA, value, mode, _limitA);
                break;
            case Channel.B:
                nextB = ApplyMode(_strengthB, value, mode, _limitB);
                break;
            default:
                nextA = ApplyMode(_strengthA, value, mode, _limitA);
                nextB = ApplyMode(_strengthB, value, mode, _limitB);
                break;
        }

        _strengthA = nextA;
        _strengthB = nextB;
        RaiseStrengthChanged();
        return Task.CompletedTask;
    }

    public Task SendWaveformAsync(Channel channel, WaveformData waveform)
    {
        // 预留适配器仅保持连接与调用链路，不执行真实波形下发。
        Logger.Debug("Ignored waveform on Yokonex WebSocket placeholder: Device={DeviceId}, Channel={Channel}", Id, channel);
        return Task.CompletedTask;
    }

    public Task ClearWaveformQueueAsync(Channel channel)
    {
        return Task.CompletedTask;
    }

    public Task SetLimitsAsync(int limitA, int limitB)
    {
        _limitA = Math.Clamp(limitA, 0, 100);
        _limitB = Math.Clamp(limitB, 0, 100);
        _strengthA = Math.Min(_strengthA, _limitA);
        _strengthB = Math.Min(_strengthB, _limitB);
        RaiseStrengthChanged();
        return Task.CompletedTask;
    }

    private static int ApplyMode(int current, int value, StrengthMode mode, int limit)
    {
        var raw = mode switch
        {
            StrengthMode.Increase => current + value,
            StrengthMode.Decrease => current - value,
            _ => value
        };
        return Math.Clamp(raw, 0, Math.Max(limit, 0));
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

    private static string GetTypeName(YokonexDeviceType type) => type switch
    {
        YokonexDeviceType.Enema => "灌肠器",
        YokonexDeviceType.Vibrator => "跳蛋",
        YokonexDeviceType.Cup => "飞机杯",
        YokonexDeviceType.SmartLock => "智能锁",
        _ => "电击器"
    };
}
