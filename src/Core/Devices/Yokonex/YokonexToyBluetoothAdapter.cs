using System;
using System.Threading;
using System.Threading.Tasks;
using ChargingPanel.Core.Bluetooth;
using ChargingPanel.Core.Devices.DGLab;

namespace ChargingPanel.Core.Devices.Yokonex
{
    /// <summary>
    /// 役次元跳蛋/飞机杯蓝牙协议常量
    /// 基于 SKJ_TOY_BLE 通信协议 V1.1
    /// </summary>
    public static class YokonexToyProtocol
    {
        /// <summary>
        /// 服务 UUID (用于设备过滤)
        /// </summary>
        public static readonly Guid ServiceUuid = Guid.Parse("0000ff40-0000-1000-8000-00805f9b34fb");
        
        /// <summary>
        /// 写入特性 UUID (WRITE_WITHOUT_RESPONSE)
        /// </summary>
        public static readonly Guid WriteCharacteristic = Guid.Parse("0000ff41-0000-1000-8000-00805f9b34fb");
        
        /// <summary>
        /// 通知特性 UUID (NOTIFY)
        /// </summary>
        public static readonly Guid NotifyCharacteristic = Guid.Parse("0000ff42-0000-1000-8000-00805f9b34fb");
        
        /// <summary>
        /// 包头
        /// </summary>
        public const byte PacketHeader = 0x35;
        
        /// <summary>
        /// 命令字 - 设备信息查询
        /// </summary>
        public const byte CmdQueryDeviceInfo = 0x10;
        
        /// <summary>
        /// 命令字 - 固定模式控制
        /// </summary>
        public const byte CmdFixedModeControl = 0x11;
        
        /// <summary>
        /// 命令字 - 速率控制
        /// </summary>
        public const byte CmdSpeedControl = 0x12;
        
        /// <summary>
        /// 命令字 - 电量上报
        /// </summary>
        public const byte CmdBatteryReport = 0x13;
        
        /// <summary>
        /// 马达选择位掩码
        /// </summary>
        public const byte MotorA = 0x01;
        public const byte MotorB = 0x02;
        public const byte MotorC = 0x04;
        public const byte MotorAB = 0x03;
        public const byte MotorABC = 0x07;
        
        /// <summary>
        /// 最大力度等级
        /// </summary>
        public const int MaxPowerLevel = 20;
    }

    /// <summary>
    /// 设备信息
    /// </summary>
    public class ToyDeviceInfo
    {
        public int ProductId { get; set; }       // 产品型号 ID (1-255)
        public int Version { get; set; }         // 产品版本号 (1-255)
        public int MotorAModeCount { get; set; } // A 马达模式数量 (0=无该马达)
        public int MotorBModeCount { get; set; } // B 马达模式数量
        public int MotorCModeCount { get; set; } // C 马达模式数量
        
        public bool HasMotorA => MotorAModeCount > 0;
        public bool HasMotorB => MotorBModeCount > 0;
        public bool HasMotorC => MotorCModeCount > 0;
    }

    /// <summary>
    /// 马达状态
    /// </summary>
    public class ToyMotorState
    {
        public int Mode { get; set; }        // 当前模式 (0=关闭, 1-N=固定模式)
        public int PowerLevel { get; set; }  // 力度等级 (0-20)
    }

    /// <summary>
    /// 役次元跳蛋/飞机杯蓝牙适配器
    /// 支持最多3个马达的独立控制
    /// </summary>
    public class YokonexToyBluetoothAdapter : IDevice
    {
        private readonly IBluetoothTransport _transport;
        private bool _isConnected;
        private bool _disposed;
        
        // 设备信息
        private ToyDeviceInfo? _deviceInfo;
        
        // 马达状态
        private readonly ToyMotorState _motorA = new();
        private readonly ToyMotorState _motorB = new();
        private readonly ToyMotorState _motorC = new();
        private int _batteryLevel = 100;
        private int _limitA = 100;
        private int _limitB = 100;

        // IDevice 属性
        public string Id { get; }
        public string Name { get; set; }
        public DeviceType Type => DeviceType.Yokonex;
        public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;
        public DeviceState State => GetState();
        public ConnectionConfig? Config { get; private set; }

        // 额外属性
        public ToyDeviceInfo? DeviceInfo => _deviceInfo;
        private bool IsConnected => _isConnected && _transport.State == BleConnectionState.Connected;
        
        // IDevice 事件
        public event EventHandler<DeviceStatus>? StatusChanged;
        public event EventHandler<StrengthInfo>? StrengthChanged;
        public event EventHandler<int>? BatteryChanged;
        public event EventHandler<Exception>? ErrorOccurred;

