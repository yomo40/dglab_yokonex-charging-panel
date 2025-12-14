using System;
using System.Threading;
using System.Threading.Tasks;
using ChargingPanel.Core.Bluetooth;
using Serilog;

namespace ChargingPanel.Core.Devices.DGLab;

/// <summary>
/// DG-LAB 蓝牙版本枚举
/// </summary>
public enum DGLabVersion
{
    /// <summary>V2 版本 (D-LAB ESTIM01)</summary>
    V2,
    /// <summary>V3 版本 (47L121000)</summary>
    V3,
    /// <summary>按钮版/传感器 (47L120100)</summary>
    Sensor
}

/// <summary>
/// DG-LAB 蓝牙适配器
/// 支持 V2、V3、按钮版
/// </summary>
public class DGLabBluetoothAdapter : IDevice, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<DGLabBluetoothAdapter>();

    public string Id { get; }
    public string Name { get; set; }
    public DeviceType Type => DeviceType.DGLab;
    public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;
    public DeviceState State => GetState();
    public ConnectionConfig? Config { get; private set; }

    /// <summary>设备版本</summary>
    public DGLabVersion Version { get; }

    public event EventHandler<DeviceStatus>? StatusChanged;
    public event EventHandler<StrengthInfo>? StrengthChanged;
    public event EventHandler<int>? BatteryChanged;
    public event EventHandler<Exception>? ErrorOccurred;

    private IBluetoothTransport? _transport;
    private CancellationTokenSource? _cts;
    private Timer? _waveformTimer;
    private readonly DGLabBluetoothProtocol _protocol = new();

    private int _strengthA;
    private int _strengthB;
    private int _limitA = 200;
    private int _limitB = 200;
    private int _batteryLevel = 100;
    private int _pendingSequenceNo;
    private bool _waitingForResponse;
    private bool _disposed;

    public DGLabBluetoothAdapter(DGLabVersion version, string? id = null, string? name = null)
    {
        Version = version;
        Id = id ?? $"dglab_bt_{Guid.NewGuid():N}"[..20];
        Name = name ?? $"郊狼 {GetVersionName()}";
    }

    private string GetVersionName() => Version switch
    {
        DGLabVersion.V2 => "V2",
        DGLabVersion.V3 => "V3",
        DGLabVersion.Sensor => "按钮版",
        _ => "未知"
    };

    /// <summary>
    /// 设置蓝牙传输层
    /// </summary>
    public void SetTransport(IBluetoothTransport transport)
    {
        _transport = transport;
        _transport.DataReceived += OnDataReceived;
        _transport.StateChanged += OnTransportStateChanged;
    }

    /// <summary>
    /// 获取设备名称前缀（用于扫描过滤）
    /// </summary>
    public static string GetDeviceNamePrefix(DGLabVersion version) => version switch
    {
        DGLabVersion.V2 => "D-LAB ESTIM01",
        DGLabVersion.V3 => "47L121000",
        DGLabVersion.Sensor => "47L120100",
        _ => ""
    };

    public async Task ConnectAsync(ConnectionConfig config, CancellationToken cancellationToken = default)
    {
        Config = config;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (_transport == null)
        {
            throw new InvalidOperationException("蓝牙传输层未设置，请先调用 SetTransport()");
        }

        if (string.IsNullOrEmpty(config.Address))
        {
            throw new ArgumentException("蓝牙地址不能为空");
        }

        UpdateStatus(DeviceStatus.Connecting);
        Logger.Information("正在连接 DG-LAB {Version} 蓝牙设备: {Address}", Version, config.Address);

        try
        {
            await _transport.ConnectAsync(config.Address, _cts.Token);

            // 订阅通知特性
            await _transport.SubscribeAsync(GetServiceUuid(), GetNotifyCharacteristicUuid());

            UpdateStatus(DeviceStatus.Connected);
            Logger.Information("DG-LAB {Version} 蓝牙连接成功", Version);

            // V3: 发送初始 BF 指令设置软上限
            if (Version == DGLabVersion.V3)
            {
                await SendBFCommandAsync();
            }

            // 启动波形定时器
            StartWaveformTimer();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "DG-LAB 蓝牙连接失败");
            UpdateStatus(DeviceStatus.Error);
            ErrorOccurred?.Invoke(this, ex);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        StopWaveformTimer();
        _cts?.Cancel();

        if (_transport != null)
        {
            await _transport.DisconnectAsync();
        }

        _strengthA = 0;
        _strengthB = 0;
        UpdateStatus(DeviceStatus.Disconnected);
        Logger.Information("DG-LAB {Version} 已断开连接", Version);
    }

    public async Task SetStrengthAsync(Channel channel, int value, StrengthMode mode = StrengthMode.Set)
    {
        EnsureConnected();

        if (Version == DGLabVersion.V3)
        {
            await SetStrengthV3Async(channel, value, mode);
        }
        else
        {
            await SetStrengthV2Async(channel, value, mode);
        }
    }

    public async Task SendWaveformAsync(Channel channel, WaveformData data)
    {
        EnsureConnected();

        if (Version == DGLabVersion.V3)
        {
            await SendWaveformV3Async(channel, data);
        }
        else
        {
            await SendWaveformV2Async(channel, data);
        }
    }

    public Task ClearWaveformQueueAsync(Channel channel)
    {
        // 蓝牙模式下不需要清空队列
        return Task.CompletedTask;
    }

    public async Task SetLimitsAsync(int limitA, int limitB)
    {
        _limitA = Math.Clamp(limitA, 0, 200);
        _limitB = Math.Clamp(limitB, 0, 200);

        if (Version == DGLabVersion.V3 && Status == DeviceStatus.Connected)
        {
            await SendBFCommandAsync();
        }
    }

    #region V3 Protocol

    private async Task SetStrengthV3Async(Channel channel, int value, StrengthMode mode)
    {
        // 等待上一个强度命令的响应
        if (_waitingForResponse)
        {
            await Task.Delay(50);
        }

        var seqNo = _protocol.GetNextSequenceNo();
        _pendingSequenceNo = seqNo;
        _waitingForResponse = true;

        var command = _protocol.BuildStrengthCommand(channel, value, mode, true);
        await WriteAsync(GetWriteCharacteristicUuid(), command);
    }

    private async Task SendWaveformV3Async(Channel channel, WaveformData data)
    {
        var freq = DGLabBluetoothProtocol.ConvertFrequency(data.Frequency);
        var waveform = new ChannelWaveform
        {
            Frequency = new[] { freq, freq, freq, freq },
            Strength = new[] { data.Strength, data.Strength, data.Strength, data.Strength }
        };

        var command = _protocol.BuildWaveformCommand(channel, waveform);
        await WriteAsync(GetWriteCharacteristicUuid(), command);
    }

    private async Task SendBFCommandAsync()
    {
        var data = new BFCommandData
        {
            LimitA = _limitA,
            LimitB = _limitB,
            FreqBalanceA = 128,
            FreqBalanceB = 128,
            StrengthBalanceA = 128,
            StrengthBalanceB = 128
        };

        var command = _protocol.BuildBFCommand(data);
        await WriteAsync(GetWriteCharacteristicUuid(), command);
        Logger.Debug("发送 BF 指令: LimitA={A}, LimitB={B}", _limitA, _limitB);
    }

    #endregion

    #region V2 Protocol

    private async Task SetStrengthV2Async(Channel channel, int value, StrengthMode mode)
    {
        // V2 协议: PWM_AB2 特性，3字节
        int targetA = _strengthA;
        int targetB = _strengthB;
        int rawValue = value * 7; // 转换为 V2 协议值

        switch (mode)
        {
            case StrengthMode.Set:
                if (channel == Channel.A || channel == Channel.AB) targetA = rawValue;
                if (channel == Channel.B || channel == Channel.AB) targetB = rawValue;
                break;
            case StrengthMode.Increase:
                if (channel == Channel.A || channel == Channel.AB) targetA = Math.Min(_strengthA * 7 + rawValue, 2047);
                if (channel == Channel.B || channel == Channel.AB) targetB = Math.Min(_strengthB * 7 + rawValue, 2047);
                break;
            case StrengthMode.Decrease:
                if (channel == Channel.A || channel == Channel.AB) targetA = Math.Max(_strengthA * 7 - rawValue, 0);
                if (channel == Channel.B || channel == Channel.AB) targetB = Math.Max(_strengthB * 7 - rawValue, 0);
                break;
        }

        // 构建 3 字节数据
        int combined = ((targetA & 0x7FF) << 11) | (targetB & 0x7FF);
        var data = new byte[3];
        data[0] = (byte)(combined & 0xFF);
        data[1] = (byte)((combined >> 8) & 0xFF);
        data[2] = (byte)((combined >> 16) & 0xFF);

        await WriteAsync(GetV2StrengthCharacteristicUuid(), data);

        _strengthA = targetA / 7;
        _strengthB = targetB / 7;

        StrengthChanged?.Invoke(this, new StrengthInfo
        {
            ChannelA = _strengthA,
            ChannelB = _strengthB,
            LimitA = _limitA,
            LimitB = _limitB
        });
    }

    private async Task SendWaveformV2Async(Channel channel, WaveformData data)
    {
        int frequency = data.Frequency;
        int x = (int)(Math.Pow(frequency / 1000.0, 0.5) * 15);
        int y = frequency - x;
        int z = Math.Clamp(data.Strength / 5, 0, 31);

        x = Math.Clamp(x, 0, 31);
        y = Math.Clamp(y, 0, 1023);

        int combined = ((z & 0x1F) << 15) | ((y & 0x3FF) << 5) | (x & 0x1F);
        var waveData = new byte[3];
        waveData[0] = (byte)(combined & 0xFF);
        waveData[1] = (byte)((combined >> 8) & 0xFF);
        waveData[2] = (byte)((combined >> 16) & 0xFF);

        if (channel == Channel.A || channel == Channel.AB)
        {
            await WriteAsync(GetV2WaveformACharacteristicUuid(), waveData);
        }
        if (channel == Channel.B || channel == Channel.AB)
        {
            await WriteAsync(GetV2WaveformBCharacteristicUuid(), waveData);
        }
    }

    #endregion

    #region Bluetooth UUIDs

    private Guid GetServiceUuid() => Version switch
    {
        DGLabVersion.V2 => Guid.Parse("955A180B-0FE2-F5AA-A094-84B8D4F3E8AD"),
        _ => Guid.Parse("0000180C-0000-1000-8000-00805F9B34FB")
    };

    private Guid GetWriteCharacteristicUuid() => Guid.Parse("0000150A-0000-1000-8000-00805F9B34FB");
    private Guid GetNotifyCharacteristicUuid() => Guid.Parse("0000150B-0000-1000-8000-00805F9B34FB");

    // V2 特性
    private Guid GetV2StrengthCharacteristicUuid() => Guid.Parse("955A1504-0FE2-F5AA-A094-84B8D4F3E8AD");
    private Guid GetV2WaveformACharacteristicUuid() => Guid.Parse("955A1505-0FE2-F5AA-A094-84B8D4F3E8AD");
    private Guid GetV2WaveformBCharacteristicUuid() => Guid.Parse("955A1506-0FE2-F5AA-A094-84B8D4F3E8AD");

    #endregion

    #region Event Handlers

    private void OnTransportStateChanged(object? sender, BleConnectionState state)
    {
        if (state == BleConnectionState.Disconnected && Status == DeviceStatus.Connected)
        {
            UpdateStatus(DeviceStatus.Disconnected);
        }
    }

    private void OnDataReceived(object? sender, BleDataReceivedEventArgs e)
    {
        if (e.Data.Length == 0) return;

        try
        {
            if (Version == DGLabVersion.V3)
            {
                ParseV3Response(e.Data);
            }
            else
            {
                ParseV2Response(e.Data);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "解析蓝牙响应失败");
        }
    }

    private void ParseV3Response(byte[] data)
    {
        if (data[0] == 0xB1)
        {
            var response = _protocol.ParseB1Response(data);
            if (response != null)
            {
                _strengthA = response.StrengthA;
                _strengthB = response.StrengthB;

                if (response.SequenceNo == _pendingSequenceNo)
                {
                    _waitingForResponse = false;
                }

                StrengthChanged?.Invoke(this, new StrengthInfo
                {
                    ChannelA = _strengthA,
                    ChannelB = _strengthB,
                    LimitA = _limitA,
                    LimitB = _limitB
                });
            }
        }
    }

    private void ParseV2Response(byte[] data)
    {
        if (data.Length == 1)
        {
            _batteryLevel = data[0];
            BatteryChanged?.Invoke(this, _batteryLevel);
        }
        else if (data.Length == 3)
        {
            int combined = data[0] | (data[1] << 8) | (data[2] << 16);
            _strengthA = ((combined >> 11) & 0x7FF) / 7;
            _strengthB = (combined & 0x7FF) / 7;

            StrengthChanged?.Invoke(this, new StrengthInfo
            {
                ChannelA = _strengthA,
                ChannelB = _strengthB,
                LimitA = _limitA,
                LimitB = _limitB
            });
        }
    }

    #endregion

    #region Helpers

    private void StartWaveformTimer()
    {
        _waveformTimer?.Dispose();
        _waveformTimer = new Timer(_ =>
        {
            // V3 需要每 100ms 发送一次 B0 指令来维持波形
        }, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }

    private void StopWaveformTimer()
    {
        _waveformTimer?.Dispose();
        _waveformTimer = null;
    }

    private void EnsureConnected()
    {
        if (Status != DeviceStatus.Connected)
        {
            throw new InvalidOperationException("设备未连接");
        }
    }

    private async Task WriteAsync(Guid characteristicUuid, byte[] data)
    {
        if (_transport == null)
        {
            throw new InvalidOperationException("蓝牙传输层未设置");
        }

        await _transport.WriteWithoutResponseAsync(GetServiceUuid(), characteristicUuid, data);
        Logger.Debug("BLE Write [{Uuid}]: {Data}", characteristicUuid, DGLabBluetoothProtocol.ToHexString(data));
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
            BatteryLevel = _batteryLevel,
            LastUpdate = DateTime.UtcNow
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopWaveformTimer();
        _cts?.Cancel();
        _cts?.Dispose();

        if (_transport != null)
        {
            _transport.StateChanged -= OnTransportStateChanged;
            _transport.DataReceived -= OnDataReceived;
            _transport.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    #endregion
}
