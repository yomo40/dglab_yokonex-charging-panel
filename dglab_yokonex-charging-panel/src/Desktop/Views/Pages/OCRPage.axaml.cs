using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ChargingPanel.Core;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.OCR;
using ChargingPanel.Desktop.Views;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ChargingPanel.Desktop.Views.Pages;

public partial class OCRPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<OCRPage>();
    private int _recognitionCount = 0;
    private List<GpuInfo> _gpuList = new();
    private Avalonia.Threading.DispatcherTimer? _updateTimer;

    public OCRPage()
    {
        InitializeComponent();
        
        // 添加默认 GPU 选项，稍后异步加载真实列表
        GPUDevice.Items.Add(new ComboBoxItem { Content = "加载中...", Tag = 0 });
        GPUDevice.SelectedIndex = 0;
        
        // 异步加载 GPU 列表（避免阻塞 UI）
        _ = LoadGpuListAsync();
        
        // 绑定滑块值变化
        ColorTolerance.PropertyChanged += (s, e) =>
        {
            if (e.Property.Name == "Value")
                ToleranceValue.Text = ((int)ColorTolerance.Value).ToString();
        };
        
        SampleRows.PropertyChanged += (s, e) =>
        {
            if (e.Property.Name == "Value")
                SampleRowsValue.Text = ((int)SampleRows.Value).ToString();
        };
        
        // 绑定OCR事件
        if (AppServices.IsInitialized)
        {
            AppServices.Instance.OCRService.BloodChanged += OnBloodChanged;
            LoadConfig();
            
            // 启动定时器定期更新识别次数
            _updateTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _updateTimer.Tick += (s, e) =>
            {
                if (AppServices.IsInitialized && AppServices.Instance.OCRService.IsRunning)
                {
                    RecognitionCount.Text = AppServices.Instance.OCRService.RecognitionCount.ToString();
                }
            };
            _updateTimer.Start();
        }
        
        // 绑定护甲启用复选框
        ArmorEnabled.PropertyChanged += (s, e) =>
        {
            if (e.Property.Name == "IsChecked")
            {
                var enabled = ArmorEnabled.IsChecked ?? false;
                ArmorAreaPanel.IsEnabled = enabled;
                ArmorConfigPanel.IsEnabled = enabled;
            }
        };
    }

    /// <summary>
    /// 异步加载 GPU 列表（避免阻塞 UI 线程）
    /// 使用 DXGI 获取真实的专用显存大小
    /// </summary>
    private async Task LoadGpuListAsync()
    {
        try
        {
            // 在后台线程执行查询
            var gpuInfoList = await Task.Run(() =>
            {
                var list = new List<GpuInfo>();
                
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        // 首先尝试使用 DXGI 获取真实显存
                        var dxgiGpus = GetGpuInfoFromDXGI();
                        if (dxgiGpus.Count > 0)
                        {
                            return dxgiGpus;
                        }
                        
                        // 如果 DXGI 失败，回退到 WMI
                        Logger.Information("DXGI query returned no results, falling back to WMI");
                        using var searcher = new ManagementObjectSearcher(
                            "SELECT Name, AdapterRAM FROM Win32_VideoController");
                        int index = 0;
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            var name = obj["Name"]?.ToString() ?? $"GPU {index}";
                            
                            // 排除虚拟适配器
                            if (name.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Remote", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            
                            // WMI AdapterRAM 是 32 位，超过 4GB 会溢出，只作为备用
                            long vramMB = 0;
                            var adapterRam = obj["AdapterRAM"];
                            if (adapterRam != null)
                            {
                                try
                                {
                                    var rawValue = Convert.ToUInt64(adapterRam);
                                    vramMB = (long)(rawValue / (1024 * 1024));
                                }
                                catch { }
                            }
                            
                            // 只添加真正的 GPU
                            if (vramMB < 128 && !GpuInfo.IsLikelyRealGpu(name)) continue;
                            
                            list.Add(new GpuInfo
                            {
                                Index = index,
                                Name = name,
                                VramMB = vramMB
                            });
                            
                            index++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(ex, "GPU enumeration failed");
                    }
                }
                
                return list;
            });
            
            // 回到 UI 线程更新
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _gpuList.Clear();
                GPUDevice.Items.Clear();
                
                if (gpuInfoList.Count > 0)
                {
                    foreach (var gpuInfo in gpuInfoList)
                    {
                        _gpuList.Add(gpuInfo);
                        
                        // 识别厂商
                        var vendor = GpuInfo.GetVendor(gpuInfo.Name);
                        var vendorIcon = vendor switch
                        {
                            "NVIDIA" => "",
                            "AMD" => "",
                            "Intel" => "",
                            _ => ""
                        };
                        
                        var displayName = gpuInfo.VramMB > 0 
                            ? $"{vendorIcon} {gpuInfo.Name} ({gpuInfo.VramMB / 1024.0:F0} GB)"
                            : $"{vendorIcon} {gpuInfo.Name}";
                        
                        GPUDevice.Items.Add(new ComboBoxItem { Content = displayName, Tag = gpuInfo.Index });
                    }
                }
                else
                {
                    _gpuList.Add(new GpuInfo { Index = 0, Name = "默认 GPU" });
                    GPUDevice.Items.Add(new ComboBoxItem { Content = "GPU 0 (默认)", Tag = 0 });
                }
                
                GPUDevice.SelectedIndex = 0;
                Logger.Information("Found {Count} GPU(s): {Names}", _gpuList.Count, 
                    string.Join(", ", _gpuList.ConvertAll(g => g.Name)));
            });
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to enumerate GPUs, using default");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _gpuList.Clear();
                GPUDevice.Items.Clear();
                _gpuList.Add(new GpuInfo { Index = 0, Name = "默认 GPU" });
                GPUDevice.Items.Add(new ComboBoxItem { Content = "GPU 0 (默认)", Tag = 0 });
                GPUDevice.SelectedIndex = 0;
            });
        }
    }
    
    /// <summary>
    /// 使用 DXGI P/Invoke 获取真实的 GPU 专用显存大小
    /// 这是获取显存的正确方法，不会有 4GB 溢出问题
    /// </summary>
    private static List<GpuInfo> GetGpuInfoFromDXGI()
    {
        var list = new List<GpuInfo>();
        var seenGpus = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // 用于去重
        
        try
        {
            // 创建 DXGI Factory
            var hr = DxgiNative.CreateDXGIFactory1(DxgiNative.IID_IDXGIFactory1, out var factoryPtr);
            if (hr != 0 || factoryPtr == IntPtr.Zero)
            {
                Log.Warning("Failed to create DXGI Factory, HRESULT: {HR}", hr);
                return list;
            }
            
            try
            {
                int adapterIndex = 0;
                int gpuIndex = 0;
                
                while (true)
                {
                    // 枚举适配器
                    hr = DxgiNative.EnumAdapters1(factoryPtr, adapterIndex, out var adapterPtr);
                    if (hr != 0 || adapterPtr == IntPtr.Zero)
                        break;
                    
                    try
                    {
                        // 获取适配器描述
                        var desc = new DxgiNative.DXGI_ADAPTER_DESC1();
                        hr = DxgiNative.GetDesc1(adapterPtr, ref desc);
                        
                        if (hr == 0)
                        {
                            var name = desc.Description?.Trim() ?? "";
                            
                            // 排除软件渲染器和虚拟适配器
                            if ((desc.Flags & 2) != 0 || // DXGI_ADAPTER_FLAG_SOFTWARE = 2
                                string.IsNullOrEmpty(name) ||
                                name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Remote", StringComparison.OrdinalIgnoreCase))
                            {
                                adapterIndex++;
                                continue;
                            }
                            
                            // DedicatedVideoMemory 是真正的专用显存大小 (64位)
                            var dedicatedVramMB = (long)(desc.DedicatedVideoMemory / (1024 * 1024));
                            
                            // 生成唯一标识符 (名称 + VendorId + DeviceId)
                            var uniqueKey = $"{name}_{desc.VendorId}_{desc.DeviceId}";
                            
                            // 跳过已经添加过的 GPU (去重)
                            if (seenGpus.Contains(uniqueKey))
                            {
                                adapterIndex++;
                                continue;
                            }
                            
                            // 只添加有专用显存的 GPU 或已知的独立显卡品牌
                            if (dedicatedVramMB >= 512 || GpuInfo.IsLikelyRealGpu(name))
                            {
                                seenGpus.Add(uniqueKey);
                                list.Add(new GpuInfo
                                {
                                    Index = gpuIndex,
                                    Name = name,
                                    VramMB = dedicatedVramMB
                                });
                                gpuIndex++;
                                
                                Log.Information("DXGI: Found GPU {Name} with {VramMB} MB dedicated VRAM", 
                                    name, dedicatedVramMB);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(adapterPtr);
                    }
                    
                    adapterIndex++;
                }
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DXGI enumeration failed");
        }
        
        return list;
    }

    private void LoadConfig()
    {
        var config = AppServices.Instance.OCRService.Config;
        
        // 血量配置
        AreaX.Text = config.Area.X.ToString();
        AreaY.Text = config.Area.Y.ToString();
        AreaWidth.Text = config.Area.Width.ToString();
        AreaHeight.Text = config.Area.Height.ToString();
        InitialBlood.Text = config.InitialBlood.ToString();
        Interval.Text = config.Interval.ToString();
        
        RecognitionMode.SelectedIndex = config.Mode switch
        {
            "auto" => 0,
            "healthbar" => 1,
            "digital" => 2,
            "model" => 3,
            _ => 0
        };
        
        HealthBarColor.SelectedIndex = config.HealthBarColor switch
        {
            "auto" => 0,
            "red" => 1,
            "green" => 2,
            "yellow" => 3,
            "blue" => 4,
            "orange" => 5,
            _ => 0
        };
        
        ColorTolerance.Value = config.ColorTolerance;
        SampleRows.Value = config.SampleRows;
        EdgeDetection.IsChecked = config.EdgeDetection;
        
        // 护甲配置
        ArmorEnabled.IsChecked = config.ArmorEnabled;
        ArmorAreaPanel.IsEnabled = config.ArmorEnabled;
        ArmorConfigPanel.IsEnabled = config.ArmorEnabled;
        ArmorAreaX.Text = config.ArmorArea.X.ToString();
        ArmorAreaY.Text = config.ArmorArea.Y.ToString();
        ArmorAreaWidth.Text = config.ArmorArea.Width.ToString();
        ArmorAreaHeight.Text = config.ArmorArea.Height.ToString();
        InitialArmor.Text = config.InitialArmor.ToString();
        
        ArmorBarColor.SelectedIndex = config.ArmorBarColor switch
        {
            "auto" => 0,
            "red" => 1,
            "green" => 2,
            "yellow" => 3,
            "blue" => 4,
            "white" => 5,
            _ => 4  // 默认蓝色
        };
        
        // AI 模型配置 - 尝试从持久化目录加载
        var persistedModelPath = Database.Instance.GetSetting<string>("ocr.modelPath", null);
        if (!string.IsNullOrEmpty(persistedModelPath) && File.Exists(persistedModelPath))
        {
            ModelPath.Text = persistedModelPath;
        }
        else
        {
            ModelPath.Text = config.ModelPath ?? "";
        }
        UseGPU.IsChecked = config.UseGPU;
        GPUDevice.SelectedIndex = config.GPUDeviceId < GPUDevice.Items.Count ? config.GPUDeviceId : 0;
    }
    
    /// <summary>
    /// 获取模型存储目录
    /// </summary>
    private static string GetModelDirectory()
    {
        var modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ChargingPanel", "models");
        Directory.CreateDirectory(modelDir);
        return modelDir;
    }

    /// <summary>
    /// 保存配置（公共方法，供外部调用）
    /// </summary>
    public void SaveSettings()
    {
        SaveConfig();
    }

    private void SaveConfig()
    {
        if (!AppServices.IsInitialized) return;
        
        var config = AppServices.Instance.OCRService.Config;
        
        if (int.TryParse(AreaX.Text, out var x)) config.Area = config.Area with { X = x };
        if (int.TryParse(AreaY.Text, out var y)) config.Area = config.Area with { Y = y };
        if (int.TryParse(AreaWidth.Text, out var w)) config.Area = config.Area with { Width = w };
        if (int.TryParse(AreaHeight.Text, out var h)) config.Area = config.Area with { Height = h };
        if (int.TryParse(InitialBlood.Text, out var blood)) config.InitialBlood = blood;
        if (int.TryParse(Interval.Text, out var interval)) config.Interval = interval;
        
        config.Mode = RecognitionMode.SelectedIndex switch
        {
            0 => "auto",
            1 => "healthbar",
            2 => "digital",
            3 => "model",
            _ => "auto"
        };
        
        config.HealthBarColor = HealthBarColor.SelectedIndex switch
        {
            0 => "auto",
            1 => "red",
            2 => "green",
            3 => "yellow",
            4 => "blue",
            5 => "orange",
            _ => "auto"
        };
        
        config.ColorTolerance = (int)ColorTolerance.Value;
        config.SampleRows = (int)SampleRows.Value;
        config.EdgeDetection = EdgeDetection.IsChecked ?? true;
        
        // 护甲配置
        config.ArmorEnabled = ArmorEnabled.IsChecked ?? false;
        if (int.TryParse(ArmorAreaX.Text, out var ax)) config.ArmorArea = config.ArmorArea with { X = ax };
        if (int.TryParse(ArmorAreaY.Text, out var ay)) config.ArmorArea = config.ArmorArea with { Y = ay };
        if (int.TryParse(ArmorAreaWidth.Text, out var aw)) config.ArmorArea = config.ArmorArea with { Width = aw };
        if (int.TryParse(ArmorAreaHeight.Text, out var ah)) config.ArmorArea = config.ArmorArea with { Height = ah };
        if (int.TryParse(InitialArmor.Text, out var armor)) config.InitialArmor = armor;
        
        config.ArmorBarColor = ArmorBarColor.SelectedIndex switch
        {
            0 => "auto",
            1 => "red",
            2 => "green",
            3 => "yellow",
            4 => "blue",
            5 => "white",
            _ => "blue"
        };
        
        // AI 模型配置
        config.ModelPath = string.IsNullOrWhiteSpace(ModelPath.Text) ? null : ModelPath.Text;
        config.UseGPU = UseGPU.IsChecked ?? true;
        config.GPUDeviceId = GPUDevice.SelectedIndex;
        
        AppServices.Instance.OCRService.UpdateConfig(config);
    }

    private void OnBloodChanged(object? sender, BloodChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // 从 OCRService 获取识别次数
            if (AppServices.IsInitialized)
            {
                RecognitionCount.Text = AppServices.Instance.OCRService.RecognitionCount.ToString();
            }
            CurrentBlood.Text = e.CurrentBlood.ToString();
            
            // 根据血量值设置颜色
            CurrentBlood.Foreground = new Avalonia.Media.SolidColorBrush(
                e.CurrentBlood switch
                {
                    > 60 => Avalonia.Media.Color.Parse("#10B981"),
                    > 30 => Avalonia.Media.Color.Parse("#F59E0B"),
                    _ => Avalonia.Media.Color.Parse("#EF4444")
                });
        });
    }

    private void OnStartClick(object? sender, RoutedEventArgs e)
    {
        if (!AppServices.IsInitialized) return;
        
        SaveConfig();
        AppServices.Instance.OCRService.Start();
        
        StatusIndicator.Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#10B981"));
        StatusText.Text = "识别中...";
        BtnStart.IsEnabled = false;
        BtnStop.IsEnabled = true;
        
        Logger.Information("OCR started");
    }

    private void OnStopClick(object? sender, RoutedEventArgs e)
    {
        if (!AppServices.IsInitialized) return;
        
        AppServices.Instance.OCRService.Stop();
        
        StatusIndicator.Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8091A8"));
        StatusText.Text = "已停止";
        BtnStart.IsEnabled = true;
        BtnStop.IsEnabled = false;
        
        Logger.Information("OCR stopped");
    }

    private async void OnPickCoordinatesClick(object? sender, RoutedEventArgs e)
    {
        Logger.Information("Opening coordinate picker...");
        
        try
        {
            // 获取主窗口
            var parent = this.Parent;
            while (parent != null && parent is not MainWindow)
                parent = (parent as Control)?.Parent;
            
            if (parent is MainWindow mainWindow)
            {
                // 最小化主窗口
                mainWindow.WindowState = WindowState.Minimized;
                
                // 等待窗口最小化
                await System.Threading.Tasks.Task.Delay(300);
                
                // 显示坐标拾取窗口
                var result = await CoordinatePickerWindow.ShowPickerAsync(mainWindow);
                
                // 恢复主窗口
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
                
                if (result != null)
                {
                    // 应用选择的区域
                    AreaX.Text = result.X.ToString();
                    AreaY.Text = result.Y.ToString();
                    AreaWidth.Text = result.Width.ToString();
                    AreaHeight.Text = result.Height.ToString();
                    
                    Logger.Information("Coordinates selected: X={X}, Y={Y}, W={Width}, H={Height}", 
                        result.X, result.Y, result.Width, result.Height);
                    
                    // 保存配置
                    SaveConfig();
                    ShowStatus($"已选择区域: ({result.X}, {result.Y}) - {result.Width}×{result.Height}");
                }
                else
                {
                    Logger.Information("Coordinate selection cancelled");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to pick coordinates");
            ShowStatus($"坐标拾取失败: {ex.Message}");
        }
    }

    private void OnRecalibrateClick(object? sender, RoutedEventArgs e)
    {
        if (!AppServices.IsInitialized) return;
        
        AppServices.Instance.OCRService.Recalibrate();
        Logger.Information("OCR recalibrated");
    }

    private async void OnLoadModelClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 OCR 模型文件",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("OCR 模型")
                    {
                        Patterns = new[] { "*.onnx", "*.pt", "*.pth", "*.json" }
                    },
                    new FilePickerFileType("所有文件")
                    {
                        Patterns = new[] { "*.*" }
                    }
                }
            });

            if (files.Count > 0)
            {
                var file = files[0];
                ApplySelectedModel(file.Path.LocalPath);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load OCR model");
            ShowStatus($"加载模型失败: {ex.Message}");
        }
    }
    
    private async void OnBrowseModelClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 ONNX 模型文件",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("ONNX 模型")
                    {
                        Patterns = new[] { "*.onnx" }
                    },
                    new FilePickerFileType("所有文件")
                    {
                        Patterns = new[] { "*.*" }
                    }
                }
            });

            if (files.Count > 0)
            {
                var file = files[0];
                ApplySelectedModel(file.Path.LocalPath);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to browse model file");
            ShowStatus($"选择模型失败: {ex.Message}");
        }
    }

    private void ApplySelectedModel(string originalPath)
    {
        if (string.IsNullOrWhiteSpace(originalPath))
        {
            return;
        }

        Logger.Information("Selected OCR model: {Path}", originalPath);

        var persistedPath = originalPath;
        var copied = false;

        try
        {
            var modelDir = GetModelDirectory();
            var targetPath = Path.Combine(modelDir, Path.GetFileName(originalPath));

            if (!string.Equals(originalPath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                ShowStatus("正在复制模型到程序目录...");
                File.Copy(originalPath, targetPath, overwrite: true);
                Logger.Information("Model copied to: {Path}", targetPath);
                persistedPath = targetPath;
                copied = true;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to copy model, using original path");
        }

        ModelPath.Text = persistedPath;
        SaveConfig();

        var fileName = Path.GetFileName(originalPath);
        if (copied)
        {
            ShowStatus($"已选择并保存模型: {fileName}");
        }
        else
        {
            ShowStatus($"已选择模型: {fileName}");
        }
    }
    
    private void OnClearModelClick(object? sender, RoutedEventArgs e)
    {
        ModelPath.Text = "";
        SaveConfig();
        Logger.Information("Cleared model selection");
        ShowStatus("已清除模型选择");
    }
    
    private async void OnPickArmorCoordinatesClick(object? sender, RoutedEventArgs e)
    {
        Logger.Information("Opening armor coordinate picker...");
        
        try
        {
            // 获取主窗口
            var parent = this.Parent;
            while (parent != null && parent is not MainWindow)
                parent = (parent as Control)?.Parent;
            
            if (parent is MainWindow mainWindow)
            {
                // 最小化主窗口
                mainWindow.WindowState = WindowState.Minimized;
                
                // 等待窗口最小化
                await System.Threading.Tasks.Task.Delay(300);
                
                // 显示坐标拾取窗口（全屏覆盖，和血量拾取一样）
                var result = await CoordinatePickerWindow.ShowPickerAsync(mainWindow);
                
                // 恢复主窗口
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
                
                if (result != null)
                {
                    // 应用选择的区域
                    ArmorAreaX.Text = result.X.ToString();
                    ArmorAreaY.Text = result.Y.ToString();
                    ArmorAreaWidth.Text = result.Width.ToString();
                    ArmorAreaHeight.Text = result.Height.ToString();
                    
                    Logger.Information("Armor coordinates selected: X={X}, Y={Y}, W={Width}, H={Height}", 
                        result.X, result.Y, result.Width, result.Height);
                    
                    // 保存配置
                    SaveConfig();
                    ShowStatus($"护甲区域已选择: ({result.X}, {result.Y}) - {result.Width}×{result.Height}");
                }
                else
                {
                    Logger.Information("Armor coordinate selection cancelled");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to pick armor coordinates");
            ShowStatus($"护甲坐标拾取失败: {ex.Message}");
        }
    }
    
    private async void OnTestRecognitionClick(object? sender, RoutedEventArgs e)
    {
        if (!AppServices.IsInitialized) return;
        
        try
        {
            SaveConfig();
            ShowStatus("正在测试识别...");
            
            var result = await AppServices.Instance.OCRService.RecognizeOnceAsync();
            if (result != null)
            {
                CurrentBlood.Text = result.Blood.ToString();
                if (ArmorEnabled.IsChecked == true)
                {
                    CurrentArmor.Text = result.Armor.ToString();
                }
                
                _recognitionCount++;
                RecognitionCount.Text = _recognitionCount.ToString();
                
                ShowStatus($"测试识别完成: HP={result.Blood}, AHP={result.Armor}");
                Logger.Information("Test recognition: Blood={Blood}, Armor={Armor}", result.Blood, result.Armor);
            }
            else
            {
                ShowStatus("识别失败，请检查配置");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Test recognition failed");
            ShowStatus($"测试失败: {ex.Message}");
        }
    }

    private void ShowStatus(string message)
    {
        var parent = this.Parent;
        while (parent != null && parent is not MainWindow)
            parent = (parent as Control)?.Parent;
        
        if (parent is MainWindow mainWindow)
        {
            mainWindow.ShowStatus(message);
        }
    }
}

/// <summary>
/// GPU 信息
/// </summary>
public class GpuInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public long VramMB { get; set; }
    
    /// <summary>
    /// 根据显卡名称估算显存 (MB)
    /// Win32_VideoController.AdapterRAM 是 32 位的，超过 4GB 会溢出
    /// </summary>
    public static long EstimateVramFromName(string name)
    {
        var upperName = name.ToUpperInvariant();
        
        // NVIDIA RTX 40 系列
        if (upperName.Contains("4090")) return 24 * 1024;
        if (upperName.Contains("4080")) return 16 * 1024;
        if (upperName.Contains("4070 TI SUPER")) return 16 * 1024;
        if (upperName.Contains("4070 TI")) return 12 * 1024;
        if (upperName.Contains("4070 SUPER")) return 12 * 1024;
        if (upperName.Contains("4070 LAPTOP")) return 8 * 1024;  // 笔记本版本
        if (upperName.Contains("4070")) return 12 * 1024;
        if (upperName.Contains("4060 LAPTOP")) return 8 * 1024;  // 笔记本版本
        if (upperName.Contains("4060 TI")) return 8 * 1024;
        if (upperName.Contains("4060")) return 8 * 1024;
        if (upperName.Contains("4050 LAPTOP")) return 6 * 1024;  // 笔记本版本
        if (upperName.Contains("4050")) return 6 * 1024;
        
        // NVIDIA RTX 30 系列
        if (upperName.Contains("3090")) return 24 * 1024;
        if (upperName.Contains("3080 TI")) return 12 * 1024;
        if (upperName.Contains("3080 LAPTOP")) return 8 * 1024;  // 笔记本版本 8GB/16GB
        if (upperName.Contains("3080")) return 10 * 1024;
        if (upperName.Contains("3070 TI LAPTOP")) return 8 * 1024;
        if (upperName.Contains("3070 TI")) return 8 * 1024;
        if (upperName.Contains("3070 LAPTOP")) return 8 * 1024;
        if (upperName.Contains("3070")) return 8 * 1024;
        if (upperName.Contains("3060 TI")) return 8 * 1024;
        if (upperName.Contains("3060 LAPTOP")) return 6 * 1024;
        if (upperName.Contains("3060")) return 12 * 1024;
        if (upperName.Contains("3050 TI LAPTOP")) return 4 * 1024;
        if (upperName.Contains("3050 LAPTOP")) return 4 * 1024;
        if (upperName.Contains("3050")) return 8 * 1024;
        
        // NVIDIA RTX 20 系列
        if (upperName.Contains("2080 TI")) return 11 * 1024;
        if (upperName.Contains("2080 SUPER")) return 8 * 1024;
        if (upperName.Contains("2080")) return 8 * 1024;
        if (upperName.Contains("2070 SUPER")) return 8 * 1024;
        if (upperName.Contains("2070")) return 8 * 1024;
        if (upperName.Contains("2060 SUPER")) return 8 * 1024;
        if (upperName.Contains("2060")) return 6 * 1024;
        
        // NVIDIA GTX 16 系列
        if (upperName.Contains("1660")) return 6 * 1024;
        if (upperName.Contains("1650")) return 4 * 1024;
        
        // NVIDIA GTX 10 系列
        if (upperName.Contains("1080 TI")) return 11 * 1024;
        if (upperName.Contains("1080")) return 8 * 1024;
        if (upperName.Contains("1070 TI")) return 8 * 1024;
        if (upperName.Contains("1070")) return 8 * 1024;
        if (upperName.Contains("1060")) return 6 * 1024;
        if (upperName.Contains("1050 TI")) return 4 * 1024;
        if (upperName.Contains("1050")) return 2 * 1024;
        
        // AMD RX 7000 系列
        if (upperName.Contains("7900 XTX")) return 24 * 1024;
        if (upperName.Contains("7900 XT")) return 20 * 1024;
        if (upperName.Contains("7800 XT")) return 16 * 1024;
        if (upperName.Contains("7700 XT")) return 12 * 1024;
        if (upperName.Contains("7600")) return 8 * 1024;
        
        // AMD RX 6000 系列
        if (upperName.Contains("6950 XT")) return 16 * 1024;
        if (upperName.Contains("6900 XT")) return 16 * 1024;
        if (upperName.Contains("6800 XT")) return 16 * 1024;
        if (upperName.Contains("6800")) return 16 * 1024;
        if (upperName.Contains("6700 XT")) return 12 * 1024;
        if (upperName.Contains("6600 XT")) return 8 * 1024;
        if (upperName.Contains("6600")) return 8 * 1024;
        if (upperName.Contains("6500 XT")) return 4 * 1024;
        
        // Intel Arc
        if (upperName.Contains("A770")) return 16 * 1024;
        if (upperName.Contains("A750")) return 8 * 1024;
        if (upperName.Contains("A580")) return 8 * 1024;
        if (upperName.Contains("A380")) return 6 * 1024;
        if (upperName.Contains("A310")) return 4 * 1024;
        
        // Intel 集成显卡
        if (upperName.Contains("INTEL") && upperName.Contains("UHD")) return 0;
        if (upperName.Contains("INTEL") && upperName.Contains("IRIS")) return 0;
        
        return 0;
    }
    
    /// <summary>
    /// 判断是否可能是真正的独立显卡
    /// </summary>
    public static bool IsLikelyRealGpu(string name)
    {
        var upperName = name.ToUpperInvariant();
        
        // NVIDIA
        if (upperName.Contains("NVIDIA") || upperName.Contains("GEFORCE") || 
            upperName.Contains("RTX") || upperName.Contains("GTX") ||
            upperName.Contains("QUADRO") || upperName.Contains("TESLA"))
            return true;
        
        // AMD
        if (upperName.Contains("AMD") || upperName.Contains("RADEON") || 
            upperName.Contains("RX "))
            return true;
        
        // Intel Arc (独立显卡)
        if (upperName.Contains("INTEL") && upperName.Contains("ARC"))
            return true;
        
        return false;
    }
    
    /// <summary>
    /// 获取显卡厂商
    /// </summary>
    public static string GetVendor(string name)
    {
        var upperName = name.ToUpperInvariant();
        
        if (upperName.Contains("NVIDIA") || upperName.Contains("GEFORCE") || 
            upperName.Contains("RTX") || upperName.Contains("GTX") ||
            upperName.Contains("QUADRO") || upperName.Contains("TESLA"))
            return "NVIDIA";
        
        if (upperName.Contains("AMD") || upperName.Contains("RADEON") || 
            upperName.Contains("RX "))
            return "AMD";
        
        if (upperName.Contains("INTEL"))
            return "Intel";
        
        return "Unknown";
    }
}

/// <summary>
/// DXGI P/Invoke 声明 - 用于获取真实的 GPU 显存大小
/// </summary>
internal static class DxgiNative
{
    public static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    
    [DllImport("dxgi.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int CreateDXGIFactory1(
        [In] ref Guid riid,
        [Out] out IntPtr ppFactory);
    
    public static int CreateDXGIFactory1(Guid riid, out IntPtr ppFactory)
    {
        return CreateDXGIFactory1(ref riid, out ppFactory);
    }
    
    // IDXGIFactory1::EnumAdapters1 通过 vtable 调用
    public static int EnumAdapters1(IntPtr factory, int index, out IntPtr adapter)
    {
        // IDXGIFactory1 vtable:
        // 0: QueryInterface, 1: AddRef, 2: Release
        // 3: SetPrivateData, 4: SetPrivateDataInterface, 5: GetPrivateData, 6: GetParent
        // 7: EnumAdapters, 8: MakeWindowAssociation, 9: GetWindowAssociation
        // 10: CreateSwapChain, 11: CreateSoftwareAdapter
        // 12: EnumAdapters1, 13: IsCurrent
        var vtable = Marshal.ReadIntPtr(factory);
        var enumAdapters1Ptr = Marshal.ReadIntPtr(vtable, 12 * IntPtr.Size);
        var enumAdapters1 = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(enumAdapters1Ptr);
        return enumAdapters1(factory, index, out adapter);
    }
    
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(IntPtr factory, int index, out IntPtr adapter);
    
    // IDXGIAdapter1::GetDesc1 通过 vtable 调用
    public static int GetDesc1(IntPtr adapter, ref DXGI_ADAPTER_DESC1 desc)
    {
        // IDXGIAdapter1 vtable:
        // 0: QueryInterface, 1: AddRef, 2: Release
        // 3: SetPrivateData, 4: SetPrivateDataInterface, 5: GetPrivateData, 6: GetParent
        // 7: EnumOutputs, 8: GetDesc, 9: CheckInterfaceSupport
        // 10: GetDesc1
        var vtable = Marshal.ReadIntPtr(adapter);
        var getDesc1Ptr = Marshal.ReadIntPtr(vtable, 10 * IntPtr.Size);
        var getDesc1 = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(getDesc1Ptr);
        return getDesc1(adapter, ref desc);
    }
    
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Delegate(IntPtr adapter, ref DXGI_ADAPTER_DESC1 desc);
    
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public ulong DedicatedVideoMemory;    // 专用显存 (64位，不会溢出)
        public ulong DedicatedSystemMemory;
        public ulong SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }
}