        // 额外事件
        public event EventHandler<ToyDeviceInfo>? DeviceInfoReceived;
        public event EventHandler<(int a, int b, int c)>? PowerLevelChanged;

        public YokonexToyBluetoothAdapter(IBluetoothTransport transport, string? id = null, string? name = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _transport.StateChanged += OnTransportStateChanged;
            _transport.DataReceived += OnDataReceived;
            Id = id ?? $"yc_toy_{Guid.NewGuid():N}".Substring(0, 20);
            Name = name ?? "役次元跳蛋/飞机杯";
        }

        /// <summary>
        /// 扫描役次元跳蛋/飞机杯设备
        /// </summary>
        public async Task<BleDeviceInfo[]> ScanDevicesAsync(int timeoutMs = 10000)
        {
            return await _transport.ScanAsync(YokonexToyProtocol.ServiceUuid, null, timeoutMs);
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
                    YokonexToyProtocol.ServiceUuid,
                    YokonexToyProtocol.NotifyCharacteristic);
                
                // 连接后查询设备信息
                await QueryDeviceInfoAsync();
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
        /// 查询设备信息
        /// </summary>
        public async Task QueryDeviceInfoAsync()
        {
            // 字节1: 包头 0x35
            // 字节2: 命令字 0x10
            // 字节3: 校验和
            var data = new byte[3];
            data[0] = YokonexToyProtocol.PacketHeader;
            data[1] = YokonexToyProtocol.CmdQueryDeviceInfo;
            data[2] = CalculateChecksum(data, 0, 2);

            await SendCommandAsync(data);
        }

        /// <summary>
        /// 设置固定模式
        /// </summary>
        /// <param name="motorMask">马达选择: 0x01=A, 0x02=B, 0x04=C, 0x03=AB, 0x07=ABC</param>
        /// <param name="mode">模式: 0=关闭, 1-N=固定模式</param>
        public async Task SetFixedModeAsync(byte motorMask, int mode)
        {
            // 验证模式范围
            mode = Math.Max(0, mode);
            if (_deviceInfo != null)
            {
                // 根据设备信息限制最大模式
                int maxMode = 0;
                if ((motorMask & YokonexToyProtocol.MotorA) != 0)
                    maxMode = Math.Max(maxMode, _deviceInfo.MotorAModeCount);
                if ((motorMask & YokonexToyProtocol.MotorB) != 0)
                    maxMode = Math.Max(maxMode, _deviceInfo.MotorBModeCount);
                if ((motorMask & YokonexToyProtocol.MotorC) != 0)
                    maxMode = Math.Max(maxMode, _deviceInfo.MotorCModeCount);
                mode = Math.Min(mode, maxMode);
            }

            // 字节1: 包头 0x35
            // 字节2: 命令字 0x11
            // 字节3: 马达选择
            // 字节4: 模式选择
            // 字节5: 校验和
            var data = new byte[5];
            data[0] = YokonexToyProtocol.PacketHeader;
            data[1] = YokonexToyProtocol.CmdFixedModeControl;
            data[2] = motorMask;
            data[3] = (byte)mode;
            data[4] = CalculateChecksum(data, 0, 4);

            await SendCommandAsync(data);
            
            // 更新本地状态
            if ((motorMask & YokonexToyProtocol.MotorA) != 0)
                _motorA.Mode = mode;
            if ((motorMask & YokonexToyProtocol.MotorB) != 0)
                _motorB.Mode = mode;
            if ((motorMask & YokonexToyProtocol.MotorC) != 0)
                _motorC.Mode = mode;
        }

        /// <summary>
        /// 设置速率（力度）
        /// 适用于手势模式、语音音乐模式、自定义模式等
        /// </summary>
        /// <param name="powerA">马达A力度: 0=关闭, 1-20=力度等级</param>
        /// <param name="powerB">马达B力度: 0=关闭, 1-20=力度等级</param>
        /// <param name="powerC">马达C力度: 0=关闭, 1-20=力度等级</param>
        public async Task SetSpeedAsync(int powerA, int powerB, int powerC)
        {
            // 限制力度范围
            powerA = Math.Clamp(powerA, 0, YokonexToyProtocol.MaxPowerLevel);
            powerB = Math.Clamp(powerB, 0, YokonexToyProtocol.MaxPowerLevel);
            powerC = Math.Clamp(powerC, 0, YokonexToyProtocol.MaxPowerLevel);

            // 字节1: 包头 0x35
            // 字节2: 命令字 0x12
            // 字节3: 马达A力度
            // 字节4: 马达B力度
            // 字节5: 马达C力度
            // 字节6: 校验和
            var data = new byte[6];
            data[0] = YokonexToyProtocol.PacketHeader;
            data[1] = YokonexToyProtocol.CmdSpeedControl;
            data[2] = (byte)powerA;
            data[3] = (byte)powerB;
            data[4] = (byte)powerC;
            data[5] = CalculateChecksum(data, 0, 5);

            await SendCommandAsync(data);
            
            // 更新本地状态
            _motorA.PowerLevel = powerA;
            _motorB.PowerLevel = powerB;
            _motorC.PowerLevel = powerC;
            
            PowerLevelChanged?.Invoke(this, (powerA, powerB, powerC));
            StrengthChanged?.Invoke(this, new StrengthInfo
            {
                ChannelA = powerA * 5,
                ChannelB = powerB * 5,
                LimitA = _limitA,
                LimitB = _limitB
            });
        }

