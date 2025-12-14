using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Events;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ChargingPanel.Core.OCR;

/// <summary>
/// OCR 血量识别服务
/// </summary>
public class OCRService : IDisposable
{
    private readonly EventService _eventService;
    private readonly ILogger _logger = Log.ForContext<OCRService>();
    
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private bool _isRunning;
    
    // 配置
    private OCRConfig _config = new();
    
    // 状态 - 血量
    private int _currentBlood = 100;
    private int _lastBlood = 100;
    private int _maxBlood = 100;
    
    // 状态 - 护甲
    private int _currentArmor = 100;
    private int _lastArmor = 100;
    private int _maxArmor = 100;
    
    private DateTime _lastTriggered = DateTime.MinValue;

    /// <summary>
    /// 血量变化事件
    /// </summary>
    public event EventHandler<BloodChangedEventArgs>? BloodChanged;
    
    /// <summary>
    /// 护甲变化事件
    /// </summary>
    public event EventHandler<ArmorChangedEventArgs>? ArmorChanged;

    /// <summary>
    /// 识别完成事件
    /// </summary>
    public event EventHandler<OCRResultEventArgs>? RecognitionCompleted;

    public OCRService(EventService eventService)
    {
        _eventService = eventService;
        LoadConfig();
    }

    /// <summary>
    /// 是否正在运行
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// 当前血量
    /// </summary>
    public int CurrentBlood => _currentBlood;
    
    /// <summary>
    /// 当前护甲
    /// </summary>
    public int CurrentArmor => _currentArmor;

    /// <summary>
    /// 获取配置
    /// </summary>
    public OCRConfig Config => _config;

    /// <summary>
    /// 加载配置
    /// </summary>
    private void LoadConfig()
    {
        try
        {
            _config = new OCRConfig
            {
                Enabled = Database.Instance.GetSetting<bool>("ocr.enabled", false),
                Interval = Database.Instance.GetSetting<int>("ocr.interval", 100),
                InitialBlood = Database.Instance.GetSetting<int>("ocr.initialBlood", 100),
                Mode = Database.Instance.GetSetting<string>("ocr.mode", "auto") ?? "auto",
                Area = new OCRArea(
                    Database.Instance.GetSetting<int>("ocr.region.x", 0),
                    Database.Instance.GetSetting<int>("ocr.region.y", 0),
                    Database.Instance.GetSetting<int>("ocr.region.width", 200),
                    Database.Instance.GetSetting<int>("ocr.region.height", 20)
                ),
                HealthBarColor = Database.Instance.GetSetting<string>("ocr.healthBarColor", "auto") ?? "auto",
                ColorTolerance = Database.Instance.GetSetting<int>("ocr.colorTolerance", 30),
                SampleRows = Database.Instance.GetSetting<int>("ocr.sampleRows", 3),
                EdgeDetection = Database.Instance.GetSetting<bool>("ocr.edgeDetection", true),
                HealthColorMin = Database.Instance.GetSetting<string>("ocr.healthColor.min", "#800000") ?? "#800000",
                HealthColorMax = Database.Instance.GetSetting<string>("ocr.healthColor.max", "#FF4444") ?? "#FF4444",
                BackgroundColorMin = Database.Instance.GetSetting<string>("ocr.bgColor.min", "#000000") ?? "#000000",
                BackgroundColorMax = Database.Instance.GetSetting<string>("ocr.bgColor.max", "#444444") ?? "#444444",
                TriggerThreshold = Database.Instance.GetSetting<int>("ocr.triggerThreshold", 5),
                CooldownMs = Database.Instance.GetSetting<int>("ocr.cooldownMs", 500)
            };
            _currentBlood = _config.InitialBlood;
            _maxBlood = _config.InitialBlood;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to load OCR config, using defaults");
        }
    }

