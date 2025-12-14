using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Serilog;
using System;
using System.Threading.Tasks;

namespace ChargingPanel.Desktop.Views;

/// <summary>
/// 坐标拾取窗口
/// </summary>
public partial class CoordinatePickerWindow : Window
{
    private static readonly ILogger Logger = Log.ForContext<CoordinatePickerWindow>();
    
    private bool _isDragging;
    private Point _startPoint;      // 窗口内逻辑坐标（用于绘制）
    private SelectedArea? _currentArea;
    private double _scaling = 1.0;

    public SelectedArea? Result { get; private set; }

    public class SelectedArea
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public CoordinatePickerWindow()
    {
        InitializeComponent();
        
        RootPanel.PointerMoved += OnPointerMoved;
        RootPanel.PointerPressed += OnPointerPressed;
        RootPanel.PointerReleased += OnPointerReleased;
        KeyDown += OnKeyDown;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        // 直接使用 Avalonia 提供的窗口内坐标
        var pos = e.GetPosition(RootPanel);
        
        // 更新十字准星
        CrosshairH.StartPoint = new Point(0, pos.Y);
        CrosshairH.EndPoint = new Point(Bounds.Width, pos.Y);
        CrosshairV.StartPoint = new Point(pos.X, 0);
        CrosshairV.EndPoint = new Point(pos.X, Bounds.Height);
        
        // 计算屏幕坐标用于显示
        int screenX = Position.X + (int)(pos.X * _scaling);
        int screenY = Position.Y + (int)(pos.Y * _scaling);
        
        if (!_isDragging)
        {
            Canvas.SetLeft(CoordTip, pos.X + 20);
            Canvas.SetTop(CoordTip, pos.Y + 20);
            CoordTipText.Text = $"屏幕 X:{screenX} Y:{screenY}";
        }
        else
        {
            // 计算选框（窗口坐标，直接用于绘制）
            double left = Math.Min(_startPoint.X, pos.X);
            double top = Math.Min(_startPoint.Y, pos.Y);
            double width = Math.Abs(pos.X - _startPoint.X);
            double height = Math.Abs(pos.Y - _startPoint.Y);
            
            Canvas.SetLeft(SelectionBox, left);
            Canvas.SetTop(SelectionBox, top);
            SelectionBox.Width = width;
            SelectionBox.Height = height;
            
            // 显示屏幕像素尺寸
            int pixelWidth = (int)(width * _scaling);
            int pixelHeight = (int)(height * _scaling);
            
            Canvas.SetLeft(SizeLabel, left + width + 8);
            Canvas.SetTop(SizeLabel, top - 5);
            SizeLabelText.Text = $"{pixelWidth} × {pixelHeight}";
            SizeLabel.IsVisible = true;
            
            Canvas.SetLeft(CoordTip, pos.X + 20);
            Canvas.SetTop(CoordTip, pos.Y + 20);
            CoordTipText.Text = $"拖动选择区域...";
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(RootPanel).Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _startPoint = e.GetPosition(RootPanel);
            
            SelectionBox.IsVisible = true;
            Canvas.SetLeft(SelectionBox, _startPoint.X);
            Canvas.SetTop(SelectionBox, _startPoint.Y);
            SelectionBox.Width = 0;
            SelectionBox.Height = 0;
            
            ButtonPanel.IsVisible = false;
            CrosshairH.IsVisible = false;
            CrosshairV.IsVisible = false;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging) return;
        
        _isDragging = false;
        SizeLabel.IsVisible = false;
        CrosshairH.IsVisible = true;
        CrosshairV.IsVisible = true;
        
        var endPoint = e.GetPosition(RootPanel);
        
        // 计算窗口内逻辑坐标
        double left = Math.Min(_startPoint.X, endPoint.X);
        double top = Math.Min(_startPoint.Y, endPoint.Y);
        double width = Math.Abs(endPoint.X - _startPoint.X);
        double height = Math.Abs(endPoint.Y - _startPoint.Y);
        
        // 转换为屏幕物理像素坐标
        int screenX = Position.X + (int)(left * _scaling);
        int screenY = Position.Y + (int)(top * _scaling);
        int pixelWidth = (int)(width * _scaling);
        int pixelHeight = (int)(height * _scaling);
        
        if (pixelWidth >= 10 && pixelHeight >= 5)
        {
            _currentArea = new SelectedArea
            {
                X = screenX,
                Y = screenY,
                Width = pixelWidth,
                Height = pixelHeight
            };
            
            InfoTitle.Text = "✅ 区域已选择";
            InfoText.Text = $"X={screenX} Y={screenY} 宽={pixelWidth} 高={pixelHeight}";
            ButtonPanel.IsVisible = true;
            
            Logger.Information("Selected: X={X} Y={Y} W={W} H={H}", screenX, screenY, pixelWidth, pixelHeight);
        }
        else
        {
            _currentArea = null;
            InfoTitle.Text = "⚠️ 区域太小";
            InfoText.Text = $"需要至少 10×5 像素";
            SelectionBox.IsVisible = false;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _currentArea != null) { Result = _currentArea; Close(); }
        else if (e.Key == Key.Escape) { Result = null; Close(); }
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (_currentArea != null) { Result = _currentArea; Close(); }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = null; Close();
    }

    public static async Task<SelectedArea?> ShowPickerAsync(Window owner)
    {
        var picker = new CoordinatePickerWindow();
        
        var screen = owner.Screens.Primary;
        if (screen != null)
        {
            picker._scaling = screen.Scaling;
            picker.Position = new PixelPoint(0, 0);
            picker.Width = screen.Bounds.Width / screen.Scaling;
            picker.Height = screen.Bounds.Height / screen.Scaling;
        }
        
        await picker.ShowDialog(owner);
        return picker.Result;
    }
}
