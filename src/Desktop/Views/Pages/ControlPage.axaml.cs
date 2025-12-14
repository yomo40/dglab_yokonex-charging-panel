using Avalonia.Controls;
using Avalonia.Interactivity;
using ChargingPanel.Core;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.DGLab;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ChargingPanel.Desktop.Views.Pages;

public partial class ControlPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<ControlPage>();
    private string? _selectedDeviceId;
#pragma warning disable CS0414 // Field is assigned but never used - reserved for slider update logic
    private bool _updatingSliders;
#pragma warning restore CS0414

    public ControlPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RefreshDeviceList();
        
        if (AppServices.IsInitialized)
        {
            AppServices.Instance.DeviceManager.DeviceStatusChanged += OnDeviceStatusChanged;
        }
    }

    private void RefreshDeviceList()
    {
        if (!AppServices.IsInitialized) return;
        
        DeviceSelector.Items.Clear();
        DeviceSelector.Items.Add(new ComboBoxItem { Content = "-- 选择设备 --", Tag = null });
        
        var devices = AppServices.Instance.DeviceManager.GetAllDevices()
            .Where(d => d.Status == DeviceStatus.Connected)
            .ToList();
        
        foreach (var device in devices)
        {
            var icon = device.Type == DeviceType.DGLab ? "⚡" : "📱";
            DeviceSelector.Items.Add(new ComboBoxItem 
            { 
                Content = $"{icon} {device.Name}",
                Tag = device.Id 
            });
        }
        
        DeviceSelector.SelectedIndex = 0;
    }

    private void OnDeviceStatusChanged(object? sender, DeviceStatusChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => RefreshDeviceList());
    }

    private void OnDeviceSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (DeviceSelector.SelectedItem is ComboBoxItem item && item.Tag is string deviceId)
        {
            _selectedDeviceId = deviceId;
            UpdateStrengthDisplay();
            UpdateDeviceTypeBadge();
        }
        else
        {
            _selectedDeviceId = null;
            ResetDisplay();
            DeviceTypeBadge.IsVisible = false;
        }
    }

    private void UpdateDeviceTypeBadge()
    {
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized)
        {
            DeviceTypeBadge.IsVisible = false;
            return;
        }

        try
        {
            var deviceInfo = AppServices.Instance.DeviceManager.GetDeviceInfo(_selectedDeviceId);
            DeviceTypeBadge.IsVisible = true;
            
            if (deviceInfo.Type == DeviceType.DGLab)
            {
                DeviceTypeText.Text = "⚡ DG-LAB";
                DeviceTypeBadge.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8b5cf6"));
                YokonexControlPanel.IsVisible = false;
            }
            else
            {
                DeviceTypeText.Text = "📱 役次元";
                DeviceTypeBadge.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#06b6d4"));
                YokonexControlPanel.IsVisible = true;
            }
            DeviceTypeText.Foreground = Avalonia.Media.Brushes.White;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to update device type badge");
            DeviceTypeBadge.IsVisible = false;
            YokonexControlPanel.IsVisible = false;
        }
    }

    private void UpdateStrengthDisplay()
    {
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized) return;
        
        try
        {
            var info = AppServices.Instance.DeviceManager.GetDeviceInfo(_selectedDeviceId);
            _updatingSliders = true;
            
            SliderA.Value = info.State.Strength.ChannelA;
            SliderB.Value = info.State.Strength.ChannelB;
            StrengthADisplay.Text = info.State.Strength.ChannelA.ToString();
            StrengthBDisplay.Text = info.State.Strength.ChannelB.ToString();
            SliderAValue.Text = info.State.Strength.ChannelA.ToString();
            SliderBValue.Text = info.State.Strength.ChannelB.ToString();
            LimitA.Text = info.State.Strength.LimitA.ToString();
            LimitB.Text = info.State.Strength.LimitB.ToString();
            
            _updatingSliders = false;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to update strength display");
        }
    }

    private void ResetDisplay()
    {
        _updatingSliders = true;
        SliderA.Value = 0;
        SliderB.Value = 0;
        StrengthADisplay.Text = "0";
        StrengthBDisplay.Text = "0";
        SliderAValue.Text = "0";
        SliderBValue.Text = "0";
        YokonexControlPanel.IsVisible = false;
        _updatingSliders = false;
    }

    private void OnSliderAChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // 只更新显示值，不立即应用
        var value = (int)e.NewValue;
        SliderAValue.Text = value.ToString();
    }

    private void OnSliderBChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // 只更新显示值，不立即应用
        var value = (int)e.NewValue;
        SliderBValue.Text = value.ToString();
    }

    private async void OnApplyStrengthA(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized) return;
        
        var value = (int)SliderA.Value;
        
        try
        {
            await AppServices.Instance.DeviceManager.SetStrengthAsync(_selectedDeviceId, Channel.A, value, StrengthMode.Set);
            StrengthADisplay.Text = value.ToString();
            ShowStatus($"通道 A 强度已设置为 {value}");
            Logger.Information("Channel A strength set to {Value}", value);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to set channel A strength");
            ShowStatus($"设置失败: {ex.Message}");
        }
    }

    private async void OnApplyStrengthB(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized) return;
        
        var value = (int)SliderB.Value;
        
        try
        {
            await AppServices.Instance.DeviceManager.SetStrengthAsync(_selectedDeviceId, Channel.B, value, StrengthMode.Set);
            StrengthBDisplay.Text = value.ToString();
            ShowStatus($"通道 B 强度已设置为 {value}");
            Logger.Information("Channel B strength set to {Value}", value);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to set channel B strength");
            ShowStatus($"设置失败: {ex.Message}");
        }
    }

    // Yokonex 设备控制方法
    private void OnYokonexEstimSliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (this.FindControl<TextBlock>("YokonexEstimValue") is TextBlock tb)
            tb.Text = ((int)e.NewValue).ToString();
    }

    private void OnYokonexVibrateSliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (this.FindControl<TextBlock>("YokonexVibrateValue") is TextBlock tb)
            tb.Text = ((int)e.NewValue).ToString();
    }

    private void OnYokonexOtherSliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (this.FindControl<TextBlock>("YokonexOtherValue") is TextBlock tb)
            tb.Text = ((int)e.NewValue).ToString();
    }

    private async void OnApplyYokonexEstim(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized) return;
        
        var slider = this.FindControl<Slider>("YokonexEstimStrength");
        var modeCombo = this.FindControl<ComboBox>("YokonexEstimMode");
        if (slider == null) return;
        
        var value = (int)slider.Value;
        var mode = modeCombo?.SelectedIndex ?? 0;
        
        try
        {
            // TODO: 实现 Yokonex 电击控制
            await AppServices.Instance.DeviceManager.SetStrengthAsync(_selectedDeviceId, Channel.A, value, StrengthMode.Set);
            ShowStatus($"电击强度已设置为 {value}，模式 {mode}");
            Logger.Information("Yokonex Estim strength set to {Value}, mode {Mode}", value, mode);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to set Yokonex Estim strength");
            ShowStatus($"设置失败: {ex.Message}");
        }
    }

    private async void OnApplyYokonexVibrate(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized) return;
        
        var slider = this.FindControl<Slider>("YokonexVibrateStrength");
        var modeCombo = this.FindControl<ComboBox>("YokonexVibrateMode");
        if (slider == null) return;
        
        var value = (int)slider.Value;
        var mode = modeCombo?.SelectedIndex ?? 0;
        
        try
        {
            // TODO: 实现 Yokonex 震动控制
            await AppServices.Instance.DeviceManager.SetStrengthAsync(_selectedDeviceId, Channel.B, value, StrengthMode.Set);
            ShowStatus($"震动强度已设置为 {value}，模式 {mode}");
            Logger.Information("Yokonex Vibrate strength set to {Value}, mode {Mode}", value, mode);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to set Yokonex Vibrate strength");
            ShowStatus($"设置失败: {ex.Message}");
        }
    }

    private async void OnApplyYokonexOther(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized) return;
        
        var slider = this.FindControl<Slider>("YokonexOtherStrength");
        var modeCombo = this.FindControl<ComboBox>("YokonexOtherMode");
        if (slider == null) return;
        
        var value = (int)slider.Value;
        var mode = modeCombo?.SelectedIndex ?? 0;
        
        try
        {
            // TODO: 根据 Yokonex 其他设备类型进行控制
            ShowStatus($"其他设备强度已设置为 {value}，模式 {mode}");
            Logger.Information("Yokonex Other device strength set to {Value}, mode {Mode}", value, mode);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to set Yokonex Other device strength");
            ShowStatus($"设置失败: {ex.Message}");
        }
    }

    private async void OnQuickControl(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized) return;
        
        var parts = tag.Split(',');
        if (parts.Length != 2) return;
        
        var channel = parts[0] == "A" ? Channel.A : Channel.B;
        if (!int.TryParse(parts[1], out var value)) return;
        
        try
        {
            if (value == 0)
            {
                // 归零
                await AppServices.Instance.DeviceManager.SetStrengthAsync(_selectedDeviceId, channel, 0, StrengthMode.Set);
            }
            else
            {
                // 增减
                var mode = value > 0 ? StrengthMode.Increase : StrengthMode.Decrease;
                await AppServices.Instance.DeviceManager.SetStrengthAsync(_selectedDeviceId, channel, Math.Abs(value), mode);
            }
            
            UpdateStrengthDisplay();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Quick control failed");
        }
    }

    private async void OnEmergencyStop(object? sender, RoutedEventArgs e)
    {
        if (!AppServices.IsInitialized) return;
        
        try
        {
            await AppServices.Instance.DeviceManager.EmergencyStopAllAsync();
            UpdateStrengthDisplay();
            Logger.Warning("Emergency stop triggered from control page");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Emergency stop failed");
        }
    }

    private async void OnSetLimits(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized) return;
        
        if (!int.TryParse(LimitA.Text, out var limitA)) limitA = 200;
        if (!int.TryParse(LimitB.Text, out var limitB)) limitB = 200;
        
        try
        {
            await AppServices.Instance.DeviceManager.SetLimitsAsync(_selectedDeviceId, limitA, limitB);
            Logger.Information("Limits set: A={LimitA}, B={LimitB}", limitA, limitB);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to set limits");
        }
    }

    private async void OnClearQueueA(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized) return;
        
        try
        {
            await AppServices.Instance.DeviceManager.ClearWaveformQueueAsync(_selectedDeviceId, Channel.A);
            Logger.Information("Cleared waveform queue for channel A");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to clear queue A");
        }
    }

    private async void OnClearQueueB(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized) return;
        
        try
        {
            await AppServices.Instance.DeviceManager.ClearWaveformQueueAsync(_selectedDeviceId, Channel.B);
            Logger.Information("Cleared waveform queue for channel B");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to clear queue B");
        }
    }
    
    private async void OnClearQueueAll(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized) return;
        
        try
        {
            await AppServices.Instance.DeviceManager.ClearWaveformQueueAsync(_selectedDeviceId, Channel.A);
            await AppServices.Instance.DeviceManager.ClearWaveformQueueAsync(_selectedDeviceId, Channel.B);
            Logger.Information("Cleared waveform queue for all channels");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to clear all queues");
        }
    }

    private async void OnWaveformClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string preset) return;
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized) return;
        
        // 检查设备类型
        var device = AppServices.Instance.DeviceManager.GetDevice(_selectedDeviceId);
        if (device == null)
        {
            ShowStatus("设备未找到或未连接");
            return;
        }

        var deviceInfo = AppServices.Instance.DeviceManager.GetDeviceInfo(_selectedDeviceId);
        if (deviceInfo.Type == DeviceType.Yokonex)
        {
            // 役次元设备不支持直接波形，使用事件ID模式
            ShowStatus("役次元设备使用事件模式，波形预设将转换为强度变化");
            Logger.Information("Yokonex device detected, waveform preset converted to intensity change");
        }

        var waveform = preset switch
        {
            "breathing" => new WaveformData { Frequency = 50, Strength = 50, Duration = 2000 },
            "heartbeat" => new WaveformData { Frequency = 30, Strength = 60, Duration = 1500 },
            "vibration" => new WaveformData { Frequency = 200, Strength = 50, Duration = 1000 },
            "climb" => new WaveformData { Frequency = 100, Strength = 30, Duration = 2000 },
            "random" => new WaveformData { Frequency = new Random().Next(50, 200), Strength = new Random().Next(20, 80), Duration = 1000 },
            "pulse" => new WaveformData { Frequency = 150, Strength = 70, Duration = 500 },
            _ => new WaveformData()
        };
        
        try
        {
            await AppServices.Instance.DeviceManager.SendWaveformAsync(_selectedDeviceId, Channel.AB, waveform);
            Logger.Information("Sent waveform preset: {Preset}", preset);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to send waveform");
            ShowStatus($"发送波形失败: {ex.Message}");
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

    #region Waveform Preset Editor

    private string? _editingPresetId;

    private void OnNewWaveformPresetClick(object? sender, RoutedEventArgs e)
    {
        _editingPresetId = null;
        WaveformPresetName.Text = "";
        WaveformPresetIcon.Text = "🌊";
        WaveformPresetChannel.SelectedIndex = 2; // AB
        WaveformPresetDuration.Text = "1000";
        WaveformPresetData.Text = "";
        WaveformPresetIntensity.Value = 50;
        WaveformPresetIntensityText.Text = "50%";
        BtnDeleteWaveformPreset.IsVisible = false;
        WaveformEditorPanel.IsVisible = true;
    }

    private void OnCloseWaveformEditorClick(object? sender, RoutedEventArgs e)
    {
        WaveformEditorPanel.IsVisible = false;
        _editingPresetId = null;
    }

    private void OnSaveWaveformPresetClick(object? sender, RoutedEventArgs e)
    {
        var name = WaveformPresetName.Text?.Trim();
        var icon = WaveformPresetIcon.Text?.Trim() ?? "🌊";
        var waveformData = WaveformPresetData.Text?.Trim() ?? "";
        
        if (string.IsNullOrEmpty(name))
        {
            ShowStatus("请输入预设名称");
            return;
        }
        
        if (string.IsNullOrEmpty(waveformData))
        {
            ShowStatus("请输入波形数据");
            return;
        }
        
        // 验证 HEX 格式
        if (!System.Text.RegularExpressions.Regex.IsMatch(waveformData, "^[0-9A-Fa-f]+$"))
        {
            ShowStatus("波形数据必须是有效的十六进制格式");
            return;
        }
        
        var channel = (WaveformPresetChannel.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "AB";
        if (!int.TryParse(WaveformPresetDuration.Text, out var duration))
            duration = 1000;
        var intensity = (int)WaveformPresetIntensity.Value;
        
        try
        {
            var db = ChargingPanel.Core.Data.Database.Instance;
            
            if (string.IsNullOrEmpty(_editingPresetId))
            {
                // 新建预设
                var preset = new ChargingPanel.Core.Data.WaveformPresetRecord
                {
                    Name = name,
                    Description = "",
                    Icon = icon,
                    Channel = channel,
                    WaveformData = waveformData.ToUpper(),
                    Duration = duration,
                    Intensity = intensity,
                    IsBuiltIn = false,
                    SortOrder = 100
                };
                db.AddWaveformPreset(preset);
                ShowStatus($"预设 \"{name}\" 已保存");
                Logger.Information("Created waveform preset: {Name}", name);
            }
            else
            {
                // 更新预设
                var existing = db.GetWaveformPreset(_editingPresetId);
                if (existing != null)
                {
                    existing.Name = name;
                    existing.Icon = icon;
                    existing.Channel = channel;
                    existing.WaveformData = waveformData.ToUpper();
                    existing.Duration = duration;
                    existing.Intensity = intensity;
                    db.UpdateWaveformPreset(_editingPresetId, existing);
                    ShowStatus($"预设 \"{name}\" 已更新");
                    Logger.Information("Updated waveform preset: {Name}", name);
                }
            }
            
            WaveformEditorPanel.IsVisible = false;
            _editingPresetId = null;
            RefreshWaveformPresets();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to save waveform preset");
            ShowStatus($"保存失败: {ex.Message}");
        }
    }

    private void OnDeleteWaveformPresetClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_editingPresetId)) return;
        
        try
        {
            var db = ChargingPanel.Core.Data.Database.Instance;
            if (db.DeleteWaveformPreset(_editingPresetId))
            {
                ShowStatus("预设已删除");
                Logger.Information("Deleted waveform preset: {Id}", _editingPresetId);
                WaveformEditorPanel.IsVisible = false;
                _editingPresetId = null;
                RefreshWaveformPresets();
            }
            else
            {
                ShowStatus("无法删除内置预设");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to delete waveform preset");
            ShowStatus($"删除失败: {ex.Message}");
        }
    }

    private void RefreshWaveformPresets()
    {
        try
        {
            var db = ChargingPanel.Core.Data.Database.Instance;
            var presets = db.GetAllWaveformPresets();
            
            WaveformPresetsPanel.Children.Clear();
            
            foreach (var preset in presets)
            {
                var btn = new Button
                {
                    Content = $"{preset.Icon} {preset.Name}",
                    Tag = preset.Id,
                    Background = preset.IsBuiltIn 
                        ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#313244"))
                        : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1E1E2E")),
                    Foreground = Avalonia.Media.Brushes.White,
                    BorderBrush = preset.IsBuiltIn
                        ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#45475A"))
                        : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8b5cf6")),
                    BorderThickness = new Avalonia.Thickness(1),
                    Margin = new Avalonia.Thickness(0, 0, 10, 10),
                    Padding = new Avalonia.Thickness(15, 10),
                    CornerRadius = new Avalonia.CornerRadius(6)
                };
                
                btn.Click += OnCustomWaveformClick;
                btn.DoubleTapped += OnWaveformPresetDoubleClick;
                
                WaveformPresetsPanel.Children.Add(btn);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to refresh waveform presets");
        }
    }

    private async void OnCustomWaveformClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string presetId) return;
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized) return;
        
        try
        {
            var db = ChargingPanel.Core.Data.Database.Instance;
            var preset = db.GetWaveformPreset(presetId);
            if (preset == null) return;
            
            // 将自定义预设转换为波形数据
            var waveform = new WaveformData
            {
                HexData = preset.WaveformData,
                Strength = preset.Intensity,
                Duration = preset.Duration
            };
            
            var channel = preset.Channel switch
            {
                "A" => Channel.A,
                "B" => Channel.B,
                _ => Channel.AB
            };
            
            await AppServices.Instance.DeviceManager.SendWaveformAsync(_selectedDeviceId, channel, waveform);
            Logger.Information("Sent custom waveform preset: {Name}", preset.Name);
            ShowStatus($"已发送波形: {preset.Name}");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to send custom waveform");
            ShowStatus($"发送波形失败: {ex.Message}");
        }
    }

    private void OnWaveformPresetDoubleClick(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string presetId) return;
        
        try
        {
            var db = ChargingPanel.Core.Data.Database.Instance;
            var preset = db.GetWaveformPreset(presetId);
            if (preset == null) return;
            
            // 内置预设不可编辑
            if (preset.IsBuiltIn)
            {
                ShowStatus("内置预设不可编辑");
                return;
            }
            
            // 填充编辑器
            _editingPresetId = presetId;
            WaveformPresetName.Text = preset.Name;
            WaveformPresetIcon.Text = preset.Icon;
            WaveformPresetChannel.SelectedIndex = preset.Channel switch
            {
                "A" => 0,
                "B" => 1,
                _ => 2
            };
            WaveformPresetDuration.Text = preset.Duration.ToString();
            WaveformPresetData.Text = preset.WaveformData;
            WaveformPresetIntensity.Value = preset.Intensity;
            WaveformPresetIntensityText.Text = $"{preset.Intensity}%";
            BtnDeleteWaveformPreset.IsVisible = true;
            WaveformEditorPanel.IsVisible = true;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to load waveform preset for editing");
        }
    }

    #endregion
}
