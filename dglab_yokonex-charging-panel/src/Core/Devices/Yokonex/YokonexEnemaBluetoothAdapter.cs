using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ChargingPanel.Core.Bluetooth;
using ChargingPanel.Core.Devices.DGLab;

namespace ChargingPanel.Core.Devices.Yokonex
{
    /// <summary>
    /// 役次元灌肠器蓝牙协议常量
    /// 基于 TDL_YISKJ-003 协议规范 V1.0
    /// </summary>
    public static class YokonexEnemaProtocol
    {
        /// <summary>
        /// 服务 UUID (用于设备过滤)
        /// </summary>
        public static readonly Guid ServiceUuid = Guid.Parse("0000ffb0-0000-1000-8000-00805f9b34fb");
        
        /// <summary>
        /// 写入特性 UUID (APP -> 设备)
        /// </summary>
        public static readonly Guid WriteCharacteristic = Guid.Parse("0000ffb1-0000-1000-8000-00805f9b34fb");
        
        /// <summary>
        /// 通知特性 UUID (设备 -> APP)
        /// </summary>
        public static readonly Guid NotifyCharacteristic = Guid.Parse("0000ffb2-0000-1000-8000-00805f9b34fb");
        
        /// <summary>
        /// AES-128 加密密钥 (16字节)
        /// 来源: 协议文档 TDL_YISKJ-003_协议规范-V1.0 image-4.png
        /// </summary>
        public static readonly byte[] AesKey = new byte[] 
        { 
            0x0F, 0x61, 0x38, 0x2B, 0xC3, 0x9C, 0x4F, 0xA5, 
            0x47, 0x67, 0x47, 0x80, 0x8A, 0xB9, 0x32, 0x10
        };
        
        /// <summary>
        /// 消息固定长度 (16字节)
        /// </summary>
        public const int MessageLength = 16;

        /// <summary>
        /// 文档帧头: BF 0F
        /// </summary>
        public const byte FrameHeader0 = 0xBF;
        public const byte FrameHeader1 = 0x0F;

        /// <summary>
        /// 文档帧类型
        /// </summary>
        public const byte FrameTypeControl = 0xA0;
        public const byte FrameTypeQuery = 0xB0;
        public const byte FrameTypeReport = 0x35;
        
        // 命令字节
        public const byte CmdPeristalticPump = 0x01;  // 蠕动泵控制
        public const byte CmdWaterPump = 0x02;        // 抽水泵控制
        public const byte CmdPause = 0x03;            // 暂停工作
        public const byte CmdQueryStatus = 0x04;      // 查询工作状态
        public const byte CmdReportStatus = 0x84;     // 上报工作状态
        public const byte CmdReportPressure = 0x85;   // 上报压力值
        public const byte CmdGetBattery = 0x06;       // 获取电量
        public const byte CmdReportBattery = 0x86;    // 上报电量
    }

    /// <summary>
    /// 蠕动泵状态
    /// </summary>
    public enum PeristalticPumpState
    {
        Stop = 0x00,
        Forward = 0x01,   // 正转
        Reverse = 0x02    // 反转
    }

    /// <summary>
    /// 抽水泵状态
    /// </summary>
    public enum WaterPumpState
    {
        Stop = 0x00,
        Forward = 0x01    // 正转
    }

    /// <summary>
    /// 灌肠器工作状态
    /// </summary>
    public class EnemaDeviceStatus
    {
        public PeristalticPumpState PeristalticPumpState { get; set; }
        public WaterPumpState WaterPumpState { get; set; }
        public int PressureA { get; set; }
        public int PressureB { get; set; }
        public int BatteryLevel { get; set; }
    }

    /// <summary>
    /// 役次元灌肠器蓝牙适配器
    /// 支持蠕动泵和抽水泵控制，带 AES-128 ECB 加密
    /// </summary>
    public class YokonexEnemaBluetoothAdapter : IDevice, IYokonexEnemaDevice
    {
        private readonly IBluetoothTransport _transport;
        private readonly Aes _aes;
        private readonly Random _random = new();
        private bool _isConnected;
        private bool _disposed;
        
        private readonly EnemaDeviceStatus _enemaStatus = new();
        private int _batteryLevel = 100;
        private int _limitA = 100;
        private int _limitB = 100;
        private int _vibrationStrength;
        