        /// <summary>
        /// 关闭所有马达
        /// </summary>
        public async Task StopAllAsync()
        {
            await SetSpeedAsync(0, 0, 0);
            await SetFixedModeAsync(YokonexToyProtocol.MotorABC, 0);
        }

        /// <summary>
        /// 获取马达状态
        /// </summary>
        public (ToyMotorState a, ToyMotorState b, ToyMotorState c) GetMotorStates()
        {
            return (_motorA, _motorB, _motorC);
        }

        #region IDevice 实现

        public async Task SetStrengthAsync(Channel channel, int value, StrengthMode mode = StrengthMode.Set)
        {
            // 将强度映射到力度等级 (0-100 -> 0-20)
            int power = (int)Math.Round(value / 5.0);
            
            switch (channel)
            {
                case Channel.A:
                    await SetSpeedAsync(power, _motorB.PowerLevel, _motorC.PowerLevel);
                    break;
                case Channel.B:
                    await SetSpeedAsync(_motorA.PowerLevel, power, _motorC.PowerLevel);
                    break;
                case Channel.AB:
                    await SetSpeedAsync(power, power, _motorC.PowerLevel);
                    break;
            }
        }

        public Task SendWaveformAsync(Channel channel, WaveformData data)
        {
            // 跳蛋/飞机杯使用固定模式，不支持自定义波形
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
                    ChannelA = _motorA.PowerLevel * 5,
                    ChannelB = _motorB.PowerLevel * 5,
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

            Console.WriteLine($"[YokonexToy] 发送命令: {BitConverter.ToString(data)}");

            await _transport.WriteWithoutResponseAsync(
                YokonexToyProtocol.ServiceUuid,
                YokonexToyProtocol.WriteCharacteristic,
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
            if (e.CharacteristicUuid != YokonexToyProtocol.NotifyCharacteristic)
            {
                return;
            }

            var data = e.Data;
            if (data.Length < 3 || data[0] != YokonexToyProtocol.PacketHeader)
            {
                return;
            }

            var cmd = data[1];
            Console.WriteLine($"[YokonexToy] 收到响应: 命令={cmd:X2}, 数据={BitConverter.ToString(data)}");

            switch (cmd)
            {
                case YokonexToyProtocol.CmdQueryDeviceInfo:
                    ParseDeviceInfo(data);
                    break;
                    
                case YokonexToyProtocol.CmdBatteryReport:
                    ParseBatteryReport(data);
                    break;
            }
        }

        private void ParseDeviceInfo(byte[] data)
        {
            if (data.Length < 10)
            {
                Console.WriteLine("[YokonexToy] 设备信息数据长度不足");
                return;
            }

            // 字节3: 产品型号 ID
            // 字节4: 产品版本号
            // 字节5: A马达模式数量
            // 字节6: B马达模式数量
            // 字节7: C马达模式数量
            // 字节8-9: 预留
            // 字节10: 校验和

            _deviceInfo = new ToyDeviceInfo
            {
                ProductId = data[2],
                Version = data[3],
                MotorAModeCount = data[4],
                MotorBModeCount = data[5],
                MotorCModeCount = data[6]
            };

            Console.WriteLine($"[YokonexToy] 设备信息: 型号ID={_deviceInfo.ProductId}, " +
                $"版本={_deviceInfo.Version}, " +
                $"马达A模式数={_deviceInfo.MotorAModeCount}, " +
                $"马达B模式数={_deviceInfo.MotorBModeCount}, " +
                $"马达C模式数={_deviceInfo.MotorCModeCount}");

            DeviceInfoReceived?.Invoke(this, _deviceInfo);
        }

        private void ParseBatteryReport(byte[] data)
        {
            if (data.Length < 5)
            {
                return;
            }

            // 字节3: 上报类型 (0x01=电池电量)
            // 字节4: 电量值 0-100
            if (data[2] == 0x01)
            {
                _batteryLevel = data[3];
                Console.WriteLine($"[YokonexToy] 电量: {_batteryLevel}%");
                BatteryChanged?.Invoke(this, _batteryLevel);
            }
        }

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