    /// <summary>
    /// 保存配置
    /// </summary>
    public void SaveConfig(OCRConfig config)
    {
        _config = config;
        
        Database.Instance.SetSetting("ocr.enabled", config.Enabled, "ocr");
        Database.Instance.SetSetting("ocr.interval", config.Interval, "ocr");
        Database.Instance.SetSetting("ocr.initialBlood", config.InitialBlood, "ocr");
        Database.Instance.SetSetting("ocr.mode", config.Mode, "ocr");
        Database.Instance.SetSetting("ocr.region.x", config.Area.X, "ocr");
        Database.Instance.SetSetting("ocr.region.y", config.Area.Y, "ocr");
        Database.Instance.SetSetting("ocr.region.width", config.Area.Width, "ocr");
        Database.Instance.SetSetting("ocr.region.height", config.Area.Height, "ocr");
        Database.Instance.SetSetting("ocr.healthBarColor", config.HealthBarColor, "ocr");
        Database.Instance.SetSetting("ocr.colorTolerance", config.ColorTolerance, "ocr");
        Database.Instance.SetSetting("ocr.sampleRows", config.SampleRows, "ocr");
        Database.Instance.SetSetting("ocr.edgeDetection", config.EdgeDetection, "ocr");
        Database.Instance.SetSetting("ocr.healthColor.min", config.HealthColorMin, "ocr");
        Database.Instance.SetSetting("ocr.healthColor.max", config.HealthColorMax, "ocr");
        Database.Instance.SetSetting("ocr.bgColor.min", config.BackgroundColorMin, "ocr");
        Database.Instance.SetSetting("ocr.bgColor.max", config.BackgroundColorMax, "ocr");
        Database.Instance.SetSetting("ocr.triggerThreshold", config.TriggerThreshold, "ocr");
        Database.Instance.SetSetting("ocr.cooldownMs", config.CooldownMs, "ocr");
    }

    /// <summary>
    /// 更新配置（供 UI 调用）
    /// </summary>
    public void UpdateConfig(OCRConfig config)
    {
        SaveConfig(config);
        _logger.Information("OCR config updated");
    }

    /// <summary>
    /// 重新校准
    /// </summary>
    public void Recalibrate()
    {
        _currentBlood = _config.InitialBlood;
        _maxBlood = _config.InitialBlood;
        _lastBlood = _config.InitialBlood;
        _currentArmor = _config.InitialArmor;
        _maxArmor = _config.InitialArmor;
        _lastArmor = _config.InitialArmor;
        _lastTriggered = DateTime.MinValue;
        _logger.Information("OCR recalibrated, blood reset to {Blood}, armor reset to {Armor}", _currentBlood, _currentArmor);
    }

    /// <summary>
    /// 开始识别
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;

