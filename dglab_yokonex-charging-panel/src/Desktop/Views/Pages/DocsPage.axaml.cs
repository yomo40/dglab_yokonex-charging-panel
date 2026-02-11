using Avalonia.Controls;
using Avalonia.Interactivity;
using Serilog;
using System;
using System.IO;
using System.Linq;

namespace ChargingPanel.Desktop.Views.Pages;

public partial class DocsPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<DocsPage>();

    public DocsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        LoadDocs();
    }

    private void OnReloadDocsClick(object? sender, RoutedEventArgs e)
    {
        LoadDocs();
    }

    private void LoadDocs()
    {
        try
        {
            var path = FindDocsPath();
            if (path == null)
            {
                SourcePathText.Text = "来源: 未找到 docs/使用说明.md";
                DocsText.Text = "未找到文档文件，请确认发布目录包含 docs/使用说明.md。";
                return;
            }

            var raw = File.ReadAllText(path);
            SourcePathText.Text = $"来源: {path}";
            DocsText.Text = raw.Trim();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load docs");
            SourcePathText.Text = "来源: 读取失败";
            DocsText.Text = $"文档加载失败: {ex.Message}";
        }
    }

    private static string? FindDocsPath()
    {
        var markdownCandidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "docs", "使用说明.md"),
            Path.Combine(AppContext.BaseDirectory, "docs", "使用说明.md"),
            Path.Combine(Directory.GetCurrentDirectory(), "docs", "使用说明.md"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "Desktop", "docs", "使用说明.md"),
            Path.Combine(Directory.GetCurrentDirectory(), "dglab_yokonex-charging-panel", "src", "Desktop", "docs", "使用说明.md")
        };

        var markdownPath = markdownCandidates.FirstOrDefault(File.Exists);
        if (markdownPath != null)
        {
            return markdownPath;
        }

        return null;
    }
}
