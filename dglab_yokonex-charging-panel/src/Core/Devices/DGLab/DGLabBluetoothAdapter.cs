using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChargingPanel.Core.Bluetooth;
using Serilog;

namespace ChargingPanel.Core.Devices.DGLab;

/// <summary>
/// DG-LAB 钃濈墮鐗堟湰鏋氫妇
/// </summary>
public enum DGLabVersion
{
    /// <summary>V2 鐗堟湰 (D-LAB ESTIM01)</summary>
    V2,
    /// <summary>V3 鐗堟湰 (47L121000)</summary>
    V3,
    /// <summary>V3 鏃犵嚎浼犳劅鍣?(47L120100锛岄鐣?</summary>
    V3WirelessSensor,
    /// <summary>鐖嵃鎸夐挳浼犳劅鍣紙澶栭儴鐢靛帇锛岄鐣欙級</summary>
    PawPrints
}

/// <summary>
/// DG-LAB 钃濈墮閫傞厤鍣ㄣ€?
/// 瑕嗙洊 V2銆乂3锛屼互鍙婇鐣欎腑鐨勪紶鎰熷櫒鍨嬪彿銆?
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

    /// <summary>璁惧鐗堟湰</summary>
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
    private long _lastStrengthCommandAtMs;
    private bool _disposed;
    private int _reconnectAttempts;
    private const int MaxReconnectAttempts = 5;
    private const int ReconnectDelayMs = 3000;
    private const int PendingResponseStaleMs = 180;
    private const int MaxUiStrength = 200;
    private const int MaxV2RawStrength = 2047;
    
    // 褰撳墠姝ｅ湪寰幆鍙戦€佺殑娉㈠舰鐘舵€?
    private ChannelWaveform? _currentWaveformA;
    private ChannelWaveform? _currentWaveformB;
    private byte[]? _currentV2WaveformA;
    private byte[]? _currentV2WaveformB;
    private List<ChannelWaveform>? _waveformSequenceA;
    private List<ChannelWaveform>? _waveformSequenceB;
    private int _waveformSequenceIndexA;
    private int _waveformSequenceIndexB;
    private List<byte[]>? _v2WaveformSequenceA;
    private List<byte[]>? _v2WaveformSequenceB;
    private int _v2WaveformSequenceIndexA;
    private int _v2WaveformSequenceIndexB;
    
    // V3 鐢甸噺鏈嶅姟 UUID锛?x180A/0x1500锛?
    private static readonly Guid BatteryServiceUuid = Guid.Parse("0000180A-0000-1000-8000-00805F9B34FB");
    private static readonly Guid BatteryCharacteristicUuid = Guid.Parse("00001500-0000-1000-8000-00805F9B34FB");

    public DGLabBluetoothAdapter(DGLabVersion version, string? id = null, string? name = null)
    {
        Version = version;
        Id = id ?? $"dglab_bt_{Guid.NewGuid():N}"[..20];
        Name = name ?? $"閮婄嫾 {GetVersionName()}";
    }

    private string GetVersionName() => Version switch
    {
        DGLabVersion.V2 => "V2",
        DGLabVersion.V3 => "V3",
        DGLabVersion.V3WirelessSensor => "V3 无线传感器",
        DGLabVersion.PawPrints => "鐖嵃鎸夐挳",
        _ => "鏈煡"
    };

    /// <summary>
    /// 娉ㄥ叆钃濈墮浼犺緭灞傚苟鎸傛帴浜嬩欢銆?
    /// </summary>
    public void SetTransport(IBluetoothTransport transport)
    {
        _transport = transport;
        _transport.DataReceived += OnDataReceived;
        _transport.StateChanged += OnTransportStateChanged;
    }

    /// <summary>
    /// 鏍规嵁璁惧鐗堟湰杩斿洖鎵弿鐢ㄧ殑鍚嶇О鍓嶇紑銆?
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
            throw new InvalidOperationException("钃濈墮浼犺緭灞傛湭璁剧疆锛岃鍏堣皟鐢?SetTransport()");
        }

        if (string.IsNullOrEmpty(config.Address))
        {
            throw new ArgumentException("钃濈墮鍦板潃涓嶈兘涓虹┖");
        }

        UpdateStatus(DeviceStatus.Connecting);
        Logger.Information("姝ｅ湪杩炴帴 DG-LAB {Version} 钃濈墮璁惧: {Address}", Version, config.Address);

        try
        {
            await _transport.ConnectAsync(config.Address, _cts.Token);

            if (Version == DGLabVersion.V3)
            {
                // V3 璧伴€氱煡鐗瑰緛銆?
                await _transport.SubscribeAsync(GetServiceUuid(), GetNotifyCharacteristicUuid());
                
                UpdateStatus(DeviceStatus.Connected);
                Logger.Information("DG-LAB V3 钃濈墮杩炴帴鎴愬姛");

                // 杩炴帴鍚庡厛涓嬪彂 BF锛屽啓鍏ヨ蒋浠朵晶寮哄害涓婇檺銆?
                await SendBFCommandAsync();
                // 鎷夎捣鐢甸噺閫氱煡銆?
                await SubscribeBatteryAsync();
            }
            else
            {
                // V2 浣跨敤 PWM_AB2锛堣/鍐?閫氱煡锛夋帴鏀跺己搴﹀彉鍖栥€?
                await _transport.SubscribeAsync(GetServiceUuid(), GetV2StrengthCharacteristicUuid());
                
                UpdateStatus(DeviceStatus.Connected);
                Logger.Information("DG-LAB V2 钃濈墮杩炴帴鎴愬姛");
                
                // 鎷夎捣 V2 鐢甸噺閫氱煡銆?
                await SubscribeV2BatteryAsync();
            }

            // 鍚姩娉㈠舰缁彂瀹氭椂鍣ㄣ€?
            StartWaveformTimer();
            
            // 鍚姩鐢甸噺杞銆?
            StartBatteryPolling();
            
            // 杩炴帴鎴愬姛鍚庢竻绌洪噸杩炶鏁般€?
            _reconnectAttempts = 0;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "DG-LAB 钃濈墮杩炴帴澶辫触");
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
        Logger.Information("DG-LAB {Version} 宸叉柇寮€杩炴帴", Version);
    }
    
    /// <summary>
    /// 鎵嬪姩瑙﹀彂涓€娆￠噸杩炴祦绋嬨€?
    /// </summary>
    public async Task ReconnectAsync()
    {
        if (Config == null)
        {
            throw new InvalidOperationException("没有保存的连接配置");
        }
        
        Logger.Information("鎵嬪姩瑙﹀彂閲嶈繛 DG-LAB {Version}", Version);
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
        // 娓呯┖娉㈠舰缂撳瓨锛屽悗缁畾鏃跺櫒涓嶄細缁х画鍙戣繖涓€閫氶亾銆?
        if (channel == Channel.A || channel == Channel.AB)
        {
            _currentWaveformA = null;
            _currentV2WaveformA = null;
            _waveformSequenceA = null;
            _v2WaveformSequenceA = null;
            _waveformSequenceIndexA = 0;
            _v2WaveformSequenceIndexA = 0;
        }
        if (channel == Channel.B || channel == Channel.AB)
        {
            _currentWaveformB = null;
            _currentV2WaveformB = null;
            _waveformSequenceB = null;
            _v2WaveformSequenceB = null;
            _waveformSequenceIndexB = 0;
            _v2WaveformSequenceIndexB = 0;
        }
        
        Logger.Debug("娓呴櫎娉㈠舰闃熷垪: channel={Channel}", channel);
        return Task.CompletedTask;
    }

    public async Task SetLimitsAsync(int limitA, int limitB)
    {
        _limitA = Math.Clamp(limitA, 0, MaxUiStrength);
        _limitB = Math.Clamp(limitB, 0, MaxUiStrength);

        if (Status != DeviceStatus.Connected)
        {
            return;
        }

        if (Version == DGLabVersion.V3)
        {
            await SendBFCommandAsync();
        }

        if (_strengthA > _limitA)
        {
            await SetStrengthAsync(Channel.A, _limitA, StrengthMode.Set);
        }

        if (_strengthB > _limitB)
        {
            await SetStrengthAsync(Channel.B, _limitB, StrengthMode.Set);
        }
    }

    #region V3 Protocol

    private async Task SetStrengthV3Async(Channel channel, int value, StrengthMode mode)
    {
        // 闃叉鈥滃崱鍦ㄧ瓑鍝嶅簲鈥濓細瓒呮椂鍚庡厑璁哥户缁彂鏂板寘锛屼繚璇佹帶鍒舵墜鎰熴€?
        if (_waitingForResponse && Environment.TickCount64 - _lastStrengthCommandAtMs > PendingResponseStaleMs)
        {
            _waitingForResponse = false;
        }

        var safeValue = NormalizeStrengthValue(channel, value, mode);
        if (mode != StrengthMode.Set && safeValue <= 0)
        {
            return;
        }

        var command = _protocol.BuildStrengthCommand(channel, safeValue, mode, true);
        // B0 绗?2 瀛楄妭楂?4 浣嶆槸搴忓垪鍙凤紝杩欓噷鍚屾璁板綍鐢ㄤ簬鍖归厤鍝嶅簲銆?
        var seqNo = (command[1] >> 4) & 0x0F;
        _pendingSequenceNo = seqNo;
        _waitingForResponse = true;
        _lastStrengthCommandAtMs = Environment.TickCount64;
        await WriteAsync(GetWriteCharacteristicUuid(), command);
    }

    private async Task SendWaveformV3Async(Channel channel, WaveformData data)
    {
        var sequence = ParseHexToWaveformSequence(data.HexData);
        if (sequence.Count == 0)
        {
            sequence.Add(CreateDefaultWaveform(data));
        }

        if (channel is Channel.A or Channel.AB)
        {
            _waveformSequenceA = sequence;
            _waveformSequenceIndexA = 0;
            _currentWaveformA = _waveformSequenceA[0];
        }

        if (channel is Channel.B or Channel.AB)
        {
            _waveformSequenceB = sequence;
            _waveformSequenceIndexB = 0;
            _currentWaveformB = _waveformSequenceB[0];
        }

        var command = BuildCurrentV3WaveformCommand();
        await WriteAsync(GetWriteCharacteristicUuid(), command);
    }
    
    /// <summary>
    /// 鏍规嵁褰撳墠鍙傛暟鐢熸垚榛樿娉㈠舰銆?
    /// </summary>
    private static ChannelWaveform CreateDefaultWaveform(WaveformData data)
    {
        var freq = DGLabBluetoothProtocol.ConvertFrequency(data.Frequency);
        var strength = Math.Clamp(data.Strength, 0, 100);
        return new ChannelWaveform
        {
            Frequency = new[] { freq, freq, freq, freq },
            Strength = new[] { strength, strength, strength, strength }
        };
    }
    
    /// <summary>
    /// 鎶?HEX 瀛楃涓茶В鏋愪负娉㈠舰鏁版嵁銆?
    /// 绾﹀畾鏍煎紡锛?6 涓崄鍏繘鍒跺瓧绗︼紙8 瀛楄妭锛? 4 棰戠巼 + 4 寮哄害銆?
    /// </summary>
    private static List<ChannelWaveform> ParseHexToWaveformSequence(string? hexData)
    {
        if (string.IsNullOrWhiteSpace(hexData))
        {
            return new List<ChannelWaveform>();
        }

        var result = new List<ChannelWaveform>();
        foreach (var segment in SplitHexSegments(hexData))
        {
            if (segment.Length != 16)
            {
                continue;
            }

            try
            {
                var bytes = new byte[8];
                for (int i = 0; i < 8; i++)
                {
                    bytes[i] = Convert.ToByte(segment.Substring(i * 2, 2), 16);
                }

                result.Add(new ChannelWaveform
                {
                    Frequency = new[] { (int)bytes[0], (int)bytes[1], (int)bytes[2], (int)bytes[3] },
                    Strength = new[]
                    {
                        Math.Clamp((int)bytes[4], 0, 100),
                        Math.Clamp((int)bytes[5], 0, 100),
                        Math.Clamp((int)bytes[6], 0, 100),
                        Math.Clamp((int)bytes[7], 0, 100)
                    }
                });
            }
            catch
            {
                // ignore invalid segment
            }
        }

        return result;
    }

    private static IEnumerable<string> SplitHexSegments(string rawHex)
    {
        var parts = rawHex
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        if (parts.Count == 1)
        {
            var merged = parts[0];
            if (merged.Length > 16 && merged.Length % 16 == 0)
            {
                for (var i = 0; i < merged.Length; i += 16)
                {
                    yield return merged.Substring(i, 16);
                }

                yield break;
            }
        }

        foreach (var part in parts)
        {
            yield return part;
        }
    }

    private byte[] BuildCurrentV3WaveformCommand()
    {
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

        return _protocol.BuildB0Command(data);
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
        Logger.Debug("鍙戦€?BF 鎸囦护: LimitA={A}, LimitB={B}", _limitA, _limitB);
    }

    #endregion

    #region V2 Protocol

    private async Task SetStrengthV2Async(Channel channel, int value, StrengthMode mode)
    {
        var safeValue = NormalizeStrengthValue(channel, value, mode);
        if (mode != StrengthMode.Set && safeValue <= 0)
        {
            return;
        }

        var limitRawA = ConvertUiStrengthToV2Raw(_limitA);
        var limitRawB = ConvertUiStrengthToV2Raw(_limitB);
        var targetA = ConvertUiStrengthToV2Raw(_strengthA);
        var targetB = ConvertUiStrengthToV2Raw(_strengthB);
        var inputRaw = ConvertUiStrengthToV2Raw(safeValue);

        switch (mode)
        {
            case StrengthMode.Set:
                if (channel is Channel.A or Channel.AB)
                {
                    targetA = Math.Min(inputRaw, limitRawA);
                }

                if (channel is Channel.B or Channel.AB)
                {
                    targetB = Math.Min(inputRaw, limitRawB);
                }
                break;

            case StrengthMode.Increase:
                if (channel is Channel.A or Channel.AB)
                {
                    targetA = Math.Min(targetA + inputRaw, limitRawA);
                }

                if (channel is Channel.B or Channel.AB)
                {
                    targetB = Math.Min(targetB + inputRaw, limitRawB);
                }
                break;

            case StrengthMode.Decrease:
                if (channel is Channel.A or Channel.AB)
                {
                    targetA = Math.Max(targetA - inputRaw, 0);
                }

                if (channel is Channel.B or Channel.AB)
                {
                    targetB = Math.Max(targetB - inputRaw, 0);
                }
                break;
        }

        targetA = Math.Clamp(targetA, 0, limitRawA);
        targetB = Math.Clamp(targetB, 0, limitRawB);

        var combined = ((targetA & 0x7FF) << 11) | (targetB & 0x7FF);
        var payload = new byte[3];
        payload[0] = (byte)(combined & 0xFF);
        payload[1] = (byte)((combined >> 8) & 0xFF);
        payload[2] = (byte)((combined >> 16) & 0xFF);

        await WriteAsync(GetV2StrengthCharacteristicUuid(), payload);

        _strengthA = ConvertV2RawToUiStrength(targetA);
        _strengthB = ConvertV2RawToUiStrength(targetB);

        StrengthChanged?.Invoke(this, new StrengthInfo
        {
            ChannelA = _strengthA,
            ChannelB = _strengthB,
            LimitA = _limitA,
            LimitB = _limitB
        });

        Logger.Debug("V2 璁剧疆寮哄害: A={A}, B={B}, raw=[{D0:X2},{D1:X2},{D2:X2}]",
            _strengthA, _strengthB, payload[0], payload[1], payload[2]);
    }

    private async Task SendWaveformV2Async(Channel channel, WaveformData data)
    {
        var sequence = ParseV2WaveformSequence(data.HexData);
        if (sequence.Count == 0)
        {
            sequence.Add(BuildV2WaveformPacket(data));
        }

        if (channel is Channel.A or Channel.AB)
        {
            _v2WaveformSequenceA = sequence;
            _v2WaveformSequenceIndexA = 0;
            _currentV2WaveformA = _v2WaveformSequenceA[0];
            await WriteAsync(GetV2WaveformACharacteristicUuid(), _currentV2WaveformA);
        }

        if (channel is Channel.B or Channel.AB)
        {
            _v2WaveformSequenceB = sequence;
            _v2WaveformSequenceIndexB = 0;
            _currentV2WaveformB = _v2WaveformSequenceB[0];
            await WriteAsync(GetV2WaveformBCharacteristicUuid(), _currentV2WaveformB);
        }

        Logger.Debug("V2 鍙戦€佹尝褰? channel={Channel}, sequenceCount={Count}", channel, sequence.Count);
    }

    private static byte[] BuildV2WaveformPacket(WaveformData data)
    {
        var pulseFreqHz = Math.Clamp(data.Frequency, 1, 1000);
        var z = Math.Clamp(data.Strength * 20 / 100, 0, 20);

        if (TryParseFrequencyPulseHint(data.HexData, out var hintedFrequency, out var hintedPulseWidthUs))
        {
            pulseFreqHz = hintedFrequency;
            z = Math.Clamp((int)Math.Round(hintedPulseWidthUs / 5d), 0, 31);
        }

        var waveformPeriodMs = Math.Max(1, 1000 / pulseFreqHz);

        int x;
        int y;
        if (waveformPeriodMs <= 20)
        {
            x = 1;
            y = Math.Max(waveformPeriodMs - 1, 0);
        }
        else if (waveformPeriodMs <= 100)
        {
            x = Math.Min(waveformPeriodMs / 20, 5);
            y = waveformPeriodMs - x;
        }
        else
        {
            x = Math.Min(waveformPeriodMs / 20, 10);
            y = waveformPeriodMs - x;
        }

        x = Math.Clamp(x, 1, 31);
        y = Math.Clamp(y, 0, 1023);

        var combined = ((z & 0x1F) << 15) | ((y & 0x3FF) << 5) | (x & 0x1F);
        return new[]
        {
            (byte)(combined & 0xFF),
            (byte)((combined >> 8) & 0xFF),
            (byte)((combined >> 16) & 0xFF)
        };
    }

    private static List<byte[]> ParseV2WaveformSequence(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<byte[]>();
        }

        var result = new List<byte[]>();
        foreach (var segment in SplitV2WaveformSegments(raw))
        {
            if (segment.Length != 6 || !IsHex(segment))
            {
                continue;
            }

            try
            {
                result.Add(new[]
                {
                    Convert.ToByte(segment.Substring(0, 2), 16),
                    Convert.ToByte(segment.Substring(2, 2), 16),
                    Convert.ToByte(segment.Substring(4, 2), 16)
                });
            }
            catch
            {
                // ignore invalid segment
            }
        }

        return result;
    }

    private static IEnumerable<string> SplitV2WaveformSegments(string raw)
    {
        var parts = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        if (parts.Count == 1)
        {
            var merged = parts[0];
            if (merged.Length > 6 && merged.Length % 6 == 0 && IsHex(merged))
            {
                for (var i = 0; i < merged.Length; i += 6)
                {
                    yield return merged.Substring(i, 6);
                }

                yield break;
            }
        }

        foreach (var part in parts)
        {
            yield return part;
        }
    }

    private static bool TryParseFrequencyPulseHint(string? raw, out int frequency, out int pulseWidthUs)
    {
        frequency = 0;
        pulseWidthUs = 0;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var parsedFrequency) || !int.TryParse(parts[1], out var parsedPulseWidthUs))
        {
            return false;
        }

        frequency = Math.Clamp(parsedFrequency, 1, 1000);
        pulseWidthUs = Math.Clamp(parsedPulseWidthUs, 5, 155);
        return true;
    }

    private static bool IsHex(string input)
    {
        foreach (var c in input)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static int ConvertUiStrengthToV2Raw(int uiStrength)
    {
        var safeUi = Math.Clamp(uiStrength, 0, MaxUiStrength);
        return (int)Math.Round(safeUi / (double)MaxUiStrength * MaxV2RawStrength);
    }

    private static int ConvertV2RawToUiStrength(int rawStrength)
    {
        var safeRaw = Math.Clamp(rawStrength, 0, MaxV2RawStrength);
        return (int)Math.Round(safeRaw / (double)MaxV2RawStrength * MaxUiStrength);
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

    // V2 鐗瑰緛鏄犲皠璇存槑锛氬崗璁噷 A34/B34 涓庡疄闄?A/B 閫氶亾鏄弽鍚戝搴旂殑銆?
    private Guid GetV2StrengthCharacteristicUuid() => Guid.Parse("955A1504-0FE2-F5AA-A094-84B8D4F3E8AD");
    private Guid GetV2WaveformACharacteristicUuid() => Guid.Parse("955A1506-0FE2-F5AA-A094-84B8D4F3E8AD");  // A閫氶亾鐢?0x1506
    private Guid GetV2WaveformBCharacteristicUuid() => Guid.Parse("955A1505-0FE2-F5AA-A094-84B8D4F3E8AD");  // B閫氶亾鐢?0x1505

    #endregion

    #region Event Handlers

    private void OnTransportStateChanged(object? sender, BleConnectionState state)
    {
        if (state == BleConnectionState.Disconnected && Status == DeviceStatus.Connected)
        {
            UpdateStatus(DeviceStatus.Disconnected);
            // 鏂嚎鎭㈠浜ょ粰浼犺緭灞傜粺涓€澶勭悊锛岄伩鍏嶅弻閲嶉噸杩炰簰鐩告墦鏋躲€?
            Logger.Information("妫€娴嬪埌钃濈墮鏂紑锛岀瓑寰呬紶杈撳眰鑷姩鎭㈠: {Version}", Version);
        }
    }
    
    /// <summary>
    /// 璁㈤槄 V3 鐢甸噺閫氱煡銆?
    /// </summary>
    private async Task SubscribeBatteryAsync()
    {
        if (Version != DGLabVersion.V3 || _transport == null) return;
        
        try
        {
            // V3 鐢甸噺鏈嶅姟锛?x180A锛岀壒寰侊細0x1500銆?
            await _transport.SubscribeAsync(BatteryServiceUuid, BatteryCharacteristicUuid);
            Logger.Debug("宸茶闃?V3 鐢甸噺閫氱煡");
            
            // 杩炰笂鍚庡厛璇讳竴娆★紝閬垮厤 UI 鍒濆€兼粸鍚庛€?
            var batteryData = await _transport.ReadAsync(BatteryServiceUuid, BatteryCharacteristicUuid);
            if (batteryData.Length > 0)
            {
                _batteryLevel = batteryData[0];
                BatteryChanged?.Invoke(this, _batteryLevel);
                Logger.Information("V3 鐢甸噺: {Battery}%", _batteryLevel);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "璁㈤槄 V3 鐢甸噺閫氱煡澶辫触");
        }
    }
    
    /// <summary>
    /// 璁㈤槄 V2 鐢甸噺閫氱煡銆?
    /// </summary>
    private async Task SubscribeV2BatteryAsync()
    {
        if (Version != DGLabVersion.V2 || _transport == null) return;
        
        try
        {
            // V2 鐢甸噺鏈嶅姟锛?55A180A锛岀壒寰侊細955A1500锛堣/閫氱煡锛夈€?
            // 鍏堣闃呴€氱煡锛岄伩鍏嶉娆″彉鍖栦涪澶便€?
            await _transport.SubscribeAsync(V2BatteryServiceUuid, V2BatteryCharacteristicUuid);
            Logger.Debug("宸茶闃?V2 鐢甸噺閫氱煡");
            
            // 杩炴帴鍚庣珛鍗宠涓€娆＄數閲忋€?
            var batteryData = await _transport.ReadAsync(V2BatteryServiceUuid, V2BatteryCharacteristicUuid);
            if (batteryData.Length > 0)
            {
                _batteryLevel = batteryData[0];
                BatteryChanged?.Invoke(this, _batteryLevel);
                Logger.Information("V2 鐢甸噺: {Battery}%", _batteryLevel);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "订阅/读取 V2 电量失败（设备可能不支持）");
        }
    }
    
    // V2 鐢甸噺鏈嶅姟 UUID
    private static readonly Guid V2BatteryServiceUuid = Guid.Parse("955A180A-0FE2-F5AA-A094-84B8D4F3E8AD");
    private static readonly Guid V2BatteryCharacteristicUuid = Guid.Parse("955A1500-0FE2-F5AA-A094-84B8D4F3E8AD");
    
    /// <summary>
    /// 鍚姩鐢甸噺杞瀹氭椂鍣ㄣ€?
    /// </summary>
    private void StartBatteryPolling()
    {
        StopBatteryPolling();
        
        // 姣?60 绉掑厹搴曡涓€娆＄數閲忋€?
        _batteryTimer = new Timer(async _ =>
        {
            if (Status != DeviceStatus.Connected || _transport == null) return;
            
            try
            {
                if (Version == DGLabVersion.V3)
                {
                    // V3: 鐩存帴璇诲彇鐢甸噺鐗规€?
                    var batteryData = await _transport.ReadAsync(BatteryServiceUuid, BatteryCharacteristicUuid);
                    if (batteryData.Length > 0)
                    {
                        _batteryLevel = batteryData[0];
                        BatteryChanged?.Invoke(this, _batteryLevel);
                    }
                }
                else
                {
                    // V2: 璇诲彇鐢甸噺鐗规€?(鏈嶅姟0x180A, 鐗规€?x1500)
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
                Logger.Warning(ex, "鐢甸噺杞澶辫触");
            }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
    }
    
    /// <summary>
    /// 鍋滄鐢甸噺杞瀹氭椂鍣?
    /// </summary>
    private void StopBatteryPolling()
    {
        _batteryTimer?.Dispose();
        _batteryTimer = null;
    }
    
    /// <summary>
    /// 鍚姩閲嶈繛瀹氭椂鍣?
    /// </summary>
    private void StartReconnectTimer()
    {
        if (_reconnectAttempts >= MaxReconnectAttempts)
        {
            Logger.Warning("已达到最大重连次数({Max})，停止重连", MaxReconnectAttempts);
            return;
        }
        
        StopReconnectTimer();
        
        _reconnectTimer = new Timer(async _ =>
        {
            await TryReconnectAsync();
        }, null, TimeSpan.FromMilliseconds(ReconnectDelayMs), Timeout.InfiniteTimeSpan);
    }
    
    /// <summary>
    /// 鍋滄閲嶈繛瀹氭椂鍣?
    /// </summary>
    private void StopReconnectTimer()
    {
        _reconnectTimer?.Dispose();
        _reconnectTimer = null;
    }
    
    /// <summary>
    /// 灏濊瘯閲嶈繛
    /// </summary>
    private async Task TryReconnectAsync()
    {
        if (Config == null || Status == DeviceStatus.Connected)
        {
            return;
        }
        
        _reconnectAttempts++;
        Logger.Information("灏濊瘯閲嶈繛 DG-LAB {Version} (绗?{Attempt}/{Max} 娆?", 
            Version, _reconnectAttempts, MaxReconnectAttempts);
        
        try
        {
            await ConnectAsync(Config, CancellationToken.None);
            Logger.Information("DG-LAB {Version} 閲嶈繛鎴愬姛", Version);
            _reconnectAttempts = 0;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "DG-LAB {Version} 閲嶈繛澶辫触", Version);
            
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
            Logger.Warning(ex, "瑙ｆ瀽钃濈墮鍝嶅簲澶辫触");
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
                else if (_waitingForResponse &&
                         Environment.TickCount64 - _lastStrengthCommandAtMs > PendingResponseStaleMs)
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
            Logger.Debug("V2 鐢甸噺: {Battery}%", _batteryLevel);
        }
        else if (data.Length == 3)
        {
            // V2 鍝嶅簲鏁版嵁鏄皬绔簭
            // 瀹樻柟鏂囨。: 21-11bit(A閫氶亾) + 10-0bit(B閫氶亾)
            int combined = data[0] | (data[1] << 8) | (data[2] << 16);
            var rawA = (combined >> 11) & 0x7FF;
            var rawB = combined & 0x7FF;
            _strengthA = ConvertV2RawToUiStrength(rawA);
            _strengthB = ConvertV2RawToUiStrength(rawB);

            StrengthChanged?.Invoke(this, new StrengthInfo
            {
                ChannelA = _strengthA,
                ChannelB = _strengthB,
                LimitA = _limitA,
                LimitB = _limitB
            });

            Logger.Debug("V2 寮哄害鍝嶅簲: A={A}, B={B}", _strengthA, _strengthB);
        }
    }

    #endregion

    #region Helpers

    private int NormalizeStrengthValue(Channel channel, int value, StrengthMode mode)
    {
        var safeValue = Math.Clamp(value, 0, MaxUiStrength);

        if (mode == StrengthMode.Set)
        {
            var channelLimit = channel switch
            {
                Channel.A => _limitA,
                Channel.B => _limitB,
                _ => Math.Min(_limitA, _limitB)
            };

            return Math.Clamp(safeValue, 0, Math.Max(0, channelLimit));
        }

        var current = channel switch
        {
            Channel.A => _strengthA,
            Channel.B => _strengthB,
            _ => Math.Min(_strengthA, _strengthB)
        };

        if (mode == StrengthMode.Increase)
        {
            var room = channel switch
            {
                Channel.A => Math.Max(0, _limitA - _strengthA),
                Channel.B => Math.Max(0, _limitB - _strengthB),
                _ => Math.Max(0, Math.Min(_limitA - _strengthA, _limitB - _strengthB))
            };

            return Math.Min(safeValue, room);
        }

        if (mode == StrengthMode.Decrease)
        {
            return Math.Min(safeValue, Math.Max(0, current));
        }

        return safeValue;
    }

    private void StartWaveformTimer()
    {
        _waveformTimer?.Dispose();
        _waveformTimer = new Timer(async _ =>
        {
            if (Status != DeviceStatus.Connected || _transport == null)
            {
                return;
            }

            try
            {
                if (Version == DGLabVersion.V3)
                {
                    if (_waitingForResponse && Environment.TickCount64 - _lastStrengthCommandAtMs < PendingResponseStaleMs)
                    {
                        return;
                    }

                    if (_waveformSequenceA is { Count: > 0 })
                    {
                        _currentWaveformA = _waveformSequenceA[_waveformSequenceIndexA];
                        _waveformSequenceIndexA = (_waveformSequenceIndexA + 1) % _waveformSequenceA.Count;
                    }

                    if (_waveformSequenceB is { Count: > 0 })
                    {
                        _currentWaveformB = _waveformSequenceB[_waveformSequenceIndexB];
                        _waveformSequenceIndexB = (_waveformSequenceIndexB + 1) % _waveformSequenceB.Count;
                    }

                    if (_currentWaveformA != null || _currentWaveformB != null)
                    {
                        var command = BuildCurrentV3WaveformCommand();
                        await _transport.WriteWithoutResponseAsync(GetServiceUuid(), GetWriteCharacteristicUuid(), command);
                    }
                }
                else
                {
                    if (_v2WaveformSequenceA is { Count: > 0 })
                    {
                        _currentV2WaveformA = _v2WaveformSequenceA[_v2WaveformSequenceIndexA];
                        _v2WaveformSequenceIndexA = (_v2WaveformSequenceIndexA + 1) % _v2WaveformSequenceA.Count;
                    }

                    if (_v2WaveformSequenceB is { Count: > 0 })
                    {
                        _currentV2WaveformB = _v2WaveformSequenceB[_v2WaveformSequenceIndexB];
                        _v2WaveformSequenceIndexB = (_v2WaveformSequenceIndexB + 1) % _v2WaveformSequenceB.Count;
                    }

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
                Logger.Warning(ex, "娉㈠舰瀹氭椂鍙戦€佸け璐?");
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
            throw new InvalidOperationException("钃濈墮浼犺緭灞傛湭璁剧疆");
        }

        if (Version == DGLabVersion.V3)
        {
            // V3: 0x150A 鐗规€у睘鎬ф槸 "鍐?锛屼娇鐢?WriteWithoutResponse
            await _transport.WriteWithoutResponseAsync(GetServiceUuid(), characteristicUuid, data);
        }
        else
        {
            // V2: PWM_AB2/A34/B34 鐗规€у睘鎬ф槸 "璇?鍐?锛岄渶瑕佷娇鐢?WriteAsync (甯﹀搷搴?
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





