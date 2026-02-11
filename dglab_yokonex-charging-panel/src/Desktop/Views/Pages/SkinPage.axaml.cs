using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ChargingPanel.Core;
using ChargingPanel.Core.Data;
using ChargingPanel.Desktop.Views;
using Serilog;
using System;
using System.IO;
using System.Text.Json;

namespace ChargingPanel.Desktop.Views.Pages;

public partial class SkinPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<SkinPage>();

    public SkinPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        LoadFromSettings();
    }

    public void SaveAndApply()
    {
        SaveToSettings();
        ApplyToUi();
    }

    private void LoadFromSettings()
    {
        if (!AppServices.IsInitialized)
        {
            return;
        }

        var db = Database.Instance;
        var theme = db.GetSetting<string>("ui.theme", "dark") ?? "dark";
        ThemeSelector.SelectedIndex = theme switch
        {
            "dark" => 0,
            "light" => 1,
            "system" => 2,
            _ => 0
        };

        AccentColorInput.Text = db.GetSetting<string>("ui.skin.accent", "#0EA5E9") ?? "#0EA5E9";
        WindowBackgroundInput.Text = db.GetSetting<string>("ui.skin.windowBackground", "#0B1421") ?? "#0B1421";
        BackgroundImagePathInput.Text = db.GetSetting<string>("ui.skin.backgroundImagePath", "") ?? "";

        var overlayOpacity = db.GetSetting<int>("ui.skin.backgroundOverlayOpacity", 70);
        BackgroundOpacitySlider.Value = overlayOpacity;
        BackgroundOpacityText.Text = $"{overlayOpacity}%";

        var overlayColorHex = db.GetSetting<string>("ui.skin.backgroundOverlayColor", WindowBackgroundInput.Text ?? "#0B1421")
                              ?? (WindowBackgroundInput.Text ?? "#0B1421");
        ApplyOverlayColorHex(overlayColorHex);

        var textBoxOpacity = Math.Clamp(db.GetSetting<int>("ui.skin.textBoxOpacity", 100), 0, 100);
        TextBoxOpacitySlider.Value = textBoxOpacity;
        TextBoxOpacityText.Text = $"{textBoxOpacity}%";

        FontFamilyInput.Text = db.GetSetting<string>("ui.skin.fontFamily", "") ?? "";
        LogoIconInput.Text = db.GetSetting<string>("ui.skin.logoIcon", "🐺") ?? "🐺";
    }

    private void SaveToSettings()
    {
        var db = Database.Instance;
        db.SetSetting("ui.theme", ThemeSelector.SelectedIndex switch
        {
            0 => "dark",
            1 => "light",
            2 => "system",
            _ => "dark"
        });
        db.SetSetting("ui.skin.accent", AccentColorInput.Text ?? "#0EA5E9");
        db.SetSetting("ui.skin.windowBackground", WindowBackgroundInput.Text ?? "#0B1421");
        db.SetSetting("ui.skin.backgroundImagePath", BackgroundImagePathInput.Text ?? "");
        db.SetSetting("ui.skin.backgroundOverlayColor", GetNormalizedOverlayHex());
        db.SetSetting("ui.skin.backgroundOverlayOpacity", (int)BackgroundOpacitySlider.Value);
        db.SetSetting("ui.skin.textBoxOpacity", (int)TextBoxOpacitySlider.Value);
        db.SetSetting("ui.skin.fontFamily", FontFamilyInput.Text ?? "");
        db.SetSetting("ui.skin.logoIcon", LogoIconInput.Text ?? "🐺");
    }

    private void ApplyToUi()
    {
        var theme = ThemeSelector.SelectedIndex switch
        {
            0 => "dark",
            1 => "light",
            2 => "system",
            _ => "dark"
        };
        App.SetTheme(theme);

        var parent = this.Parent;
        while (parent != null && parent is not MainWindow)
        {
            parent = (parent as Control)?.Parent;
        }

        if (parent is MainWindow window)
        {
            window.ApplySkinPreferences();
        }
    }

    private void OnApplyAndSaveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            SaveAndApply();
            StatusText.Text = "皮肤已保存并应用";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to save skin settings");
            StatusText.Text = $"保存失败: {ex.Message}";
        }
    }

    private void OnResetDefaultClick(object? sender, RoutedEventArgs e)
    {
        ThemeSelector.SelectedIndex = 0;
        AccentColorInput.Text = "#0EA5E9";
        WindowBackgroundInput.Text = "#0B1421";
        BackgroundImagePathInput.Text = "";
        ApplyOverlayColorHex("#0B1421");
        BackgroundOpacitySlider.Value = 70;
        BackgroundOpacityText.Text = "70%";
        TextBoxOpacitySlider.Value = 100;
        TextBoxOpacityText.Text = "100%";
        FontFamilyInput.Text = "";
        LogoIconInput.Text = "🐺";
        SaveAndApply();
        StatusText.Text = "已恢复默认皮肤";
    }

    private async void OnExportSkinClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                StatusText.Text = "无法打开导出对话框";
                return;
            }

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出皮肤包",
                SuggestedFileName = $"skin-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } }
                }
            });

            if (file == null)
            {
                return;
            }

            var package = CreateSkinPackage();
            var json = JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true });
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);
            StatusText.Text = $"已导出: {file.Name}";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to export skin package");
            StatusText.Text = $"导出失败: {ex.Message}";
        }
    }

    private async void OnImportSkinClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                StatusText.Text = "无法打开导入对话框";
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入皮肤包",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
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
            var package = JsonSerializer.Deserialize<SkinPackage>(content);
            if (package == null)
            {
                StatusText.Text = "皮肤包格式错误";
                return;
            }

            ThemeSelector.SelectedIndex = package.Theme switch
            {
                "dark" => 0,
                "light" => 1,
                "system" => 2,
                _ => 0
            };
            AccentColorInput.Text = package.AccentColor;
            WindowBackgroundInput.Text = package.WindowBackground;
            BackgroundImagePathInput.Text = package.BackgroundImagePath ?? "";

            ApplyOverlayColorHex(package.BackgroundOverlayColor);
            BackgroundOpacitySlider.Value = Math.Clamp(package.BackgroundOverlayOpacity, 0, 100);
            BackgroundOpacityText.Text = $"{(int)BackgroundOpacitySlider.Value}%";
            TextBoxOpacitySlider.Value = Math.Clamp(package.TextBoxOpacity ?? 100, 0, 100);
            TextBoxOpacityText.Text = $"{(int)TextBoxOpacitySlider.Value}%";

            FontFamilyInput.Text = package.FontFamily;
            LogoIconInput.Text = package.LogoIcon;

            SaveAndApply();
            StatusText.Text = $"已导入并应用: {files[0].Name}";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to import skin package");
            StatusText.Text = $"导入失败: {ex.Message}";
        }
    }

    private void OnBackgroundSwatchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex })
        {
            ApplyOverlayColorHex(hex);
        }
    }

    private void OnApplyBackgroundColorClick(object? sender, RoutedEventArgs e)
    {
        ApplyOverlayColorHex(BackgroundOverlayColorInput.Text);
    }

    private void OnBackgroundOpacityChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        BackgroundOpacityText.Text = $"{(int)e.NewValue}%";
    }

    private void OnTextBoxOpacityChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        TextBoxOpacityText.Text = $"{(int)e.NewValue}%";
    }

    private async void OnPickBackgroundImageClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                StatusText.Text = "无法打开文件选择器";
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择背景图",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("图片文件") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" } }
                }
            });

            if (files.Count == 0)
            {
                return;
            }

            BackgroundImagePathInput.Text = files[0].Path.LocalPath;
            StatusText.Text = $"已选择背景图: {files[0].Name}";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to pick background image");
            StatusText.Text = $"选择失败: {ex.Message}";
        }
    }

    private void OnClearBackgroundImageClick(object? sender, RoutedEventArgs e)
    {
        BackgroundImagePathInput.Text = "";
        StatusText.Text = "已清除背景图";
    }

    private string GetNormalizedOverlayHex()
    {
        return TryParseColor(BackgroundOverlayColorInput.Text, out var color)
            ? ToRgbHex(color)
            : "#0B1421";
    }

    private void ApplyOverlayColorHex(string? rawHex)
    {
        if (!TryParseColor(rawHex, out var color))
        {
            color = Color.Parse("#0B1421");
        }

        var normalizedHex = ToRgbHex(color);
        BackgroundOverlayColorInput.Text = normalizedHex;
        WindowBackgroundInput.Text = normalizedHex;
        BackgroundColorPreviewText.Text = normalizedHex;
        if (BackgroundColorPreviewBox != null)
        {
            BackgroundColorPreviewBox.Background = new SolidColorBrush(color);
        }
    }

    private SkinPackage CreateSkinPackage()
    {
        return new SkinPackage
        {
            Version = 3,
            Theme = ThemeSelector.SelectedIndex switch
            {
                0 => "dark",
                1 => "light",
                2 => "system",
                _ => "dark"
            },
            AccentColor = AccentColorInput.Text ?? "#0EA5E9",
            WindowBackground = WindowBackgroundInput.Text ?? "#0B1421",
            BackgroundImagePath = BackgroundImagePathInput.Text ?? "",
            BackgroundOverlayColor = GetNormalizedOverlayHex(),
            BackgroundOverlayOpacity = (int)BackgroundOpacitySlider.Value,
            TextBoxOpacity = (int)TextBoxOpacitySlider.Value,
            FontFamily = FontFamilyInput.Text ?? "",
            LogoIcon = LogoIconInput.Text ?? "🐺",
            ExportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };
    }

    private static string ToRgbHex(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static bool TryParseColor(string? value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            color = Color.Parse(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class SkinPackage
    {
        public int Version { get; set; }
        public string Theme { get; set; } = "dark";
        public string AccentColor { get; set; } = "#0EA5E9";
        public string WindowBackground { get; set; } = "#0B1421";
        public string BackgroundImagePath { get; set; } = "";
        public string BackgroundOverlayColor { get; set; } = "#0B1421";
        public int BackgroundOverlayOpacity { get; set; } = 70;
        public int? TextBoxOpacity { get; set; } = 100;
        public string FontFamily { get; set; } = "";
        public string LogoIcon { get; set; } = "🐺";
        public string ExportedAt { get; set; } = "";
    }
}

