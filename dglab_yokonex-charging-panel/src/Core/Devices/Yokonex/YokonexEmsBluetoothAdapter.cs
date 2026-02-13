using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChargingPanel.Core.Bluetooth;
using ChargingPanel.Core.Devices.DGLab;
using Serilog;

namespace ChargingPanel.Core.Devices.Yokonex
{
    /// <summary>
    /// 役次元 EMS 蓝牙协议常量。
    /// 兼容 V1.6 与 V2.0 的核心命令定义。
    /// </summary>
    public static class YokonexEmsProtocol
    {
        /// <summary>
        /// 服务 UUID（扫描过滤用）。
        /// </summary>
        public static readonly Guid ServiceUuid = Guid.Parse("0000ff30-0000-1000-8000-00805f9b34fb");
        
        /// <summary>
        /// 写入特征 UUID（WriteWithoutResponse）。
        /// </summary>
        public static readonly Guid WriteCharacteristic = Guid.Parse("0000ff31-0000-1000-8000-00805f9b34fb");
        
        /// <summary>
        /// 通知特征 UUID（Notify）。
        /// </summary>
        public static readonly Guid NotifyCharacteristic = Guid.Parse("0000ff32-0000-1000-8000-00805f9b34fb");
        
        /// <summary>
        /// 包头
        /// </summary>
        public const byte PacketHeader = 0x35;
        
        /// <summary>
        /// 命令字 - 通道控制
        /// </summary>
        public const byte CmdChannelControl = 0x11;
        
        /// <summary>
        /// 命令字 - 马达控制
        /// </summary>
        public const byte CmdMotorControl = 0x12;
        
        /// <summary>
        /// 命令字 - 计步功能
        /// </summary>
        public const byte CmdPedometer = 0x13;
        
        /// <summary>
        /// 命令字 - 角度功能
        /// </summary>
        public const byte CmdAngle = 0x14;
        
        /// <summary>
        /// 命令字 - 查询命令（V1.6 文档）
        /// </summary>
        public const byte CmdQueryV1 = 0x71;
        
        /// <summary>
        /// 命令字 - 查询命令（V2.0 文档）
        /// </summary>
        public const byte CmdQueryV2 = 0x71;

        /// <summary>
        /// 兼容旧版本实现的查询命令（文档未推荐，仅兜底）。
        /// </summary>
        public const byte CmdQueryLegacy = 0x15;

        /// <summary>
        /// 查询类型（0x71 子命令）。
        /// </summary>
        public const byte QueryChannelAStatus = 0x01;
        public const byte QueryChannelBStatus = 0x02;
        public const byte QueryMotorStatus = 0x03;
        public const byte QueryBatteryLevel = 0x04;
        public const byte QueryStepCount = 0x05;
        public const byte QueryAngleData = 0x06;
        
        /// <summary>
        /// 通道号
        /// </summary>
        public const byte ChannelA = 0x01;
        public const byte ChannelB = 0x02;
        public const byte ChannelAB = 0x03;
        
        /// <summary>
        /// 协议最大强度（276 级，0x01~0x114）。
        /// </summary>
        public const int MaxStrength = 276;
        
        /// <summary>
        /// 固定模式数量（16 种）。
        /// </summary>
        public const int FixedModeCount = 16;
        
        /// <summary>
        /// 自定义模式编号
        /// </summary>
        public const byte CustomMode = 0x11;

        /// <summary>
        /// V2.0 通道控制模式：固定模式
        /// </summary>
        public const byte V2ChannelModeFixed = 0x01;

        /// <summary>
        /// V2.0 通道控制模式：实时模式（频率/脉宽）
        /// </summary>
        public const byte V2ChannelModeRealtime = 0x02;

        /// <summary>
        /// V2.0 通道控制模式：频率播放列表模式
        /// </summary>
        public const byte V2ChannelModeFrequencySequence = 0x03;

        /// <summary>
        /// V2.0 频率模式最大点数
        /// </summary>
        public const int V2FrequencySequenceMaxPoints = 100;
    }

    /// <summary>
    /// 役次元电击器通道状态
    /// </summary>
    public class YokonexEmsChannelState
    {
        public bool Enabled { get; set; }
        public int Strength { get; set; }  // 1-276
        public int Mode { get; set; }       // 1-16 固定模式, 17=自定义
        public int Frequency { get; set; }  // 1-100 Hz (仅自定义模式)
        public int PulseTime { get; set; }  // 0-100 us (仅自定义模式)
    }

    /// <summary>
    /// 役次元电击器马达状态
    /// </summary>
    public enum YokonexMotorState
    {
        Off = 0x00,
        On = 0x01,
        Preset1 = 0x11,
        Preset2 = 0x12,
        Preset3 = 0x13
    }

    /// <summary>
    /// 计步器状态
    /// </summary>
    public enum PedometerState
    {
        Off = 0x00,
        On = 0x01,
        Reset = 0x02,
        Pause = 0x03,
        Resume = 0x04
    }

    /// <summary>
    /// 役次元 EMS 蓝牙适配器。
    /// 包含双通道、电机、计步、角度上报等能力。
    /// </summary>
    public class YokonexEmsBluetoothAdapter : IDevice, IYokonexEmsDevice
    {
        private readonly IBluetoothTransport _transport;
        private bool _isConnected;
        private bool _disposed;
        