        // 重连和电量轮询
        private Timer? _batteryTimer;
        private Timer? _reconnectTimer;
        private int _reconnectAttempts;
        private const int MaxReconnectAttempts = 5;
        private const int ReconnectDelayMs = 3000;

        // IDevice 属性
        public string Id { get; }
        public string Name { get; set; }
        public DeviceType Type => DeviceType.Yokonex;
        public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;
        public DeviceState State => GetState();
        public ConnectionConfig? Config { get; private set; }
        public YokonexProtocolGeneration ProtocolGeneration { get; }

        // 额外属性
        public string MacAddress => _transport.MacAddress;
        private bool IsConnected => _isConnected && _transport.State == BleConnectionState.Connected;
        
        // IDevice 事件
#pragma warning disable CS0067 // StrengthChanged 为接口兼容保留
        public event EventHandler<DeviceStatus>? StatusChanged;
        public event EventHandler<StrengthInfo>? StrengthChanged;
        public event EventHandler<int>? BatteryChanged;
        public event EventHandler<Exception>? ErrorOccurred;
#pragma warning restore CS0067

        // 额外事件
        public event EventHandler<(int a, int b)>? PressureChanged;
        public event EventHandler<EnemaDeviceStatus>? EnemaStatusChanged;
        
        // IYokonexEnemaDevice 事件
        public event EventHandler<bool>? InjectionStateChanged;
        
        // IYokonexDevice 属性
        public YokonexDeviceType YokonexType => YokonexDeviceType.Enema;

        public YokonexEnemaBluetoothAdapter(
            IBluetoothTransport transport,
            YokonexProtocolGeneration generation = YokonexProtocolGeneration.EnemaV1_0,
            string? id = null,
            string? name = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _transport.StateChanged += OnTransportStateChanged;
            _transport.DataReceived += OnDataReceived;
            ProtocolGeneration = generation == YokonexProtocolGeneration.Auto
                ? YokonexProtocolGeneration.EnemaV1_0
                : generation;
            Id = id ?? $"yc_enema_{Guid.NewGuid():N}".Substring(0, 20);
            Name = name ?? "役次元灌肠器";
            
            // 初始化 AES-128 ECB
            _aes = Aes.Create();
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;  // 我们手动处理填充
            _aes.Key = YokonexEnemaProtocol.AesKey;
        }

        /// <summary>
        /// 扫描役次元灌肠器设备
        /// </summary>
        public async Task<BleDeviceInfo[]> ScanDevicesAsync(int timeoutMs = 10000)
        {
            return await _transport.ScanAsync(YokonexEnemaProtocol.ServiceUuid, null, timeoutMs);
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
                    YokonexEnemaProtocol.ServiceUuid,
                    YokonexEnemaProtocol.NotifyCharacteristic);
                UpdateStatus(DeviceStatus.Connected);
                
                // 启动电量轮询
                StartBatteryPolling();
                
                // 重置重连计数
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
        /// 断开连接
        /// </summary>
        public async Task DisconnectAsync()
        {
            StopBatteryPolling();
            StopReconnectTimer();
            await _transport.DisconnectAsync();
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
            
            Console.WriteLine("[YokonexEnema] 手动触发重连");
            _reconnectAttempts = 0;
            await TryReconnectAsync();
        }

        /// <summary>
        /// 控制蠕动泵
        /// </summary>
        /// <param name="state">工作状态：停止/正转/反转</param>
        /// <param name="durationSeconds">工作时间（秒），0-65535</param>
        public async Task SetPeristalticPumpAsync(PeristalticPumpState state, int durationSeconds = 0)
        {
            durationSeconds = Math.Clamp(durationSeconds, 0, 0xFFFF);
            
            // 文档帧: BF 0F A0 + cmd + payload
            var plaintext = CreateFrame(YokonexEnemaProtocol.FrameTypeControl, YokonexEnemaProtocol.CmdPeristalticPump);
            plaintext[4] = (byte)state;
            plaintext[5] = (byte)((durationSeconds >> 8) & 0xFF);
            plaintext[6] = (byte)(durationSeconds & 0xFF);
            FillRandom(plaintext, 7, 9);

            await SendEncryptedCommandAsync(plaintext);
            
            _enemaStatus.PeristalticPumpState = state;
            EnemaStatusChanged?.Invoke(this, _enemaStatus);
        }

