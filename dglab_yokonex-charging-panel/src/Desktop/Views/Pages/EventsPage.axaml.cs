using Avalonia.Controls;
using Avalonia.Interactivity;
using ChargingPanel.Core;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.DGLab;
using ChargingPanel.Core.Devices.Yokonex;
using ChargingPanel.Core.Events;
using ChargingPanel.Core.Protocols;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace ChargingPanel.Desktop.Views.Pages;

public partial class EventsPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<EventsPage>();
    private readonly DeviceActionTranslatorRegistry _translatorRegistry = new();
    
    public ObservableCollection<EventViewModel> Events { get; } = new();
    private EventViewModel? _selectedEvent;

    public EventsPage()
    {
        InitializeComponent();
        EventList.ItemsSource = Events;
        
        if (AppServices.IsInitialized)
        {
            LoadEvents();
        }
    }
    
    /// <summary>
    /// 保存设置（事件已自动保存到数据库）
    /// </summary>
    public void SaveSettings()
    {
        // 事件配置在编辑时已自动保存到数据库
        Logger.Debug("Events are auto-saved to database");
    }

    private void LoadEvents()
    {
        Events.Clear();
        var records = Database.Instance
            .GetAllEvents()
            .Where(r => EventTriggerPolicy.CanBeTriggerRule(r.EventId))
            .ToList();
        foreach (var record in records)
        {
            Events.Add(new EventViewModel
            {
                Id = record.EventId,
                Name = record.Name,
                Enabled = record.Enabled,
                TriggerType = record.TriggerType,
                MinChange = record.MinChange,
                MaxChange = record.MaxChange,
                ActionType = record.ActionType,
                Strength = record.Strength,
                Duration = record.Duration,
                Channel = record.Channel,
                Priority = record.Priority,
                Description = GetEventDescription(record),
                TargetDeviceType = record.TargetDeviceType,
                WaveformData = record.WaveformData,
                ConditionField = record.ConditionField,
                ConditionOperator = record.ConditionOperator,
                ConditionValue = record.ConditionValue,
                ConditionMaxValue = record.ConditionMaxValue
            });
        }
    }

    private string GetEventDescription(EventRecord record)
    {
        var trigger = record.TriggerType switch
        {
            "hp-decrease" or "decrease" => $"血量减少 ≥{record.MinChange}%",
            "hp-increase" or "increase" => $"血量增加 ≥{record.MinChange}%",
            "armor-decrease" => $"护甲减少 ≥{record.MinChange}%",
            "armor-increase" => $"护甲增加 ≥{record.MinChange}%",
            "death" or "knocked" => "角色倒地/死亡",
            "revive" => "角色复活",
            "new-round" => "新回合开始",
            "game-over" => "游戏结束",
            _ => record.TriggerType
        };
        
        var action = record.ActionType switch
        {
            "set" => $"设置强度 {record.Strength}",
            "increase" => $"增加强度 {record.Strength}",
            "decrease" => $"减少强度 {record.Strength}",
            "waveform" => "发送波形",
            "pulse" => "发送脉冲",
            "custom_waveform" => "自定义波形参数",
            "enema" => "灌肠操作",
            "vibrate" => "震动模式",
            "query_status" => "查询设备状态",
            "query_battery" => "查询设备电量",
            "query_device_info" => "查询设备信息",
            "toy_motor_1" => $"马达1力度 {record.Strength}",
            "toy_motor_2" => $"马达2力度 {record.Strength}",
            "toy_motor_3" => $"马达3力度 {record.Strength}",
            "toy_motor_all" => $"全部马达力度 {record.Strength}",
            "toy_stop_all" => "停止全部马达",
            "smart_lock" => "上锁",
            "smart_unlock" => "解锁",
            "smart_temp_unlock" => $"临时解锁 {record.Strength} 秒",
            "smart_query_state" => "查询锁状态",
            "stop_all" => "停止全部动作",
            _ => record.ActionType
        };
        
        return $"{trigger} → {action} (通道{record.Channel})";
    }

    private void OnEventSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (EventList.SelectedItem is EventViewModel evt)
        {
            _selectedEvent = evt;
            
            EventId.Text = evt.Id;
            EventName.Text = evt.Name;
            
            TriggerType.SelectedIndex = evt.TriggerType switch
            {
                "hp-decrease" or "decrease" => 0,
                "hp-increase" or "increase" => 1,
                "armor-decrease" => 2,
                "armor-increase" => 3,
                "death" or "knocked" => 4,
                "revive" => 5,
                "new-round" => 6,
                "game-over" => 7,
                _ => 0
            };
            
            ChangeValue.Text = evt.MinChange.ToString();
            ConditionFieldInput.Text = evt.ConditionField ?? string.Empty;
            ConditionValueInput.Text = evt.ConditionValue?.ToString(CultureInfo.InvariantCulture) ?? "0";
            ConditionMaxValueInput.Text = evt.ConditionMaxValue?.ToString(CultureInfo.InvariantCulture) ?? "0";
            SyncConditionOperatorSelection(evt.ConditionOperator);
            UpdateConditionMaxValuePanel();
            
            // 先更新设备类型，这会更新动作类型选项
            TargetDeviceType.SelectedIndex = evt.TargetDeviceType switch
            {
                "DGLab" => 0,
                "Yokonex_Estim" => 1,
                "Yokonex_Enema" => 2,
                "Yokonex_Vibrator" => 3,
                "Yokonex_Cup" => 4,
                _ => 6
            };
            
            // 设置动作类型
            for (int i = 0; i < ActionType.Items.Count; i++)
            {
                if ((ActionType.Items[i] as ComboBoxItem)?.Tag?.ToString() == evt.ActionType)
                {
                    ActionType.SelectedIndex = i;
                    break;
                }
            }
            
            ActionStrength.Text = evt.Strength.ToString();
            ActionDuration.Text = evt.Duration.ToString();

            if (evt.ActionType is "waveform" or "custom_waveform")
            {
                if (evt.ActionType == "waveform" && LooksLikeHexWaveform(evt.WaveformData))
                {
                    WaveformHexInput.Text = evt.WaveformData;
                }
                else
                {
                    WaveformHexInput.Text = evt.WaveformData ?? "";
                    for (int i = 0; i < WaveformType.Items.Count; i++)
                    {
                        if ((WaveformType.Items[i] as ComboBoxItem)?.Tag?.ToString() == evt.WaveformData)
                        {
                            WaveformType.SelectedIndex = i;
                            break;
                        }
                    }
                }
            }
            else
            {
                WaveformHexInput.Text = "";
            }
            
            ActionChannel.SelectedIndex = evt.Channel switch
            {
                "A" => 0,
                "B" => 1,
                _ => 2
            };
            
            // 更新变化量面板的显示
            UpdateChangeValuePanel(evt.TriggerType);
        }
    }
    
    private void OnTriggerTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        // 防止初始化时调用
        if (ChangeValuePanel == null || ChangeValueLabel == null)
            return;
            
        var triggerTag = (TriggerType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "hp-decrease";
        UpdateChangeValuePanel(triggerTag);
    }
    
    private void UpdateChangeValuePanel(string triggerType)
    {
        // 防止控件未初始化时调用
        if (ChangeValuePanel == null || ChangeValueLabel == null)
            return;
            
        // 根据触发类型更新变化量面板的标签和可见性
        switch (triggerType)
        {
            case "hp-decrease":
            case "hp-increase":
                ChangeValuePanel.IsVisible = true;
                ChangeValueLabel.Text = "指定血量变化 (%)";
                break;
            case "armor-decrease":
            case "armor-increase":
                ChangeValuePanel.IsVisible = true;
                ChangeValueLabel.Text = "指定护甲变化 (%)";
                break;
            case "death":
            case "knocked":
            case "revive":
            case "new-round":
            case "game-over":
                // 这些事件不需要变化量参数
                ChangeValuePanel.IsVisible = false;
                break;
            default:
                ChangeValuePanel.IsVisible = true;
                ChangeValueLabel.Text = "指定变化量";
                break;
        }
    }

    private void OnConditionOperatorChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateConditionMaxValuePanel();
    }

    private void SyncConditionOperatorSelection(string? operatorTag)
    {
        if (ConditionOperator == null)
        {
            return;
        }

        var normalized = operatorTag?.Trim().ToLowerInvariant() ?? string.Empty;
        for (var i = 0; i < ConditionOperator.Items.Count; i++)
        {
            if ((ConditionOperator.Items[i] as ComboBoxItem)?.Tag?.ToString()?.ToLowerInvariant() == normalized)
            {
                ConditionOperator.SelectedIndex = i;
                return;
            }
        }

        ConditionOperator.SelectedIndex = 0;
    }

    private void UpdateConditionMaxValuePanel()
    {
        if (ConditionMaxValuePanel == null || ConditionOperator == null)
        {
            return;
        }

        var op = (ConditionOperator.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        ConditionMaxValuePanel.IsVisible = op is "between" or "outside";
    }
     
    private void OnTargetDeviceTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ActionType == null) return;
        
        var deviceType = (TargetDeviceType?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        UpdateActionTypeOptions(deviceType);
    }
    
    private void UpdateActionTypeOptions(string deviceType)
    {
        if (ActionType == null) return;
        
        var currentAction = (ActionType.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        ActionType.Items.Clear();
        
        // 根据设备类型添加支持的动作
        switch (deviceType)
        {
            case "DGLab":
                // 郊狼设备支持的动作：强度控制 + 波形
                ActionType.Items.Add(new ComboBoxItem { Content = "设置强度", Tag = "set" });
                ActionType.Items.Add(new ComboBoxItem { Content = "增加强度", Tag = "increase" });
                ActionType.Items.Add(new ComboBoxItem { Content = "减少强度", Tag = "decrease" });
                ActionType.Items.Add(new ComboBoxItem { Content = "发送波形", Tag = "waveform" });
                break;
                
            case "Yokonex_Estim":
                // 役次元电击器：强度控制 + 16种固定模式
                ActionType.Items.Add(new ComboBoxItem { Content = "设置强度", Tag = "set" });
                ActionType.Items.Add(new ComboBoxItem { Content = "增加强度", Tag = "increase" });
                ActionType.Items.Add(new ComboBoxItem { Content = "减少强度", Tag = "decrease" });
                ActionType.Items.Add(new ComboBoxItem { Content = "切换模式", Tag = "mode" });
                ActionType.Items.Add(new ComboBoxItem { Content = "自定义波形参数", Tag = "custom_waveform" });
                break;
                
            case "Yokonex_Enema":
                // 役次元灌肠器：蠕动泵/抽水泵控制
                ActionType.Items.Add(new ComboBoxItem { Content = "启动蠕动泵", Tag = "peristaltic_start" });
                ActionType.Items.Add(new ComboBoxItem { Content = "停止蠕动泵", Tag = "peristaltic_stop" });
                ActionType.Items.Add(new ComboBoxItem { Content = "启动抽水泵", Tag = "water_start" });
                ActionType.Items.Add(new ComboBoxItem { Content = "停止抽水泵", Tag = "water_stop" });
                ActionType.Items.Add(new ComboBoxItem { Content = "全部暂停", Tag = "pause_all" });
                ActionType.Items.Add(new ComboBoxItem { Content = "查询状态", Tag = "query_status" });
                ActionType.Items.Add(new ComboBoxItem { Content = "查询电量", Tag = "query_battery" });
                break;
                
            case "Yokonex_Vibrator":
            case "Yokonex_Cup":
                // 役次元跳蛋/飞机杯：力度控制 + 固定模式
                ActionType.Items.Add(new ComboBoxItem { Content = "设置力度", Tag = "set" });
                ActionType.Items.Add(new ComboBoxItem { Content = "增加力度", Tag = "increase" });
                ActionType.Items.Add(new ComboBoxItem { Content = "减少力度", Tag = "decrease" });
                ActionType.Items.Add(new ComboBoxItem { Content = "切换模式", Tag = "vibrate_mode" });
                ActionType.Items.Add(new ComboBoxItem { Content = "马达1力度", Tag = "toy_motor_1" });
                ActionType.Items.Add(new ComboBoxItem { Content = "马达2力度", Tag = "toy_motor_2" });
                ActionType.Items.Add(new ComboBoxItem { Content = "马达3力度", Tag = "toy_motor_3" });
                ActionType.Items.Add(new ComboBoxItem { Content = "全部马达力度", Tag = "toy_motor_all" });
                ActionType.Items.Add(new ComboBoxItem { Content = "停止全部马达", Tag = "toy_stop_all" });
                ActionType.Items.Add(new ComboBoxItem { Content = "查询设备信息", Tag = "query_device_info" });
                break;

            case "Yokonex_SmartLock":
                ActionType.Items.Add(new ComboBoxItem { Content = "上锁", Tag = "smart_lock" });
                ActionType.Items.Add(new ComboBoxItem { Content = "解锁", Tag = "smart_unlock" });
                ActionType.Items.Add(new ComboBoxItem { Content = "临时解锁", Tag = "smart_temp_unlock" });
                ActionType.Items.Add(new ComboBoxItem { Content = "查询锁状态", Tag = "smart_query_state" });
                break;
                
            default: // All - 只显示通用动作
                ActionType.Items.Add(new ComboBoxItem { Content = "设置强度", Tag = "set" });
                ActionType.Items.Add(new ComboBoxItem { Content = "增加强度", Tag = "increase" });
                ActionType.Items.Add(new ComboBoxItem { Content = "减少强度", Tag = "decrease" });
                ActionType.Items.Add(new ComboBoxItem { Content = "停止全部动作 (IM)", Tag = "stop_all" });
                break;
        }
        
        // 尝试恢复之前选择的动作
        ActionType.SelectedIndex = 0;
        for (int i = 0; i < ActionType.Items.Count; i++)
        {
            if ((ActionType.Items[i] as ComboBoxItem)?.Tag?.ToString() == currentAction)
            {
                ActionType.SelectedIndex = i;
                break;
            }
        }
        
        // 更新强度标签和面板
        var selectedAction = (ActionType.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        UpdateStrengthLabel(deviceType, selectedAction);
        UpdateActionPanels(deviceType, selectedAction);
    }
    
    private void UpdateStrengthLabel(string deviceType, string? actionType = null)
    {
        if (StrengthLabel == null) return;

        if (actionType == "smart_temp_unlock")
        {
            StrengthLabel.Text = "临时解锁时长 (秒)";
            return;
        }

        if (actionType == "custom_waveform")
        {
            StrengthLabel.Text = "频率 Hz (1-100)";
            return;
        }

        if (actionType is "toy_motor_1" or "toy_motor_2" or "toy_motor_3" or "toy_motor_all")
        {
            StrengthLabel.Text = "马达力度 (0-20 或 0-100)";
            return;
        }
        
        StrengthLabel.Text = deviceType switch
        {
            "DGLab" => "强度值 (0-200)",
            "Yokonex_Estim" => "强度值 (1-276)",
            "Yokonex_Vibrator" or "Yokonex_Cup" => "力度 (0-20)",
            "Yokonex_Enema" => "持续时间 (秒)",
            "Yokonex_SmartLock" => "临时解锁时长 (秒)",
            _ => "强度值"
        };
    }
    
    private void UpdateActionPanels(string deviceType, string? actionType)
    {
        if (WaveformPanel == null || ModePanel == null) return;
        
        // 隐藏所有特殊面板
        WaveformPanel.IsVisible = false;
        ModePanel.IsVisible = false;
        
        // 根据设备和动作类型显示对应面板
        if (deviceType == "DGLab" && actionType == "waveform")
        {
            WaveformPanel.IsVisible = true;
            UpdateWaveformOptions();
        }
        else if (deviceType == "Yokonex_Estim" && actionType == "custom_waveform")
        {
            WaveformPanel.IsVisible = true;
            UpdateEmsCustomWaveformOptions();
        }
        else if (deviceType == "Yokonex_Estim" && actionType == "mode")
        {
            ModePanel.IsVisible = true;
            UpdateEstimModeOptions();
        }
        else if ((deviceType == "Yokonex_Vibrator" || deviceType == "Yokonex_Cup") && actionType == "vibrate_mode")
        {
            ModePanel.IsVisible = true;
            UpdateVibrateModeOptions();
        }
    }

    private void UpdateEmsCustomWaveformOptions()
    {
        if (WaveformType == null || WaveformHexInput == null) return;
        WaveformType.Items.Clear();
        WaveformType.Items.Add(new ComboBoxItem { Content = "自定义参数", Tag = "custom_waveform" });
        WaveformType.SelectedIndex = 0;
        WaveformHexInput.Watermark = "可选：频率,脉宽 例如：50,20";
    }
    
    private void UpdateWaveformOptions()
    {
        if (WaveformType == null || WaveformHexInput == null) return;
        WaveformType.Items.Clear();
        // 郊狼波形类型
        WaveformType.Items.Add(new ComboBoxItem { Content = "呼吸", Tag = "breath" });
        WaveformType.Items.Add(new ComboBoxItem { Content = "潮汐", Tag = "tide" });
        WaveformType.Items.Add(new ComboBoxItem { Content = "心跳", Tag = "heartbeat" });
        WaveformType.Items.Add(new ComboBoxItem { Content = "节奏", Tag = "rhythm" });
        WaveformType.Items.Add(new ComboBoxItem { Content = "渐强", Tag = "crescendo" });
        WaveformType.Items.Add(new ComboBoxItem { Content = "渐弱", Tag = "decrescendo" });
        WaveformType.Items.Add(new ComboBoxItem { Content = "脉冲", Tag = "pulse" });
        WaveformType.Items.Add(new ComboBoxItem { Content = "随机", Tag = "random" });
        WaveformType.Items.Add(new ComboBoxItem { Content = "全局随机播放列表", Tag = "playlist:auto" });
        WaveformType.SelectedIndex = WaveformType.Items.Count - 1;
        WaveformHexInput.Watermark = "例如：0A0A0A0A14141414,0F0F0F0F00000000";
    }
    
    private void UpdateEstimModeOptions()
    {
        if (ModeType == null) return;
        ModeType.Items.Clear();
        // 役次元电击器16种模式
        for (int i = 1; i <= 16; i++)
        {
            ModeType.Items.Add(new ComboBoxItem { Content = $"模式 {i}", Tag = i.ToString() });
        }
        ModeType.SelectedIndex = 0;
    }
    
    private void UpdateVibrateModeOptions()
    {
        if (ModeType == null) return;
        ModeType.Items.Clear();
        // 役次元跳蛋/飞机杯模式 (通常有多种，这里列出常见的)
        ModeType.Items.Add(new ComboBoxItem { Content = "关闭", Tag = "0" });
        for (int i = 1; i <= 10; i++)
        {
            ModeType.Items.Add(new ComboBoxItem { Content = $"模式 {i}", Tag = i.ToString() });
        }
        ModeType.SelectedIndex = 1;
    }
    
    private void OnActionTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (WaveformPanel == null || ModePanel == null) return;
        
        var deviceType = (TargetDeviceType?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        var actionType = (ActionType?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        
        UpdateActionPanels(deviceType, actionType);
        UpdateStrengthLabel(deviceType, actionType);
        
        // 更新强度面板的可见性
        var hideStrength = actionType is "peristaltic_stop"
            or "water_stop"
            or "pause_all"
            or "query_status"
            or "query_battery"
            or "query_device_info"
            or "toy_stop_all"
            or "smart_lock"
            or "smart_unlock"
            or "smart_query_state"
            or "stop_all";

        if (StrengthLabel != null)
        {
            var strengthPanel = StrengthLabel.Parent as StackPanel;
            if (strengthPanel?.Parent is Grid grid)
            {
                grid.IsVisible = !hideStrength;
                if (actionType is "peristaltic_start" or "water_start")
                {
                    StrengthLabel.Text = "工作时间 (秒)";
                }
                else if (actionType == "custom_waveform")
                {
                    StrengthLabel.Text = "频率 Hz (1-100)";
                }
                else if (actionType == "smart_temp_unlock")
                {
                    StrengthLabel.Text = "临时解锁时长 (秒)";
                }
            }
        }
    }

    private void OnAddEventClick(object? sender, RoutedEventArgs e)
    {
        var newId = $"event_{Guid.NewGuid():N}".Substring(0, 20);
        
        _selectedEvent = new EventViewModel
        {
            Id = newId,
            Name = "新事件",
            Enabled = true,
            TriggerType = "hp-decrease",
            MinChange = 10,
            MaxChange = 10,
            ActionType = "set",
            Strength = 50,
            Duration = 1000,
            Channel = "AB",
            Priority = 10,
            TargetDeviceType = "All"
        };
        
        EventId.Text = newId;
        EventName.Text = "新事件";
        TriggerType.SelectedIndex = 0;
        ChangeValue.Text = "10";
        ConditionFieldInput.Text = string.Empty;
        ConditionOperator.SelectedIndex = 0;
        ConditionValueInput.Text = "0";
        ConditionMaxValueInput.Text = "0";
        UpdateConditionMaxValuePanel();
        TargetDeviceType.SelectedIndex = 6; // 所有设备
        UpdateActionTypeOptions("All");
        ActionType.SelectedIndex = 0;
        ActionStrength.Text = "50";
        ActionDuration.Text = "1000";
        ActionChannel.SelectedIndex = 2;
        
        UpdateChangeValuePanel("hp-decrease");
    }

    private void OnSaveEventClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedEvent == null) return;
        
        var changeVal = int.TryParse(ChangeValue.Text, out var cv) ? cv : 10;
        var actionType = (ActionType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "set";
        var deviceType = (TargetDeviceType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        var triggerType = (TriggerType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "hp-decrease";
        var parsedStrength = int.TryParse(ActionStrength.Text, out var str) ? str : 50;
        var parsedDuration = int.TryParse(ActionDuration.Text, out var dur) ? dur : 1000;
        var conditionField = string.IsNullOrWhiteSpace(ConditionFieldInput.Text)
            ? null
            : ConditionFieldInput.Text.Trim();
        var conditionOperator = (ConditionOperator.SelectedItem as ComboBoxItem)?.Tag?.ToString()?.Trim().ToLowerInvariant();
        var conditionValue = ParseOptionalDouble(ConditionValueInput.Text);
        var conditionMaxValue = ParseOptionalDouble(ConditionMaxValueInput.Text);

        if (string.IsNullOrWhiteSpace(conditionField) || string.IsNullOrWhiteSpace(conditionOperator))
        {
            conditionField = null;
            conditionOperator = null;
            conditionValue = null;
            conditionMaxValue = null;
        }
        else if (conditionOperator is not "between" and not "outside")
        {
            conditionMaxValue = null;
        }

        if (!EventTriggerPolicy.CanBeTriggerRule(triggerType))
        {
            Logger.Warning("Blocked save for non-rule trigger type: {TriggerType}", triggerType);
            return;
        }
        
        // 获取额外数据（波形类型或模式）
        var waveformData = "";
        if (actionType == "waveform")
        {
            var customHex = WaveformHexInput?.Text?.Trim() ?? "";
            waveformData = !string.IsNullOrWhiteSpace(customHex)
                ? customHex
                : (WaveformType?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "playlist:auto";
        }
        else if (actionType is "mode" or "vibrate_mode")
        {
            waveformData = (ModeType?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "1";
        }
        else if (actionType == "custom_waveform")
        {
            var custom = WaveformHexInput?.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(custom))
            {
                waveformData = custom;
            }
            else
            {
                var frequency = Math.Clamp(parsedStrength, 1, 100);
                var pulse = Math.Clamp(parsedDuration, 0, 100);
                waveformData = $"{frequency},{pulse}";
            }
        }

        var normalizedStrength = parsedStrength;
        var normalizedDuration = parsedDuration;

        if (actionType is "peristaltic_start" or "water_start")
        {
            normalizedStrength = Math.Clamp(parsedStrength, 1, 300);
            normalizedDuration = Math.Clamp(parsedStrength * 1000, 1000, 300000);
        }
        else if (actionType is "peristaltic_stop" or "water_stop" or "pause_all"
                 or "query_status" or "query_battery"
                 or "query_device_info"
                 or "toy_stop_all"
                 or "smart_lock" or "smart_unlock" or "smart_query_state"
                 or "stop_all")
        {
            normalizedStrength = 0;
            normalizedDuration = 0;
        }
        else if (actionType == "smart_temp_unlock")
        {
            normalizedStrength = Math.Clamp(parsedStrength, 1, 3600);
            normalizedDuration = normalizedStrength * 1000;
        }
        else if (actionType == "custom_waveform")
        {
            normalizedStrength = Math.Clamp(parsedStrength, 1, 100);
            normalizedDuration = Math.Clamp(parsedDuration, 0, 100);
        }
        
        var record = new EventRecord
        {
            EventId = EventId.Text ?? "",
            Name = EventName.Text ?? "",
            Enabled = _selectedEvent.Enabled,
            TriggerType = triggerType,
            MinChange = changeVal,
            MaxChange = changeVal,
            Action = actionType,
            ActionType = actionType,
            Value = normalizedStrength,
            Strength = normalizedStrength,
            Duration = normalizedDuration,
            Channel = (ActionChannel.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "AB",
            Priority = 10,
            TargetDeviceType = deviceType,
            WaveformData = waveformData,
            ConditionField = conditionField,
            ConditionOperator = conditionOperator,
            ConditionValue = conditionValue,
            ConditionMaxValue = conditionMaxValue
        };
        
        Database.Instance.SaveEvent(record);
        LoadEvents();
        
        Logger.Information("Event saved: {EventId}", record.EventId);
    }

    private void OnEventToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle && toggle.DataContext is EventViewModel evt)
        {
            evt.Enabled = toggle.IsChecked ?? false;
            
            // 保存到数据库
            var record = Database.Instance.GetEventByEventId(evt.Id);
            if (record != null)
            {
                record.Enabled = evt.Enabled;
                Database.Instance.SaveEvent(record);
                Logger.Information("Event {EventId} enabled: {Enabled}", evt.Id, evt.Enabled);
            }
        }
    }

    private void OnDeleteEventClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string eventId)
        {
            Database.Instance.DeleteEvent(eventId);
            LoadEvents();
            
            if (_selectedEvent?.Id == eventId)
            {
                _selectedEvent = null;
                EventId.Text = "";
                EventName.Text = "";
            }
            
            Logger.Information("Event deleted: {EventId}", eventId);
        }
    }

    private async void OnTestEventClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedEvent == null || !AppServices.IsInitialized) return;
        
        try
        {
            var deviceType = (TargetDeviceType?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
            var actionType = (ActionType?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "set";
            var connectedDevices = AppServices.Instance.DeviceManager.GetConnectedDevices();
            
            // 根据目标设备类型过滤设备
            var filteredDevices = deviceType == "All" 
                ? connectedDevices 
                : connectedDevices.Where(d => MatchesDeviceType(d, deviceType)).ToList();
            
            if (filteredDevices.Count == 0)
            {
                Logger.Warning("No connected devices matching type {DeviceType} for test", deviceType);
                return;
            }
            
            var device = filteredDevices.First();
            var deviceId = device.Id;
            var channel = (ActionChannel.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
            {
                "A" => Channel.A,
                "B" => Channel.B,
                _ => Channel.AB
            };
            
            var strength = int.TryParse(ActionStrength.Text, out var s) ? s : 50;
            var duration = int.TryParse(ActionDuration.Text, out var d) ? d : 1000;
            var waveformData = "";
            if (actionType == "waveform")
            {
                var customHex = WaveformHexInput?.Text?.Trim() ?? "";
                waveformData = !string.IsNullOrWhiteSpace(customHex)
                    ? customHex
                    : (WaveformType?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "playlist:auto";
            }
            else if (actionType is "mode" or "vibrate_mode")
            {
                waveformData = (ModeType?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "1";
            }
            else if (actionType == "custom_waveform")
            {
                var custom = WaveformHexInput?.Text?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(custom))
                {
                    waveformData = custom;
                }
                else
                {
                    var frequency = Math.Clamp(strength, 1, 100);
                    var pulse = Math.Clamp(duration, 0, 100);
                    waveformData = $"{frequency},{pulse}";
                }
            }

            if (actionType is "peristaltic_start" or "water_start")
            {
                strength = Math.Clamp(strength, 1, 300);
                duration = Math.Clamp(strength * 1000, 1000, 300000);
            }
            else if (actionType == "smart_temp_unlock")
            {
                strength = Math.Clamp(strength, 1, 3600);
                duration = strength * 1000;
            }
            else if (actionType is "peristaltic_stop" or "water_stop" or "pause_all"
                     or "query_status" or "query_battery"
                     or "query_device_info"
                     or "toy_stop_all"
                     or "smart_lock" or "smart_unlock" or "smart_query_state"
                     or "stop_all")
            {
                strength = 0;
                duration = 0;
            }

            var target = AppServices.Instance.DeviceManager.GetDevice(deviceId);
            var channels = channel == Channel.AB
                ? new[] { Channel.A, Channel.B }
                : new[] { channel };

            var request = new DeviceActionRequest
            {
                EventId = EventId.Text ?? _selectedEvent.Id,
                ActionType = actionType,
                Value = strength,
                RawValue = strength,
                DurationMs = duration,
                WaveformData = waveformData,
                Channels = channels
            };

            await _translatorRegistry.ExecuteAsync(target, request);
            
            Logger.Information("Event test triggered: action={Action}, strength={Strength}, channel={Channel}", 
                actionType, strength, channel);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Event test failed");
        }
    }

    private static double? ParseOptionalDouble(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            ? value
            : null;
    }
     
    private static bool MatchesDeviceType(DeviceInfo device, string targetType)
    {
        return targetType switch
        {
            "DGLab" => device.Type == DeviceType.DGLab,
            "Yokonex_Estim" => device.Type == DeviceType.Yokonex && device.YokonexType == YokonexDeviceType.Estim,
            "Yokonex_Enema" => device.Type == DeviceType.Yokonex && device.YokonexType == YokonexDeviceType.Enema,
            "Yokonex_Vibrator" => device.Type == DeviceType.Yokonex && device.YokonexType == YokonexDeviceType.Vibrator,
            "Yokonex_Cup" => device.Type == DeviceType.Yokonex && device.YokonexType == YokonexDeviceType.Cup,
            "Yokonex_SmartLock" => device.Type == DeviceType.Yokonex && device.YokonexType == YokonexDeviceType.SmartLock,
            _ => true
        };
    }

    private static bool LooksLikeHexWaveform(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var parts = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

        if (parts.Length == 0)
        {
            return false;
        }

        if (parts.Length > 1)
        {
            return parts.All(p =>
                p.Length == 16 &&
                System.Text.RegularExpressions.Regex.IsMatch(p, "^[0-9A-Fa-f]+$"));
        }

        var single = parts[0];
        return System.Text.RegularExpressions.Regex.IsMatch(single, "^[0-9A-Fa-f]+$");
    }
}

public class EventViewModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string TriggerType { get; set; } = "";
    public int MinChange { get; set; }
    public int MaxChange { get; set; }
    public string ActionType { get; set; } = "";
    public int Strength { get; set; }
    public int Duration { get; set; }
    public string Channel { get; set; } = "";
    public int Priority { get; set; }
    public string Description { get; set; } = "";
    public string TargetDeviceType { get; set; } = "All";
    public string? WaveformData { get; set; }
    public string? ConditionField { get; set; }
    public string? ConditionOperator { get; set; }
    public double? ConditionValue { get; set; }
    public double? ConditionMaxValue { get; set; }
}
