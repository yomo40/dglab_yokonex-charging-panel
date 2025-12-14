using System;
using System.Threading;
using System.Threading.Tasks;
using ChargingPanel.Core.Bluetooth;
using ChargingPanel.Core.Devices.DGLab;
using Serilog;

namespace ChargingPanel.Core.Devices.Yokonex
{
    /// <summary>
    /// 役次元电击器蓝牙协议常量
    /// 基于 YSKJ_EMS_BLE 通信协议 V1.6
    /// </summary>
    public static class YokonexEmsProtocol
    {
        /// <summary>
        /// 服务 UUID (用于设备过滤)
        /// </summary>
        public static readonly Guid ServiceUuid = Guid.Parse("0000ff30-0000-1000-8000-00805f9b34fb");
        
        /// <summary>
        /// 写入特性 UUID (WRITE_WITHOUT_RESPONSE)
        /// </summary>
        public static readonly Guid WriteCharacteristic = Guid.Parse("0000ff31-0000-1000-8000-00805f9b34fb");
        
        /// <summary>
        /// 通知特性 UUID (NOTIFY)
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
        /// 通道号
        /// </summary>
        public const byte ChannelA = 0x01;
        public const byte ChannelB = 0x02;
        public const byte ChannelAB = 0x03;
        
        /// <summary>
        /// 最大强度等级 (276级: 0x01-0x114)
        /// </summary>
        public const int MaxStrength = 276;
        
        /// <summary>
        /// 固定模式数量 (16种)
        /// </summary>
        public const int FixedModeCount = 16;
        
        /// <summary>
        /// 自定义模式编号
        /// </summary>
        public const byte CustomMode = 0x11;
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
    /// 役次元电击器蓝牙适配器
    /// 支持双通道 EMS 控制、马达控制、计步功能、角度功能
    /// </summary>
    public class YokonexEmsBluetoothAdapter : IDevice, IYokonexEmsDevice
    {
        private readonly IBluetoothTransport _transport;
        private bool _isConnected;
        private bool _disposed;
        
        // 通道状态
        private readonly YokonexEmsChannelState _channelA = new();
        private readonly YokonexEmsChannelState _channelB = new();
        private int _batteryLevel = 100;
        private int _limitA = 100;
        private int _limitB = 100;
        
        // 计步器状态
        private int _stepCount = 0;
        private bool _angleEnabled = false;
        private (float X, float Y, float Z) _currentAngle = (0, 0, 0);
        private (bool A, bool B) _channelConnectionState = (false, false);
        private YokonexMotorState _motorState = YokonexMotorState.Off;

        // IDevice 属性
        public string Id { get; }
        public string Name { get; set; }
        public DeviceType Type => DeviceType.Yokonex;
        public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;
        public DeviceState State => GetState();
        public ConnectionConfig? Config { get; private set; }
        
        // IYokonexDevice 属性
        public YokonexDeviceType YokonexType => YokonexDeviceType.Estim;
        
        // IYokonexEmsDevice 属性
        public int StepCount => _stepCount;
        public (float X, float Y, float Z) CurrentAngle => _currentAngle;
        public (bool ChannelA, bool ChannelB) ChannelConnectionState => _channelConnectionState;

        // IDevice 事件
        public event EventHandler<DeviceStatus>? StatusChanged;
        public event EventHandler<StrengthInfo>? StrengthChanged;
        public event EventHandler<int>? BatteryChanged;
        public event EventHandler<Exception>? ErrorOccurred;

        // IYokonexEmsDevice 事件
        public event EventHandler<int>? StepCountChanged;
        public event EventHandler<(float X, float Y, float Z)>? AngleChanged;
        public event EventHandler<(bool ChannelA, bool ChannelB)>? ChannelConnectionChanged;
        public event EventHandler<YokonexMotorState>? MotorStateChanged;

        public YokonexEmsBluetoothAdapter(IBluetoothTransport transport, string? id = null, string? name = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _transport.StateChanged += OnTransportStateChanged;
            _transport.DataReceived += OnDataReceived;
            Id = id ?? $"yc_ems_{Guid.NewGuid():N}".Substring(0, 20);
            Name = name ?? "役次元电击器";
        }