        /// <summary>
        /// 控制抽水泵
        /// </summary>
        /// <param name="state">工作状态：停止/正转</param>
        /// <param name="durationSeconds">工作时间（秒），0-65535</param>
        public async Task SetWaterPumpAsync(WaterPumpState state, int durationSeconds = 0)
        {
            durationSeconds = Math.Clamp(durationSeconds, 0, 0xFFFF);
            
            var plaintext = CreateFrame(YokonexEnemaProtocol.FrameTypeControl, YokonexEnemaProtocol.CmdWaterPump);
            plaintext[4] = (byte)state;
            plaintext[5] = (byte)((durationSeconds >> 8) & 0xFF);
            plaintext[6] = (byte)(durationSeconds & 0xFF);
            FillRandom(plaintext, 7, 9);

            await SendEncryptedCommandAsync(plaintext);
            
            _enemaStatus.WaterPumpState = state;
            EnemaStatusChanged?.Invoke(this, _enemaStatus);
        }

        /// <summary>
        /// 暂停所有工作（蠕动泵和抽水泵都停止）
        /// </summary>
        public async Task PauseAllAsync()
        {
            var plaintext = CreateFrame(YokonexEnemaProtocol.FrameTypeControl, YokonexEnemaProtocol.CmdPause);
            FillRandom(plaintext, 4, 12);

            await SendEncryptedCommandAsync(plaintext);
            
            _enemaStatus.PeristalticPumpState = PeristalticPumpState.Stop;
            _enemaStatus.WaterPumpState = WaterPumpState.Stop;
            EnemaStatusChanged?.Invoke(this, _enemaStatus);
        }

        /// <summary>
        /// 查询工作状态
        /// </summary>
        public async Task QueryStatusAsync()
        {
            var plaintext = CreateFrame(YokonexEnemaProtocol.FrameTypeQuery, YokonexEnemaProtocol.CmdQueryStatus);
            FillRandom(plaintext, 4, 12);

            await SendEncryptedCommandAsync(plaintext);
        }

        /// <summary>
        /// 获取电量
        /// </summary>
        public async Task QueryBatteryAsync()
        {
            var plaintext = CreateFrame(YokonexEnemaProtocol.FrameTypeQuery, YokonexEnemaProtocol.CmdGetBattery);
            FillRandom(plaintext, 4, 12);

            await SendEncryptedCommandAsync(plaintext);
        }

        /// <summary>
        /// 获取当前灌肠器状态
        /// </summary>
        public EnemaDeviceStatus GetEnemaStatus() => _enemaStatus;

        #region IYokonexEnemaDevice 实现
        
        /// <summary>
        /// 是否正在注入
        /// </summary>
        public bool IsInjecting => _enemaStatus.PeristalticPumpState != PeristalticPumpState.Stop;
        
        /// <summary>
        /// 当前注入强度 (0-100)
        /// </summary>
        public int InjectionStrength { get; private set; }
        
        /// <summary>
        /// 当前振动强度 (灌肠器无振动功能，始终为0)
        /// </summary>
        public int VibrationStrength => _vibrationStrength;
        
        /// <summary>
        /// 设置注入强度
        /// </summary>
        public Task SetInjectionStrengthAsync(int strength)
        {
            InjectionStrength = Math.Clamp(strength, 0, 100);
            RaiseStrengthChanged();
            return Task.CompletedTask;
        }
        
        /// <summary>
        /// 开始注入
        /// </summary>
        public async Task StartInjectionAsync()
        {
            // 根据强度计算持续时间 (强度越高，持续时间越长)
            int duration = InjectionStrength > 0 ? (InjectionStrength * 60 / 100) : 10; // 最长60秒
            await SetPeristalticPumpAsync(PeristalticPumpState.Forward, duration);
            InjectionStateChanged?.Invoke(this, true);
        }
        
        /// <summary>
        /// 停止注入
        /// </summary>
        public async Task StopInjectionAsync()
        {
            await PauseAllAsync();
            InjectionStrength = 0;
            InjectionStateChanged?.Invoke(this, false);
            RaiseStrengthChanged();
        }
        
        /// <summary>
        /// 设置振动强度 (灌肠器不支持振动)
        /// </summary>
        public Task SetVibrationStrengthAsync(int strength)
        {
            // 灌肠器没有振动功能
            _vibrationStrength = 0;
            RaiseStrengthChanged();
            return Task.CompletedTask;
        }
        
        #endregion

        #region IDevice 实现

