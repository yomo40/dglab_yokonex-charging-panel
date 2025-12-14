using Avalonia.Controls;
using Avalonia.Interactivity;
using ChargingPanel.Core;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices;
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
                Description = GetEventDescription(record)
            });
        }
    }

    private string GetEventDescription(EventRecord record)
    {
        var trigger = record.TriggerType switch
        {
            "decrease" => $"血量减少 {record.MinChange}-{record.MaxChange}%",
            "increase" => $"血量增加 {record.MinChange}-{record.MaxChange}%",
            "death" => "角色死亡",
            "revive" => "角色复活",
            "threshold" => $"血量阈值触发",
            _ => record.TriggerType
        };
        
        var action = record.ActionType switch
        {
            "set" => $"设置强度 {record.Strength}",
            "increase" => $"增加强度 {record.Strength}",
            "decrease" => $"减少强度 {record.Strength}",
            "waveform" => "发送波形",
            "pulse" => "发送脉冲",
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
                "decrease" => 0,
                "increase" => 1,
                "death" => 2,
                "revive" => 3,
                "threshold" => 4,
                _ => 0
            };
            
            MinChange.Text = evt.MinChange.ToString();
            MaxChange.Text = evt.MaxChange.ToString();
            
            ActionType.SelectedIndex = evt.ActionType switch
            {
                "set" => 0,
                "increase" => 1,
                "decrease" => 2,
                "waveform" => 3,
                "pulse" => 4,
                _ => 0
            };
            
            ActionStrength.Text = evt.Strength.ToString();
            ActionDuration.Text = evt.Duration.ToString();
            
            ActionChannel.SelectedIndex = evt.Channel switch
            {
                "A" => 0,
                "B" => 1,
                _ => 2
            };
            
            Priority.Text = evt.Priority.ToString();
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
            TriggerType = "decrease",
            MinChange = 1,
            MaxChange = 100,
            ActionType = "set",
            Strength = 50,
            Duration = 1000,
            Channel = "AB",
            Priority = 10
        };
        
        EventId.Text = newId;
        EventName.Text = "新事件";
        TriggerType.SelectedIndex = 0;
        MinChange.Text = "1";
        MaxChange.Text = "100";
        ActionType.SelectedIndex = 0;
        ActionStrength.Text = "50";
        ActionDuration.Text = "1000";
        ActionChannel.SelectedIndex = 2;
        Priority.Text = "10";
    }

    private void OnSaveEventClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedEvent == null) return;
        
        var record = new EventRecord
        {
            EventId = EventId.Text ?? "",
            Name = EventName.Text ?? "",
            Enabled = _selectedEvent.Enabled,
            TriggerType = (TriggerType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "decrease",
            MinChange = int.TryParse(MinChange.Text, out var min) ? min : 1,
            MaxChange = int.TryParse(MaxChange.Text, out var max) ? max : 100,
            ActionType = (ActionType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "set",
            Strength = int.TryParse(ActionStrength.Text, out var str) ? str : 50,
            Duration = int.TryParse(ActionDuration.Text, out var dur) ? dur : 1000,
            Channel = (ActionChannel.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "AB",
            Priority = int.TryParse(Priority.Text, out var pri) ? pri : 10
        };
        
        Database.Instance.SaveEvent(record);
        LoadEvents();
        
        Logger.Information("Event saved: {EventId}", record.EventId);
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
            var connectedDevices = AppServices.Instance.DeviceManager.GetConnectedDevices();
            if (connectedDevices.Count == 0)
            {
                Logger.Warning("No connected devices for test");
                return;
            }
            
            var deviceId = connectedDevices.First().Id;
            var channel = (ActionChannel.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
            {
                "A" => Channel.A,
                "B" => Channel.B,
                _ => Channel.AB
            };
            
            var strength = int.TryParse(ActionStrength.Text, out var s) ? s : 50;
            
            await AppServices.Instance.DeviceManager.SetStrengthAsync(
                deviceId, channel, strength, StrengthMode.Set, "EventTest");
            
            Logger.Information("Event test triggered: strength={Strength}, channel={Channel}", strength, channel);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Event test failed");
        }
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
}
