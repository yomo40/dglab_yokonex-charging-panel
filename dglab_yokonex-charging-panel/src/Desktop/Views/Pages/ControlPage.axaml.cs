using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ChargingPanel.Core;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Devices.DGLab;
using ChargingPanel.Core.Devices.Yokonex;
using ChargingPanel.Core.Protocols;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ChargingPanel.Desktop.Views.Pages;

public partial class ControlPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<ControlPage>();
    private const int DGLabStrengthMax = 200;
    private const int YokonexEmsStrengthMax = 276;
    private const int YokonexMotorStrengthMax = 20;
    private const int YokonexInjectionStrengthMax = 100;
    private const int HeartbeatIntervalMinimumMs = 1000;

    private string? _selectedDeviceId;
    private bool _updatingSliders;
    private bool _updatingDefaultSliders;
    private readonly DispatcherTimer _statusRefreshDebounceTimer;
    private bool _isDeviceStatusSubscribed;
    private bool _deviceRefreshRequested;
    private CancellationTokenSource? _waveformPlaylistCts;
    private Task? _waveformPlaylistTask;

    public ControlPage()
    {
        InitializeComponent();
        _statusRefreshDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _statusRefreshDebounceTimer.Tick += OnStatusRefreshDebounceTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RefreshDeviceList();
        RefreshWaveformPresets();
        LoadDeviceDefaultSettings();
        
        if (AppServices.IsInitialized && !_isDeviceStatusSubscribed)
        {
            AppServices.Instance.DeviceManager.DeviceStatusChanged += OnDeviceStatusChanged;
            _isDeviceStatusSubscribed = true;
        }
    }

    private void RefreshDeviceList()
    {
        if (!AppServices.IsInitialized) return;

        var previousSelectedId = _selectedDeviceId;

        DeviceSelector.Items.Clear();
        DeviceSelector.Items.Add(new ComboBoxItem { Content = "-- 选择设备 --", Tag = null });
        
        var devices = AppServices.Instance.DeviceManager.GetAllDevices()
            .Where(d => d.Status == DeviceStatus.Connected)
            .Where(d => d switch
            {
                { Type: DeviceType.DGLab, DGLabVersion: DGLabVersion.V3WirelessSensor or DGLabVersion.PawPrints } => false,
                { Type: DeviceType.Yokonex, YokonexType: YokonexDeviceType.SmartLock } => false,
                _ => true
            })
            .ToList();
        
        var selectedIndex = 0;
        var currentIndex = 1;

        foreach (var device in devices)
        {
            var icon = device.Type switch
            {
                DeviceType.DGLab => "郊狼",
                DeviceType.Yokonex => "役次元",
                DeviceType.Virtual => "虚拟",
                _ => "设备"
            };
            DeviceSelector.Items.Add(new ComboBoxItem 
            { 
                Content = $"[{icon}] {device.Name}",
                Tag = device.Id 
            });

            if (!string.IsNullOrWhiteSpace(previousSelectedId) &&
                string.Equals(previousSelectedId, device.Id, StringComparison.Ordinal))
            {
                selectedIndex = currentIndex;
            }

            currentIndex++;
        }

        DeviceSelector.SelectedIndex = selectedIndex;
    }

    private void OnDeviceStatusChanged(object? sender, DeviceStatusChangedEventArgs e)
    {
        RequestDeviceListRefresh();
    }

    private void RequestDeviceListRefresh()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RequestDeviceListRefresh);
            return;
        }

        _deviceRefreshRequested = true;
        if (!_statusRefreshDebounceTimer.IsEnabled)
        {
            _statusRefreshDebounceTimer.Start();
        }
    }

    private void OnStatusRefreshDebounceTick(object? sender, EventArgs e)
    {
        _statusRefreshDebounceTimer.Stop();
        if (!_deviceRefreshRequested)
        {
            return;
        }

        _deviceRefreshRequested = false;
        RefreshDeviceList();
    }

    private void OnDeviceSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (DeviceSelector.SelectedItem is ComboBoxItem item && item.Tag is string deviceId)
        {
            if (!string.Equals(_selectedDeviceId, deviceId, StringComparison.Ordinal))
            {
                StopWaveformPlaylist();
            }

            _selectedDeviceId = deviceId;
            UpdateStrengthDisplay();
            UpdateDeviceTypeBadge();
        }
        else
        {
            StopWaveformPlaylist();
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
            EstimStrengthPanel.IsVisible = false;
            YokonexControlPanel.IsVisible = false;
            DGLabControlPanel.IsVisible = false;
            SensorControlPanel.IsVisible = false;
            UnsubscribeYokonexEvents();
            UnsubscribeDGLabAccessoryEvents();
            return;
        }

        try
        {
            var deviceInfo = AppServices.Instance.DeviceManager.GetDeviceInfo(_selectedDeviceId);
            var device = AppServices.Instance.DeviceManager.GetDevice(_selectedDeviceId);
            var yokonexType = ResolveYokonexType(deviceInfo, device);
            DeviceTypeBadge.IsVisible = true;

            EstimStrengthPanel.IsVisible = false;
            YokonexControlPanel.IsVisible = false;
            DGLabControlPanel.IsVisible = false;
            SensorControlPanel.IsVisible = false;
            DGLabOnlyControls.IsVisible = false;
            YokonexChannelStatus.IsVisible = false;
            UnsubscribeYokonexEvents();
            UnsubscribeDGLabAccessoryEvents();

            if (deviceInfo.Type == DeviceType.DGLab)
            {
                DeviceTypeText.Text = "郊狼电击器";
                DeviceTypeBadge.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0EA5E9"));
                EstimStrengthPanel.IsVisible = true;
                DGLabControlPanel.IsVisible = true;
                DGLabOnlyControls.IsVisible = true;
                ElectroAdvancedTagText.Text = "DG-LAB";
                // 47L120100 / PawPrints 仅保留后端预留，不再在控制页展示。
                SensorControlPanel.IsVisible = false;
            }
            else if (deviceInfo.Type == DeviceType.Yokonex)
            {
                var typeText = GetYokonexTypeText(yokonexType);
                DeviceTypeText.Text = $"役次元{typeText}";
                DeviceTypeBadge.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#14B8A6"));
                var showYokonexPanel = yokonexType != YokonexDeviceType.SmartLock;
                var connectedYokonexDevices = AppServices.Instance.DeviceManager.GetAllDevices()
                    .Where(d => d.Status == DeviceStatus.Connected
                                && d.Type == DeviceType.Yokonex
                                && d.YokonexType != YokonexDeviceType.SmartLock)
                    .ToList();

                YokonexControlPanel.IsVisible = showYokonexPanel;
                if (showYokonexPanel)
                {
                    ConfigureYokonexSections(yokonexType, connectedYokonexDevices);
                }

                if (yokonexType == YokonexDeviceType.Estim)
                {
                    EstimStrengthPanel.IsVisible = true;
                    DGLabControlPanel.IsVisible = true;
                    DGLabOnlyControls.IsVisible = false;
                    ElectroAdvancedTagText.Text = "YOKONEX EMS";
                    YokonexChannelStatus.IsVisible = true;
                    UpdateYokonexStatus();
                    SubscribeYokonexEvents();
                }
            }
            else if (deviceInfo.Type == DeviceType.Virtual)
            {
                // 虚拟设备 - 显示通用控制面板
                DeviceTypeText.Text = "虚拟设备";
                DeviceTypeBadge.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F59E0B"));
                EstimStrengthPanel.IsVisible = true;
                DGLabControlPanel.IsVisible = true;
                DGLabOnlyControls.IsVisible = true;
                ElectroAdvancedTagText.Text = "VIRTUAL";
                SensorControlPanel.IsVisible = false;
            }
            DeviceTypeText.Foreground = Avalonia.Media.Brushes.White;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to update device type badge");
            DeviceTypeBadge.IsVisible = false;
            EstimStrengthPanel.IsVisible = false;
            YokonexControlPanel.IsVisible = false;
            DGLabControlPanel.IsVisible = false;
            SensorControlPanel.IsVisible = false;
            DGLabOnlyControls.IsVisible = false;
            YokonexChannelStatus.IsVisible = false;
            UnsubscribeYokonexEvents();
            UnsubscribeDGLabAccessoryEvents();
        }
    }

    private static string GetYokonexTypeText(YokonexDeviceType type) => type switch
    {
        YokonexDeviceType.Enema => "灌肠器",
        YokonexDeviceType.Vibrator => "跳蛋",
        YokonexDeviceType.Cup => "飞机杯",
        YokonexDeviceType.SmartLock => "智能锁（预留）",
        _ => "电击器"
    };

    private static YokonexDeviceType ResolveYokonexType(DeviceInfo info, IDevice? device)
    {
        if (info.YokonexType.HasValue)
        {
            return info.YokonexType.Value;
        }

        if (device is IYokonexDevice yokonexDevice)
        {
            return yokonexDevice.YokonexType;
        }

        if (device is IYokonexEnemaDevice)
        {
            return YokonexDeviceType.Enema;
        }

        if (device is IYokonexToyDevice)
        {
            return YokonexDeviceType.Vibrator;
        }

        return YokonexDeviceType.Estim;
    }

    private void ConfigureYokonexSections(YokonexDeviceType selectedType, IReadOnlyCollection<DeviceInfo> connectedYokonexDevices)
    {
        var isMultiDeviceMode = connectedYokonexDevices.Count > 1;
        var hasEstim = connectedYokonexDevices.Any(d => (d.YokonexType ?? YokonexDeviceType.Estim) == YokonexDeviceType.Estim);
        var hasEnema = connectedYokonexDevices.Any(d => d.YokonexType == YokonexDeviceType.Enema);
        var hasVibrator = connectedYokonexDevices.Any(d => d.YokonexType == YokonexDeviceType.Vibrator);
        var hasCup = connectedYokonexDevices.Any(d => d.YokonexType == YokonexDeviceType.Cup);
        var hasToy = hasVibrator || hasCup;

        var showEstim = isMultiDeviceMode ? hasEstim : selectedType == YokonexDeviceType.Estim;
        var showEnema = isMultiDeviceMode ? hasEnema : selectedType == YokonexDeviceType.Enema;
        var showToy = isMultiDeviceMode ? hasToy : selectedType is YokonexDeviceType.Vibrator or YokonexDeviceType.Cup;

        YokonexEstimSection.IsVisible = showEstim;
        YokonexEnemaSection.IsVisible = showEnema;
        YokonexToySection.IsVisible = showToy;

        YokonexEstimTitle.Text = "电击器控制";
        YokonexEnemaTitle.Text = "灌肠器控制";
        YokonexToyTitle.Text = isMultiDeviceMode
            ? (hasVibrator && hasCup ? "跳蛋/飞机杯控制" : (hasCup ? "飞机杯控制" : "跳蛋控制"))
            : (selectedType == YokonexDeviceType.Cup ? "飞机杯控制" : "跳蛋控制");
    }

    private string GetCurrentDeviceTypeName()
    {
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized)
        {
            return "未知设备";
        }

        try
        {
            var info = AppServices.Instance.DeviceManager.GetDeviceInfo(_selectedDeviceId);
            var device = AppServices.Instance.DeviceManager.GetDevice(_selectedDeviceId);
            if (info.Type == DeviceType.DGLab)
            {
                return "郊狼电击器";
            }

            if (info.Type != DeviceType.Yokonex)
            {
                return "设备";
            }

            return GetYokonexTypeText(ResolveYokonexType(info, device));
        }
        catch
        {
            return "设备";
        }
    }

    private (string DeviceId, string DeviceName, IDevice Device)? ResolveYokonexTargetDevice(Func<IDevice, bool> predicate)
    {
        if (!AppServices.IsInitialized)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(_selectedDeviceId))
        {
            var selectedDevice = AppServices.Instance.DeviceManager.GetDevice(_selectedDeviceId);
            if (selectedDevice.Status == DeviceStatus.Connected && predicate(selectedDevice))
            {
                var selectedInfo = AppServices.Instance.DeviceManager.GetDeviceInfo(_selectedDeviceId);
                return (_selectedDeviceId, selectedInfo.Name, selectedDevice);
            }
        }

        var candidates = AppServices.Instance.DeviceManager.GetAllDevices()
            .Where(d => d.Status == DeviceStatus.Connected && d.Type == DeviceType.Yokonex)
            .ToList();

        foreach (var info in candidates)
        {
            var device = AppServices.Instance.DeviceManager.GetDevice(info.Id);
            if (predicate(device))
            {
                return (info.Id, info.Name, device);
            }
        }

        return null;
    }

    private string BuildRoutedSuffix(string targetDeviceId, string targetDeviceName)
    {
        return string.Equals(targetDeviceId, _selectedDeviceId, StringComparison.Ordinal)
            ? string.Empty
            : $"（已路由到 {targetDeviceName}）";
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (AppServices.IsInitialized && _isDeviceStatusSubscribed)
        {
            AppServices.Instance.DeviceManager.DeviceStatusChanged -= OnDeviceStatusChanged;
            _isDeviceStatusSubscribed = false;
        }

        _statusRefreshDebounceTimer.Stop();
        _deviceRefreshRequested = false;
        StopWaveformPlaylist();
        UnsubscribeYokonexEvents();
        UnsubscribeDGLabAccessoryEvents();
    }
    
    private IYokonexEmsDevice? _currentYokonexDevice;
    private IDGLabExternalVoltageSensorDevice? _currentExternalVoltageDevice;
    
    private void SubscribeYokonexEvents()
    {
        // 取消之前的订阅
        UnsubscribeYokonexEvents();
        
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized) return;
        
        var device = AppServices.Instance.DeviceManager.GetDevice(_selectedDeviceId);
        if (device is IYokonexEmsDevice emsDevice)
        {
            _currentYokonexDevice = emsDevice;
            emsDevice.ChannelConnectionChanged += OnChannelConnectionChanged;
            emsDevice.StepCountChanged += OnStepCountChanged;
            emsDevice.AngleChanged += OnAngleChanged;
        }
    }
    
    private void UnsubscribeYokonexEvents()
    {
        if (_currentYokonexDevice != null)
        {
            _currentYokonexDevice.ChannelConnectionChanged -= OnChannelConnectionChanged;
            _currentYokonexDevice.StepCountChanged -= OnStepCountChanged;
            _currentYokonexDevice.AngleChanged -= OnAngleChanged;
            _currentYokonexDevice = null;
        }
    }
    
    private void OnChannelConnectionChanged(object? sender, (bool ChannelA, bool ChannelB) state)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateChannelConnectionUI(state));
    }
    
    private void OnStepCountChanged(object? sender, int stepCount)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            StepCountText.Text = stepCount.ToString();
        });
    }
    
    private void OnAngleChanged(object? sender, (float X, float Y, float Z) angle)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            AngleText.Text = $"X:{angle.X:F0}° Y:{angle.Y:F0}° Z:{angle.Z:F0}°";
        });
    }
    
    private void UpdateYokonexStatus()
    {
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized) return;
        
        var device = AppServices.Instance.DeviceManager.GetDevice(_selectedDeviceId);
        if (device is IYokonexEmsDevice emsDevice)
        {
            UpdateChannelConnectionUI(emsDevice.ChannelConnectionState);
            StepCountText.Text = emsDevice.StepCount.ToString();
            var angle = emsDevice.CurrentAngle;
            AngleText.Text = $"X:{angle.X:F0}° Y:{angle.Y:F0}° Z:{angle.Z:F0}°";
        }
    }
    
    private void UpdateChannelConnectionUI((bool ChannelA, bool ChannelB) state)
    {
        // 更新 A 通道指示灯
        ChannelAIndicator.Fill = new Avalonia.Media.SolidColorBrush(
            state.ChannelA ? Avalonia.Media.Color.Parse("#10B981") : Avalonia.Media.Color.Parse("#EF4444"));
        ChannelAStatusText.Text = state.ChannelA ? "A: 已接入" : "A: 断开";
        ChannelAStatusText.Foreground = new Avalonia.Media.SolidColorBrush(
            state.ChannelA ? Avalonia.Media.Color.Parse("#10B981") : Avalonia.Media.Color.Parse("#EF4444"));
        
        // 更新 B 通道指示灯
        ChannelBIndicator.Fill = new Avalonia.Media.SolidColorBrush(
            state.ChannelB ? Avalonia.Media.Color.Parse("#10B981") : Avalonia.Media.Color.Parse("#EF4444"));
        ChannelBStatusText.Text = state.ChannelB ? "B: 已接入" : "B: 断开";
        ChannelBStatusText.Foreground = new Avalonia.Media.SolidColorBrush(
            state.ChannelB ? Avalonia.Media.Color.Parse("#10B981") : Avalonia.Media.Color.Parse("#EF4444"));
    }

    private void UpdateStrengthDisplay()
    {
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized) return;
        
        try
        {
            var info = AppServices.Instance.DeviceManager.GetDeviceInfo(_selectedDeviceId);
            _updatingSliders = true;

            SliderA.Maximum = Math.Max(info.State.Strength.LimitA, info.Type == DeviceType.Yokonex ? YokonexEmsStrengthMax : DGLabStrengthMax);
            SliderB.Maximum = Math.Max(info.State.Strength.LimitB, info.Type == DeviceType.Yokonex ? YokonexEmsStrengthMax : DGLabStrengthMax);
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
        EstimStrengthPanel.IsVisible = false;
        YokonexControlPanel.IsVisible = false;
        DGLabControlPanel.IsVisible = false;
        DGLabOnlyControls.IsVisible = false;
        SensorControlPanel.IsVisible = false;
        YokonexChannelStatus.IsVisible = false;
        UnsubscribeYokonexEvents();
        UnsubscribeDGLabAccessoryEvents();
        SensorRealtimeVoltage.Text = "-- V";
        _updatingSliders = false;
    }

    private void SubscribeDGLabAccessoryEvents()
    {
        UnsubscribeDGLabAccessoryEvents();
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized)
        {
            return;
        }

        try
        {
            var device = AppServices.Instance.DeviceManager.GetDevice(_selectedDeviceId);
            if (device is IDGLabExternalVoltageSensorDevice voltageSensor)
            {
                _currentExternalVoltageDevice = voltageSensor;
                _currentExternalVoltageDevice.ExternalVoltageChanged += OnExternalVoltageChanged;
                SensorRealtimeVoltage.Text = $"{voltageSensor.LastVoltage:F2} V";
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "SubscribeDGLabAccessoryEvents failed");
        }
    }

    private void UnsubscribeDGLabAccessoryEvents()
    {
        if (_currentExternalVoltageDevice != null)
        {
            _currentExternalVoltageDevice.ExternalVoltageChanged -= OnExternalVoltageChanged;
            _currentExternalVoltageDevice = null;
        }
    }

    private void OnExternalVoltageChanged(object? sender, double voltage)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SensorRealtimeVoltage.Text = $"{voltage:F2} V";
        });
    }

    private void OnInjectSensorVoltageClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized)
        {
            ShowStatus("请先选择设备");
            return;
        }

        if (!double.TryParse(SensorSimVoltage.Text, out var voltage))
        {
            ShowStatus("请输入有效电压值");
            return;
        }

        InjectExternalVoltage(voltage);
    }

    private void OnAutoInjectSensorVoltageClick(object? sender, RoutedEventArgs e)
    {
        if (!double.TryParse(SensorVoltageMin.Text, out var minV))
        {
            minV = 0.49;
        }
        if (!double.TryParse(SensorVoltageMax.Text, out var maxV))
        {
            maxV = 0.84;
        }
        if (maxV < minV)
        {
            (minV, maxV) = (maxV, minV);
        }

        var random = Random.Shared.NextDouble();
        var voltage = minV + (maxV - minV) * random;
        SensorSimVoltage.Text = voltage.ToString("F2");
        InjectExternalVoltage(voltage);
    }

    private void InjectExternalVoltage(double voltage)
    {
        try
        {
            var device = AppServices.Instance.DeviceManager.GetDevice(_selectedDeviceId!);
            if (device is DGLabAccessoryPlaceholderAdapter placeholder)
            {
                placeholder.InjectExternalVoltage(voltage, "ControlPage");
                SensorRealtimeVoltage.Text = $"{voltage:F2} V";
                ShowStatus($"已注入外部电压: {voltage:F2}V");
                return;
            }

            ShowStatus("当前设备不支持外部电压注入");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Inject external voltage failed");
            ShowStatus($"注入失败: {ex.Message}");
        }
    }

    private void OnSliderAChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSliders)
        {
            return;
        }

        // 只更新显示值，不立即应用
        var value = (int)e.NewValue;
        SliderAValue.Text = value.ToString();
    }

    private void OnSliderBChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSliders)
        {
            return;
        }

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
        if (!AppServices.IsInitialized) return;
        
        var slider = this.FindControl<Slider>("YokonexEstimStrength");
        var modeCombo = this.FindControl<ComboBox>("YokonexEstimMode");
        if (slider == null) return;
        
        var value = (int)slider.Value;
        var mode = (modeCombo?.SelectedIndex ?? 0) + 1; // 模式从1开始
        
        try
        {
            var target = ResolveYokonexTargetDevice(d => d is ChargingPanel.Core.Devices.Yokonex.IYokonexEmsDevice);
            if (!target.HasValue)
            {
                ShowStatus("当前没有可用的役次元电击器");
                return;
            }

            var (targetId, targetName, device) = target.Value;
            var routedSuffix = BuildRoutedSuffix(targetId, targetName);
            
            // 役次元电击器使用 IYokonexEmsDevice 接口
            if (device is ChargingPanel.Core.Devices.Yokonex.IYokonexEmsDevice emsDevice)
            {
                // 设置固定模式 (1-16)，按厂商双通道模型同步 A/B。
                await emsDevice.SetFixedModeAsync(Channel.A, mode);
                await emsDevice.SetFixedModeAsync(Channel.B, mode);
                // 设置强度 (通过通用接口，会自动映射到 0-276)
                await AppServices.Instance.DeviceManager.SetStrengthAsync(targetId, Channel.AB, value, StrengthMode.Set);
                AppServices.Instance.DeviceManager.RecordDeviceAction(
                    targetId,
                    "电击模式设置",
                    new { mode, strength = value, channel = "AB" },
                    "ControlPage");
                ShowStatus($"电击强度 {value}，模式 {mode}{routedSuffix}");
            }
            else
            {
                ShowStatus("当前设备不是役次元电击器，无法应用电击模式");
                return;
            }
            
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
        if (!AppServices.IsInitialized) return;
        
        var slider = this.FindControl<Slider>("YokonexVibrateStrength");
        var modeCombo = this.FindControl<ComboBox>("YokonexVibrateMode");
        if (slider == null) return;
        
        var value = (int)slider.Value;
        var mode = (modeCombo?.SelectedIndex ?? 0) + 1;
        
        try
        {
            var target = ResolveYokonexTargetDevice(d => d is ChargingPanel.Core.Devices.Yokonex.IYokonexToyDevice);
            if (!target.HasValue)
            {
                ShowStatus("当前没有可用的跳蛋/飞机杯设备");
                return;
            }

            var (targetId, targetName, device) = target.Value;
            var routedSuffix = BuildRoutedSuffix(targetId, targetName);
            
            // 役次元跳蛋/飞机杯使用 IYokonexToyDevice 接口
            if (device is ChargingPanel.Core.Devices.Yokonex.IYokonexToyDevice toyDevice)
            {
                // 跳蛋/飞机杯: 强度范围 0-20
                var mappedValue = (int)(value * 0.2); // 0-100 -> 0-20
                if (value <= 0)
                {
                    await toyDevice.StopAllMotorsAsync();
                    ShowStatus($"震动已停止{routedSuffix}");
                }
                else
                {
                    await toyDevice.SetFixedModeAsync(mode);
                    await toyDevice.SetAllMotorsAsync(mappedValue, mappedValue, mappedValue);
                    AppServices.Instance.DeviceManager.RecordDeviceAction(
                        targetId,
                        "跳蛋/飞机杯模式设置",
                        new { mode, strength = mappedValue },
                        "ControlPage");
                    ShowStatus($"震动强度 {mappedValue}/20，模式 {mode}{routedSuffix}");
                }
            }
            else
            {
                ShowStatus("当前设备不支持跳蛋/飞机杯震动控制");
                return;
            }
            
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
        if (!AppServices.IsInitialized) return;
        
        var slider = this.FindControl<Slider>("YokonexOtherStrength");
        var modeCombo = this.FindControl<ComboBox>("YokonexOtherMode");
        if (slider == null) return;
        
        var value = (int)slider.Value;
        var modeTag = (modeCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "inject";
        
        try
        {
            var target = ResolveYokonexTargetDevice(d => d is ChargingPanel.Core.Devices.Yokonex.IYokonexEnemaDevice
                                                         || d is YokonexEnemaBluetoothAdapter);
            if (!target.HasValue)
            {
                ShowStatus("当前没有可用的灌肠设备");
                return;
            }

            var (targetId, targetName, device) = target.Value;
            var routedSuffix = BuildRoutedSuffix(targetId, targetName);
            
            if (device is YokonexEnemaBluetoothAdapter enemaBluetoothDevice)
            {
                switch (modeTag)
                {
                    case "inject":
                        await enemaBluetoothDevice.SetPeristalticPumpAsync(
                            value > 0 ? PeristalticPumpState.Forward : PeristalticPumpState.Stop,
                            Math.Max(value, 1));
                        AppServices.Instance.DeviceManager.RecordDeviceAction(
                            targetId,
                            "灌肠器蠕动泵控制",
                            new { mode = modeTag, value },
                            "ControlPage");
                        ShowStatus(value > 0 ? $"蠕动泵启动 {value}s{routedSuffix}" : $"蠕动泵已停止{routedSuffix}");
                        break;
                    case "suction":
                        await enemaBluetoothDevice.SetWaterPumpAsync(
                            value > 0 ? WaterPumpState.Forward : WaterPumpState.Stop,
                            Math.Max(value, 1));
                        AppServices.Instance.DeviceManager.RecordDeviceAction(
                            targetId,
                            "灌肠器抽水泵控制",
                            new { mode = modeTag, value },
                            "ControlPage");
                        ShowStatus(value > 0 ? $"抽水泵启动 {value}s{routedSuffix}" : $"抽水泵已停止{routedSuffix}");
                        break;
                    case "cycle":
                        if (value > 0)
                        {
                            await enemaBluetoothDevice.SetPeristalticPumpAsync(PeristalticPumpState.Forward, Math.Max(value, 1));
                            await enemaBluetoothDevice.SetWaterPumpAsync(WaterPumpState.Forward, Math.Max(value, 1));
                            AppServices.Instance.DeviceManager.RecordDeviceAction(
                                targetId,
                                "灌肠器循环模式",
                                new { mode = modeTag, value },
                                "ControlPage");
                            ShowStatus($"循环模式已启动 {value}s{routedSuffix}");
                        }
                        else
                        {
                            await enemaBluetoothDevice.PauseAllAsync();
                            AppServices.Instance.DeviceManager.RecordDeviceAction(
                                targetId,
                                "灌肠器循环模式",
                                new { mode = modeTag, value = 0 },
                                "ControlPage");
                            ShowStatus($"循环模式已停止{routedSuffix}");
                        }
                        break;
                    default:
                        await enemaBluetoothDevice.PauseAllAsync();
                        AppServices.Instance.DeviceManager.RecordDeviceAction(
                            targetId,
                            "灌肠器暂停",
                            new { mode = modeTag, value = 0 },
                            "ControlPage");
                        ShowStatus($"灌肠器已暂停{routedSuffix}");
                        break;
                }
            }
            // 役次元灌肠器接口（IM 通道下退化到注入语义）
            else if (device is ChargingPanel.Core.Devices.Yokonex.IYokonexEnemaDevice enemaDevice)
            {
                // 灌肠器: 注入强度 0-100%
                await enemaDevice.SetInjectionStrengthAsync(value);
                if (value > 0)
                {
                    await enemaDevice.StartInjectionAsync();
                    AppServices.Instance.DeviceManager.RecordDeviceAction(
                        targetId,
                        "灌肠器注入启动",
                        new { strength = value },
                        "ControlPage");
                    ShowStatus($"注入强度 {value}%{routedSuffix}");
                }
                else
                {
                    await enemaDevice.StopInjectionAsync();
                    AppServices.Instance.DeviceManager.RecordDeviceAction(
                        targetId,
                        "灌肠器注入停止",
                        new { strength = 0 },
                        "ControlPage");
                    ShowStatus($"注入已停止{routedSuffix}");
                }
            }
            else if (device is ChargingPanel.Core.Devices.Yokonex.IYokonexToyDevice toyDevice)
            {
                // 跳蛋/飞机杯: 设置所有马达
                var mappedValue = (int)(value * 0.2); // 0-100 -> 0-20
                await toyDevice.SetAllMotorsAsync(mappedValue, mappedValue, mappedValue);
                AppServices.Instance.DeviceManager.RecordDeviceAction(
                    targetId,
                    "跳蛋/飞机杯强度设置",
                    new { strength = mappedValue, mode = modeTag },
                    "ControlPage");
                var toyName = GetCurrentDeviceTypeName();
                ShowStatus($"{toyName}马达强度 {mappedValue}/20{routedSuffix}");
            }
            else
            {
                ShowStatus($"当前设备不支持灌肠动作控制: {GetCurrentDeviceTypeName()}");
                return;
            }
            
            Logger.Information("Yokonex Other device strength set to {Value}, modeTag {ModeTag}", value, modeTag);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to set Yokonex Other device strength");
            ShowStatus($"设置失败: {ex.Message}");
        }
    }

    private async void OnQuickControl(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag)
        {
            Logger.Warning("OnQuickControl: Invalid sender or tag");
            return;
        }
        
        if (string.IsNullOrEmpty(_selectedDeviceId))
        {
            ShowStatus("请先选择一个设备");
            Logger.Warning("OnQuickControl: No device selected");
            return;
        }
        
        if (!AppServices.IsInitialized)
        {
            ShowStatus("服务未初始化");
            Logger.Warning("OnQuickControl: AppServices not initialized");
            return;
        }
        
        // 检查设备连接状态
        try
        {
            var deviceInfo = AppServices.Instance.DeviceManager.GetDeviceInfo(_selectedDeviceId);
            if (deviceInfo.Status != DeviceStatus.Connected)
            {
                ShowStatus($"设备未连接 (当前状态: {deviceInfo.Status})");
                Logger.Warning("OnQuickControl: Device not connected, status={Status}", deviceInfo.Status);
                return;
            }
        }
        catch (Exception ex)
        {
            ShowStatus("设备不存在或已移除");
            Logger.Warning(ex, "OnQuickControl: Failed to get device info");
            return;
        }
        
        var parts = tag.Split(',');
        if (parts.Length != 2)
        {
            Logger.Warning("OnQuickControl: Invalid tag format: {Tag}", tag);
            return;
        }
        
        var channel = parts[0] == "A" ? Channel.A : Channel.B;
        if (!int.TryParse(parts[1], out var value))
        {
            Logger.Warning("OnQuickControl: Invalid value in tag: {Tag}", tag);
            return;
        }
        
        Logger.Information("OnQuickControl: Channel={Channel}, Value={Value}, DeviceId={DeviceId}", parts[0], value, _selectedDeviceId);
        
        try
        {
            if (value == 0)
            {
                // 归零
                await AppServices.Instance.DeviceManager.SetStrengthAsync(_selectedDeviceId, channel, 0, StrengthMode.Set);
                ShowStatus($"通道 {parts[0]} 已归零");
                Logger.Information("Channel {Channel} reset to 0", parts[0]);
            }
            else
            {
                // 增减
                var mode = value > 0 ? StrengthMode.Increase : StrengthMode.Decrease;
                await AppServices.Instance.DeviceManager.SetStrengthAsync(_selectedDeviceId, channel, Math.Abs(value), mode);
                ShowStatus($"通道 {parts[0]} {(value > 0 ? "+" : "")}{value}");
                Logger.Information("Channel {Channel} adjusted by {Value}", parts[0], value);
            }
            
            // 延迟一小段时间后更新显示，确保设备状态已更新
            await Task.Delay(50);
            UpdateStrengthDisplay();
            Logger.Debug("Quick control: Channel={Channel}, Value={Value}", parts[0], value);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Quick control failed");
            ShowStatus($"控制失败: {ex.Message}");
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
        var supportsWaveform = deviceInfo.Type == DeviceType.DGLab
                               || (deviceInfo.Type == DeviceType.Yokonex && device is ChargingPanel.Core.Devices.Yokonex.IYokonexEmsDevice)
                               || deviceInfo.Type == DeviceType.Virtual;
        if (!supportsWaveform)
        {
            ShowStatus($"当前设备不支持波形预设: {GetCurrentDeviceTypeName()}");
            return;
        }

        if (deviceInfo.Type == DeviceType.Yokonex)
        {
            if (device is ChargingPanel.Core.Devices.Yokonex.IYokonexEmsDevice)
            {
                ShowStatus("役次元电击器已启用波形映射：将按频率/脉宽发送自定义模式");
                Logger.Information("Yokonex EMS waveform preset will map to custom mode parameters");
            }
            else
            {
                // 其他役次元设备仍不支持通用波形队列
                ShowStatus("当前役次元设备不支持通用波形队列，已按设备协议降级处理");
                Logger.Information("Non-EMS Yokonex device uses protocol-specific fallback for waveform preset");
            }
        }

        if (preset == "random" && await TrySendRandomPresetWaveformAsync())
        {
            return;
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

    private async Task<bool> TrySendRandomPresetWaveformAsync()
    {
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized)
        {
            return false;
        }

        var presets = Database.Instance.GetAllWaveformPresets();
        if (presets.Count == 0)
        {
            return false;
        }

        var preset = presets[Random.Shared.Next(presets.Count)];
        var waveform = WaveformPresetExchangeService.BuildWaveformData(preset);
        var channel = preset.Channel switch
        {
            "A" => Channel.A,
            "B" => Channel.B,
            _ => Channel.AB
        };

        await AppServices.Instance.DeviceManager.SendWaveformAsync(_selectedDeviceId, channel, waveform);
        Logger.Information("Sent random waveform from preset pool: {Preset}", preset.Name);
        ShowStatus($"已随机发送预设波形: {preset.Name}");
        return true;
    }

    private async void OnStartWaveformPlaylistClick(object? sender, RoutedEventArgs e)
    {
        await StartWaveformPlaylistAsync();
    }

    private void OnStopWaveformPlaylistClick(object? sender, RoutedEventArgs e)
    {
        StopWaveformPlaylist();
        ShowStatus("已停止波形播放列表");
    }

    private async Task StartWaveformPlaylistAsync()
    {
        if (string.IsNullOrEmpty(_selectedDeviceId) || !AppServices.IsInitialized)
        {
            ShowStatus("请先选择并连接一个支持波形的设备");
            return;
        }

        var deviceInfo = AppServices.Instance.DeviceManager.GetDeviceInfo(_selectedDeviceId);
        var device = AppServices.Instance.DeviceManager.GetDevice(_selectedDeviceId);
        var supportsWaveform = deviceInfo.Type == DeviceType.DGLab
                               || (deviceInfo.Type == DeviceType.Yokonex && device is IYokonexEmsDevice)
                               || deviceInfo.Type == DeviceType.Virtual;
        if (!supportsWaveform)
        {
            ShowStatus($"当前设备不支持波形播放列表: {GetCurrentDeviceTypeName()}");
            return;
        }

        var presets = Database.Instance
            .GetAllWaveformPresets()
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Name)
            .ToList();

        if (presets.Count == 0)
        {
            ShowStatus("没有可播放的波形预设，请先创建至少一个预设");
            return;
        }

        StopWaveformPlaylist();

        // 仅一条时视为单一波形，发送一次即可。
        if (presets.Count == 1)
        {
            var single = presets[0];
            var waveform = WaveformPresetExchangeService.BuildWaveformData(single);
            var channel = single.Channel switch
            {
                "A" => Channel.A,
                "B" => Channel.B,
                _ => Channel.AB
            };

            await AppServices.Instance.DeviceManager.SendWaveformAsync(_selectedDeviceId, channel, waveform);
            ShowStatus($"仅检测到一个预设，已按单一波形发送: {single.Name}");
            return;
        }

        _waveformPlaylistCts = new CancellationTokenSource();
        SetWaveformPlaylistButtons(isRunning: true);
        _waveformPlaylistTask = RunWaveformPlaylistLoopAsync(presets, _waveformPlaylistCts.Token);
        ShowStatus($"波形播放列表已启动（{GetWaveformPlaylistMode()}）");
    }

    private async Task RunWaveformPlaylistLoopAsync(IReadOnlyList<WaveformPresetRecord> presets, CancellationToken ct)
    {
        var mode = GetWaveformPlaylistMode();
        var nextIndex = 0;

        try
        {
            while (!ct.IsCancellationRequested && !string.IsNullOrEmpty(_selectedDeviceId))
            {
                var preset = mode == "random"
                    ? presets[Random.Shared.Next(presets.Count)]
                    : presets[nextIndex++ % presets.Count];

                var waveform = WaveformPresetExchangeService.BuildWaveformData(preset);
                var channel = preset.Channel switch
                {
                    "A" => Channel.A,
                    "B" => Channel.B,
                    _ => Channel.AB
                };

                await AppServices.Instance.DeviceManager.SendWaveformAsync(_selectedDeviceId, channel, waveform);
                Logger.Information("Playlist waveform sent: {Preset}, channel={Channel}, mode={Mode}", preset.Name, channel, mode);

                var delayMs = Math.Clamp(preset.Duration <= 0 ? 1000 : preset.Duration, 100, 600000);
                await Task.Delay(delayMs, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Waveform playlist loop stopped unexpectedly");
            ShowStatus($"波形播放列表异常停止: {ex.Message}");
        }
        finally
        {
            _waveformPlaylistTask = null;
            SetWaveformPlaylistButtons(isRunning: false);
        }
    }

    private void StopWaveformPlaylist()
    {
        if (_waveformPlaylistCts != null)
        {
            _waveformPlaylistCts.Cancel();
            _waveformPlaylistCts.Dispose();
            _waveformPlaylistCts = null;
        }

        _waveformPlaylistTask = null;
        SetWaveformPlaylistButtons(isRunning: false);
    }

    private string GetWaveformPlaylistMode()
    {
        return (WaveformPlaylistMode?.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "random"
            ? "random"
            : "loop";
    }

    private void SetWaveformPlaylistButtons(bool isRunning)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetWaveformPlaylistButtons(isRunning));
            return;
        }

        BtnStartWaveformPlaylist.IsEnabled = !isRunning;
        BtnStopWaveformPlaylist.IsEnabled = isRunning;
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

    private void LoadDeviceDefaultSettings()
    {
        try
        {
            if (!AppServices.IsInitialized)
            {
                return;
            }

            var settings = Database.Instance.GetAllSettings();
            var dglabLimitA = GetIntSetting(settings, "device.defaults.dglab.limitA", DGLabStrengthMax);
            var dglabLimitB = GetIntSetting(settings, "device.defaults.dglab.limitB", DGLabStrengthMax);
            DefaultDGLabQueue.Text = GetStringSetting(settings, "device.defaults.dglab.maxWaveformQueue", "500");

            var yokonexEmsMax = GetIntSetting(settings, "device.defaults.yokonex.ems.maxStrength", YokonexEmsStrengthMax);
            var yokonexMotorMax = GetIntSetting(settings, "device.defaults.yokonex.motor.maxStrength", YokonexMotorStrengthMax);
            var yokonexInjectionMax = GetIntSetting(settings, "device.defaults.yokonex.enema.maxInjection", YokonexInjectionStrengthMax);

            _updatingDefaultSliders = true;
            DefaultDGLabLimitASlider.Value = ConvertAbsoluteToPercent(dglabLimitA, DGLabStrengthMax);
            DefaultDGLabLimitBSlider.Value = ConvertAbsoluteToPercent(dglabLimitB, DGLabStrengthMax);
            DefaultYokonexEmsMaxSlider.Value = ConvertAbsoluteToPercent(yokonexEmsMax, YokonexEmsStrengthMax);
            DefaultYokonexMotorMaxSlider.Value = ConvertAbsoluteToPercent(yokonexMotorMax, YokonexMotorStrengthMax);
            DefaultYokonexInjectionMaxSlider.Value = ConvertAbsoluteToPercent(yokonexInjectionMax, YokonexInjectionStrengthMax);
            _updatingDefaultSliders = false;

            UpdateDefaultSliderText(DefaultDGLabLimitAValueText, DefaultDGLabLimitASlider.Value, DGLabStrengthMax);
            UpdateDefaultSliderText(DefaultDGLabLimitBValueText, DefaultDGLabLimitBSlider.Value, DGLabStrengthMax);
            UpdateDefaultSliderText(DefaultYokonexEmsMaxValueText, DefaultYokonexEmsMaxSlider.Value, YokonexEmsStrengthMax);
            UpdateDefaultSliderText(DefaultYokonexMotorMaxValueText, DefaultYokonexMotorMaxSlider.Value, YokonexMotorStrengthMax);
            UpdateDefaultSliderText(DefaultYokonexInjectionMaxValueText, DefaultYokonexInjectionMaxSlider.Value, YokonexInjectionStrengthMax);

            AutoReconnectDefault.IsChecked = GetBoolSetting(settings, "device.autoReconnect", true);
            Database.Instance.SetSetting("device.heartbeatInterval", HeartbeatIntervalMinimumMs.ToString());
            DefaultSettingsStatus.Text = "已加载";
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to load default device settings");
            DefaultSettingsStatus.Text = "加载失败";
        }
    }

    private static bool GetBoolSetting(Dictionary<string, string> settings, string key, bool defaultValue)
    {
        return settings.TryGetValue(key, out var value)
            ? value.Equals("true", StringComparison.OrdinalIgnoreCase)
            : defaultValue;
    }

    private static string GetStringSetting(Dictionary<string, string> settings, string key, string defaultValue)
    {
        return settings.TryGetValue(key, out var value) ? value : defaultValue;
    }

    private static int GetIntSetting(Dictionary<string, string> settings, string key, int defaultValue)
    {
        return settings.TryGetValue(key, out var value) && int.TryParse(value, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static int ConvertAbsoluteToPercent(int absoluteValue, int maxAbsolute)
    {
        if (maxAbsolute <= 0)
        {
            return 0;
        }

        return Math.Clamp((int)Math.Round(absoluteValue * 100d / maxAbsolute), 0, 100);
    }

    private static int ConvertPercentToAbsolute(double percent, int maxAbsolute)
    {
        if (maxAbsolute <= 0)
        {
            return 0;
        }

        var safePercent = Math.Clamp(percent, 0, 100);
        return Math.Clamp((int)Math.Round(safePercent * maxAbsolute / 100d), 0, maxAbsolute);
    }

    private static void UpdateDefaultSliderText(TextBlock target, double percent, int maxAbsolute)
    {
        var safePercent = Math.Clamp((int)Math.Round(percent), 0, 100);
        var absolute = ConvertPercentToAbsolute(safePercent, maxAbsolute);
        target.Text = $"{safePercent}%（{absolute}）";
    }

    private void OnDefaultDGLabLimitASliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingDefaultSliders)
        {
            return;
        }

        UpdateDefaultSliderText(DefaultDGLabLimitAValueText, e.NewValue, DGLabStrengthMax);
    }

    private void OnDefaultDGLabLimitBSliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingDefaultSliders)
        {
            return;
        }

        UpdateDefaultSliderText(DefaultDGLabLimitBValueText, e.NewValue, DGLabStrengthMax);
    }

    private void OnDefaultYokonexEmsMaxSliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingDefaultSliders)
        {
            return;
        }

        UpdateDefaultSliderText(DefaultYokonexEmsMaxValueText, e.NewValue, YokonexEmsStrengthMax);
    }

    private void OnDefaultYokonexMotorMaxSliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingDefaultSliders)
        {
            return;
        }

        UpdateDefaultSliderText(DefaultYokonexMotorMaxValueText, e.NewValue, YokonexMotorStrengthMax);
    }

    private void OnDefaultYokonexInjectionMaxSliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingDefaultSliders)
        {
            return;
        }

        UpdateDefaultSliderText(DefaultYokonexInjectionMaxValueText, e.NewValue, YokonexInjectionStrengthMax);
    }

    public void SaveDeviceDefaults()
    {
        if (!AppServices.IsInitialized)
        {
            return;
        }

        var dglabLimitA = ConvertPercentToAbsolute(DefaultDGLabLimitASlider.Value, DGLabStrengthMax);
        var dglabLimitB = ConvertPercentToAbsolute(DefaultDGLabLimitBSlider.Value, DGLabStrengthMax);
        var yokonexEmsMax = ConvertPercentToAbsolute(DefaultYokonexEmsMaxSlider.Value, YokonexEmsStrengthMax);
        var yokonexMotorMax = ConvertPercentToAbsolute(DefaultYokonexMotorMaxSlider.Value, YokonexMotorStrengthMax);
        var yokonexInjectionMax = ConvertPercentToAbsolute(DefaultYokonexInjectionMaxSlider.Value, YokonexInjectionStrengthMax);

        Database.Instance.SetSetting("device.defaults.dglab.limitA", dglabLimitA.ToString());
        Database.Instance.SetSetting("device.defaults.dglab.limitB", dglabLimitB.ToString());
        Database.Instance.SetSetting("device.defaults.dglab.maxWaveformQueue", DefaultDGLabQueue.Text ?? "500");

        Database.Instance.SetSetting("device.defaults.yokonex.ems.maxStrength", yokonexEmsMax.ToString());
        Database.Instance.SetSetting("device.defaults.yokonex.motor.maxStrength", yokonexMotorMax.ToString());
        Database.Instance.SetSetting("device.defaults.yokonex.enema.maxInjection", yokonexInjectionMax.ToString());

        Database.Instance.SetSetting("device.autoReconnect", (AutoReconnectDefault.IsChecked ?? true).ToString().ToLowerInvariant());
        Database.Instance.SetSetting("device.heartbeatInterval", HeartbeatIntervalMinimumMs.ToString());

        // 兼容旧逻辑：同步维护历史全局参数键
        Database.Instance.SetSetting("device.maxStrength", dglabLimitA.ToString());
        Database.Instance.SetSetting("device.maxWaveformQueue", DefaultDGLabQueue.Text ?? "500");
    }

    private void OnSaveDeviceDefaults(object? sender, RoutedEventArgs e)
    {
        try
        {
            SaveDeviceDefaults();
            DefaultSettingsStatus.Text = $"已保存 {DateTime.Now:HH:mm:ss}";
            ShowStatus("设备默认设置已保存");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to save device defaults");
            DefaultSettingsStatus.Text = "保存失败";
            ShowStatus($"保存失败: {ex.Message}");
        }
    }

    #region Waveform Preset Editor

    private string? _editingPresetId;
    
    private async void OnExportWaveformPresetsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                ShowStatus("无法打开导出对话框");
                return;
            }

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出波形预设包",
                SuggestedFileName = $"wave-presets-{DateTime.Now:yyyyMMdd-HHmmss}.cpwave.json",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Wave Preset Package") { Patterns = new[] { "*.cpwave.json", "*.json" } },
                    new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } }
                }
            });

            if (file == null)
            {
                return;
            }

            var db = Database.Instance;
            var exportTargets = db.GetAllWaveformPresets().Where(p => !p.IsBuiltIn).ToList();
            var json = WaveformPresetExchangeService.ExportPackage(exportTargets, includeBuiltIn: false);

            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);

            ShowStatus($"已导出波形预设 {exportTargets.Count} 条: {file.Name}");
            Logger.Information("Exported waveform preset package: {FileName}, count={Count}", file.Name, exportTargets.Count);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to export waveform presets");
            ShowStatus($"导出失败: {ex.Message}");
        }
    }

    private async void OnImportWaveformPresetsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                ShowStatus("无法打开导入对话框");
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入波形预设包",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Wave Preset Package") { Patterns = new[] { "*.cpwave.json", "*.json" } },
                    new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } }
                }
            });

            if (files.Count == 0)
            {
                return;
            }

            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();

            if (!WaveformPresetExchangeService.TryImportPackage(content, out var importedPresets, out var error))
            {
                ShowStatus($"导入失败: {error}");
                return;
            }

            var db = Database.Instance;
            var existing = db.GetAllWaveformPresets();
            var existingKeys = new HashSet<string>(
                existing.Select(BuildPresetDedupKey),
                StringComparer.OrdinalIgnoreCase);

            var addedCount = 0;
            var skipCount = 0;
            var sortBase = existing.Count == 0 ? 100 : existing.Max(p => p.SortOrder) + 1;

            foreach (var preset in importedPresets)
            {
                var key = BuildPresetDedupKey(preset);
                if (existingKeys.Contains(key))
                {
                    skipCount++;
                    continue;
                }

                preset.IsBuiltIn = false;
                preset.SortOrder = sortBase + addedCount;
                db.AddWaveformPreset(preset);
                existingKeys.Add(key);
                addedCount++;
            }

            RefreshWaveformPresets();
            ShowStatus($"导入完成: 新增 {addedCount} 条，跳过重复 {skipCount} 条");
            Logger.Information("Imported waveform preset package: added={Added}, skipped={Skipped}", addedCount, skipCount);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to import waveform presets");
            ShowStatus($"导入失败: {ex.Message}");
        }
    }

    private static string BuildPresetDedupKey(WaveformPresetRecord preset)
    {
        return $"{preset.Name.Trim()}|{preset.Channel.Trim().ToUpperInvariant()}|{preset.WaveformData.Trim().ToUpperInvariant()}";
    }

    private void OnNewWaveformPresetClick(object? sender, RoutedEventArgs e)
    {
        _editingPresetId = null;
        WaveformPresetName.Text = "";
        WaveformPresetIcon.Text = "W";
        WaveformPresetChannel.SelectedIndex = 2; // AB
        WaveformPresetDuration.Text = "1000";
        WaveformPresetPattern.SelectedIndex = 0;
        WaveformPresetFrequency.Value = 50;
        WaveformPresetPulse.Value = 20;
        WaveformPresetFrequencyText.Text = "50 Hz";
        WaveformPresetPulseText.Text = "20 us";
        WaveformPresetIntensity.Value = 50;
        WaveformPresetIntensityText.Text = "50%";
        UpdateWaveformGraphEditor();
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
        var icon = string.IsNullOrWhiteSpace(WaveformPresetIcon.Text)
            ? "W"
            : WaveformPresetIcon.Text.Trim();
        var waveformData = BuildWaveformPayloadFromEditor();
        
        if (string.IsNullOrEmpty(name))
        {
            ShowStatus("请输入预设名称");
            return;
        }
        
        if (string.IsNullOrEmpty(waveformData))
        {
            ShowStatus("波形参数无效");
            return;
        }
        
        if (!WaveformPresetExchangeService.TryNormalizePayload(
                waveformData,
                out var normalizedWaveformData,
                out _,
                out _,
                out _))
        {
            ShowStatus("波形数据格式无效（支持 HEX，或 freq,pulse）");
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
                    WaveformData = normalizedWaveformData,
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
                    existing.WaveformData = normalizedWaveformData;
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
                var iconPrefix = string.IsNullOrWhiteSpace(preset.Icon) ? string.Empty : $"{preset.Icon} ";
                var btn = new Button
                {
                    Content = $"{iconPrefix}{preset.Name}",
                    Tag = preset.Id,
                    Background = preset.IsBuiltIn 
                        ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1F2B3E"))
                        : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#172033")),
                    Foreground = Avalonia.Media.Brushes.White,
                    BorderBrush = preset.IsBuiltIn
                        ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#314764"))
                        : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0EA5E9")),
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
            var device = AppServices.Instance.DeviceManager.GetDevice(_selectedDeviceId);
            var info = AppServices.Instance.DeviceManager.GetDeviceInfo(_selectedDeviceId);
            var supportsWaveform = info.Type == DeviceType.DGLab
                                   || (info.Type == DeviceType.Yokonex && device is ChargingPanel.Core.Devices.Yokonex.IYokonexEmsDevice)
                                   || info.Type == DeviceType.Virtual;
            if (!supportsWaveform)
            {
                ShowStatus($"当前设备不支持波形预设: {GetCurrentDeviceTypeName()}");
                return;
            }

            var db = ChargingPanel.Core.Data.Database.Instance;
            var preset = db.GetWaveformPreset(presetId);
            if (preset == null) return;
            
            // 使用统一翻译层构造跨设备可用波形
            var waveform = WaveformPresetExchangeService.BuildWaveformData(preset);
            
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
            WaveformPresetIcon.Text = string.IsNullOrWhiteSpace(preset.Icon) ? "W" : preset.Icon;
            WaveformPresetChannel.SelectedIndex = preset.Channel switch
            {
                "A" => 0,
                "B" => 1,
                _ => 2
            };
            WaveformPresetDuration.Text = preset.Duration.ToString();
            WaveformPresetPattern.SelectedIndex = 0;

            if (WaveformPresetExchangeService.TryParseFrequencyPulse(preset.WaveformData, out var frequency, out var pulse))
            {
                WaveformPresetFrequency.Value = frequency;
                WaveformPresetPulse.Value = pulse;
            }
            else
            {
                WaveformPresetFrequency.Value = 50;
                WaveformPresetPulse.Value = 20;
                ShowStatus("该预设为旧版 HEX 数据，已转换为图形参数模式进行编辑");
            }

            WaveformPresetIntensity.Value = preset.Intensity;
            WaveformPresetIntensityText.Text = $"{preset.Intensity}%";
            UpdateWaveformGraphEditor();
            BtnDeleteWaveformPreset.IsVisible = true;
            WaveformEditorPanel.IsVisible = true;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to load waveform preset for editing");
        }
    }

    private void OnWaveformPresetIntensityChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (WaveformPresetIntensityText == null)
        {
            return;
        }

        WaveformPresetIntensityText.Text = $"{(int)e.NewValue}%";
    }

    private void OnWaveformPatternChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (WaveformPresetPattern == null || WaveformPresetFrequency == null || WaveformPresetPulse == null)
        {
            return;
        }

        var pattern = (WaveformPresetPattern.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "pulse";
        switch (pattern)
        {
            case "square":
                WaveformPresetFrequency.Value = 45;
                WaveformPresetPulse.Value = 50;
                break;
            case "sine":
                WaveformPresetFrequency.Value = 30;
                WaveformPresetPulse.Value = 80;
                break;
            case "triangle":
                WaveformPresetFrequency.Value = 40;
                WaveformPresetPulse.Value = 60;
                break;
            case "saw":
                WaveformPresetFrequency.Value = 65;
                WaveformPresetPulse.Value = 35;
                break;
            default:
                WaveformPresetFrequency.Value = 50;
                WaveformPresetPulse.Value = 20;
                break;
        }

        UpdateWaveformGraphEditor();
    }

    private void OnWaveformGraphSettingChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateWaveformGraphEditor();
    }

    private string BuildWaveformPayloadFromEditor()
    {
        var frequency = Math.Clamp((int)Math.Round(WaveformPresetFrequency.Value), 1, 100);
        var pulse = Math.Clamp((int)Math.Round(WaveformPresetPulse.Value), 0, 100);
        return $"{frequency},{pulse}";
    }

    private void UpdateWaveformGraphEditor()
    {
        if (WaveformPresetFrequency == null ||
            WaveformPresetPulse == null ||
            WaveformPresetPattern == null ||
            WaveformPresetFrequencyText == null ||
            WaveformPresetPulseText == null ||
            WaveformPresetPayloadText == null ||
            WaveformPreviewLine == null)
        {
            return;
        }

        var frequency = Math.Clamp((int)Math.Round(WaveformPresetFrequency.Value), 1, 100);
        var pulse = Math.Clamp((int)Math.Round(WaveformPresetPulse.Value), 0, 100);
        var pattern = (WaveformPresetPattern.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "pulse";

        WaveformPresetFrequencyText.Text = $"{frequency} Hz";
        WaveformPresetPulseText.Text = $"{pulse} us";
        WaveformPresetPayloadText.Text = $"输出参数：{frequency},{pulse}";

        // 简单图形化预览：显示 0-1 周期波形随参数变化的形态。
        const double width = 420;
        const double height = 90;
        const int sampleCount = 96;

        var cycles = Math.Clamp(frequency / 20.0, 1.0, 5.0);
        var duty = Math.Clamp(pulse / 100.0, 0.05, 0.95);
        var points = new AvaloniaList<Point>(sampleCount);

        for (var i = 0; i < sampleCount; i++)
        {
            var x = i * (width - 1) / (sampleCount - 1);
            var t = i / (double)(sampleCount - 1);
            var phase = (t * cycles) % 1.0;

            double level = pattern switch
            {
                "square" => phase < duty ? 1.0 : 0.0,
                "sine" => 0.5 + 0.5 * Math.Sin(2 * Math.PI * phase),
                "triangle" => phase < 0.5 ? phase * 2 : (1 - phase) * 2,
                "saw" => 1 - phase,
                _ => phase < duty ? 1.0 - phase / duty : 0.0
            };

            var y = (height - 6) - level * (height - 12);
            points.Add(new Point(x, y));
        }

        WaveformPreviewLine.Points = points;
    }

    #endregion
}