        // 当前通道状态
        private readonly YokonexEmsChannelState _channelA = new();
        private readonly YokonexEmsChannelState _channelB = new();
        private int _batteryLevel = 100;
        private int _limitA = YokonexEmsProtocol.MaxStrength;
        private int _limitB = YokonexEmsProtocol.MaxStrength;
        
        // 计步/角度相关状态
        private int _stepCount = 0;
        private bool _angleEnabled = false;
        private (float X, float Y, float Z) _currentAngle = (0, 0, 0);
        private (bool A, bool B) _channelConnectionState = (false, false);
        private YokonexMotorState _motorState = YokonexMotorState.Off;
        
        // 定时任务：重连 + 电量轮询
        private Timer? _batteryTimer;
        private Timer? _reconnectTimer;
        private int _reconnectAttempts;
        private const int MaxReconnectAttempts = 5;
        private const int ReconnectDelayMs = 3000;

        // IDevice 基础字段
        public string Id { get; }
        public string Name { get; set; }
        public DeviceType Type => DeviceType.Yokonex;
        public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;
        public DeviceState State => GetState();
        public ConnectionConfig? Config { get; private set; }
        public YokonexProtocolGeneration ProtocolGeneration { get; }
        
        // IYokonexDevice 扩展字段
        public YokonexDeviceType YokonexType => YokonexDeviceType.Estim;
        
        // EMS 扩展字段
        public int StepCount => _stepCount;
        public (float X, float Y, float Z) CurrentAngle => _currentAngle;
        public (bool ChannelA, bool ChannelB) ChannelConnectionState => _channelConnectionState;

        // IDevice 事件
        public event EventHandler<DeviceStatus>? StatusChanged;
        public event EventHandler<StrengthInfo>? StrengthChanged;
        public event EventHandler<int>? BatteryChanged;
        public event EventHandler<Exception>? ErrorOccurred;

        // EMS 扩展事件
        public event EventHandler<int>? StepCountChanged;
        public event EventHandler<(float X, float Y, float Z)>? AngleChanged;
        public event EventHandler<(bool ChannelA, bool ChannelB)>? ChannelConnectionChanged;
        public event EventHandler<YokonexMotorState>? MotorStateChanged;

        public YokonexEmsBluetoothAdapter(
            IBluetoothTransport transport,
            YokonexProtocolGeneration generation = YokonexProtocolGeneration.EmsV1_6,
            string? id = null,
            string? name = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _transport.StateChanged += OnTransportStateChanged;
            _transport.DataReceived += OnDataReceived;
            ProtocolGeneration = generation == YokonexProtocolGeneration.Auto
                ? YokonexProtocolGeneration.EmsV1_6
                : generation;
            Id = id ?? $"yc_ems_{Guid.NewGuid():N}".Substring(0, 20);
            Name = name ?? "役次元电击器";
        }

        /// <summary>
        /// 扫描可用的役次元 EMS 设备。
        /// </summary>
        public async Task<BleDeviceInfo[]> ScanDevicesAsync(int timeoutMs = 10000)
        {
            return await _transport.ScanAsync(YokonexEmsProtocol.ServiceUuid, null, timeoutMs);
        }

        /// <summary>
        /// 实现 IDevice.ConnectAsync。
        /// </summary>
        public async Task ConnectAsync(ConnectionConfig config, CancellationToken cancellationToken = default)
        {
            Config = config;
            UpdateStatus(DeviceStatus.Connecting);
            
            try
            {
                await _transport.ConnectAsync(config.Address ?? "");
                await _transport.DiscoverServicesAsync();
                await _transport.SubscribeAsync(
                    YokonexEmsProtocol.ServiceUuid,
                    YokonexEmsProtocol.NotifyCharacteristic);
                UpdateStatus(DeviceStatus.Connected);
                
                // 连接成功后拉起电量轮询。
                StartBatteryPolling();
                
                // 重连计数归零。
                _reconnectAttempts = 0;
            }
            catch (Exception ex)
            {
                UpdateStatus(DeviceStatus.Error);
                ErrorOccurred?.Invoke(this, ex);
                throw;
            }
        }

        /// <summary>
        /// 断开连接。
        /// </summary>
        public async Task DisconnectAsync()
        {
            StopBatteryPolling();
            StopReconnectTimer();
            await _transport.DisconnectAsync();
        }
        
        /// <summary>
        /// 手动触发重连。
        /// </summary>
        public async Task ReconnectAsync()
        {
            if (Config == null)
            {
                throw new InvalidOperationException("没有保存的连接配置");
            }
            
            Console.WriteLine("[YokonexEMS] 手动触发重连");
            _reconnectAttempts = 0;
            await TryReconnectAsync();
        }

