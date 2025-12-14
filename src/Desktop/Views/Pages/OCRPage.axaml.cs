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
    /// </summary>
    private async Task LoadGpuListAsync()
    {
        try
        {
            // 在后台线程执行 WMI 查询
            var gpuInfoList = await Task.Run(() =>
            {
                var list = new List<GpuInfo>();
                
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        // 只查询真正的显卡，排除 USB 设备和其他非 GPU 设备
                        using var searcher = new ManagementObjectSearcher(
                            "SELECT * FROM Win32_VideoController WHERE AdapterCompatibility IS NOT NULL");
                        int index = 0;
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            var name = obj["Name"]?.ToString() ?? $"GPU {index}";
                            
                            // 排除 USB 显示适配器和虚拟适配器
                            if (name.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Remote", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            
                            var adapterRam = obj["AdapterRAM"];
                            var vramMB = adapterRam != null ? Convert.ToInt64(adapterRam) / (1024 * 1024) : 0;
                            
                            // 只添加有显存的 GPU（真正的显卡通常有显存）
                            if (vramMB < 128) continue; // 小于 128MB 可能不是真正的 GPU
                            
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
                        Logger.Warning(ex, "WMI query failed");
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
                        
                        var displayName = gpuInfo.VramMB > 0 
                            ? $"{gpuInfo.Name} ({gpuInfo.VramMB / 1024.0:F1} GB)"
                            : gpuInfo.Name;
                        
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
            _recognitionCount++;
            RecognitionCount.Text = _recognitionCount.ToString();
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
        
        StatusIndicator.Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#6B7280"));
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
            // 获取主窗口
            var parent = this.Parent;
            while (parent != null && parent is not Window)
                parent = (parent as Control)?.Parent;
            
            if (parent is not Window window)
                return;

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
                var path = file.Path.LocalPath;
                
                Logger.Information("Loading OCR model from: {Path}", path);
                
                // TODO: 实际加载模型逻辑
                ShowStatus($"已选择模型文件: {Path.GetFileName(path)}");
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
                var originalPath = file.Path.LocalPath;
                Logger.Information("Selected ONNX model: {Path}", originalPath);
                
                // 复制模型到程序数据目录以便持久化
                try
                {
                    var modelDir = GetModelDirectory();
                    var targetPath = Path.Combine(modelDir, Path.GetFileName(originalPath));
                    
                    if (originalPath != targetPath)
                    {
                        ShowStatus("正在复制模型到程序目录...");
                        File.Copy(originalPath, targetPath, overwrite: true);
                        Logger.Information("Model copied to: {Path}", targetPath);
                    }
                    
                    ModelPath.Text = targetPath;
                    
                    // 保存到配置
                    Database.Instance.SetSetting("ocr.modelPath", targetPath, "ocr");
                    
                    ShowStatus($"已选择并保存模型: {Path.GetFileName(originalPath)}");
                }
                catch (Exception copyEx)
                {
                    Logger.Warning(copyEx, "Failed to copy model, using original path");
                    ModelPath.Text = originalPath;
                    ShowStatus($"已选择模型: {Path.GetFileName(originalPath)} (未能复制到程序目录)");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to browse model file");
            ShowStatus($"选择模型失败: {ex.Message}");
        }
    }
    
    private void OnClearModelClick(object? sender, RoutedEventArgs e)
    {
        ModelPath.Text = "";
        Logger.Information("Cleared model selection");
        ShowStatus("已清除模型选择");
    }
    
    private async void OnPickArmorCoordinatesClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            ShowStatus("请在屏幕上框选护甲条区域...");
            
            var picker = new CoordinatePickerWindow();
            
            var parent = this.Parent;
            while (parent != null && parent is not Window)
                parent = (parent as Control)?.Parent;
            
            if (parent is Window parentWindow)
            {
                await picker.ShowDialog(parentWindow);
            }
            else
            {
                picker.Show();
            }
            
            if (picker.Result != null)
            {
                var region = picker.Result;
                ArmorAreaX.Text = region.X.ToString();
                ArmorAreaY.Text = region.Y.ToString();
                ArmorAreaWidth.Text = region.Width.ToString();
                ArmorAreaHeight.Text = region.Height.ToString();
                
                Logger.Information("Armor region selected: X={X}, Y={Y}, W={W}, H={H}", 
                    region.X, region.Y, region.Width, region.Height);
                ShowStatus($"护甲区域已选择: ({region.X}, {region.Y}) {region.Width}x{region.Height}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to pick armor coordinates");
            ShowStatus($"拾取失败: {ex.Message}");
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
}
