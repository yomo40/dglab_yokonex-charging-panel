using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Events;
using ChargingPanel.Core.OCR.Events;
using ChargingPanel.Core.OCR.Recognition;
using ChargingPanel.Core.OCR.Region;
using ChargingPanel.Core.Services;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ChargingPanel.Core.OCR;

/// <summary>
/// OCR 血量识别服务
/// </summary>
public class OCRService : IDisposable
{
    private readonly ILogger _logger = Log.ForContext<OCRService>();
    private readonly OnnxOCREngine _onnxEngine = new();
    private readonly OcrRegionManager _regionManager = new();
    private readonly OcrColorRecognitionEngine _colorRecognitionEngine = new();
    private readonly OcrDigitRecognitionEngine _digitRecognitionEngine = new();
    private readonly OcrAdaptiveIntervalPolicy _intervalPolicy = new();
    private readonly OcrEventEmitter _eventEmitter;
    
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
    
    // 识别次数
    private int _recognitionCount = 0;
    
    private string _lastRoundState = "playing";
    private string _previousRoundState = "playing";

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
    
    /// <summary>
    /// 死亡检测事件
    /// </summary>
    public event EventHandler? DeathDetected;
    
    /// <summary>
    /// 回合状态变化事件
    /// </summary>
    public event EventHandler<Services.RoundStateChangedEventArgs>? RoundStateChanged;

