using Avalonia.Controls;
using Avalonia.Interactivity;
using ChargingPanel.Core;
using ChargingPanel.Core.Data;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace ChargingPanel.Desktop.Views.Pages;

public partial class PluginsPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<PluginsPage>();
    
    public ObservableCollection<ScriptViewModel> Scripts { get; } = new();
    private ScriptViewModel? _selectedScript;
    
    public PluginsPage()
    {
        InitializeComponent();
        ScriptList.ItemsSource = Scripts;
        
        // 设置默认脚本模板
        CodeEditor.Text = GetDefaultScriptTemplate();
        
        // 与 EventsPage 一致：在构造函数中直接加载
        if (AppServices.IsInitialized)
        {
            LoadScripts();
        }
        
        Logger.Information("PluginsPage constructor completed");
    }

    private void LoadScripts()
    {
        Scripts.Clear();
        var records = Database.Instance.GetAllScripts();
        foreach (var record in records)
        {
            Scripts.Add(new ScriptViewModel
            {
                Id = record.Id,
                Name = record.Name,
                Game = record.Game,
                Version = record.Version,
                Enabled = record.Enabled,
                Content = record.Code
            });
        }
    }

    private string GetDefaultScriptTemplate()
    {
        return @"// 游戏适配脚本模板（程序内 JS）
//
// 可用 API（当前实现）:
//   console.log/info/warn/error/debug(...)
//   event.trigger(eventId, deviceId?, multiplier?)
//   game.publishEvent(eventType, oldValue?, newValue?)
//   device.getConnectedDevices()
//   device.setStrength(deviceId, channel, value)
//   device.increaseStrength(deviceId, channel, value)
//   device.decreaseStrength(deviceId, channel, value)
//   device.sendWaveform(deviceId, channel, frequency, strength, duration)
//   device.emergencyStop()
//   utils.delay(ms) / utils.random(min,max) / utils.clamp(v,min,max)
//   bridge.config(...) / bridge.startWebSocket() / bridge.startHTTP() / bridge.startUDP({port}) / bridge.onEvent(fn)

console.log('脚本已加载');

// 声明桥接配置（可选）
bridge.config({
    name: '示例脚本',
    version: '1.0.0'
});

// 按需启用通道（可选）
bridge.startWebSocket();
bridge.startHTTP();

// 映射外部 MOD 事件到程序事件（推荐）
bridge.onEvent(function (payload) {
    if (!payload) return null;
    var type = (payload.type || payload.event || '').toLowerCase();

    // 血量减少
    if (type === 'damage' || type === 'hp_lost') {
        var oldHp = Number(payload.oldHp || payload.oldValue || 100);
        var newHp = Number(payload.hp || payload.newValue || 0);
        return {
            eventId: 'lost-hp',
            gameEventType: 'HealthLost',
            oldValue: oldHp,
            newValue: newHp
        };
    }

    // 下一回合
    if (type === 'next_turn' || type === 'new_round') {
        return {
            eventId: 'new-round',
            gameEventType: 'NewRound'
        };
    }

    // 角色死亡
    if (type === 'death' || type === 'player_dead') {
        return {
            eventId: 'dead',
            gameEventType: 'Death',
            oldValue: Number(payload.oldValue || 1),
            newValue: 0
        };
    }

    return null;
});

// 本地测试（可注释）
// game.publishEvent('lost-hp', 100, 80);
// utils.delay(300);
// game.publishEvent('new-round', 0, 0);
// utils.delay(300);
// game.publishEvent('dead', 10, 0);
";
    }

    private void OnScriptSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ScriptList.SelectedItem is ScriptViewModel script)
        {
            _selectedScript = script;
            ScriptFileName.Text = $"{script.Name} ({script.Game})";
            ScriptName.Text = script.Name;
            ScriptGame.Text = script.Game;
            ScriptVersion.Text = script.Version;
            CodeEditor.Text = script.Content;
            Logger.Information("Selected script: {Name}", script.Name);
        }
    }

    private void OnNewScriptClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var newId = $"script_{Guid.NewGuid():N}".Substring(0, 20);
            
            _selectedScript = new ScriptViewModel
            {
                Id = newId,
                Name = "新脚本",
                Game = "未指定",
                Version = "1.0.0",
                Enabled = false,
                Content = GetDefaultScriptTemplate()
            };
            
            ScriptFileName.Text = "新脚本 (未保存)";
            ScriptName.Text = "新脚本";
            ScriptGame.Text = "未指定";
            ScriptVersion.Text = "1.0.0";
            CodeEditor.Text = GetDefaultScriptTemplate();
            
            Logger.Information("New script created: {ScriptId}", newId);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to create new script");
        }
    }

    private async void OnImportScriptClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;
            
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "导入脚本",
                AllowMultiple = true,  // 支持多选
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("JavaScript") { Patterns = new[] { "*.js" } },
                    new Avalonia.Platform.Storage.FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } }
                }
            });
            
            if (files.Count > 0)
            {
                foreach (var file in files)
                {
                    await using var stream = await file.OpenReadAsync();
                    using var reader = new StreamReader(stream);
                    var content = await reader.ReadToEndAsync();
                    
                    var newId = $"script_{Guid.NewGuid():N}".Substring(0, 20);
                    var fileName = Path.GetFileNameWithoutExtension(file.Name);
                    
                    // 直接保存到数据库
                    var record = new ScriptRecord
                    {
                        Id = newId,
                        Name = fileName,
                        Game = "未指定",
                        Version = "1.0.0",
                        Enabled = false,
                        Code = content
                    };
                    
                    Database.Instance.SaveScript(record);
                    Logger.Warning("[PluginsPage] Script imported and saved: {FileName} -> {Id}", file.Name, newId);
                }
                
                // 重新加载列表
                LoadScripts();
                
                // 如果只导入了一个文件，选中它并显示在编辑器中
                if (files.Count == 1)
                {
                    var lastScript = Scripts.LastOrDefault();
                    if (lastScript != null)
                    {
                        _selectedScript = lastScript;
                        ScriptFileName.Text = $"{lastScript.Name} (已导入)";
                        ScriptName.Text = lastScript.Name;
                        ScriptGame.Text = lastScript.Game;
                        ScriptVersion.Text = lastScript.Version;
                        CodeEditor.Text = lastScript.Content;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to import script");
        }
    }

    private void OnSaveScriptClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            // 如果没有选中脚本，创建一个新的
            if (_selectedScript == null)
            {
                var newId = $"script_{Guid.NewGuid():N}".Substring(0, 20);
                _selectedScript = new ScriptViewModel
                {
                    Id = newId,
                    Name = ScriptName.Text ?? "新脚本",
                    Game = ScriptGame.Text ?? "未指定",
                    Version = ScriptVersion.Text ?? "1.0.0",
                    Enabled = false,
                    Content = CodeEditor.Text ?? ""
                };
                Logger.Information("Created new script for save: {ScriptId}", newId);
            }
            else
            {
                // 更新 ViewModel
                _selectedScript.Name = ScriptName.Text ?? "未命名";
                _selectedScript.Game = ScriptGame.Text ?? "未指定";
                _selectedScript.Version = ScriptVersion.Text ?? "1.0.0";
                _selectedScript.Content = CodeEditor.Text ?? "";
            }
            
            var record = new ScriptRecord
            {
                Id = _selectedScript.Id,
                Name = _selectedScript.Name,
                Game = _selectedScript.Game,
                Version = _selectedScript.Version,
                Enabled = _selectedScript.Enabled,
                Code = _selectedScript.Content  // 使用 Code 字段保存
            };
            
            Database.Instance.SaveScript(record);
            
            // 更新文件名显示
            ScriptFileName.Text = $"{_selectedScript.Name} ({_selectedScript.Game}) - 已保存";
            
            // 重新加载列表
            LoadScripts();
            
            // 重新选中当前脚本
            var savedScript = Scripts.FirstOrDefault(s => s.Id == _selectedScript.Id);
            if (savedScript != null)
            {
                _selectedScript = savedScript;
            }
            
            Logger.Information("Script saved: {ScriptId} - {ScriptName}", record.Id, record.Name);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to save script: {Message}", ex.Message);
        }
    }

    private async void OnExportScriptClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                Logger.Warning("Cannot get TopLevel for file dialog");
                return;
            }
            
            // 使用脚本名称或默认名称
            var suggestedName = _selectedScript?.Name ?? ScriptName.Text ?? "script";
            if (string.IsNullOrWhiteSpace(suggestedName)) suggestedName = "script";
            
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "导出脚本",
                SuggestedFileName = $"{suggestedName}.js",
                FileTypeChoices = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("JavaScript") { Patterns = new[] { "*.js" } }
                }
            });
            
            if (file != null)
            {
                await using var stream = await file.OpenWriteAsync();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(CodeEditor.Text ?? "");
                
                ScriptFileName.Text = $"{suggestedName} - 已导出";
                Logger.Information("Script exported: {FileName}", file.Name);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to export script: {Message}", ex.Message);
        }
    }

    private void OnScriptToggleChanged(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is ToggleSwitch toggle && toggle.DataContext is ScriptViewModel script)
            {
                script.Enabled = toggle.IsChecked ?? false;
                
                var record = new ScriptRecord
                {
                    Id = script.Id,
                    Name = script.Name,
                    Game = script.Game,
                    Version = script.Version,
                    Enabled = script.Enabled,
                    Code = script.Content  // 使用 Code 字段
                };
                
                Database.Instance.SaveScript(record);
                Logger.Information("Script {ScriptId} enabled: {Enabled}", script.Id, script.Enabled);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to toggle script");
        }
    }

    private void OnDeleteScriptClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string scriptId)
        {
            Database.Instance.DeleteScript(scriptId);
            LoadScripts();
            
            if (_selectedScript?.Id == scriptId)
            {
                _selectedScript = null;
                ScriptFileName.Text = "未选择脚本";
                ScriptName.Text = "";
                ScriptGame.Text = "";
                ScriptVersion.Text = "";
                CodeEditor.Text = GetDefaultScriptTemplate();
            }
            
            Logger.Information("Script deleted: {ScriptId}", scriptId);
        }
    }
}

public class ScriptViewModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Game { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Enabled { get; set; }
    public string Content { get; set; } = "";
}