        // 释放旧的 CancellationTokenSource
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _isRunning = true;
        _captureTask = Task.Run(CaptureLoopAsync);
        _logger.Information("OCR service started");
    }

    /// <summary>
    /// 停止识别
    /// </summary>
    public void Stop()
    {
        if (!_isRunning) return;

        _cts?.Cancel();
        _isRunning = false;
        try
        {
            _captureTask?.Wait(1000);
        }
        catch (AggregateException) { }
        _logger.Information("OCR service stopped");
    }

    /// <summary>
    /// 单次识别
    /// </summary>
    public async Task<OCRResult> RecognizeOnceAsync()
    {
        Image<Rgba32>? screenshot = null;
        try
        {
            screenshot = CaptureRegion(_config.RegionX, _config.RegionY, _config.RegionWidth, _config.RegionHeight);
            var result = AnalyzeHealthBar(screenshot);
            
            RecognitionCompleted?.Invoke(this, new OCRResultEventArgs { Result = result });
            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Recognition failed");
            return new OCRResult { Success = false, Error = ex.Message };
        }
        finally
        {
            screenshot?.Dispose();
        }
    }

    /// <summary>
    /// 设置识别区域
    /// </summary>
    public void SetRegion(int x, int y, int width, int height)
    {
        _config.RegionX = x;
        _config.RegionY = y;
        _config.RegionWidth = width;
        _config.RegionHeight = height;
        SaveConfig(_config);
    }

    /// <summary>
    /// 验证 OCR 区域是否有效
    /// </summary>
    private bool IsValidRegion()
    {
        return _config.RegionWidth >= 10 && _config.RegionHeight >= 5 &&
               _config.RegionX >= 0 && _config.RegionY >= 0;
    }

    private async Task CaptureLoopAsync()
    {
        _logger.Information("OCR capture loop started");
        
        // 检查区域是否有效
        if (!IsValidRegion())
        {
            _logger.Warning("OCR region is invalid: X={X}, Y={Y}, W={W}, H={H}. Please set a valid region first.",
                _config.RegionX, _config.RegionY, _config.RegionWidth, _config.RegionHeight);
        }
        
        while (!_cts!.Token.IsCancellationRequested)
        {
            try
            {
                // 再次检查区域有效性
                if (!IsValidRegion())
                {
                    await Task.Delay(1000, _cts.Token);
                    continue;
                }
                
                Image<Rgba32>? screenshot = null;
                try
                {
                    screenshot = CaptureRegion(_config.RegionX, _config.RegionY, _config.RegionWidth, _config.RegionHeight);
                    var result = AnalyzeHealthBar(screenshot);

                    if (result.Success)
                    {
                        var newBlood = result.HealthPercent;
                        var bloodChange = newBlood - _currentBlood;

                        if (Math.Abs(bloodChange) >= _config.TriggerThreshold)
                        {
                            _lastBlood = _currentBlood;
                            _currentBlood = newBlood;

                            BloodChanged?.Invoke(this, new BloodChangedEventArgs
                            {
                                CurrentBlood = _currentBlood,
                                PreviousBlood = _lastBlood,
                                Change = bloodChange
                            });

                            // 检查冷却时间
                            if ((DateTime.UtcNow - _lastTriggered).TotalMilliseconds >= _config.CooldownMs)
                            {
                                await TriggerEventsAsync(bloodChange);
                                _lastTriggered = DateTime.UtcNow;
                            }
                        }
                    }

                    RecognitionCompleted?.Invoke(this, new OCRResultEventArgs { Result = result });
                }
                finally
                {
                    // 确保释放截图资源
                    screenshot?.Dispose();
                }

                await Task.Delay(_config.Interval, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in capture loop");
                await Task.Delay(1000, _cts.Token);
            }
        }
        
        _logger.Information("OCR capture loop ended");
    }

    private async Task TriggerEventsAsync(int bloodChange)
    {
        if (bloodChange < 0)
        {
            // 血量损失
            await _eventService.TriggerEventAsync("lost-hp", multiplier: Math.Abs(bloodChange) / 10.0);
        }
        else if (bloodChange > 0)
        {
            // 血量恢复
            await _eventService.TriggerEventAsync("add-hp", multiplier: bloodChange / 10.0);
        }

        // 检查死亡
        if (_currentBlood <= 0)
        {
            await _eventService.TriggerEventAsync("dead");
        }
    }

    /// <summary>
    /// 截取屏幕区域
    /// </summary>
    private Image<Rgba32> CaptureRegion(int x, int y, int width, int height)
    {
        // Windows 屏幕截图
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return CaptureRegionWindows(x, y, width, height);
        }
        
        throw new PlatformNotSupportedException("Screen capture is only supported on Windows");
    }

    private Image<Rgba32> CaptureRegionWindows(int x, int y, int width, int height)
    {
        // 使用 Windows API 截图
        var hdcScreen = GetDC(IntPtr.Zero);
        var hdcMem = CreateCompatibleDC(hdcScreen);
        var hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
        var hOld = SelectObject(hdcMem, hBitmap);

        BitBlt(hdcMem, 0, 0, width, height, hdcScreen, x, y, SRCCOPY);
        SelectObject(hdcMem, hOld);

        // 获取位图数据
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // 负值表示自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB
            }
        };

        var pixels = new byte[width * height * 4];
        GetDIBits(hdcMem, hBitmap, 0, (uint)height, pixels, ref bmi, DIB_RGB_COLORS);

        // 转换为 ImageSharp 图像
        var image = new Image<Rgba32>(width, height);
        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
            {
                int i = (py * width + px) * 4;
                image[px, py] = new Rgba32(pixels[i + 2], pixels[i + 1], pixels[i], 255);
            }
        }

        DeleteObject(hBitmap);
        DeleteDC(hdcMem);
        ReleaseDC(IntPtr.Zero, hdcScreen);

        return image;
    }

    /// <summary>
    /// 分析血条
    /// </summary>
    private OCRResult AnalyzeHealthBar(Image<Rgba32> image)
    {
        var healthColor = ParseColorRange(_config.HealthColorMin, _config.HealthColorMax);
        var bgColor = ParseColorRange(_config.BackgroundColorMin, _config.BackgroundColorMax);

        int healthPixels = 0;
        int bgPixels = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    
                    if (IsInColorRange(pixel, healthColor.min, healthColor.max))
                    {
                        healthPixels++;
                    }
                    else if (IsInColorRange(pixel, bgColor.min, bgColor.max))
                    {
                        bgPixels++;
                    }
                }
            }
        });

        int totalRelevantPixels = healthPixels + bgPixels;
        if (totalRelevantPixels < 10)
        {
            return new OCRResult { Success = false, Error = "No health bar detected" };
        }

        int healthPercent = (int)Math.Round(100.0 * healthPixels / totalRelevantPixels);

        return new OCRResult
        {
            Success = true,
            HealthPercent = healthPercent,
            HealthPixels = healthPixels,
            BackgroundPixels = bgPixels,
            TotalPixels = image.Width * image.Height
        };
    }

    private (Rgba32 min, Rgba32 max) ParseColorRange(string minHex, string maxHex)
    {
        return (ParseHexColor(minHex), ParseHexColor(maxHex));
    }

    private Rgba32 ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
        {
            return new Rgba32(
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16),
                255);
        }
        return new Rgba32(0, 0, 0, 255);
    }

    private bool IsInColorRange(Rgba32 pixel, Rgba32 min, Rgba32 max)
    {
        return pixel.R >= min.R && pixel.R <= max.R &&
               pixel.G >= min.G && pixel.G <= max.G &&
               pixel.B >= min.B && pixel.B <= max.B;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }

    #region Windows API

    private const int SRCCOPY = 0x00CC0020;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    #endregion
}

