using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Services;
using ChargingPanel.Desktop.Views.Pages;

namespace ChargingPanel.Desktop.Views;

public partial class SensorRuleDialog : Window
{
    private string? _sensorId;
    private string? _ruleId;
    
    public SensorRule? Result { get; private set; }
    
    public SensorRuleDialog()
    {
        InitializeComponent();
    }
    
    public void SetSensorId(string? sensorId)
    {
        _sensorId = sensorId;
    }
    
    public void SetDevices(List<SensorDeviceItem> devices)
    {
        TargetDevice.Items.Clear();
        TargetDevice.Items.Add(new ComboBoxItem { Content = "所有设备", Tag = "" });
        
        foreach (var device in devices)
        {
            if (!string.IsNullOrEmpty(device.Id))
            {
                TargetDevice.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });
            }
        }
        
        TargetDevice.SelectedIndex = 0;
    }
    
    public void SetRule(SensorRule rule)
    {
        _ruleId = rule.Id;
        _sensorId = rule.DeviceId;
        
        RuleName.Text = rule.Name;
        
        // 设置传感器类型
        SensorType.SelectedIndex = rule.SensorType switch
        {
            Core.Services.SensorType.Step => 0,
            Core.Services.SensorType.Angle => 1,
            Core.Services.SensorType.Channel => 2,
            Core.Services.SensorType.Pressure => 3,
            Core.Services.SensorType.ExternalVoltage => 4,
            _ => 0
        };
        
        // 设置触发类型
        TriggerType.SelectedIndex = rule.TriggerType switch
        {
            SensorTriggerType.Threshold => 0,
            SensorTriggerType.Change => 1,
            SensorTriggerType.Connect => 2,
            SensorTriggerType.Disconnect => 3,
            _ => 0
        };
        
        Threshold.Text = rule.Threshold.ToString();
        
        // 设置目标通道
        TargetChannel.SelectedIndex = rule.TargetChannel switch
        {
            Channel.A => 0,
            Channel.B => 1,
            Channel.AB => 2,
            _ => 0
        };
        
        // 设置动作类型
        ActionType.SelectedIndex = rule.Action switch
        {
            SensorAction.Set => 0,
            SensorAction.Increase => 1,
            SensorAction.Decrease => 2,
            SensorAction.Pulse => 3,
            SensorAction.Wave => 4,
            _ => 1
        };
        
        Value.Text = rule.Value.ToString();
        Duration.Text = rule.Duration.ToString();
        Cooldown.Text = rule.CooldownMs.ToString();
        RuleEnabled.IsChecked = rule.Enabled;
        
        // 设置目标设备
        for (int i = 0; i < TargetDevice.Items.Count; i++)
        {
            if (TargetDevice.Items[i] is ComboBoxItem item && item.Tag?.ToString() == rule.TargetDeviceId)
            {
                TargetDevice.SelectedIndex = i;
                break;
            }
        }
    }
    
    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        // 验证输入
        if (string.IsNullOrWhiteSpace(RuleName.Text))
        {
            RuleName.Focus();
            return;
        }
        
        // 解析传感器类型
        var sensorType = (SensorType.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "Step" => Core.Services.SensorType.Step,
            "Angle" => Core.Services.SensorType.Angle,
            "Channel" => Core.Services.SensorType.Channel,
            "Pressure" => Core.Services.SensorType.Pressure,
            "ExternalVoltage" => Core.Services.SensorType.ExternalVoltage,
            _ => Core.Services.SensorType.Step
        };
        
        // 解析触发类型
        var triggerType = (TriggerType.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "Threshold" => SensorTriggerType.Threshold,
            "Change" => SensorTriggerType.Change,
            "Connect" => SensorTriggerType.Connect,
            "Disconnect" => SensorTriggerType.Disconnect,
            _ => SensorTriggerType.Threshold
        };
        
        // 解析目标通道
        var targetChannel = (TargetChannel.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "A" => Channel.A,
            "B" => Channel.B,
            "AB" => Channel.AB,
            _ => Channel.A
        };
        
        // 解析动作类型
        var action = (ActionType.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "Set" => SensorAction.Set,
            "Increase" => SensorAction.Increase,
            "Decrease" => SensorAction.Decrease,
            "Pulse" => SensorAction.Pulse,
            "Wave" => SensorAction.Wave,
            _ => SensorAction.Increase
        };
        
        // 解析目标设备
        var targetDeviceId = (TargetDevice.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrEmpty(targetDeviceId)) targetDeviceId = null;
        
        Result = new SensorRule
        {
            Id = _ruleId ?? "",
            Name = RuleName.Text,
            DeviceId = _sensorId,
            SensorType = sensorType,
            TriggerType = triggerType,
            Threshold = double.TryParse(Threshold.Text, out var t) ? t : 10,
            TargetDeviceId = targetDeviceId,
            TargetChannel = targetChannel,
            Action = action,
            Value = int.TryParse(Value.Text, out var v) ? v : 10,
            Duration = int.TryParse(Duration.Text, out var d) ? d : 500,
            CooldownMs = int.TryParse(Cooldown.Text, out var c) ? c : 1000,
            Enabled = RuleEnabled.IsChecked ?? true
        };
        
        Close();
    }
    
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }
}