        /// <summary>
        /// 扫描役次元电击器设备
        /// </summary>
        public async Task<BleDeviceInfo[]> ScanDevicesAsync(int timeoutMs = 10000)
        {
            return await _transport.ScanAsync(YokonexEmsProtocol.ServiceUuid, null, timeoutMs);
        }

        /// <summary>
        /// 连接到设备 (IDevice 接口)
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
            }
            catch (Exception ex)
            {
                UpdateStatus(DeviceStatus.Error);
                ErrorOccurred?.Invoke(this, ex);
                throw;
            }
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public async Task DisconnectAsync()
        {
            await _transport.DisconnectAsync();
        }

        /// <summary>
        /// 设置通道强度
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
            // 参数验证
            strength = Math.Clamp(strength, 0, YokonexEmsProtocol.MaxStrength);
            mode = Math.Clamp(mode, 1, YokonexEmsProtocol.CustomMode);
            frequency = Math.Clamp(frequency, 0, 100);
            pulseTime = Math.Clamp(pulseTime, 0, 100);

            // 构建命令
            // 字节1: 包头 0x35
            // 字节2: 命令字 0x11
            // 字节3: 通道号
            // 字节4: 通道开启状态
            // 字节5-6: 通道强度 (0x01-0x114)
            // 字节7: 通道模式
            // 字节8: 通道频率 (仅自定义模式)
            // 字节9: 脉冲时间 (仅自定义模式)
            // 字节10: 校验和
            
            var data = new byte[10];
            data[0] = YokonexEmsProtocol.PacketHeader;
            data[1] = YokonexEmsProtocol.CmdChannelControl;
            data[2] = channel;
            data[3] = enabled ? (byte)0x01 : (byte)0x00;
            
            // 强度为双字节，高位在前
            data[4] = (byte)((strength >> 8) & 0xFF);
            data[5] = (byte)(strength & 0xFF);
            
            data[6] = (byte)mode;
            data[7] = mode == YokonexEmsProtocol.CustomMode ? (byte)frequency : (byte)0x00;
            data[8] = mode == YokonexEmsProtocol.CustomMode ? (byte)pulseTime : (byte)0x00;
            
            // 计算校验和
            data[9] = CalculateChecksum(data, 0, 9);

            await SendCommandAsync(data);
            
            // 更新本地状态
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
            int strength = channel == Channel.A ? _channelA.Strength : _channelB.Strength;
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
            int strength = channel == Channel.A ? _channelA.Strength : _channelB.Strength;
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

        #region IDevice 实现

        public async Task SetStrengthAsync(Channel channel, int value, StrengthMode mode = StrengthMode.Set)
        {
            // 将 0-100 映射到 0-276
            int mappedValue = (int)(value * 2.76);
            
            switch (channel)
            {
                case Channel.A:
                    await SetChannelAAsync(mappedValue, value > 0);
                    break;
                case Channel.B:
                    await SetChannelBAsync(mappedValue, value > 0);
                    break;
                case Channel.AB:
                    await SetChannelABAsync(mappedValue, value > 0);
                    break;
            }
        }

        public Task SendWaveformAsync(Channel channel, WaveformData data)
        {
            // 电击器使用模式控制，不使用波形
            // 可以通过自定义模式实现类似效果
            return Task.CompletedTask;
        }

        public Task ClearWaveformQueueAsync(Channel channel)
        {
            return Task.CompletedTask;
        }

        public Task SetLimitsAsync(int limitA, int limitB)
        {
            _limitA = limitA;
            _limitB = limitB;
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
                UpdateStatus(DeviceStatus.Connected);
            else if (state == BleConnectionState.Disconnected)
                UpdateStatus(DeviceStatus.Disconnected);
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
            
            // 解析不同类型的响应
            Console.WriteLine($"[YokonexEMS] 收到响应: 命令={cmd:X2}, 数据={BitConverter.ToString(data)}");
        }

        private bool IsConnected => _isConnected && _transport.State == BleConnectionState.Connected;

        #endregion

        public void Dispose()
        {
            if (!_disposed)
            {
                _transport.StateChanged -= OnTransportStateChanged;
                _transport.DataReceived -= OnDataReceived;
                _transport.Dispose();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }
}
