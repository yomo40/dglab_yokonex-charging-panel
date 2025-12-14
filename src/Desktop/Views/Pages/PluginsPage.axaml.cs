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
        
        if (AppServices.IsInitialized)
        {
            LoadScripts();
        }
        
        // 设置默认脚本模板
        CodeEditor.Text = GetDefaultScriptTemplate();
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
                Content = record.Content
            });
        }
    }

    private string GetDefaultScriptTemplate()
    {
        return @"// 游戏适配脚本模板
// 
// 可用 API:
//   device.setStrength(channel, value)      - 设置强度
//   device.addStrength(channel, value)      - 增加强度
//   device.sendWaveform(channel, waveform)  - 发送波形
//   events.trigger(eventId)                 - 触发事件
//   events.onBloodChange(callback)          - 监听血量变化
//   storage.get(key)                        - 获取存储值
//   storage.set(key, value)                 - 设置存储值
//   script.log(message)                     - 输出日志

// 初始化
script.log('脚本已加载');

// 监听血量变化
events.onBloodChange(function(oldValue, newValue) {
    var change = oldValue - newValue;
    
    if (change > 0) {
        // 掉血
        script.log('掉血: ' + change);
        
        if (change >= 30) {
            // 大量掉血
            device.setStrength('AB', 80);
        } else if (change >= 10) {
            // 中量掉血
            device.setStrength('AB', 50);
        } else {
            // 少量掉血
            device.addStrength('AB', change * 2);
        }
    } else if (change < 0) {
        // 回血
        script.log('回血: ' + (-change));
    }
    
    if (newValue <= 0) {
        // 死亡
        script.log('角色死亡!');
        events.trigger('dead');
    }
});

// 返回脚本元信息
return {
    name: '示例脚本',
    game: '通用',
    version: '1.0.0'
};
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
        }
    }

    private void OnNewScriptClick(object? sender, RoutedEventArgs e)
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
    }

    private void OnImportScriptClick(object? sender, RoutedEventArgs e)
    {
        // TODO: 实现文件选择对话框导入脚本
        Logger.Information("Script import not yet implemented");
    }

    private void OnSaveScriptClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedScript == null) return;
        
        var record = new ScriptRecord
        {
            Id = _selectedScript.Id,
            Name = ScriptName.Text ?? "未命名",
            Game = ScriptGame.Text ?? "未指定",
            Version = ScriptVersion.Text ?? "1.0.0",
            Enabled = _selectedScript.Enabled,
            Content = CodeEditor.Text ?? ""
        };
        
        Database.Instance.SaveScript(record);
        LoadScripts();
        
        Logger.Information("Script saved: {ScriptId}", record.Id);
    }

    private void OnExportScriptClick(object? sender, RoutedEventArgs e)
    {
        // TODO: 实现脚本导出
        Logger.Information("Script export not yet implemented");
    }

    private void OnScriptToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is ScriptViewModel script)
        {
            script.Enabled = cb.IsChecked ?? false;
            
            var record = new ScriptRecord
            {
                Id = script.Id,
                Name = script.Name,
                Game = script.Game,
                Version = script.Version,
                Enabled = script.Enabled,
                Content = script.Content
            };
            
            Database.Instance.SaveScript(record);
            Logger.Information("Script {ScriptId} enabled: {Enabled}", script.Id, script.Enabled);
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