    public OCRService(EventService eventService)
    {
        _eventEmitter = new OcrEventEmitter(eventService);
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
    /// 识别次数
    /// </summary>
    public int RecognitionCount => _recognitionCount;

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
                InitialArmor = Database.Instance.GetSetting<int>("ocr.initialArmor", 100),
                Mode = Database.Instance.GetSetting<string>("ocr.mode", "auto") ?? "auto",
                Area = new OCRArea(
                    Database.Instance.GetSetting<int>("ocr.region.x", 0),
                    Database.Instance.GetSetting<int>("ocr.region.y", 0),
                    Database.Instance.GetSetting<int>("ocr.region.width", 200),
                    Database.Instance.GetSetting<int>("ocr.region.height", 20)
                ),
                ArmorEnabled = Database.Instance.GetSetting<bool>("ocr.armorEnabled", false),
                ArmorArea = new OCRArea(
                    Database.Instance.GetSetting<int>("ocr.armor.region.x", 0),
                    Database.Instance.GetSetting<int>("ocr.armor.region.y", 0),
                    Database.Instance.GetSetting<int>("ocr.armor.region.width", 200),
                    Database.Instance.GetSetting<int>("ocr.armor.region.height", 20)
                ),
                HealthBarColor = Database.Instance.GetSetting<string>("ocr.healthBarColor", "auto") ?? "auto",
                ArmorBarColor = Database.Instance.GetSetting<string>("ocr.armorBarColor", "blue") ?? "blue",
                ColorTolerance = Database.Instance.GetSetting<int>("ocr.colorTolerance", 30),
                SampleRows = Database.Instance.GetSetting<int>("ocr.sampleRows", 3),
                EdgeDetection = Database.Instance.GetSetting<bool>("ocr.edgeDetection", true),
                HealthColorMin = Database.Instance.GetSetting<string>("ocr.healthColor.min", "#800000") ?? "#800000",
                HealthColorMax = Database.Instance.GetSetting<string>("ocr.healthColor.max", "#FF4444") ?? "#FF4444",
                ArmorColorMin = Database.Instance.GetSetting<string>("ocr.armorColor.min", "#0000AA") ?? "#0000AA",
                ArmorColorMax = Database.Instance.GetSetting<string>("ocr.armorColor.max", "#6666FF") ?? "#6666FF",
                BackgroundColorMin = Database.Instance.GetSetting<string>("ocr.bgColor.min", "#000000") ?? "#000000",
                BackgroundColorMax = Database.Instance.GetSetting<string>("ocr.bgColor.max", "#444444") ?? "#444444",
                TriggerThreshold = Database.Instance.GetSetting<int>("ocr.triggerThreshold", 5),
                CooldownMs = Database.Instance.GetSetting<int>("ocr.cooldownMs", 500),
                ModelPath = Database.Instance.GetSetting<string>("ocr.modelPath", null),
                UseGPU = Database.Instance.GetSetting<bool>("ocr.useGpu", true),
                GPUDeviceId = Database.Instance.GetSetting<int>("ocr.gpuDeviceId", 0)
            };
            _currentBlood = _config.InitialBlood;
            _maxBlood = _config.InitialBlood;
            _lastBlood = _config.InitialBlood;
            _currentArmor = _config.InitialArmor;
            _maxArmor = _config.InitialArmor;
            _lastArmor = _config.InitialArmor;
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
        Database.Instance.SetSetting("ocr.initialArmor", config.InitialArmor, "ocr");
        Database.Instance.SetSetting("ocr.mode", config.Mode, "ocr");
        Database.Instance.SetSetting("ocr.region.x", config.Area.X, "ocr");
        Database.Instance.SetSetting("ocr.region.y", config.Area.Y, "ocr");
        Database.Instance.SetSetting("ocr.region.width", config.Area.Width, "ocr");
        Database.Instance.SetSetting("ocr.region.height", config.Area.Height, "ocr");
        Database.Instance.SetSetting("ocr.armorEnabled", config.ArmorEnabled, "ocr");
        Database.Instance.SetSetting("ocr.armor.region.x", config.ArmorArea.X, "ocr");
        Database.Instance.SetSetting("ocr.armor.region.y", config.ArmorArea.Y, "ocr");
        Database.Instance.SetSetting("ocr.armor.region.width", config.ArmorArea.Width, "ocr");
        Database.Instance.SetSetting("ocr.armor.region.height", config.ArmorArea.Height, "ocr");
        Database.Instance.SetSetting("ocr.healthBarColor", config.HealthBarColor, "ocr");
        Database.Instance.SetSetting("ocr.armorBarColor", config.ArmorBarColor, "ocr");
        Database.Instance.SetSetting("ocr.colorTolerance", config.ColorTolerance, "ocr");
        Database.Instance.SetSetting("ocr.sampleRows", config.SampleRows, "ocr");
        Database.Instance.SetSetting("ocr.edgeDetection", config.EdgeDetection, "ocr");
        Database.Instance.SetSetting("ocr.healthColor.min", config.HealthColorMin, "ocr");
        Database.Instance.SetSetting("ocr.healthColor.max", config.HealthColorMax, "ocr");
        Database.Instance.SetSetting("ocr.armorColor.min", config.ArmorColorMin, "ocr");
        Database.Instance.SetSetting("ocr.armorColor.max", config.ArmorColorMax, "ocr");
        Database.Instance.SetSetting("ocr.bgColor.min", config.BackgroundColorMin, "ocr");
        Database.Instance.SetSetting("ocr.bgColor.max", config.BackgroundColorMax, "ocr");
        Database.Instance.SetSetting("ocr.triggerThreshold", config.TriggerThreshold, "ocr");
        Database.Instance.SetSetting("ocr.cooldownMs", config.CooldownMs, "ocr");
        Database.Instance.SetSetting("ocr.modelPath", config.ModelPath, "ocr");
        Database.Instance.SetSetting("ocr.useGpu", config.UseGPU, "ocr");
        Database.Instance.SetSetting("ocr.gpuDeviceId", config.GPUDeviceId, "ocr");
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
        _lastRoundState = "playing";
        _previousRoundState = "playing";
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
            var result = ShouldUseModelMode() && TryAnalyzeWithModel(screenshot, out var modelResult)
                ? modelResult!
                : AnalyzeHealthByConfiguredMode(screenshot);
            
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
        return _regionManager.IsHealthRegionValid(_config);
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
                Image<Rgba32>? armorScreenshot = null;
                try
                {
                    // 血量识别（model 模式优先；内置模式支持 auto/digital/healthbar）
                    screenshot = CaptureRegion(_config.RegionX, _config.RegionY, _config.RegionWidth, _config.RegionHeight);
                    var result = ShouldUseModelMode() && TryAnalyzeWithModel(screenshot, out var modelResult)
                        ? modelResult!
                        : AnalyzeHealthByConfiguredMode(screenshot);
                    
                    // 每次识别都增加计数
                    _recognitionCount++;

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

                            await TriggerBloodEventsAsync(bloodChange);
                        }
                    }
                    
                    // 护甲识别 (如果启用)
                    if (_config.ArmorEnabled)
                    {
                        // model 模式下直接使用模型输出的护甲值。
                        if (ShouldUseModelMode() && result.Success)
                        {
                            var newArmor = Math.Clamp(result.Armor, 0, 100);
                            var armorChange = newArmor - _currentArmor;

                            if (Math.Abs(armorChange) >= _config.TriggerThreshold)
                            {
                                _lastArmor = _currentArmor;
                                _currentArmor = newArmor;

                                ArmorChanged?.Invoke(this, new ArmorChangedEventArgs
                                {
                                    CurrentArmor = _currentArmor,
                                    PreviousArmor = _lastArmor,
                                    Change = armorChange
                                });

                                await TriggerArmorEventsAsync(armorChange);
                            }
                        }
                        else if (IsValidArmorRegion())
                        {
                            armorScreenshot = CaptureRegion(_config.ArmorRegionX, _config.ArmorRegionY, 
                                _config.ArmorRegionWidth, _config.ArmorRegionHeight);
                            var armorResult = AnalyzeArmorBar(armorScreenshot);
                            
                            if (armorResult.Success)
                            {
                                var newArmor = armorResult.Armor;
                                var armorChange = newArmor - _currentArmor;
                                
                                if (Math.Abs(armorChange) >= _config.TriggerThreshold)
                                {
                                    _lastArmor = _currentArmor;
                                    _currentArmor = newArmor;
                                    
                                    ArmorChanged?.Invoke(this, new ArmorChangedEventArgs
                                    {
                                        CurrentArmor = _currentArmor,
                                        PreviousArmor = _lastArmor,
                                        Change = armorChange
                                    });
                                    
                                    await TriggerArmorEventsAsync(armorChange);
                                }
                                
                                result.Armor = newArmor;
                            }
                        }
                    }

                    if (ShouldUseModelMode() && result.Success)
                    {
                        HandleModelRoundState();
                    }

                    RecognitionCompleted?.Invoke(this, new OCRResultEventArgs { Result = result });
                }
                finally
                {
                    // 确保释放截图资源
                    screenshot?.Dispose();
                    armorScreenshot?.Dispose();
                }

                var adaptiveInterval = _intervalPolicy.GetNextDelayMs(_config.Interval);
                await Task.Delay(adaptiveInterval, _cts.Token);
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
    
    /// <summary>
    /// 验证护甲 OCR 区域是否有效
    /// </summary>
    private bool IsValidArmorRegion()
    {
        return _regionManager.IsArmorRegionValid(_config);
    }

    private bool ShouldUseModelMode()
    {
        return _config.Mode.Equals("model", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryAnalyzeWithModel(Image<Rgba32> screenshot, out OCRResult? result)
    {
        result = null;
        if (!ShouldUseModelMode())
        {
            return false;
        }

        var pathValidation = OcrModelRequirements.ValidatePath(_config.ModelPath);
        if (!pathValidation.IsValid)
        {
            _logger.Warning("OCR mode=model 但模型配置不合法：{Message}，已回退颜色识别模式", pathValidation.Message);
            return false;
        }

        try
        {
            var modelPath = pathValidation.FullPath!;
            if (!_onnxEngine.IsInitialized ||
                !string.Equals(_onnxEngine.ModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                _onnxEngine.Initialize(modelPath, _config.UseGPU, _config.GPUDeviceId);
                var ioNames = _onnxEngine.GetModelIoNames();
                var metadataValidation = OcrModelRequirements.ValidateMetadata(ioNames.Inputs, ioNames.Outputs);
                if (!metadataValidation.IsValid)
                {
                    _logger.Warning("ONNX 模型输出不符合规范：{Message}", metadataValidation.Message);
                    return false;
                }
            }

            var model = _onnxEngine.Recognize(screenshot);
            if (model == null || !model.Success)
            {
                return false;
            }

            _lastRoundState = model.RoundState;
            result = new OCRResult
            {
                Success = true,
                Blood = Math.Clamp(model.Blood, 0, 100),
                Armor = Math.Clamp(model.Armor, 0, 100),
                Error = null
            };
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "ONNX OCR 推理失败，已回退颜色识别模式");
            return false;
        }
    }

    private void HandleModelRoundState()
    {
        if (string.Equals(_lastRoundState, _previousRoundState, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var current = _lastRoundState;
        var previous = _previousRoundState;
        _previousRoundState = current;

        _eventEmitter.EmitRoundState(current);

        if (TryParseRoundState(previous, out var oldState) && TryParseRoundState(current, out var newState))
        {
            RoundStateChanged?.Invoke(this, new RoundStateChangedEventArgs
            {
                OldState = oldState,
                NewState = newState
            });
        }
    }

    private static bool TryParseRoundState(string value, out RoundState state)
    {
        state = value.ToLowerInvariant() switch
        {
            "new_round" => RoundState.NewRound,
            "game_over" => RoundState.GameOver,
            "playing" => RoundState.Playing,
            _ => RoundState.Playing
        };
        return true;
    }

    private OCRResult AnalyzeHealthByConfiguredMode(Image<Rgba32> image)
    {
        var mode = (_config.Mode ?? "auto").Trim().ToLowerInvariant();
        return mode switch
        {
            "digital" => AnalyzeDigitalFirst(image),
            "healthbar" => AnalyzeHealthBar(image),
            "auto" => AnalyzeAutoMode(image),
            _ => AnalyzeHealthBar(image)
        };
    }

    private OCRResult AnalyzeDigitalFirst(Image<Rgba32> image)
    {
        if (TryAnalyzeDigital(image, out var digitalResult))
        {
            return digitalResult;
        }

        return AnalyzeHealthBar(image);
    }

    private OCRResult AnalyzeAutoMode(Image<Rgba32> image)
    {
        var barResult = AnalyzeHealthBar(image);
        if (!TryAnalyzeDigital(image, out var digitalResult))
        {
            return barResult;
        }

        if (!barResult.Success)
        {
            return digitalResult;
        }

        return MergeHealthResults(barResult, digitalResult);
    }

    private bool TryAnalyzeDigital(Image<Rgba32> image, out OCRResult result)
    {
        if (!_digitRecognitionEngine.TryAnalyzeHealthPercent(image, out result))
        {
            return false;
        }

        if (result.NumericValue.HasValue)
        {
            result.HealthPercent = NormalizeNumericValueToPercent(result.NumericValue.Value);
        }

        result.HealthPercent = Math.Clamp(result.HealthPercent, 0, 100);
        result.Success = true;
        result.Error = null;
        if (result.Confidence <= 0)
        {
            result.Confidence = 0.55;
        }

        return true;
    }

    private int NormalizeNumericValueToPercent(int numericValue)
    {
        if (numericValue <= 100)
        {
            return Math.Clamp(numericValue, 0, 100);
        }

        // 兼容显示绝对血量的 HUD（例如 200/200）：动态映射到 0-100%
        if (numericValue > _maxBlood)
        {
            _maxBlood = numericValue;
        }

        if (_maxBlood <= 0)
        {
            _maxBlood = numericValue;
        }

        return Math.Clamp((int)Math.Round(100.0 * numericValue / _maxBlood, MidpointRounding.AwayFromZero), 0, 100);
    }

    private static OCRResult MergeHealthResults(OCRResult barResult, OCRResult digitalResult)
    {
        var diff = Math.Abs(barResult.HealthPercent - digitalResult.HealthPercent);
        int mergedPercent;

        if (diff <= 8)
        {
            mergedPercent = (int)Math.Round(digitalResult.HealthPercent * 0.7 + barResult.HealthPercent * 0.3,
                MidpointRounding.AwayFromZero);
        }
        else if (digitalResult.Confidence >= 0.72)
        {
            mergedPercent = digitalResult.HealthPercent;
        }
        else if (barResult.Confidence >= 0.25)
        {
            mergedPercent = barResult.HealthPercent;
        }
        else
        {
            mergedPercent = digitalResult.HealthPercent;
        }

        return new OCRResult
        {
            Success = true,
            HealthPercent = Math.Clamp(mergedPercent, 0, 100),
            Armor = barResult.Armor,
            HealthPixels = barResult.HealthPixels,
            BackgroundPixels = barResult.BackgroundPixels,
            TotalPixels = Math.Max(barResult.TotalPixels, digitalResult.TotalPixels),
            NumericValue = digitalResult.NumericValue,
            Confidence = Math.Max(barResult.Confidence, digitalResult.Confidence)
        };
    }

    private async Task TriggerBloodEventsAsync(int _)
    {
        await _eventEmitter.EmitBloodChangedAsync(_lastBlood, _currentBlood, _config.CooldownMs);
        if (_currentBlood <= 0 && _lastBlood > 0)
        {
            DeathDetected?.Invoke(this, EventArgs.Empty);
        }
    }
    
    private async Task TriggerArmorEventsAsync(int _)
    {
        await _eventEmitter.EmitArmorChangedAsync(_lastArmor, _currentArmor, _config.CooldownMs);
    }
    
    /// <summary>
    /// 分析护甲条
    /// </summary>
    private OCRResult AnalyzeArmorBar(Image<Rgba32> image)
    {
        return _colorRecognitionEngine.AnalyzeArmorBar(image, _config);
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
        return _colorRecognitionEngine.AnalyzeHealthBar(image, _config);
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _onnxEngine.Dispose();
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
    public int? NumericValue { get; set; }  // 数字识别到的原始值（可选）
    public double Confidence { get; set; }  // 识别置信度（0-1）
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