        /// <summary>
        /// 设置通道输出参数。
        /// </summary>
        /// <param name="channel">通道: 1=A, 2=B, 3=AB</param>
        /// <param name="strength">强度: 1-276</param>
        /// <param name="enabled">是否开启通道</param>
        /// <param name="mode">模式: 1-16=固定模式, 17=自定义</param>
        /// <param name="frequency">频率: 1-100Hz (仅自定义模式)</param>
        /// <param name="pulseTime">脉冲时间: 0-100us (仅自定义模式)</param>
        public async Task SetChannelAsync(byte channel, int strength, bool enabled = true, 
            int mode = 1, int frequency = 0, int pulseTime = 0)
        {
            // 入参保护，避免下发非法值。
            strength = Math.Clamp(strength, 0, YokonexEmsProtocol.MaxStrength);
            mode = Math.Clamp(mode, 1, YokonexEmsProtocol.CustomMode);
            frequency = Math.Clamp(frequency, 0, 100);
            pulseTime = Math.Clamp(pulseTime, 0, 100);

            ApplyChannelStateUpdate(channel, strength, enabled, mode, frequency, pulseTime);

            if (ProtocolGeneration == YokonexProtocolGeneration.EmsV2_0)
            {
                await SendV2ChannelCommandAsync(mode);
            }
            else
            {
                await SendV1ChannelCommandAsync(channel, strength, enabled, mode, frequency, pulseTime);
            }

            StrengthChanged?.Invoke(this, new StrengthInfo 
            { 
                ChannelA = _channelA.Strength, 
                ChannelB = _channelB.Strength,
                LimitA = _limitA,
                LimitB = _limitB
            });
        }

        /// <summary>
        /// 设置 A 通道强度
        /// </summary>
        public Task SetChannelAAsync(int strength, bool enabled = true, int mode = 1)
        {
            return SetChannelAsync(YokonexEmsProtocol.ChannelA, strength, enabled, mode);
        }

        /// <summary>
        /// 设置 B 通道强度
        /// </summary>
        public Task SetChannelBAsync(int strength, bool enabled = true, int mode = 1)
        {
            return SetChannelAsync(YokonexEmsProtocol.ChannelB, strength, enabled, mode);
        }

        /// <summary>
        /// 同时设置 AB 通道强度
        /// </summary>
        public Task SetChannelABAsync(int strength, bool enabled = true, int mode = 1)
        {
            return SetChannelAsync(YokonexEmsProtocol.ChannelAB, strength, enabled, mode);
        }

        /// <summary>
        /// 设置自定义模式参数
        /// </summary>
        public Task SetCustomModeAsync(byte channel, int strength, int frequency, int pulseTime)
        {
            return SetChannelAsync(channel, strength, true, YokonexEmsProtocol.CustomMode, frequency, pulseTime);
        }

        /// <summary>
        /// 关闭通道
        /// </summary>
        public async Task StopChannelAsync(byte channel = YokonexEmsProtocol.ChannelAB)
        {
            await SetChannelAsync(channel, 0, false);
        }

        /// <summary>
        /// 控制马达
        /// </summary>
        public async Task SetMotorAsync(YokonexMotorState state)
        {
            var data = new byte[4];
            data[0] = YokonexEmsProtocol.PacketHeader;
            data[1] = YokonexEmsProtocol.CmdMotorControl;
            data[2] = (byte)state;
            data[3] = CalculateChecksum(data, 0, 3);

            await SendCommandAsync(data);
            _motorState = state;
            MotorStateChanged?.Invoke(this, state);
        }
        
        #region IYokonexEmsDevice 接口实现

        /// <summary>
        /// 设置马达状态 (IYokonexEmsDevice)
        /// </summary>
        public Task SetMotorStateAsync(YokonexMotorState state) => SetMotorAsync(state);
        
        /// <summary>
        /// 设置计步器状态 (IYokonexEmsDevice)
        /// </summary>
        public Task SetPedometerStateAsync(PedometerState state) => SetPedometerAsync(state);
        
        /// <summary>
        /// 启用/禁用角度传感器 (IYokonexEmsDevice)
        /// </summary>
        public async Task SetAngleSensorEnabledAsync(bool enabled)
        {
            await SetAngleReportingAsync(enabled);
            _angleEnabled = enabled;
        }
        
        /// <summary>
        /// 设置自定义波形参数 (IYokonexEmsDevice)
        /// </summary>
        public async Task SetCustomWaveformAsync(Channel channel, int frequency, int pulseTime)
        {
            byte ch = channel switch
            {
                Channel.A => YokonexEmsProtocol.ChannelA,
                Channel.B => YokonexEmsProtocol.ChannelB,
                _ => YokonexEmsProtocol.ChannelAB
            };
            int strength = channel switch
            {
                Channel.A => _channelA.Strength,
                Channel.B => _channelB.Strength,
                _ => Math.Max(_channelA.Strength, _channelB.Strength)
            };
            await SetCustomModeAsync(ch, strength, frequency, pulseTime);
        }
        
        /// <summary>
        /// 设置固定模式 (IYokonexEmsDevice)
        /// </summary>
        public async Task SetFixedModeAsync(Channel channel, int mode)
        {
            byte ch = channel switch
            {
                Channel.A => YokonexEmsProtocol.ChannelA,
                Channel.B => YokonexEmsProtocol.ChannelB,
                _ => YokonexEmsProtocol.ChannelAB
            };
            int strength = channel switch
            {
                Channel.A => _channelA.Strength,
                Channel.B => _channelB.Strength,
                _ => Math.Max(_channelA.Strength, _channelB.Strength)
            };
            await SetChannelAsync(ch, strength, true, Math.Clamp(mode, 1, 16));
        }
        
        #endregion