/// <summary>
/// OCR 配置
/// </summary>
public class OCRConfig
{
    public bool Enabled { get; set; }
    public int Interval { get; set; } = 100;
    public int InitialBlood { get; set; } = 100;
    public int InitialArmor { get; set; } = 100;  // 护甲初始值
    public string Mode { get; set; } = "auto";  // auto, healthbar, digital, model
    
    // 区域配置 - 血量
    public OCRArea Area { get; set; } = new();
    public int RegionX { get => Area.X; set => Area = Area with { X = value }; }
    public int RegionY { get => Area.Y; set => Area = Area with { Y = value }; }
    public int RegionWidth { get => Area.Width; set => Area = Area with { Width = value }; }
    public int RegionHeight { get => Area.Height; set => Area = Area with { Height = value }; }
    
    // 区域配置 - 护甲
    public bool ArmorEnabled { get; set; } = false;
    public OCRArea ArmorArea { get; set; } = new();
    public int ArmorRegionX { get => ArmorArea.X; set => ArmorArea = ArmorArea with { X = value }; }
    public int ArmorRegionY { get => ArmorArea.Y; set => ArmorArea = ArmorArea with { Y = value }; }
    public int ArmorRegionWidth { get => ArmorArea.Width; set => ArmorArea = ArmorArea with { Width = value }; }
    public int ArmorRegionHeight { get => ArmorArea.Height; set => ArmorArea = ArmorArea with { Height = value }; }
    
    // 血条配置
    public string HealthBarColor { get; set; } = "auto";
    public string ArmorBarColor { get; set; } = "blue";  // 护甲条颜色
    public int ColorTolerance { get; set; } = 30;
    public int SampleRows { get; set; } = 3;
    public bool EdgeDetection { get; set; } = true;
    
    // 旧的颜色配置（保留兼容）
    public string HealthColorMin { get; set; } = "#800000";
    public string HealthColorMax { get; set; } = "#FF4444";
    public string BackgroundColorMin { get; set; } = "#000000";
    public string BackgroundColorMax { get; set; } = "#444444";
    
    // 护甲颜色配置
    public string ArmorColorMin { get; set; } = "#0000AA";
    public string ArmorColorMax { get; set; } = "#6666FF";
    
    public int TriggerThreshold { get; set; } = 5;
    public int CooldownMs { get; set; } = 500;
    
    // AI 模型配置
    public string? ModelPath { get; set; }
    public bool UseGPU { get; set; } = true;  // 使用 NVIDIA GPU 加速
    public int GPUDeviceId { get; set; } = 0;
}

/// <summary>
/// OCR 区域
/// </summary>
public record struct OCRArea(int X, int Y, int Width, int Height);

/// <summary>
/// OCR 结果
/// </summary>
public class OCRResult
{
    public bool Success { get; set; }
    public int Blood { get; set; }  // 血量百分比
    public int Armor { get; set; }  // 护甲百分比
    public int HealthPercent { get => Blood; set => Blood = value; }  // 兼容旧代码
    public int HealthPixels { get; set; }
    public int BackgroundPixels { get; set; }
    public int TotalPixels { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// 血量变化事件参数
/// </summary>
public class BloodChangedEventArgs : EventArgs
{
    public int CurrentBlood { get; set; }
    public int PreviousBlood { get; set; }
    public int Change { get; set; }
}

/// <summary>
/// 护甲变化事件参数
/// </summary>
public class ArmorChangedEventArgs : EventArgs
{
    public int CurrentArmor { get; set; }
    public int PreviousArmor { get; set; }
    public int Change { get; set; }
}

/// <summary>
/// OCR 结果事件参数
/// </summary>
public class OCRResultEventArgs : EventArgs
{
    public OCRResult Result { get; set; } = new();
}
