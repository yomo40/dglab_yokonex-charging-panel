using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ChargingPanel.Core;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.Yokonex;
using ChargingPanel.Core.Services;
using ChargingPanel.Desktop.Views;
using Serilog;

namespace ChargingPanel.Desktop.Views.Pages;

public partial class SensorPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<SensorPage>();
    
    private readonly ObservableCollection<SensorViewModel> _sensors = new();
    private readonly ObservableCollection<RuleViewModel> _rules = new();
    private readonly ObservableCollection<SensorDeviceItem> _devices = new();
    
    private SensorViewModel? _selectedSensor;
    private DispatcherTimer? _updateTimer;
    
    // 役次元传感器服务引用
    private YokonexSensorService? _yokonexSensorService;
    
    public SensorPage()
    {
        InitializeComponent();
        
        SensorList.ItemsSource = _sensors;
        RulesList.ItemsSource = _rules;
        TargetDeviceCombo.ItemsSource = _devices;
        
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }
    
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        LoadSensors();
        LoadDevices();
        
        // 获取役次元传感器服务
        if (AppServices.IsInitialized)
        {
            _yokonexSensorService = YokonexSensorService.Instance;
            if (_yokonexSensorService != null)
            {
                _yokonexSensorService.StepCountChanged += OnYokonexStepCountChanged;
                _yokonexSensorService.AngleChanged += OnYokonexAngleChanged;
            }
        }
        
        // 启动定时更新
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _updateTimer.Tick += OnUpdateTick;
        _updateTimer.Start();
    }
    
    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _updateTimer?.Stop();
        _updateTimer = null;
        
        // 取消订阅
        if (_yokonexSensorService != null)
        {
            _yokonexSensorService.StepCountChanged -= OnYokonexStepCountChanged;
            _yokonexSensorService.AngleChanged -= OnYokonexAngleChanged;
        }
    }
    
    private void OnYokonexStepCountChanged(object? sender, (string DeviceId, int StepCount) e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_selectedSensor != null)
            {
                StepCountText.Text = e.StepCount.ToString();
            }
        });
    }
    
    private void OnYokonexAngleChanged(object? sender, (string DeviceId, float X, float Y, float Z) e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_selectedSensor != null)
            {
                AngleText.Text = $"X:{e.X:F1}° Y:{e.Y:F1}° Z:{e.Z:F1}°";
            }
        });
    }
    
    private void LoadSensors()
    {
        _sensors.Clear();
        
        try
        {
            if (!AppServices.IsInitialized) return;
            
            var deviceManager = AppServices.Instance.DeviceManager;
            
            // 加载役次元传感器 (从已连接的设备中获取)
            var yokonexDevices = deviceManager.GetAllDevices()
                .Where(d => d.Type == DeviceType.Yokonex);
            
            foreach (var device in yokonexDevices)
            {
                // 役次元电击器有计步和角度传感器功能
                _sensors.Add(new SensorViewModel
                {
                    Id = $"yoko_sensor_{device.Id}",
                    Name = $"{device.Name} 传感器",
                    TypeName = "役次元运动传感器",
                    Status = device.Status,
                    StatusText = GetStatusText(device.Status),
                    StatusColor = GetStatusBrush(device.Status),
                    DeviceInfo = device
                });
            }
            
            SensorCountText.Text = _sensors.Count.ToString();
            StatusText.Text = $"已加载 {_sensors.Count} 个传感器";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "加载传感器失败");
            StatusText.Text = "加载传感器失败";
        }
    }
    
    private void LoadDevices()
    {
        _devices.Clear();
        
        // 添加"所有设备"选项
        _devices.Add(new SensorDeviceItem
        {
            Id = "",
            Name = "所有设备",
            DeviceInfo = null
        });
        
        try
        {
            if (!AppServices.IsInitialized) return;
            
            var deviceManager = AppServices.Instance.DeviceManager;
            var devices = deviceManager.GetAllDevices();
            
            foreach (var device in devices)
            {
                _devices.Add(new SensorDeviceItem
                {
                    Id = device.Id,
                    Name = device.Name,
                    DeviceInfo = device
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "加载设备列表失败");
        }
    }
    
    private void LoadRules(SensorViewModel sensor)
    {
        _rules.Clear();
        LoadRulesFromService();
    }
    
    private void OnUpdateTick(object? sender, EventArgs e)
    {
        // 定时更新逻辑（如需要）
    }
    
    private void OnScanSensors(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = "正在扫描传感器...";
        
        try
        {
            if (!AppServices.IsInitialized) return;
            
            // 重新加载传感器列表
            LoadSensors();
            LoadDevices();
            StatusText.Text = "扫描完成";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "扫描传感器失败");
            StatusText.Text = "扫描失败: " + ex.Message;
        }
    }
    
    private void OnSensorSelected(object? sender, SelectionChangedEventArgs e)
    {
        _selectedSensor = SensorList.SelectedItem as SensorViewModel;
        
        if (_selectedSensor != null)
        {
            ConfigPanel.IsVisible = true;
            NoSelectionPanel.IsVisible = false;
            
            SensorNameInput.Text = _selectedSensor.Name;
            SensorTypeText.Text = _selectedSensor.TypeName;
            SensorStatusText.Text = _selectedSensor.StatusText;
            
            // 显示役次元传感器数据面板
            YokonexSensorData.IsVisible = true;
            AngleData.IsVisible = true;
            
            // 初始化传感器数据显示
            StepCountText.Text = "等待数据...";
            AngleText.Text = "等待数据...";
            
            // 启用传感器数据上报
            EnableYokonexSensorReporting(_selectedSensor.DeviceInfo?.Id);
            
            LoadRules(_selectedSensor);
        }
        else
        {
            ConfigPanel.IsVisible = false;
            NoSelectionPanel.IsVisible = true;
        }
    }
    
    private async void EnableYokonexSensorReporting(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || !AppServices.IsInitialized) return;
        
        try
        {
            var deviceManager = AppServices.Instance.DeviceManager;
            var device = deviceManager.GetDevice(deviceId);
            
            if (device is IYokonexEmsDevice emsDevice)
            {
                // 启用计步和角度传感器
                await emsDevice.SetPedometerStateAsync(PedometerState.On);
                await emsDevice.SetAngleSensorEnabledAsync(true);
                StatusText.Text = "已启用传感器数据上报";
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "启用传感器数据上报失败");
            StatusText.Text = "启用传感器失败: " + ex.Message;
        }
    }
    
    private async void OnAddRule(object? sender, RoutedEventArgs e)
    {
        if (_selectedSensor == null)
        {
            StatusText.Text = "请先选择一个传感器";
            return;
        }
        
        try
        {
            // 创建规则编辑对话框
            var dialog = new SensorRuleDialog();
            dialog.SetSensorId(_selectedSensor.DeviceInfo?.Id);
            dialog.SetDevices(_devices.ToList());
            
            var parent = this.Parent;
            while (parent != null && parent is not Window)
                parent = (parent as Control)?.Parent;
            
            if (parent is Window window)
            {
                await dialog.ShowDialog(window);
                
                if (dialog.Result != null)
                {
                    // 保存规则到服务
                    var ruleService = SensorRuleService.Instance;
                    if (ruleService != null)
                    {
                        ruleService.AddRule(dialog.Result);
                        LoadRulesFromService();
                        StatusText.Text = $"规则 '{dialog.Result.Name}' 已添加";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "添加规则失败");
            StatusText.Text = "添加规则失败: " + ex.Message;
        }
    }
    
    private async void OnEditRule(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is RuleViewModel rule)
        {
            try
            {
                var ruleService = SensorRuleService.Instance;
                var existingRule = ruleService?.GetRule(rule.Id);
                if (existingRule == null)
                {
                    StatusText.Text = "规则不存在";
                    return;
                }
                
                var dialog = new SensorRuleDialog();
                dialog.SetRule(existingRule);
                dialog.SetDevices(_devices.ToList());
                
                var parent = this.Parent;
                while (parent != null && parent is not Window)
                    parent = (parent as Control)?.Parent;
                
                if (parent is Window window)
                {
                    await dialog.ShowDialog(window);
                    
                    if (dialog.Result != null)
                    {
                        ruleService?.UpdateRule(rule.Id, dialog.Result);
                        LoadRulesFromService();
                        StatusText.Text = $"规则 '{dialog.Result.Name}' 已更新";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "编辑规则失败");
                StatusText.Text = "编辑规则失败: " + ex.Message;
            }
        }
    }
    
    private void OnDeleteRule(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is RuleViewModel rule)
        {
            try
            {
                var ruleService = SensorRuleService.Instance;
                if (ruleService != null)
                {
                    ruleService.DeleteRule(rule.Id);
                    LoadRulesFromService();
                    StatusText.Text = $"规则已删除";
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "删除规则失败");
                StatusText.Text = "删除规则失败: " + ex.Message;
            }
        }
    }
    
    private void OnRuleToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle && toggle.DataContext is RuleViewModel rule)
        {
            try
            {
                rule.IsEnabled = toggle.IsChecked ?? false;
                
                var ruleService = SensorRuleService.Instance;
                var existingRule = ruleService?.GetRule(rule.Id);
                if (existingRule != null)
                {
                    existingRule.Enabled = rule.IsEnabled;
                    ruleService?.UpdateRule(rule.Id, existingRule);
                    StatusText.Text = $"规则 '{rule.TriggerTypeName}' {(rule.IsEnabled ? "已启用" : "已禁用")}";
                    Logger.Information("Rule {RuleId} enabled: {Enabled}", rule.Id, rule.IsEnabled);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "切换规则状态失败");
                StatusText.Text = "切换规则状态失败: " + ex.Message;
            }
        }
    }
    
    private void LoadRulesFromService()
    {
        _rules.Clear();
        
        var ruleService = SensorRuleService.Instance;
        if (ruleService == null) return;
        
        var rules = _selectedSensor?.DeviceInfo?.Id != null
            ? ruleService.GetAllRules().Where(r => r.DeviceId == null || r.DeviceId == _selectedSensor.DeviceInfo.Id)
            : ruleService.GetAllRules();
        
        foreach (var rule in rules)
        {
            var targetDevice = _devices.FirstOrDefault(d => d.Id == rule.TargetDeviceId);
            _rules.Add(new RuleViewModel
            {
                Id = rule.Id,
                TriggerTypeName = GetTriggerTypeName(rule.SensorType, rule.TriggerType),
                ActionDescription = GetActionDescription(rule),
                TargetDeviceId = rule.TargetDeviceId,
                TargetDeviceName = targetDevice?.Name ?? "所有设备",
                IsEnabled = rule.Enabled
            });
        }
        
        RuleCountText.Text = _rules.Count.ToString();
    }
    
    private static string GetTriggerTypeName(SensorType sensorType, SensorTriggerType triggerType)
    {
        var sensor = sensorType switch
        {
            SensorType.Step => "步数",
            SensorType.Angle => "角度",
            SensorType.Channel => "通道",
            _ => "未知"
        };
        
        var trigger = triggerType switch
        {
            SensorTriggerType.Threshold => "超过阈值",
            SensorTriggerType.Change => "变化时",
            SensorTriggerType.Connect => "连接时",
            SensorTriggerType.Disconnect => "断开时",
            _ => ""
        };
        
        return $"{sensor}{trigger}";
    }
    
    private static string GetActionDescription(SensorRule rule)
    {
        var action = rule.Action switch
        {
            SensorAction.Set => "设置",
            SensorAction.Increase => "增加",
            SensorAction.Decrease => "减少",
            SensorAction.Pulse => "脉冲",
            SensorAction.Wave => "波形",
            _ => "未知"
        };
        
        return $"{action}强度 {rule.Value}，通道 {rule.TargetChannel}，持续 {rule.Duration}ms";
    }
    
    private void OnTargetDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selectedDevice = TargetDeviceCombo.SelectedItem as SensorDeviceItem;
        if (selectedDevice != null)
        {
            StatusText.Text = $"已设置目标设备: {selectedDevice.Name}";
        }
    }
    
    private static string GetStatusText(DeviceStatus status) => status switch
    {
        DeviceStatus.Connected => "已连接",
        DeviceStatus.Connecting => "连接中",
        DeviceStatus.Disconnected => "未连接",
        DeviceStatus.Error => "错误",
        _ => "未知"
    };
    
    private static IBrush GetStatusBrush(DeviceStatus status) => status switch
    {
        DeviceStatus.Connected => Brushes.LimeGreen,
        DeviceStatus.Connecting => Brushes.Orange,
        DeviceStatus.Disconnected => Brushes.Gray,
        DeviceStatus.Error => Brushes.Red,
        _ => Brushes.Gray
    };
}

/// <summary>
/// 传感器视图模型
/// </summary>
public class SensorViewModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TypeName { get; set; } = "";
    public DeviceStatus Status { get; set; }
    public string StatusText { get; set; } = "";
    public IBrush StatusColor { get; set; } = Brushes.Gray;
    public DeviceInfo? DeviceInfo { get; set; }
}

/// <summary>
/// 规则视图模型
/// </summary>
public class RuleViewModel
{
    public string Id { get; set; } = "";
    public string TriggerTypeName { get; set; } = "";
    public string ActionDescription { get; set; } = "";
    public string? TargetDeviceId { get; set; }
    public string TargetDeviceName { get; set; } = "";
    public bool IsEnabled { get; set; }
}

/// <summary>
/// 设备选择项
/// </summary>
public class SensorDeviceItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DeviceInfo? DeviceInfo { get; set; }
}