        /// <summary>
        /// 控制计步器
        /// </summary>
        public async Task SetPedometerAsync(PedometerState state)
        {
            var data = new byte[4];
            data[0] = YokonexEmsProtocol.PacketHeader;
            data[1] = YokonexEmsProtocol.CmdPedometer;
            data[2] = (byte)state;
            data[3] = CalculateChecksum(data, 0, 3);

            await SendCommandAsync(data);
        }

        /// <summary>
        /// 启用/禁用角度上报
        /// </summary>
        public async Task SetAngleReportingAsync(bool enabled)
        {
            var data = new byte[4];
            data[0] = YokonexEmsProtocol.PacketHeader;
            data[1] = YokonexEmsProtocol.CmdAngle;
            data[2] = enabled ? (byte)0x01 : (byte)0x00;
            data[3] = CalculateChecksum(data, 0, 3);

            await SendCommandAsync(data);
        }

        /// <summary>
        /// 获取当前通道状态
        /// </summary>
        public (YokonexEmsChannelState channelA, YokonexEmsChannelState channelB) GetChannelStates()
        {
            return (_channelA, _channelB);
        }
        
        /// <summary>
        /// 查询设备状态（通道、计步、角度等）。
        /// </summary>
        /// <param name="queryType">查询类型: 0x01=A状态, 0x02=B状态, 0x03=马达, 0x04=电量, 0x05=计步, 0x06=角度</param>
        public async Task QueryStatusAsync(byte queryType = 0x01)
        {
            var data = new byte[4];
            data[0] = YokonexEmsProtocol.PacketHeader;
            data[1] = GetQueryCommand();
            data[2] = queryType;
            data[3] = CalculateChecksum(data, 0, 3);

            await SendCommandAsync(data);
        }
        
        /// <summary>
        /// 查询通道连接状态（电极片是否接入）。
        /// </summary>
        public async Task QueryChannelConnectionAsync()
        {
            await QueryStatusAsync(YokonexEmsProtocol.QueryChannelAStatus);
            await QueryStatusAsync(YokonexEmsProtocol.QueryChannelBStatus);
        }
        
        /// <summary>
        /// 查询计步数据
        /// </summary>
        public Task QueryStepCountAsync() => QueryStatusAsync(YokonexEmsProtocol.QueryStepCount);
        
        /// <summary>
        /// 查询角度数据
        /// </summary>
        public Task QueryAngleAsync() => QueryStatusAsync(YokonexEmsProtocol.QueryAngleData);

        #region IDevice 实现

        public async Task SetStrengthAsync(Channel channel, int value, StrengthMode mode = StrengthMode.Set)
        {
            // 强度输入兼容两种语义：
            // 1) 0~100：按百分比输入。
            // 2) 101~276：按协议绝对值输入（内部会换算回百分比状态）。
            var normalizedDelta = NormalizeDeltaInput(value);
            int targetValue;
            switch (mode)
            {
                case StrengthMode.Increase:
                    // Increase 模式：在当前值基础上叠加。
                    if (channel == Channel.A || channel == Channel.AB)
                    {
                        var currentA = ToNormalizedPercent(_channelA.Strength);
                        targetValue = Math.Clamp(currentA + normalizedDelta, 0, 100);
                    }
                    else
                    {
                        var currentB = ToNormalizedPercent(_channelB.Strength);
                        targetValue = Math.Clamp(currentB + normalizedDelta, 0, 100);
                    }
                    break;
                case StrengthMode.Decrease:
                    // Decrease 模式：在当前值基础上递减。
                    if (channel == Channel.A || channel == Channel.AB)
                    {
                        var currentA = ToNormalizedPercent(_channelA.Strength);
                        targetValue = Math.Clamp(currentA - normalizedDelta, 0, 100);
                    }
                    else
                    {
                        var currentB = ToNormalizedPercent(_channelB.Strength);
                        targetValue = Math.Clamp(currentB - normalizedDelta, 0, 100);
                    }
                    break;
                case StrengthMode.Set:
                default:
                    targetValue = NormalizeSetInput(value);
                    break;
            }
            
            // 把 0~100 的通用强度映射到协议 0~276。
            var mappedValue = ToDeviceStrength(targetValue);
            
            switch (channel)
            {
                case Channel.A:
                    await SetChannelAAsync(mappedValue, targetValue > 0);
                    break;
                case Channel.B:
                    await SetChannelBAsync(mappedValue, targetValue > 0);
                    break;
                case Channel.AB:
                    // AB 需要拆开分别计算。
                    if (mode == StrengthMode.Increase || mode == StrengthMode.Decrease)
                    {
                        var currentA = ToNormalizedPercent(_channelA.Strength);
                        var currentB = ToNormalizedPercent(_channelB.Strength);
                        var targetA = mode == StrengthMode.Increase 
                            ? Math.Clamp(currentA + normalizedDelta, 0, 100) 
                            : Math.Clamp(currentA - normalizedDelta, 0, 100);
                        var targetB = mode == StrengthMode.Increase 
                            ? Math.Clamp(currentB + normalizedDelta, 0, 100) 
                            : Math.Clamp(currentB - normalizedDelta, 0, 100);
                        await SetChannelAAsync(ToDeviceStrength(targetA), targetA > 0);
                        await SetChannelBAsync(ToDeviceStrength(targetB), targetB > 0);
                    }
                    else
                    {
                        await SetChannelABAsync(mappedValue, targetValue > 0);
                    }
                    break;
            }
        }

