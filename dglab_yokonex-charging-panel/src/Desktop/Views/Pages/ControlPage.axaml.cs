using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Application.Devices;
using ChargingPanel.Desktop.ViewModels.Devices;
using Serilog;
using ChargingPanel.Desktop.Bootstrap;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ChargingPanel.Desktop.Views.Pages;

/// <summary>
/// 设备控制页。只通过 <see cref="DeviceControlViewModel"/> 下发标准能力命令，
/// 并如实渲染 Executed / Rejected / OutcomeUnknown。
/// 绝不把"消息已发送"当作"设备已执行"。
/// </summary>
public partial class ControlPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<ControlPage>();

    private readonly IDeviceQueryService _query;
    private readonly IDeviceApplicationService _appService;
    private readonly DeviceControlViewModel _controlViewModel;
    private readonly List<DeviceControlSlot> _slots = new();

    public ControlPage()
    {
        InitializeComponent();

        _query = ResolveQueryService();
        _appService = ResolveApplicationService();
        _controlViewModel = new DeviceControlViewModel(_appService, _query);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RefreshDeviceSelector();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        // 只读状态流：离屏即释放，不持有跨页面订阅。
    }

    private void RefreshDeviceSelector()
    {
        var previousSelected = _selectedDeviceId;
        DeviceSelector.Items.Clear();
        DeviceSelector.Items.Add(new ComboBoxItem { Content = "-- 选择设备 --", Tag = null });

        var activeDevices = _query.GetActiveDevices();
        var selectedIndex = 0;
        var currentIndex = 1;
        foreach (var device in activeDevices)
        {
            var item = new ComboBoxItem
            {
                Content = $"[活动] {device.InstanceId.Value.ToString("D")}",
                Tag = device.InstanceId
            };
            DeviceSelector.Items.Add(item);
            if (previousSelected is { } id && id == device.InstanceId)
            {
                selectedIndex = currentIndex;
            }

            currentIndex++;
        }

        DeviceSelector.SelectedIndex = selectedIndex;
        OnDeviceSelected(null, null!);
    }

    private DeviceInstanceId? _selectedDeviceId;

    private void OnDeviceSelected(object? sender, SelectionChangedEventArgs e)
    {
        var item = DeviceSelector.SelectedItem as ComboBoxItem;
        var selectedId = item?.Tag as DeviceInstanceId?;
        _selectedDeviceId = selectedId;

        if (selectedId is null)
        {
            _controlViewModel.ClearControls();
            ControlsPanel.Children.Clear();
            _slots.Clear();
            DeviceTypeBadge.IsVisible = false;
            LastResultText.Text = string.Empty;
            return;
        }

        var active = _query.GetActiveDevice(selectedId.Value);
        if (active is null)
        {
            _controlViewModel.ClearControls();
            ControlsPanel.Children.Clear();
            _slots.Clear();
            DeviceTypeBadge.IsVisible = false;
            LastResultText.Text = "设备没有活动 Session。";
            return;
        }

        _controlViewModel.Load(active);
        DeviceTypeBadge.IsVisible = true;
        DeviceTypeText.Text = _controlViewModel.DeviceTitle;
        RenderControls();
        LastResultText.Text = string.Empty;
    }

    private void RenderControls()
    {
        ControlsPanel.Children.Clear();
        _slots.Clear();
        foreach (var slot in _controlViewModel.Controls)
        {
            _slots.Add(slot);
            ControlsPanel.Children.Add(CreateControlRow(slot));
        }

        if (_slots.Count == 0)
        {
            ControlsPanel.Children.Add(new TextBlock
            {
                Text = "该设备没有可用的强度控制项。",
                Foreground = new SolidColorBrush(Color.Parse("#9AB0C8")),
                FontSize = 13,
                Margin = new Avalonia.Thickness(8, 8, 0, 0)
            });
        }
    }

    private Grid CreateControlRow(DeviceControlSlot slot)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            ColumnSpacing = 8,
            Margin = new Avalonia.Thickness(0, 6, 0, 6)
        };

        var title = new TextBlock
        {
            Text = slot.Title,
            Foreground = new SolidColorBrush(Color.Parse("#E6EDF7")),
            FontSize = 13,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            MinWidth = 180
        };
        Grid.SetColumn(title, 0);
        row.Children.Add(title);

        var max = (int)Math.Max(slot.Range.Maximum, 1);
        var slider = new Slider
        {
            Minimum = slot.Range.Minimum,
            Maximum = slot.Range.Maximum,
            Value = slot.Range.Minimum,
            MinWidth = 180,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);

        var valueText = new TextBlock
        {
            Text = slot.Range.Minimum.ToString("0"),
            Foreground = new SolidColorBrush(Color.Parse("#E6EDF7")),
            Width = 40,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetColumn(valueText, 2);
        row.Children.Add(valueText);

        slider.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty)
            {
                valueText.Text = Math.Round(slider.Value).ToString();
            }
        };

        var applyButton = new Button
        {
            Content = "发送",
            Padding = new Avalonia.Thickness(12, 6),
            Background = new SolidColorBrush(Color.Parse("#0EA5E9")),
            Foreground = Brushes.White,
            CornerRadius = new Avalonia.CornerRadius(8)
        };
        applyButton.Click += async (_, _) => await ApplySlotAsync(slot, Math.Round(slider.Value));
        Grid.SetColumn(applyButton, 3);
        row.Children.Add(applyButton);

        return row;
    }

    private async Task ApplySlotAsync(DeviceControlSlot slot, double level)
    {
        try
        {
            var result = await _controlViewModel.ExecuteAsync(slot, level);
            LastResultText.Text = _controlViewModel.LastResultText;
            LastResultText.Foreground = new SolidColorBrush(
                Color.Parse(_controlViewModel.LastCommandRejected || _controlViewModel.LastCommandOutcomeUnknown
                    ? "#F59E0B"
                    : "#10B981"));
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "控制命令执行失败");
            LastResultText.Text = $"执行失败: {ex.Message}";
            LastResultText.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
        }
    }

    private async void OnEmergencyStop(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _appService.EmergencyStopAsync("桌面控制页急停", System.Threading.CancellationToken.None);
            LastResultText.Text = "紧急停止已执行";
            LastResultText.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "紧急停止失败");
            LastResultText.Text = $"紧急停止失败: {ex.Message}";
        }
    }

    private static IDeviceQueryService ResolveQueryService()
    {
        return ApplicationHost.TryGetService<IDeviceQueryService>()
            ?? throw new InvalidOperationException("设备查询服务不可用。");
    }

    private static IDeviceApplicationService ResolveApplicationService()
    {
        return ApplicationHost.TryGetService<IDeviceApplicationService>()
            ?? throw new InvalidOperationException("设备应用服务不可用。");
    }
}
