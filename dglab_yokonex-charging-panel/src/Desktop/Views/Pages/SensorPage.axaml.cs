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
using ChargingPanel.Core.Devices.DGLab;
using ChargingPanel.Core.Devices.Yokonex;
using ChargingPanel.Core.Events;
using ChargingPanel.Core.Services;
using ChargingPanel.Desktop.Views;
using Serilog;

namespace ChargingPanel.Desktop.Views.Pages;

public partial class SensorPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<SensorPage>();
    private sealed record QuickRulePreset(string ButtonText, string RuleName, SensorType SensorType, double Threshold);
    
    private readonly ObservableCollection<SensorViewModel> _sensors = new();
    private readonly ObservableCollection<RuleViewModel> _rules = new();
    private readonly ObservableCollection<SensorDeviceItem> _devices = new();
    
    private SensorViewModel? _selectedSensor;
    private DispatcherTimer? _updateTimer;
    private IDisposable? _externalVoltageSubscription;
    private QuickRulePreset? _primaryQuickRule;
    private QuickRulePreset? _secondaryQuickRule;
    
    // 役次元传感器服务引用
    private YokonexSensorService? _yokonexSensorService;
    
    public SensorPage()
    {
        InitializeComponent();
        
        SensorList.ItemsSource = _sensors;
        RulesList.ItemsSource = _rules;
        TargetDeviceCombo.ItemsSource = _devices;
        ConfigureQuickRuleButtons(null);
        
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
                _yokonexSensorService.PressureChanged += OnYokonexPressureChanged;
            }

            _externalVoltageSubscription = EventBus.Instance.SubscribeGameEvent(
                GameEventType.ExternalVoltageChanged,
                OnExternalVoltageChanged);
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
            _yokonexSensorService.PressureChanged -= OnYokonexPressureChanged;
        }

        _externalVoltageSubscription?.Dispose();
        _externalVoltageSubscription = null;
    }
    
    private void OnYokonexStepCountChanged(object? sender, (string DeviceId, int StepCount) e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_selectedSensor?.DeviceInfo?.Id == e.DeviceId)
            {
                StepCountText.Text = e.StepCount.ToString();
            }
        });
    }
    
    private void OnYokonexAngleChanged(object? sender, (string DeviceId, float X, float Y, float Z) e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_selectedSensor?.DeviceInfo?.Id == e.DeviceId)
            {
                AngleText.Text = $"X:{e.X:F1}° Y:{e.Y:F1}° Z:{e.Z:F1}°";
            }
        });
    }

    private void OnYokonexPressureChanged(object? sender, (string DeviceId, int PressureA, int PressureB) e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_selectedSensor?.DeviceInfo?.Id == e.DeviceId)
            {
                PressureText.Text = $"A:{e.PressureA} / B:{e.PressureB}";
            }
        });
    }

    private void OnExternalVoltageChanged(GameEvent evt)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_selectedSensor?.DeviceInfo?.Id == null)
            {
                return;
            }

            if (!string.Equals(_selectedSensor.DeviceInfo.Id, evt.TargetDeviceId, StringComparison.Ordinal))
            {
                return;
            }

            var voltage = ResolveEventValue(evt, "voltage", evt.NewValue / 1000.0);
            ExternalVoltageText.Text = $"{voltage:F2}V";
        });
    }
    
    private void LoadSensors()
    {
        _sensors.Clear();
        
        try
        {
            if (!AppServices.IsInitialized) return;
            
            var deviceManager = AppServices.Instance.DeviceManager;
            
            var allDevices = deviceManager.GetAllDevices();

            foreach (var device in allDevices)
            {
                if (device.Type == DeviceType.Yokonex && device.YokonexType == YokonexDeviceType.Estim)
                {
                    _sensors.Add(new SensorViewModel
                    {
                        Id = $"yoko_sensor_{device.Id}",
                        Name = $"{device.Name} 传感器",
                        TypeName = "役次元运动传感器",
                        Status = device.Status,
                        StatusText = GetStatusText(device.Status),
                        StatusColor = GetStatusBrush(device.Status),
                        DeviceInfo = device,
                        SupportsMotion = true
                    });
                }
                else if (device.Type == DeviceType.Yokonex && device.YokonexType == YokonexDeviceType.Enema)
                {
                    _sensors.Add(new SensorViewModel
                    {
                        Id = $"yoko_pressure_{device.Id}",
                        Name = $"{device.Name} 压力传感器",
                        TypeName = "役次元压力传感器",
                        Status = device.Status,
                        StatusText = GetStatusText(device.Status),
                        StatusColor = GetStatusBrush(device.Status),
                        DeviceInfo = device,
                        SupportsPressure = true
                    });
                }
                else if (device.Type == DeviceType.DGLab &&
                         device.DGLabVersion is DGLabVersion.PawPrints or DGLabVersion.V3WirelessSensor)
                {
                    _sensors.Add(new SensorViewModel
                    {
                        Id = $"dglab_voltage_{device.Id}",
                        Name = $"{device.Name} 外部电压传感器",
                        TypeName = "DG-LAB 外部电压传感器",
                        Status = device.Status,
                        StatusText = GetStatusText(device.Status),
                        StatusColor = GetStatusBrush(device.Status),
                        DeviceInfo = device,
                        SupportsExternalVoltage = true
                    });
                }
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
            
            YokonexSensorData.IsVisible = _selectedSensor.SupportsMotion;
            AngleData.IsVisible = _selectedSensor.SupportsMotion;
            PressureData.IsVisible = _selectedSensor.SupportsPressure;
            ExternalVoltageData.IsVisible = _selectedSensor.SupportsExternalVoltage;

            if (_selectedSensor.SupportsMotion)
            {
                StepCountText.Text = "等待数据...";
                AngleText.Text = "等待数据...";
            }
            if (_selectedSensor.SupportsPressure)
            {
                PressureText.Text = "A:0 / B:0";
            }
            if (_selectedSensor.SupportsExternalVoltage)
            {
                ExternalVoltageText.Text = "0.00V";
            }
            ConfigureQuickRuleButtons(_selectedSensor);
            
            // 启用传感器数据上报
            EnableSensorReporting(_selectedSensor.DeviceInfo?.Id);
            
            LoadRules(_selectedSensor);
        }
        else
        {
            ConfigPanel.IsVisible = false;
            NoSelectionPanel.IsVisible = true;
            ConfigureQuickRuleButtons(null);
        }
    }
    
    private async void EnableSensorReporting(string? deviceId)
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
            else if (device is YokonexEnemaBluetoothAdapter)
            {
                StatusText.Text = "已监听灌肠器压力数据";
            }
            else if (device is IDGLabExternalVoltageSensorDevice)
            {
                StatusText.Text = "已监听外部电压事件";
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

    private async void OnQuickAddPrimaryRule(object? sender, RoutedEventArgs e)
    {
        await CreateQuickRuleAsync(_primaryQuickRule);
    }

    private async void OnQuickAddSecondaryRule(object? sender, RoutedEventArgs e)
    {
        await CreateQuickRuleAsync(_secondaryQuickRule);
    }

    private async Task CreateQuickRuleAsync(QuickRulePreset? preset)
    {
        if (_selectedSensor == null)
        {
            StatusText.Text = "请先选择传感器";
            return;
        }

        if (preset == null)
        {
            StatusText.Text = "当前传感器暂无快捷规则";
            return;
        }

        await OpenQuickRuleDialogAsync(
            preset.SensorType,
            ruleName: preset.RuleName,
            threshold: preset.Threshold);
    }

    private async Task OpenQuickRuleDialogAsync(SensorType sensorType, string ruleName, double threshold)
    {
        try
        {
            var dialog = new SensorRuleDialog();
            dialog.SetSensorId(_selectedSensor?.DeviceInfo?.Id);
            dialog.SetDevices(_devices.ToList());
            dialog.SetRule(new SensorRule
            {
                Id = string.Empty,
                Name = ruleName,
                DeviceId = _selectedSensor?.DeviceInfo?.Id,
                SensorType = sensorType,
                TriggerType = SensorTriggerType.Threshold,
                Threshold = threshold,
                TargetChannel = Channel.A,
                Action = SensorAction.Pulse,
                Value = 20,
                Duration = 300,
                CooldownMs = 1000,
                Enabled = true
            });

            var parent = this.Parent;
            while (parent != null && parent is not Window)
            {
                parent = (parent as Control)?.Parent;
            }

            if (parent is Window window)
            {
                await dialog.ShowDialog(window);
                if (dialog.Result != null)
                {
                    SensorRuleService.Instance?.AddRule(dialog.Result);
                    LoadRulesFromService();
                    StatusText.Text = $"规则 '{dialog.Result.Name}' 已添加";
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "快速创建规则失败");
            StatusText.Text = $"快速创建规则失败: {ex.Message}";
        }
    }

    private void ConfigureQuickRuleButtons(SensorViewModel? sensor)
    {
        _primaryQuickRule = null;
        _secondaryQuickRule = null;

        if (sensor?.SupportsMotion == true)
        {
            _primaryQuickRule = new QuickRulePreset(
                ButtonText: "新增步数规则",
                RuleName: "步数触发规则",
                SensorType: SensorType.Step,
                Threshold: 10);
            _secondaryQuickRule = new QuickRulePreset(
                ButtonText: "新增角度规则",
                RuleName: "角度触发规则",
                SensorType: SensorType.Angle,
                Threshold: 15);
        }
        else if (sensor?.SupportsPressure == true)
        {
            _primaryQuickRule = new QuickRulePreset(
                ButtonText: "新增压力规则",
                RuleName: "压力触发规则",
                SensorType: SensorType.Pressure,
                Threshold: 15);
        }
        else if (sensor?.SupportsExternalVoltage == true)
        {
            _primaryQuickRule = new QuickRulePreset(
                ButtonText: "新增电压规则",
                RuleName: "外部电压触发规则",
                SensorType: SensorType.ExternalVoltage,
                Threshold: 0.6);
        }

        QuickRulePrimaryButton.IsVisible = _primaryQuickRule != null;
        QuickRuleSecondaryButton.IsVisible = _secondaryQuickRule != null;

        if (_primaryQuickRule != null)
        {
            QuickRulePrimaryButton.Content = _primaryQuickRule.ButtonText;
        }

        if (_secondaryQuickRule != null)
        {
            QuickRuleSecondaryButton.Content = _secondaryQuickRule.ButtonText;
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
            SensorType.Pressure => "压力",
            SensorType.ExternalVoltage => "外部电压",
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

    private static double ResolveEventValue(GameEvent evt, string key, double fallback)
    {
        if (evt.Data.TryGetValue(key, out var raw))
        {
            return raw switch
            {
                byte b => b,
                short s => s,
                int i => i,
                long l => l,
                float f => f,
                double d => d,
                decimal m => (double)m,
                string text when double.TryParse(text, out var parsed) => parsed,
                _ => fallback
            };
        }

        return fallback;
    }
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
    public bool SupportsMotion { get; set; }
    public bool SupportsPressure { get; set; }
    public bool SupportsExternalVoltage { get; set; }
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
