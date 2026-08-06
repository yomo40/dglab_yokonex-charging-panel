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
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core;
using ChargingPanel.Core.Application.Devices;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Services;
using ChargingPanel.Desktop.Views;
using Serilog;
using ChargingPanel.Desktop.Bootstrap;

namespace ChargingPanel.Desktop.Views.Pages;

/// <summary>
/// 传感器页。设备数据来自 <see cref="IDeviceQueryService"/>，
/// 规则编辑仍使用 legacy SensorRuleService（数据实体不涉及设备访问）。
/// 不引用厂商适配器或 legacy 通道枚举成员。
/// </summary>
public partial class SensorPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<SensorPage>();
    private sealed record QuickRulePreset(string ButtonText, string RuleName, SensorType SensorType, double Threshold);

    private readonly ObservableCollection<SensorViewModel> _sensors = new();
    private readonly ObservableCollection<RuleViewModel> _rules = new();
    private readonly ObservableCollection<SensorDeviceItem> _devices = new();

    private SensorViewModel? _selectedSensor;
    private DispatcherTimer? _updateTimer;
    private QuickRulePreset? _primaryQuickRule;
    private QuickRulePreset? _secondaryQuickRule;

    private IDeviceQueryService? _query;

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
        _query = ApplicationHost.TryGetService<IDeviceQueryService>();
        LoadSensors();
        LoadDevices();

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
    }

    private void OnUpdateTick(object? sender, EventArgs e)
    {
        // 预留：定时刷新只读状态流。
    }

    private void LoadSensors()
    {
        _sensors.Clear();
        SensorCountText.Text = "0";

        try
        {
            if (_query is null)
            {
                return;
            }

            foreach (var instance in _query.GetDeviceSnapshot())
            {
                var modelId = instance.ModelId.Value;
                if (modelId.StartsWith("yokonex.ems", StringComparison.Ordinal))
                {
                    _sensors.Add(new SensorViewModel
                    {
                        Id = instance.Id.Value.ToString("D"),
                        Name = $"{instance.ModelId.Value} 传感器",
                        TypeName = "役次元运动传感器",
                        SupportsMotion = true
                    });
                }
                else if (modelId.StartsWith("yokonex.enema", StringComparison.Ordinal))
                {
                    _sensors.Add(new SensorViewModel
                    {
                        Id = instance.Id.Value.ToString("D"),
                        Name = $"{instance.ModelId.Value} 压力传感器",
                        TypeName = "役次元压力传感器",
                        SupportsPressure = true
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
        _devices.Add(new SensorDeviceItem { Id = "", Name = "所有设备" });

        try
        {
            if (_query is null)
            {
                return;
            }

            foreach (var instance in _query.GetDeviceSnapshot())
            {
                _devices.Add(new SensorDeviceItem
                {
                    Id = instance.Id.Value.ToString("D"),
                    Name = instance.ModelId.Value
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "加载设备列表失败");
        }
    }

    private void OnScanSensors(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = "正在扫描传感器...";
        LoadSensors();
        LoadDevices();
        StatusText.Text = "扫描完成";
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

            YokonexSensorData.IsVisible = _selectedSensor.SupportsMotion;
            AngleData.IsVisible = _selectedSensor.SupportsMotion;
            PressureData.IsVisible = _selectedSensor.SupportsPressure;

            if (_selectedSensor.SupportsMotion)
            {
                StepCountText.Text = "等待数据...";
                AngleText.Text = "等待数据...";
            }

            if (_selectedSensor.SupportsPressure)
            {
                PressureText.Text = "A:0 / B:0";
            }

            ConfigureQuickRuleButtons(_selectedSensor);
            LoadRules(_selectedSensor);
        }
        else
        {
            ConfigPanel.IsVisible = false;
            NoSelectionPanel.IsVisible = true;
            ConfigureQuickRuleButtons(null);
        }
    }

    private void LoadRules(SensorViewModel sensor)
    {
        _rules.Clear();
        LoadRulesFromService();
    }

    private void OnAddRule(object? sender, RoutedEventArgs e)
    {
        if (_selectedSensor == null)
        {
            StatusText.Text = "请先选择一个传感器";
            return;
        }

        try
        {
            var dialog = new SensorRuleDialog();
            dialog.SetSensorId(_selectedSensor.Id);
            dialog.SetDevices(_devices.ToList());

            ShowDialogAndApply(dialog);
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

        await OpenQuickRuleDialogAsync(preset.SensorType, preset.RuleName, preset.Threshold);
    }

    private async Task OpenQuickRuleDialogAsync(SensorType sensorType, string ruleName, double threshold)
    {
        try
        {
            var dialog = new SensorRuleDialog();
            dialog.SetSensorId(_selectedSensor?.Id);
            dialog.SetDevices(_devices.ToList());

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
            _primaryQuickRule = new QuickRulePreset("新增步数规则", "步数触发规则", SensorType.Step, 10);
            _secondaryQuickRule = new QuickRulePreset("新增角度规则", "角度触发规则", SensorType.Angle, 15);
        }
        else if (sensor?.SupportsPressure == true)
        {
            _primaryQuickRule = new QuickRulePreset("新增压力规则", "压力触发规则", SensorType.Pressure, 15);
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
                {
                    parent = (parent as Control)?.Parent;
                }

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
                    StatusText.Text = "规则已删除";
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
        if (ruleService == null)
        {
            return;
        }

        var rules = _selectedSensor?.Id != null
            ? ruleService.GetAllRules().Where(r => r.DeviceId == null || r.DeviceId == _selectedSensor.Id)
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

    private async void ShowDialogAndApply(SensorRuleDialog dialog)
    {
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
}

/// <summary>传感器视图模型。</summary>
public class SensorViewModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string StatusText { get; set; } = "未连接";
    public bool SupportsMotion { get; set; }
    public bool SupportsPressure { get; set; }
}

/// <summary>规则视图模型。</summary>
public class RuleViewModel
{
    public string Id { get; set; } = "";
    public string TriggerTypeName { get; set; } = "";
    public string ActionDescription { get; set; } = "";
    public string? TargetDeviceId { get; set; }
    public string TargetDeviceName { get; set; } = "";
    public bool IsEnabled { get; set; }
}

/// <summary>设备选择项。</summary>
public class SensorDeviceItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}