        public Task SendWaveformAsync(Channel channel, WaveformData data)
        {
            // EMS 没有 DG-LAB 的“波形队列”概念。
            // 这里统一映射为厂商协议的自定义模式（模式 17）：强度 + 频率/脉宽。
            return SendWaveformAsCustomModeAsync(channel, data);
        }

        public Task ClearWaveformQueueAsync(Channel channel)
        {
            return Task.CompletedTask;
        }

        public Task SetLimitsAsync(int limitA, int limitB)
        {
            _limitA = Math.Clamp(limitA, 0, YokonexEmsProtocol.MaxStrength);
            _limitB = Math.Clamp(limitB, 0, YokonexEmsProtocol.MaxStrength);
            return Task.CompletedTask;
        }

        private DeviceState GetState()
        {
            return new DeviceState
            {
                Status = Status,
                Strength = new StrengthInfo
                {
                    ChannelA = _channelA.Strength,
                    ChannelB = _channelB.Strength,
                    LimitA = _limitA,
                    LimitB = _limitB
                },
                BatteryLevel = _batteryLevel,
                LastUpdate = DateTime.UtcNow
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
        #endregion

        #region 私有方法

        private static int ToNormalizedPercent(int deviceStrength)
        {
            var safe = Math.Clamp(deviceStrength, 0, YokonexEmsProtocol.MaxStrength);
            return (int)Math.Round(safe * 100d / YokonexEmsProtocol.MaxStrength);
        }

        private static int ToDeviceStrength(int normalizedPercent)
        {
            var safe = Math.Clamp(normalizedPercent, 0, 100);
            return (int)Math.Round(safe * YokonexEmsProtocol.MaxStrength / 100d);
        }

        private static int NormalizeSetInput(int value)
        {
            if (value <= 100)
            {
                return Math.Clamp(value, 0, 100);
            }

            return ToNormalizedPercent(value);
        }

        private static int NormalizeDeltaInput(int value)
        {
            if (value <= 100)
            {
                return Math.Clamp(value, 0, 100);
            }

            var normalized = ToNormalizedPercent(value);
            return Math.Max(1, normalized);
        }

        private void ApplyChannelStateUpdate(byte channel, int strength, bool enabled, int mode, int frequency, int pulseTime)
        {
            if ((channel & YokonexEmsProtocol.ChannelA) != 0)
            {
                _channelA.Enabled = enabled;
                _channelA.Strength = strength;
                _channelA.Mode = mode;
                _channelA.Frequency = frequency;
                _channelA.PulseTime = pulseTime;
            }

            if ((channel & YokonexEmsProtocol.ChannelB) != 0)
            {
                _channelB.Enabled = enabled;
                _channelB.Strength = strength;
                _channelB.Mode = mode;
                _channelB.Frequency = frequency;
                _channelB.PulseTime = pulseTime;
            }
        }

        private async Task SendV1ChannelCommandAsync(byte channel, int strength, bool enabled, int mode, int frequency, int pulseTime)
        {
            // V1.6：按 A/B/AB 通道直接下发，支持固定/自定义模式。
            var data = new byte[10];
            data[0] = YokonexEmsProtocol.PacketHeader;
            data[1] = YokonexEmsProtocol.CmdChannelControl;
            data[2] = channel;
            data[3] = enabled ? (byte)0x01 : (byte)0x00;
            data[4] = (byte)((strength >> 8) & 0xFF);
            data[5] = (byte)(strength & 0xFF);
            data[6] = (byte)mode;
            data[7] = mode == YokonexEmsProtocol.CustomMode ? (byte)frequency : (byte)0x00;
            data[8] = mode == YokonexEmsProtocol.CustomMode ? (byte)pulseTime : (byte)0x00;
            data[9] = CalculateChecksum(data, 0, 9);

            await SendCommandAsync(data);
        }

        private async Task SendV2ChannelCommandAsync(int mode)
        {
            if (mode == YokonexEmsProtocol.CustomMode)
            {
                // V2.0 实时模式：一次写入 A/B 的强度 + 频率 + 脉宽。
                var data = new byte[12];
                data[0] = YokonexEmsProtocol.PacketHeader;
                data[1] = YokonexEmsProtocol.CmdChannelControl;
                data[2] = YokonexEmsProtocol.V2ChannelModeRealtime;

                var aStrength = _channelA.Enabled ? _channelA.Strength : 0;
                var bStrength = _channelB.Enabled ? _channelB.Strength : 0;
                var aFrequency = _channelA.Enabled ? Math.Clamp(_channelA.Frequency, 1, 100) : 0;
                var bFrequency = _channelB.Enabled ? Math.Clamp(_channelB.Frequency, 1, 100) : 0;
                var aPulse = _channelA.Enabled ? Math.Clamp(_channelA.PulseTime, 0, 100) : 0;
                var bPulse = _channelB.Enabled ? Math.Clamp(_channelB.PulseTime, 0, 100) : 0;

                data[3] = (byte)((aStrength >> 8) & 0xFF);
                data[4] = (byte)(aStrength & 0xFF);
                data[5] = (byte)aFrequency;
                data[6] = (byte)aPulse;
                data[7] = (byte)((bStrength >> 8) & 0xFF);
                data[8] = (byte)(bStrength & 0xFF);
                data[9] = (byte)bFrequency;
                data[10] = (byte)bPulse;
                data[11] = CalculateChecksum(data, 0, 11);

                await SendCommandAsync(data);
                return;
            }

            // V2.0 固定模式：一次写入 A/B 的强度 + 固定模式。
            var fixedData = new byte[10];
            fixedData[0] = YokonexEmsProtocol.PacketHeader;
            fixedData[1] = YokonexEmsProtocol.CmdChannelControl;
            fixedData[2] = YokonexEmsProtocol.V2ChannelModeFixed;

            var channelAStrength = _channelA.Enabled ? _channelA.Strength : 0;
            var channelBStrength = _channelB.Enabled ? _channelB.Strength : 0;
            var channelAMode = _channelA.Enabled ? Math.Clamp(_channelA.Mode, 1, YokonexEmsProtocol.FixedModeCount) : 0;
            var channelBMode = _channelB.Enabled ? Math.Clamp(_channelB.Mode, 1, YokonexEmsProtocol.FixedModeCount) : 0;

            fixedData[3] = (byte)((channelAStrength >> 8) & 0xFF);
            fixedData[4] = (byte)(channelAStrength & 0xFF);
            fixedData[5] = (byte)channelAMode;
            fixedData[6] = (byte)((channelBStrength >> 8) & 0xFF);
            fixedData[7] = (byte)(channelBStrength & 0xFF);
            fixedData[8] = (byte)channelBMode;
            fixedData[9] = CalculateChecksum(fixedData, 0, 9);

            await SendCommandAsync(fixedData);
        }

        private async Task SendV2FrequencyModeAsync(byte channel, int strength, IReadOnlyList<(byte frequency, byte pulseTime)> points)
        {
            if (channel != YokonexEmsProtocol.ChannelA && channel != YokonexEmsProtocol.ChannelB)
            {
                throw new ArgumentException("V2 频率模式仅支持单通道 A 或 B（AB 需分开发送）", nameof(channel));
            }

            var safeStrength = Math.Clamp(strength, 0, YokonexEmsProtocol.MaxStrength);
            var safeCount = Math.Clamp(points.Count, 1, YokonexEmsProtocol.V2FrequencySequenceMaxPoints);
            var data = new byte[7 + safeCount * 2];
            data[0] = YokonexEmsProtocol.PacketHeader;
            data[1] = YokonexEmsProtocol.CmdChannelControl;
            data[2] = YokonexEmsProtocol.V2ChannelModeFrequencySequence;
            data[3] = channel;
            data[4] = (byte)((safeStrength >> 8) & 0xFF);
            data[5] = (byte)(safeStrength & 0xFF);

            var offset = 6;
            for (var i = 0; i < safeCount; i++)
            {
                data[offset++] = points[i].frequency;
                data[offset++] = points[i].pulseTime;
            }

            data[offset] = CalculateChecksum(data, 0, offset);
            await SendCommandAsync(data);
        }

        private static bool TryParseV2FrequencySequence(string? raw, out List<(byte frequency, byte pulseTime)> points)
        {
            points = new List<(byte frequency, byte pulseTime)>();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            // 支持两种配置写法：
            // 1) "40:10;45:12;50:15"
            // 2) "40,10,45,12,50,15"
            var normalized = raw.Trim();
            var sequenceItems = normalized.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (sequenceItems.Length > 0 && sequenceItems.Any(item => item.Contains(':')))
            {
                foreach (var item in sequenceItems)
                {
                    var pair = item.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (pair.Length != 2 ||
                        !int.TryParse(pair[0], out var freq) ||
                        !int.TryParse(pair[1], out var pulse))
                    {
                        continue;
                    }

                    points.Add(((byte)Math.Clamp(freq, 1, 100), (byte)Math.Clamp(pulse, 0, 100)));
                }
            }
            else
            {
                var values = normalized
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(v => int.TryParse(v, out var n) ? n : -1)
                    .ToArray();
                for (var i = 0; i + 1 < values.Length; i += 2)
                {
                    if (values[i] < 0 || values[i + 1] < 0)
                    {
                        continue;
                    }

                    points.Add(((byte)Math.Clamp(values[i], 1, 100), (byte)Math.Clamp(values[i + 1], 0, 100)));
                }
            }

            return points.Count > 0;
        }

        private async Task SendWaveformAsCustomModeAsync(Channel channel, WaveformData waveform)
        {
            if (waveform == null)
            {
                throw new ArgumentNullException(nameof(waveform));
            }

            var (frequency, pulseTime) = ResolveCustomWaveformParameters(waveform);
            var strengthPercent = Math.Clamp(waveform.Strength, 0, 100);

            // 先同步目标强度，避免后续自定义模式和状态缓存不一致。
            await SetStrengthAsync(channel, strengthPercent, StrengthMode.Set);

            if (strengthPercent <= 0)
            {
                return;
            }

            // V2.0 额外支持频率播放列表（0x03）：
            // 约定 HexData 可写成 "f:p;f:p;..." 或 "f,p,f,p,..."。
            if (ProtocolGeneration == YokonexProtocolGeneration.EmsV2_0 &&
                TryParseV2FrequencySequence(waveform.HexData, out var points))
            {
                var deviceStrength = ToDeviceStrength(strengthPercent);
                if (channel == Channel.A || channel == Channel.AB)
                {
                    await SendV2FrequencyModeAsync(YokonexEmsProtocol.ChannelA, deviceStrength, points);
                }

                if (channel == Channel.B || channel == Channel.AB)
                {
                    await SendV2FrequencyModeAsync(YokonexEmsProtocol.ChannelB, deviceStrength, points);
                }

                return;
            }

            await SetCustomWaveformAsync(channel, frequency, pulseTime);
        }

        private static (int frequency, int pulseTime) ResolveCustomWaveformParameters(WaveformData waveform)
        {
            var frequency = Math.Clamp(waveform.Frequency, 1, 100);
            var pulseTime = waveform.Duration is >= 1 and <= 100
                ? waveform.Duration
                : 20;

            if (TryParseFrequencyPulseHint(waveform.HexData, out var hintedFrequency, out var hintedPulseTime))
            {
                frequency = hintedFrequency;
                pulseTime = hintedPulseTime;
            }

            return (frequency, pulseTime);
        }

        private static bool TryParseFrequencyPulseHint(string? raw, out int frequency, out int pulseTime)
        {
            frequency = 0;
            pulseTime = 0;

            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var parts = raw
                .Split(new[] { ',', ':', ';', '/', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                return false;
            }

            if (!int.TryParse(parts[0], out var parsedFrequency) || !int.TryParse(parts[1], out var parsedPulseTime))
            {
                return false;
            }

            frequency = Math.Clamp(parsedFrequency, 1, 100);
            pulseTime = Math.Clamp(parsedPulseTime, 0, 100);
            return true;
        }

        private async Task SendCommandAsync(byte[] data)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("设备未连接");
            }

            await _transport.WriteWithoutResponseAsync(
                YokonexEmsProtocol.ServiceUuid,
                YokonexEmsProtocol.WriteCharacteristic,
                data);
        }

        private static byte CalculateChecksum(byte[] data, int start, int length)
        {
            byte sum = 0;
            for (int i = start; i < start + length; i++)
            {
                sum += data[i];
            }
            return sum;
        }

        private void OnTransportStateChanged(object? sender, BleConnectionState state)
        {
            _isConnected = state == BleConnectionState.Connected;
            if (state == BleConnectionState.Connected)
            {
                UpdateStatus(DeviceStatus.Connected);
            }
            else if (state == BleConnectionState.Disconnected)
            {
                UpdateStatus(DeviceStatus.Disconnected);
                // 自动恢复由传输层统一处理，避免适配器和传输层同时重连。
                Console.WriteLine("[YokonexEMS] 检测到断开，等待传输层自动恢复");
            }
        }

        private void OnDataReceived(object? sender, BleDataReceivedEventArgs e)
        {
            if (e.CharacteristicUuid != YokonexEmsProtocol.NotifyCharacteristic)
            {
                return;
            }

            var data = e.Data;
            if (data.Length < 3 || data[0] != YokonexEmsProtocol.PacketHeader)
            {
                return;
            }

            var cmd = data[1];
            
            // 按命令类型拆分解析响应。
            Console.WriteLine($"[YokonexEMS] 收到响应: 命令={cmd:X2}, 数据={BitConverter.ToString(data)}");
            
            switch (cmd)
            {
                case YokonexEmsProtocol.CmdChannelControl:
                    // 通道控制响应：同时带通道连接状态。
                    if (data.Length >= 4)
                    {
                        // byte3=A 通道连接状态（0/1），byte4=B 通道连接状态。
                        var connA = data[2] == 0x01;
                        var connB = data[3] == 0x01;
                        if (_channelConnectionState != (connA, connB))
                        {
                            _channelConnectionState = (connA, connB);
                            ChannelConnectionChanged?.Invoke(this, _channelConnectionState);
                            Console.WriteLine($"[YokonexEMS] 通道连接状态: A={connA}, B={connB}");
                        }
                    }
                    break;
                    
                case YokonexEmsProtocol.CmdPedometer:
                    // 计步响应
                    if (data.Length >= 6)
                    {
                        // byte3~6：步数（4 字节，大端）。
                        _stepCount = (data[2] << 24) | (data[3] << 16) | (data[4] << 8) | data[5];
                        StepCountChanged?.Invoke(this, _stepCount);
                        Console.WriteLine($"[YokonexEMS] 计步: {_stepCount}");
                    }
                    break;
                    
                case YokonexEmsProtocol.CmdAngle:
                    // 角度响应
                    if (data.Length >= 8)
                    {
                        // byte3~4=X，byte5~6=Y，byte7~8=Z。
                        var rawX = (short)((data[2] << 8) | data[3]);
                        var rawY = (short)((data[4] << 8) | data[5]);
                        var rawZ = (short)((data[6] << 8) | data[7]);
                        // 转成角度值（后续可按机型做标定）。
                        _currentAngle = (rawX / 100.0f, rawY / 100.0f, rawZ / 100.0f);
                        AngleChanged?.Invoke(this, _currentAngle);
                        Console.WriteLine($"[YokonexEMS] 角度: X={_currentAngle.X:F2}, Y={_currentAngle.Y:F2}, Z={_currentAngle.Z:F2}");
                    }
                    break;
                    
                case YokonexEmsProtocol.CmdQueryV1:
                case YokonexEmsProtocol.CmdQueryLegacy:
                    // 查询响应：按 queryType 进一步分发解析。
                    if (data.Length >= 4)
                    {
                        var queryType = data[2];
                        switch (queryType)
                        {
                            case YokonexEmsProtocol.QueryChannelAStatus:
                                if (data.Length >= 4)
                                {
                                    var connectedA = data[3] != 0x00;
                                    _channelConnectionState = (connectedA, _channelConnectionState.B);
                                    ChannelConnectionChanged?.Invoke(this, _channelConnectionState);
                                }
                                break;

                            case YokonexEmsProtocol.QueryChannelBStatus:
                                if (data.Length >= 4)
                                {
                                    var connectedB = data[3] != 0x00;
                                    _channelConnectionState = (_channelConnectionState.A, connectedB);
                                    ChannelConnectionChanged?.Invoke(this, _channelConnectionState);
                                }
                                break;

                            case YokonexEmsProtocol.QueryBatteryLevel:
                                _batteryLevel = data[3];
                                BatteryChanged?.Invoke(this, _batteryLevel);
                                Console.WriteLine($"[YokonexEMS] 电量: {_batteryLevel}%");
                                break;

                            case YokonexEmsProtocol.QueryStepCount:
                                if (data.Length >= 7)
                                {
                                    _stepCount = (data[3] << 24) | (data[4] << 16) | (data[5] << 8) | data[6];
                                    StepCountChanged?.Invoke(this, _stepCount);
                                }
                                else if (data.Length >= 5)
                                {
                                    _stepCount = (data[3] << 8) | data[4];
                                    StepCountChanged?.Invoke(this, _stepCount);
                                }
                                break;

                            case YokonexEmsProtocol.QueryAngleData:
                                if (data.Length >= 9)
                                {
                                    var rawX = (short)((data[3] << 8) | data[4]);
                                    var rawY = (short)((data[5] << 8) | data[6]);
                                    var rawZ = (short)((data[7] << 8) | data[8]);
                                    _currentAngle = (rawX / 100.0f, rawY / 100.0f, rawZ / 100.0f);
                                    AngleChanged?.Invoke(this, _currentAngle);
                                }
                                break;
                        }
                    }
                    break;
            }
        }

        private bool IsConnected => _isConnected && _transport.State == BleConnectionState.Connected;
        
        #region 电量轮询和自动重连
        
        /// <summary>
        /// 启动电量轮询定时器。
        /// </summary>
        private void StartBatteryPolling()
        {
            StopBatteryPolling();
            
            // 每 60 秒兜底轮询一次电量。
            _batteryTimer = new Timer(async _ =>
            {
                if (!IsConnected) return;
                
                try
                {
                    await QueryStatusAsync(YokonexEmsProtocol.QueryBatteryLevel);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[YokonexEMS] 电量轮询失败: {ex.Message}");
                }
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
        }
        
        /// <summary>
        /// 停止电量轮询定时器。
        /// </summary>
        private void StopBatteryPolling()
        {
            _batteryTimer?.Dispose();
            _batteryTimer = null;
        }
        
        /// <summary>
        /// 启动重连定时器。
        /// </summary>
        private void StartReconnectTimer()
        {
            if (_reconnectAttempts >= MaxReconnectAttempts)
            {
                Console.WriteLine($"[YokonexEMS] 已达到最大重连次数 ({MaxReconnectAttempts})，停止重连");
                return;
            }
            
            StopReconnectTimer();
            
            _reconnectTimer = new Timer(async _ =>
            {
                await TryReconnectAsync();
            }, null, TimeSpan.FromMilliseconds(ReconnectDelayMs), Timeout.InfiniteTimeSpan);
        }
        
        /// <summary>
        /// 停止重连定时器。
        /// </summary>
        private void StopReconnectTimer()
        {
            _reconnectTimer?.Dispose();
            _reconnectTimer = null;
        }
        
        /// <summary>
        /// 尝试执行重连。
        /// </summary>
        private async Task TryReconnectAsync()
        {
            if (Config == null || Status == DeviceStatus.Connected)
            {
                return;
            }
            
            _reconnectAttempts++;
            Console.WriteLine($"[YokonexEMS] 尝试重连 (第 {_reconnectAttempts}/{MaxReconnectAttempts} 次)");
            
            try
            {
                await ConnectAsync(Config, CancellationToken.None);
                Console.WriteLine("[YokonexEMS] 重连成功");
                _reconnectAttempts = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[YokonexEMS] 重连失败: {ex.Message}");
                
                if (_reconnectAttempts < MaxReconnectAttempts)
                {
                    StartReconnectTimer();
                }
                else
                {
                    Console.WriteLine("[YokonexEMS] 重连失败，已达到最大重连次数");
                    UpdateStatus(DeviceStatus.Error);
                }
            }
        }
        
        #endregion

        #endregion

        private byte GetQueryCommand()
        {
            return ProtocolGeneration == YokonexProtocolGeneration.EmsV2_0
                ? YokonexEmsProtocol.CmdQueryV2
                : YokonexEmsProtocol.CmdQueryV1;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                StopBatteryPolling();
                StopReconnectTimer();
                _transport.StateChanged -= OnTransportStateChanged;
                _transport.DataReceived -= OnDataReceived;
                _transport.Dispose();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }
}
