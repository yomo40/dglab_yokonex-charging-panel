using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using ChargingPanel.Core.Data;
using ChargingPanel.Desktop.Views;
using System;

namespace ChargingPanel.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        
        // 从数据库加载主题设置
        ApplyTheme();
    }
    
    /// <summary>
    /// 应用主题设置
    /// </summary>
    public void ApplyTheme()
    {
        try
        {
            var themeSetting = Database.Instance.GetSetting<string>("ui.theme", "dark") ?? "dark";
            RequestedThemeVariant = themeSetting.ToLower() switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                "system" => ThemeVariant.Default,
                _ => ThemeVariant.Dark
            };
        }
        catch
        {
            // 数据库未初始化时使用默认暗色主题
            RequestedThemeVariant = ThemeVariant.Dark;
        }
    }
    
    /// <summary>
    /// 切换主题
    /// </summary>
    public static void SetTheme(string theme)
    {
        if (Application.Current is App app)
        {
            app.RequestedThemeVariant = theme.ToLower() switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                "system" => ThemeVariant.Default,
                _ => ThemeVariant.Dark
            };
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