        public async Task SetStrengthAsync(Channel channel, int value, StrengthMode mode = StrengthMode.Set)
        {
            // 统一强度语义：A/AB 映射注入强度，B 预留（当前无振动功能）
            if (!IsConnected)
            {
                throw new InvalidOperationException("设备未连接");
            }

            var safeValue = Math.Clamp(value, 0, 100);

            if (channel == Channel.B)
            {
                await SetVibrationStrengthAsync(safeValue);
                return;
            }

            var target = ApplyStrengthMode(InjectionStrength, safeValue, mode);
            await SetInjectionStrengthAsync(target);
            if (target <= 0)
            {
                await StopInjectionAsync();
            }
            else
            {
                await StartInjectionAsync();
            }
        }

        public Task SendWaveformAsync(Channel channel, WaveformData data)
        {
            // 灌肠器不支持波形
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
                    ChannelA = InjectionStrength,
                    ChannelB = VibrationStrength,
                    LimitA = _limitA,
                    LimitB = _limitB
                },
                BatteryLevel = _batteryLevel,
                LastUpdate = DateTime.UtcNow
            };
        }

        private static int ApplyStrengthMode(int current, int value, StrengthMode mode)
        {
            return mode switch
            {
                StrengthMode.Increase => Math.Clamp(current + value, 0, 100),
                StrengthMode.Decrease => Math.Clamp(current - value, 0, 100),
                _ => Math.Clamp(value, 0, 100)
            };
        }

        private void RaiseStrengthChanged()
        {
            StrengthChanged?.Invoke(this, new StrengthInfo
            {
                ChannelA = InjectionStrength,
                ChannelB = VibrationStrength,
                LimitA = _limitA,
                LimitB = _limitB
            });
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

        #region AES 加密/解密

        /// <summary>
        /// AES-128 ECB 加密
        /// </summary>
        private byte[] Encrypt(byte[] plaintext)
        {
            if (plaintext.Length != YokonexEnemaProtocol.MessageLength)
            {
                throw new ArgumentException($"明文长度必须为 {YokonexEnemaProtocol.MessageLength} 字节");
            }

            using var encryptor = _aes.CreateEncryptor();
            return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        }

        /// <summary>
        /// AES-128 ECB 解密
        /// </summary>
        private byte[] Decrypt(byte[] ciphertext)
        {
            if (ciphertext.Length != YokonexEnemaProtocol.MessageLength)
            {
                throw new ArgumentException($"密文长度必须为 {YokonexEnemaProtocol.MessageLength} 字节");
            }

            using var decryptor = _aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        }

        #endregion

        #region 私有方法

        private async Task SendEncryptedCommandAsync(byte[] plaintext)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("设备未连接");
            }

            var ciphertext = Encrypt(plaintext);
            
            Console.WriteLine($"[YokonexEnema] 发送明文: {BitConverter.ToString(plaintext)}");
            Console.WriteLine($"[YokonexEnema] 发送密文: {BitConverter.ToString(ciphertext)}");

            await _transport.WriteWithoutResponseAsync(
                YokonexEnemaProtocol.ServiceUuid,
                YokonexEnemaProtocol.WriteCharacteristic,
                ciphertext);
        }

        private void FillRandom(byte[] buffer, int offset, int count)
        {
            for (int i = offset; i < offset + count && i < buffer.Length; i++)
            {
                buffer[i] = (byte)_random.Next(256);
            }
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
                // 触发自动重连
                StartReconnectTimer();
            }
        }
        
        #region 电量轮询和自动重连
        
        /// <summary>
        /// 启动电量轮询定时器
        /// </summary>
        private void StartBatteryPolling()
        {
            StopBatteryPolling();
            
            // 每 60 秒轮询一次电量
            _batteryTimer = new Timer(async _ =>
            {
                if (!IsConnected) return;
                
                try
                {
                    await QueryBatteryAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[YokonexEnema] 电量轮询失败: {ex.Message}");
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
                Console.WriteLine($"[YokonexEnema] 已达到最大重连次数 ({MaxReconnectAttempts})，停止重连");
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
            Console.WriteLine($"[YokonexEnema] 尝试重连 (第 {_reconnectAttempts}/{MaxReconnectAttempts} 次)");
            
            try
            {
                await ConnectAsync(Config, CancellationToken.None);
                Console.WriteLine("[YokonexEnema] 重连成功");
                _reconnectAttempts = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[YokonexEnema] 重连失败: {ex.Message}");
                
                if (_reconnectAttempts < MaxReconnectAttempts)
                {
                    StartReconnectTimer();
                }
                else
                {
                    Console.WriteLine("[YokonexEnema] 重连失败，已达到最大重连次数");
                    UpdateStatus(DeviceStatus.Error);
                }
            }
        }
        
        #endregion

        private void OnDataReceived(object? sender, BleDataReceivedEventArgs e)
        {
            if (e.CharacteristicUuid != YokonexEnemaProtocol.NotifyCharacteristic)
            {
                return;
            }

            if (e.Data.Length != YokonexEnemaProtocol.MessageLength)
            {
                Console.WriteLine($"[YokonexEnema] 收到非法长度数据: {e.Data.Length}");
                return;
            }

            try
            {
                var plaintext = Decrypt(e.Data);
                Console.WriteLine($"[YokonexEnema] 收到密文: {BitConverter.ToString(e.Data)}");
                Console.WriteLine($"[YokonexEnema] 解密明文: {BitConverter.ToString(plaintext)}");

                ParseResponse(plaintext);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[YokonexEnema] 解密失败: {ex.Message}");
            }
        }

        private void ParseResponse(byte[] plaintext)
        {
            var (cmd, payloadOffset) = ExtractFrameCommand(plaintext);

            switch (cmd)
            {
                case YokonexEnemaProtocol.CmdReportStatus:
                    if (plaintext.Length < payloadOffset + 2)
                        return;
                    _enemaStatus.PeristalticPumpState = (PeristalticPumpState)plaintext[payloadOffset];
                    _enemaStatus.WaterPumpState = (WaterPumpState)plaintext[payloadOffset + 1];
                    EnemaStatusChanged?.Invoke(this, _enemaStatus);
                    Console.WriteLine($"[YokonexEnema] 工作状态: 蠕动泵={_enemaStatus.PeristalticPumpState}, 抽水泵={_enemaStatus.WaterPumpState}");
                    break;

                case YokonexEnemaProtocol.CmdReportPressure:
                    if (plaintext.Length < payloadOffset + 4)
                        return;
                    _enemaStatus.PressureA = (plaintext[payloadOffset] << 8) | plaintext[payloadOffset + 1];
                    _enemaStatus.PressureB = (plaintext[payloadOffset + 2] << 8) | plaintext[payloadOffset + 3];
                    PressureChanged?.Invoke(this, (_enemaStatus.PressureA, _enemaStatus.PressureB));
                    break;

                case YokonexEnemaProtocol.CmdReportBattery:
                    if (plaintext.Length < payloadOffset + 1)
                        return;
                    _batteryLevel = plaintext[payloadOffset];
                    _enemaStatus.BatteryLevel = _batteryLevel;
                    BatteryChanged?.Invoke(this, _batteryLevel);
                    Console.WriteLine($"[YokonexEnema] 电量: {_batteryLevel}%");
                    break;

                default:
                    Console.WriteLine($"[YokonexEnema] 未知响应命令: {cmd:X2}");
                    break;
            }
        }

        private static byte[] CreateFrame(byte frameType, byte cmd)
        {
            var plaintext = new byte[YokonexEnemaProtocol.MessageLength];
            plaintext[0] = YokonexEnemaProtocol.FrameHeader0;
            plaintext[1] = YokonexEnemaProtocol.FrameHeader1;
            plaintext[2] = frameType;
            plaintext[3] = cmd;
            return plaintext;
        }

        private static (byte cmd, int payloadOffset) ExtractFrameCommand(byte[] plaintext)
        {
            // 文档帧: BF 0F {A0/B0/35} CMD ...
            if (plaintext.Length >= 4 &&
                plaintext[0] == YokonexEnemaProtocol.FrameHeader0 &&
                plaintext[1] == YokonexEnemaProtocol.FrameHeader1)
            {
                return (plaintext[3], 4);
            }

            // 兼容旧实现: CMD 在首字节
            return (plaintext[0], 1);
        }

        #endregion

        public void Dispose()
        {
            if (!_disposed)
            {
                StopBatteryPolling();
                StopReconnectTimer();
                _transport.StateChanged -= OnTransportStateChanged;
                _transport.DataReceived -= OnDataReceived;
                _aes.Dispose();
                _transport.Dispose();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }
}
