using Avalonia.Controls;
using Avalonia.Interactivity;
using ChargingPanel.Core;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.DGLab;
using ChargingPanel.Core.Devices.Yokonex;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace ChargingPanel.Desktop.Views.Pages;

public partial class EventsPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<EventsPage>();
    
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
        var records = Database.Instance.GetAllEvents();
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
                WaveformData = record.WaveformData
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
            "death" => "角色死亡",
            "knocked" => "角色倒地",
            "revive" => "角色复活",
            "new-round" => "新回合开始",
            "game-over" => "游戏结束",
            "step-count-changed" => $"步数变化 ≥{record.MinChange}",
            "angle-changed" => $"角度变化 ≥{record.MinChange}°",
            _ => record.TriggerType
        };
        
        var action = record.ActionType switch
        {
            "set" => $"设置强度 {record.Strength}",
            "increase" => $"增加强度 {record.Strength}",
            "decrease" => $"减少强度 {record.Strength}",
            "waveform" => "发送波形",
            "pulse" => "发送脉冲",
            "enema" => "灌肠操作",
            "vibrate" => "震动模式",
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
                "death" => 4,
                "knocked" => 5,
                "revive" => 6,
                "new-round" => 7,
                "game-over" => 8,
                "step-count-changed" => 9,
                "angle-changed" => 10,
                _ => 0
            };
            
            ChangeValue.Text = evt.MinChange.ToString();
            
            // 先更新设备类型，这会更新动作类型选项
            TargetDeviceType.SelectedIndex = evt.TargetDeviceType switch
            {
                "DGLab" => 0,
                "Yokonex_Estim" => 1,
                "Yokonex_Enema" => 2,
                "Yokonex_Vibrator" => 3,
                "Yokonex_Cup" => 4,
                _ => 5
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
            case "step-count-changed":
                ChangeValuePanel.IsVisible = true;
                ChangeValueLabel.Text = "指定步数变化";
                break;
            case "angle-changed":
                ChangeValuePanel.IsVisible = true;
                ChangeValueLabel.Text = "指定角度变化 (°)";
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
                break;
                
            case "Yokonex_Enema":
                // 役次元灌肠器：蠕动泵/抽水泵控制
                ActionType.Items.Add(new ComboBoxItem { Content = "启动蠕动泵", Tag = "peristaltic_start" });
                ActionType.Items.Add(new ComboBoxItem { Content = "停止蠕动泵", Tag = "peristaltic_stop" });
                ActionType.Items.Add(new ComboBoxItem { Content = "启动抽水泵", Tag = "water_start" });
                ActionType.Items.Add(new ComboBoxItem { Content = "停止抽水泵", Tag = "water_stop" });
                ActionType.Items.Add(new ComboBoxItem { Content = "全部暂停", Tag = "pause_all" });
                break;
                
            case "Yokonex_Vibrator":
            case "Yokonex_Cup":
                // 役次元跳蛋/飞机杯：力度控制 + 固定模式
                ActionType.Items.Add(new ComboBoxItem { Content = "设置力度", Tag = "set" });
                ActionType.Items.Add(new ComboBoxItem { Content = "增加力度", Tag = "increase" });
                ActionType.Items.Add(new ComboBoxItem { Content = "减少力度", Tag = "decrease" });
                ActionType.Items.Add(new ComboBoxItem { Content = "切换模式", Tag = "vibrate_mode" });
                break;
                
            default: // All - 只显示通用动作
                ActionType.Items.Add(new ComboBoxItem { Content = "设置强度", Tag = "set" });
                ActionType.Items.Add(new ComboBoxItem { Content = "增加强度", Tag = "increase" });
                ActionType.Items.Add(new ComboBoxItem { Content = "减少强度", Tag = "decrease" });
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
        UpdateStrengthLabel(deviceType);
        UpdateActionPanels(deviceType, currentAction);
    }
    
    private void UpdateStrengthLabel(string deviceType)
    {
        if (StrengthLabel == null) return;
        
        StrengthLabel.Text = deviceType switch
        {
            "DGLab" => "强度值 (0-200)",
            "Yokonex_Estim" => "强度值 (1-276)",
            "Yokonex_Vibrator" or "Yokonex_Cup" => "力度 (0-20)",
            "Yokonex_Enema" => "持续时间 (秒)",
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
    
    private void UpdateWaveformOptions()
    {
        if (WaveformType == null) return;
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
        WaveformType.SelectedIndex = 0;
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
        
        // 更新强度面板的可见性
        var hideStrength = actionType is "peristaltic_start" or "peristaltic_stop" 
            or "water_start" or "water_stop" or "pause_all";
        // 灌肠器的启动操作需要持续时间，停止操作不需要
        if (StrengthLabel != null)
        {
            var strengthPanel = StrengthLabel.Parent as StackPanel;
            if (strengthPanel?.Parent is Grid grid)
            {
                grid.IsVisible = !hideStrength || actionType is "peristaltic_start" or "water_start";
                if (actionType is "peristaltic_start" or "water_start")
                {
                    StrengthLabel.Text = "工作时间 (秒)";
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
        TargetDeviceType.SelectedIndex = 5; // 所有设备
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
        
        // 获取额外数据（波形类型或模式）
        var waveformData = "";
        if (actionType == "waveform")
        {
            waveformData = (WaveformType?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "breath";
        }
        else if (actionType is "mode" or "vibrate_mode")
        {
            waveformData = (ModeType?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "1";
        }
        
        var record = new EventRecord
        {
            EventId = EventId.Text ?? "",
            Name = EventName.Text ?? "",
            Enabled = _selectedEvent.Enabled,
            TriggerType = (TriggerType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "hp-decrease",
            MinChange = changeVal,
            MaxChange = changeVal,
            ActionType = actionType,
            Strength = int.TryParse(ActionStrength.Text, out var str) ? str : 50,
            Duration = int.TryParse(ActionDuration.Text, out var dur) ? dur : 1000,
            Channel = (ActionChannel.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "AB",
            Priority = 10,
            TargetDeviceType = deviceType,
            WaveformData = waveformData
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
            var waveformData = "";
            if (actionType == "waveform")
            {
                waveformData = (WaveformType?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "breath";
            }
            else if (actionType is "mode" or "vibrate_mode")
            {
                waveformData = (ModeType?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "1";
            }
            
            // 根据动作类型执行测试
            switch (actionType)
            {
                case "set":
                case "increase":
                case "decrease":
                    var mode = actionType switch
                    {
                        "increase" => StrengthMode.Increase,
                        "decrease" => StrengthMode.Decrease,
                        _ => StrengthMode.Set
                    };
                    await AppServices.Instance.DeviceManager.SetStrengthAsync(
                        deviceId, channel, strength, mode, "EventTest");
                    break;
                    
                case "waveform":
                    // 郊狼波形测试
                    var waveform = new WaveformData
                    {
                        Frequency = 100,
                        Strength = strength,
                        Duration = 1000
                    };
                    await AppServices.Instance.DeviceManager.SendWaveformAsync(deviceId, channel, waveform);
                    break;
                    
                case "mode":
                case "vibrate_mode":
                    // 模式切换测试 - 通过强度设置触发
                    await AppServices.Instance.DeviceManager.SetStrengthAsync(
                        deviceId, channel, strength, StrengthMode.Set, "EventTest");
                    break;
                    
                case "peristaltic_start":
                case "water_start":
                case "peristaltic_stop":
                case "water_stop":
                case "pause_all":
                    // 灌肠器测试 - 需要直接访问设备
                    Logger.Information("灌肠器动作测试: {Action}", actionType);
                    break;
            }
            
            Logger.Information("Event test triggered: action={Action}, strength={Strength}, channel={Channel}", 
                actionType, strength, channel);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Event test failed");
        }
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
            _ => true
        };
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
}
