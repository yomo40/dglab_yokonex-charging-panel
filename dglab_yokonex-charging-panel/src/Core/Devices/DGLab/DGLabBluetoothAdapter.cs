using System;
using System.Linq;
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
    /// <summary>V3 无线传感器 (47L120100，预留)</summary>
    V3WirelessSensor,
    /// <summary>爪印按钮传感器（外部电压，预留）</summary>
    PawPrints
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
    private Timer? _batteryTimer;
    private Timer? _reconnectTimer;
    private readonly DGLabBluetoothProtocol _protocol = new();

    private int _strengthA;
    private int _strengthB;
    private int _limitA = 200;
    private int _limitB = 200;
    private int _batteryLevel = 100;
    private int _pendingSequenceNo;
    private bool _waitingForResponse;
    private bool _disposed;
    private int _reconnectAttempts;
    private const int MaxReconnectAttempts = 5;
    private const int ReconnectDelayMs = 3000;
    
    // 波形持续发送状态
    private ChannelWaveform? _currentWaveformA;
    private ChannelWaveform? _currentWaveformB;
    private byte[]? _currentV2WaveformA;
    private byte[]? _currentV2WaveformB;
    
    // V3 电量服务 UUID
    private static readonly Guid BatteryServiceUuid = Guid.Parse("0000180A-0000-1000-8000-00805F9B34FB");
    private static readonly Guid BatteryCharacteristicUuid = Guid.Parse("00001500-0000-1000-8000-00805F9B34FB");

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
        DGLabVersion.V3WirelessSensor => "V3 无线传感器",
        DGLabVersion.PawPrints => "爪印按钮",
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
        DGLabVersion.V3WirelessSensor => "47L120100",
        DGLabVersion.PawPrints => "PawPrints",
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

            if (Version == DGLabVersion.V3)
            {
                // V3: 订阅通知特性
                await _transport.SubscribeAsync(GetServiceUuid(), GetNotifyCharacteristicUuid());
                
                UpdateStatus(DeviceStatus.Connected);
                Logger.Information("DG-LAB V3 蓝牙连接成功");

                // 发送初始 BF 指令设置软上限
                await SendBFCommandAsync();
                // 订阅电量通知
                await SubscribeBatteryAsync();
            }
            else
            {
                // V2: 订阅强度通知 (PWM_AB2 属性是 读/写/通知)
                await _transport.SubscribeAsync(GetServiceUuid(), GetV2StrengthCharacteristicUuid());
                
                UpdateStatus(DeviceStatus.Connected);
                Logger.Information("DG-LAB V2 蓝牙连接成功");
                
                // V2: 订阅电量通知
                await SubscribeV2BatteryAsync();
            }

            // 启动波形定时器
            StartWaveformTimer();
            
            // 启动电量轮询定时器
            StartBatteryPolling();
            
            // 重置重连计数
            _reconnectAttempts = 0;
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
        StopBatteryPolling();
        StopReconnectTimer();
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
    
    /// <summary>
    /// 手动触发重连
    /// </summary>
    public async Task ReconnectAsync()
    {
        if (Config == null)
        {
            throw new InvalidOperationException("没有保存的连接配置");
        }
        
        Logger.Information("手动触发重连 DG-LAB {Version}", Version);
        _reconnectAttempts = 0;
        await TryReconnectAsync();
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
        // 清除波形状态，停止定时器持续发送
        if (channel == Channel.A || channel == Channel.AB)
        {
            _currentWaveformA = null;
            _currentV2WaveformA = null;
        }
        if (channel == Channel.B || channel == Channel.AB)
        {
            _currentWaveformB = null;
            _currentV2WaveformB = null;
        }
        
        Logger.Debug("清除波形队列: channel={Channel}", channel);
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
        ChannelWaveform waveform;
        
        // 如果有自定义 HEX 数据，解析它
        if (!string.IsNullOrEmpty(data.HexData))
        {
            waveform = ParseHexToWaveform(data.HexData) ?? CreateDefaultWaveform(data);
        }
        else
        {
            waveform = CreateDefaultWaveform(data);
        }

        // 保存当前波形状态，用于定时器持续发送
        if (channel == Channel.A || channel == Channel.AB)
        {
            _currentWaveformA = waveform;
        }
        if (channel == Channel.B || channel == Channel.AB)
        {
            _currentWaveformB = waveform;
        }

        var command = _protocol.BuildWaveformCommand(channel, waveform);
        await WriteAsync(GetWriteCharacteristicUuid(), command);
    }
    
    /// <summary>
    /// 创建默认波形
    /// </summary>
    private static ChannelWaveform CreateDefaultWaveform(WaveformData data)
    {
        var freq = DGLabBluetoothProtocol.ConvertFrequency(data.Frequency);
        return new ChannelWaveform
        {
            Frequency = new[] { freq, freq, freq, freq },
            Strength = new[] { data.Strength, data.Strength, data.Strength, data.Strength }
        };
    }
    
    /// <summary>
    /// 解析 HEX 字符串为波形数据
    /// HEX 格式: 16 字符 (8 字节) = 4 频率 + 4 强度
    /// </summary>
    private static ChannelWaveform? ParseHexToWaveform(string hexData)
    {
        // 取第一段 HEX 数据
        var firstHex = hexData.Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(firstHex) || firstHex.Length != 16)
        {
            return null;
        }
        
        try
        {
            // 解析 8 字节: 前 4 字节是频率，后 4 字节是强度
            var bytes = new byte[8];
            for (int i = 0; i < 8; i++)
            {
                bytes[i] = Convert.ToByte(firstHex.Substring(i * 2, 2), 16);
            }
            
            return new ChannelWaveform
            {
                Frequency = new int[] { bytes[0], bytes[1], bytes[2], bytes[3] },
                Strength = new int[] { bytes[4], bytes[5], bytes[6], bytes[7] }
            };
        }
        catch
        {
            return null;
        }
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
        // V2 协议: PWM_AB2 特性，3字节，小端序
        // 官方文档: 23-22bit(保留) + 21-11bit(A通道强度) + 10-0bit(B通道强度)
        int targetA = _strengthA * 7; // 当前值转为 V2 原始值 (0-2047)
        int targetB = _strengthB * 7;
        int rawValue = value * 7; // 输入值转为 V2 协议值

        switch (mode)
        {
            case StrengthMode.Set:
                if (channel == Channel.A || channel == Channel.AB) targetA = rawValue;
                if (channel == Channel.B || channel == Channel.AB) targetB = rawValue;
                break;
            case StrengthMode.Increase:
                if (channel == Channel.A || channel == Channel.AB) targetA = Math.Min(targetA + rawValue, 2047);
                if (channel == Channel.B || channel == Channel.AB) targetB = Math.Min(targetB + rawValue, 2047);
                break;
            case StrengthMode.Decrease:
                if (channel == Channel.A || channel == Channel.AB) targetA = Math.Max(targetA - rawValue, 0);
                if (channel == Channel.B || channel == Channel.AB) targetB = Math.Max(targetB - rawValue, 0);
                break;
        }

        // 构建 3 字节数据 (小端序: 低字节在前)
        // 官方文档: 21-11bit(A通道) + 10-0bit(B通道)
        // combined = (A << 11) | B
        int combined = ((targetA & 0x7FF) << 11) | (targetB & 0x7FF);
        var data = new byte[3];
        data[0] = (byte)(combined & 0xFF);          // 低字节
        data[1] = (byte)((combined >> 8) & 0xFF);   // 中字节
        data[2] = (byte)((combined >> 16) & 0xFF);  // 高字节

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
        
        Logger.Debug("V2 设置强度: A={A}, B={B}, raw=[{D0:X2},{D1:X2},{D2:X2}]", 
            _strengthA, _strengthB, data[0], data[1], data[2]);
    }

    private async Task SendWaveformV2Async(Channel channel, WaveformData data)
    {
        // V2 波形格式: PWM_x34，小端序
        // 19-15bit(Z) + 14-5bit(Y) + 4-0bit(X)
        // X: 脉冲数量 (1-31)，连续发出的脉冲数
        // Y: 间隔时间 (0-1023)，X个脉冲后的间隔毫秒数
        // Z: 脉冲宽度 (0-31), 实际宽度 = Z * 5us
        // 波形频率(周期) = X + Y (ms)
        
        // 输入的 Frequency 是脉冲频率 (Hz)，需要转换为波形频率 (ms)
        // 脉冲频率 = 1000 / 波形频率
        // 例如: 100Hz → 波形频率 = 10ms, 10Hz → 波形频率 = 100ms
        int pulseFreqHz = Math.Clamp(data.Frequency, 1, 1000);
        int waveformPeriodMs = 1000 / pulseFreqHz;  // 波形周期 (ms)
        
        // 根据官方文档示例:
        // 参数【1,9】代表每隔9ms发出1个脉冲，总共耗时10ms，脉冲频率100Hz
        // 参数【5,95】代表每隔95ms发出5个脉冲，总共耗时100ms，体感脉冲频率10Hz
        // 
        // 策略: 对于高频(>50Hz)使用 X=1，对于低频使用较大的 X 来产生"合并脉冲"效果
        int x, y;
        if (waveformPeriodMs <= 20)
        {
            // 高频 (>=50Hz): X=1, Y=周期-1
            x = 1;
            y = Math.Max(waveformPeriodMs - 1, 0);
        }
        else if (waveformPeriodMs <= 100)
        {
            // 中频 (10-50Hz): X=1-5, Y=周期-X
            x = Math.Min(waveformPeriodMs / 20, 5);
            y = waveformPeriodMs - x;
        }
        else
        {
            // 低频 (<10Hz): X=5-10, Y=周期-X，产生"合并脉冲"效果
            x = Math.Min(waveformPeriodMs / 20, 10);
            y = waveformPeriodMs - x;
        }
        
        // V2 的 Z 参数控制脉冲宽度，范围 0-31，实际宽度 = Z * 5us
        // 官方建议: Z > 20 时容易引起刺痛，所以默认使用 Z=20 (100us)
        // 将 Strength (0-100) 映射到 Z (0-20)，避免刺痛
        int z = Math.Clamp(data.Strength * 20 / 100, 0, 20);

        x = Math.Clamp(x, 1, 31);
        y = Math.Clamp(y, 0, 1023);

        // 官方文档: 19-15bit(Z) + 14-5bit(Y) + 4-0bit(X)
        // 示例: x=1,y=9,z=20 → combined=0x0A0121 → bytes=[0x21,0x01,0x0A]
        int combined = ((z & 0x1F) << 15) | ((y & 0x3FF) << 5) | (x & 0x1F);
        var waveData = new byte[3];
        // 小端序: 低字节在前
        waveData[0] = (byte)(combined & 0xFF);          // 低字节
        waveData[1] = (byte)((combined >> 8) & 0xFF);   // 中字节
        waveData[2] = (byte)((combined >> 16) & 0xFF);  // 高字节

        // 保存当前波形状态，用于定时器持续发送
        if (channel == Channel.A || channel == Channel.AB)
        {
            _currentV2WaveformA = waveData;
            await WriteAsync(GetV2WaveformACharacteristicUuid(), waveData);
        }
        if (channel == Channel.B || channel == Channel.AB)
        {
            _currentV2WaveformB = waveData;
            await WriteAsync(GetV2WaveformBCharacteristicUuid(), waveData);
        }
        
        Logger.Debug("V2 发送波形: channel={Channel}, X={X}, Y={Y}, Z={Z}, raw=[{D0:X2},{D1:X2},{D2:X2}]", 
            channel, x, y, z, waveData[0], waveData[1], waveData[2]);
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
    // 注意: 官方协议中 PWM_A34 (0x1505) 实际控制 B 通道，PWM_B34 (0x1506) 实际控制 A 通道
    private Guid GetV2StrengthCharacteristicUuid() => Guid.Parse("955A1504-0FE2-F5AA-A094-84B8D4F3E8AD");
    private Guid GetV2WaveformACharacteristicUuid() => Guid.Parse("955A1506-0FE2-F5AA-A094-84B8D4F3E8AD");  // A通道用 0x1506
    private Guid GetV2WaveformBCharacteristicUuid() => Guid.Parse("955A1505-0FE2-F5AA-A094-84B8D4F3E8AD");  // B通道用 0x1505

    #endregion

    #region Event Handlers

    private void OnTransportStateChanged(object? sender, BleConnectionState state)
    {
        if (state == BleConnectionState.Disconnected && Status == DeviceStatus.Connected)
        {
            UpdateStatus(DeviceStatus.Disconnected);
            // 触发自动重连
            StartReconnectTimer();
        }
    }
    
    /// <summary>
    /// 订阅 V3 电量通知
    /// </summary>
    private async Task SubscribeBatteryAsync()
    {
        if (Version != DGLabVersion.V3 || _transport == null) return;
        
        try
        {
            // V3 电量服务: 0x180A, 特性: 0x1500
            await _transport.SubscribeAsync(BatteryServiceUuid, BatteryCharacteristicUuid);
            Logger.Debug("已订阅 V3 电量通知");
            
            // 立即读取一次电量
            var batteryData = await _transport.ReadAsync(BatteryServiceUuid, BatteryCharacteristicUuid);
            if (batteryData.Length > 0)
            {
                _batteryLevel = batteryData[0];
                BatteryChanged?.Invoke(this, _batteryLevel);
                Logger.Information("V3 电量: {Battery}%", _batteryLevel);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "订阅 V3 电量通知失败");
        }
    }
    
    /// <summary>
    /// 订阅 V2 电量通知
    /// </summary>
    private async Task SubscribeV2BatteryAsync()
    {
        if (Version != DGLabVersion.V2 || _transport == null) return;
        
        try
        {
            // V2 电量服务: 955A180A, 特性: 955A1500
            // 官方文档: Battery_Level 属性是 读/通知
            
            // 订阅电量通知
            await _transport.SubscribeAsync(V2BatteryServiceUuid, V2BatteryCharacteristicUuid);
            Logger.Debug("已订阅 V2 电量通知");
            
            // 立即读取一次电量
            var batteryData = await _transport.ReadAsync(V2BatteryServiceUuid, V2BatteryCharacteristicUuid);
            if (batteryData.Length > 0)
            {
                _batteryLevel = batteryData[0];
                BatteryChanged?.Invoke(this, _batteryLevel);
                Logger.Information("V2 电量: {Battery}%", _batteryLevel);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "订阅/读取 V2 电量失败（可能不支持）");
        }
    }
    
    // V2 电量服务 UUID
    private static readonly Guid V2BatteryServiceUuid = Guid.Parse("955A180A-0FE2-F5AA-A094-84B8D4F3E8AD");
    private static readonly Guid V2BatteryCharacteristicUuid = Guid.Parse("955A1500-0FE2-F5AA-A094-84B8D4F3E8AD");
    
    /// <summary>
    /// 启动电量轮询定时器
    /// </summary>
    private void StartBatteryPolling()
    {
        StopBatteryPolling();
        
        // 每 60 秒轮询一次电量
        _batteryTimer = new Timer(async _ =>
        {
            if (Status != DeviceStatus.Connected || _transport == null) return;
            
            try
            {
                if (Version == DGLabVersion.V3)
                {
                    // V3: 直接读取电量特性
                    var batteryData = await _transport.ReadAsync(BatteryServiceUuid, BatteryCharacteristicUuid);
                    if (batteryData.Length > 0)
                    {
                        _batteryLevel = batteryData[0];
                        BatteryChanged?.Invoke(this, _batteryLevel);
                    }
                }
                else
                {
                    // V2: 读取电量特性 (官方文档: 服务0x180A, 特性0x1500)
                    var batteryData = await _transport.ReadAsync(V2BatteryServiceUuid, V2BatteryCharacteristicUuid);
                    if (batteryData.Length > 0)
                    {
                        _batteryLevel = batteryData[0];
                        BatteryChanged?.Invoke(this, _batteryLevel);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "电量轮询失败");
            }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
    }
    
    /// <summary>
    /// 停止电量轮询定时器
    /// </summary>
    private void StopBatteryPolling()
    {
        _batteryTimer?.Dispose();
        _batteryTimer = null;
    }
    
    /// <summary>
    /// 启动重连定时器
    /// </summary>
    private void StartReconnectTimer()
    {
        if (_reconnectAttempts >= MaxReconnectAttempts)
        {
            Logger.Warning("已达到最大重连次数 ({Max})，停止重连", MaxReconnectAttempts);
            return;
        }
        
        StopReconnectTimer();
        
        _reconnectTimer = new Timer(async _ =>
        {
            await TryReconnectAsync();
        }, null, TimeSpan.FromMilliseconds(ReconnectDelayMs), Timeout.InfiniteTimeSpan);
    }
    
    /// <summary>
    /// 停止重连定时器
    /// </summary>
    private void StopReconnectTimer()
    {
        _reconnectTimer?.Dispose();
        _reconnectTimer = null;
    }
    
    /// <summary>
    /// 尝试重连
    /// </summary>
    private async Task TryReconnectAsync()
    {
        if (Config == null || Status == DeviceStatus.Connected)
        {
            return;
        }
        
        _reconnectAttempts++;
        Logger.Information("尝试重连 DG-LAB {Version} (第 {Attempt}/{Max} 次)", 
            Version, _reconnectAttempts, MaxReconnectAttempts);
        
        try
        {
            await ConnectAsync(Config, CancellationToken.None);
            Logger.Information("DG-LAB {Version} 重连成功", Version);
            _reconnectAttempts = 0;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "DG-LAB {Version} 重连失败", Version);
            
            if (_reconnectAttempts < MaxReconnectAttempts)
            {
                StartReconnectTimer();
            }
            else
            {
                Logger.Error("DG-LAB {Version} 重连失败，已达到最大重连次数", Version);
                UpdateStatus(DeviceStatus.Error);
            }
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
            Logger.Debug("V2 电量: {Battery}%", _batteryLevel);
        }
        else if (data.Length == 3)
        {
            // V2 响应数据是小端序
            // 官方文档: 21-11bit(A通道) + 10-0bit(B通道)
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

            Logger.Debug("V2 强度响应: A={A}, B={B}", _strengthA, _strengthB);
        }
    }

    #endregion

    #region Helpers

    private void StartWaveformTimer()
    {
        _waveformTimer?.Dispose();
        _waveformTimer = new Timer(async _ =>
        {
            if (Status != DeviceStatus.Connected || _transport == null) return;
            
            try
            {
                if (Version == DGLabVersion.V3)
                {
                    // V3: 每 100ms 发送一次 B0 指令来维持波形
                    if (_currentWaveformA != null || _currentWaveformB != null)
                    {
                        // 官方示例: 频率{0,0,0,0} + 强度{0,0,0,101} 使该通道放弃全部4组数据
                        var invalidWaveform = new ChannelWaveform
                        {
                            Frequency = new[] { 0, 0, 0, 0 },
                            Strength = new[] { 0, 0, 0, 101 }
                        };
                        
                        var data = new B0CommandData
                        {
                            SequenceNo = 0,
                            StrengthModeA = StrengthParsingMode.NoChange,
                            StrengthModeB = StrengthParsingMode.NoChange,
                            StrengthValueA = 0,
                            StrengthValueB = 0,
                            WaveformA = _currentWaveformA ?? invalidWaveform,
                            WaveformB = _currentWaveformB ?? invalidWaveform
                        };
                        
                        var command = _protocol.BuildB0Command(data);
                        await _transport.WriteWithoutResponseAsync(GetServiceUuid(), GetWriteCharacteristicUuid(), command);
                    }
                }
                else
                {
                    // V2: 每 100ms 发送波形数据来维持输出
                    // 注意: V2 的 PWM_A34/B34 特性是 读/写 属性，需要使用 WriteAsync
                    if (_currentV2WaveformA != null)
                    {
                        await _transport.WriteAsync(GetServiceUuid(), GetV2WaveformACharacteristicUuid(), _currentV2WaveformA);
                    }
                    if (_currentV2WaveformB != null)
                    {
                        await _transport.WriteAsync(GetServiceUuid(), GetV2WaveformBCharacteristicUuid(), _currentV2WaveformB);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "波形定时发送失败");
            }
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

        if (Version == DGLabVersion.V3)
        {
            // V3: 0x150A 特性属性是 "写"，使用 WriteWithoutResponse
            await _transport.WriteWithoutResponseAsync(GetServiceUuid(), characteristicUuid, data);
        }
        else
        {
            // V2: PWM_AB2/A34/B34 特性属性是 "读/写"，需要使用 WriteAsync (带响应)
            await _transport.WriteAsync(GetServiceUuid(), characteristicUuid, data);
        }
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
        StopBatteryPolling();
        StopReconnectTimer();
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
