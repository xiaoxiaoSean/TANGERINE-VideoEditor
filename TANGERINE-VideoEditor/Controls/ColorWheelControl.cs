using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TANGERINE_VideoEditor.Controls;

/// <summary>
/// Interactive color wheel control for Lumetri color grading.
/// Allows dragging a puck to adjust color balance in shadows/midtones/highlights.
/// </summary>
public class ColorWheelControl : Canvas
{
    private Ellipse? _puck;
    private Point _center;
    private double _radius;
    private bool _isDragging;

    public static readonly DependencyProperty RedValueProperty =
        DependencyProperty.Register(nameof(RedValue), typeof(double), typeof(ColorWheelControl),
            new PropertyMetadata(0.0, OnColorValueChanged));

    public static readonly DependencyProperty BlueValueProperty =
        DependencyProperty.Register(nameof(BlueValue), typeof(double), typeof(ColorWheelControl),
            new PropertyMetadata(0.0, OnColorValueChanged));

    public double RedValue
    {
        get => (double)GetValue(RedValueProperty);
        set => SetValue(RedValueProperty, value);
    }

    public double BlueValue
    {
        get => (double)GetValue(BlueValueProperty);
        set => SetValue(BlueValueProperty, value);
    }

    public event EventHandler<ColorWheelChangedEventArgs>? ColorChanged;

    public ColorWheelControl()
    {
        Width = 200;
        Height = 200;
        Background = Brushes.Transparent;

        Loaded += OnLoaded;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _center = new Point(Width / 2, Height / 2);
        _radius = Math.Min(Width, Height) / 2 - 20;

        DrawWheel();
        UpdatePuckPosition();
    }

    private void DrawWheel()
    {
        Children.Clear();

        // Draw color wheel background
        var wheel = new Ellipse
        {
            Width = _radius * 2,
            Height = _radius * 2,
            Fill = CreateWheelGradient(),
            Stroke = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
            StrokeThickness = 2
        };
        SetLeft(wheel, _center.X - _radius);
        SetTop(wheel, _center.Y - _radius);
        Children.Add(wheel);

        // Draw center cross
        var hLine = new Line
        {
            X1 = _center.X - 10, Y1 = _center.Y,
            X2 = _center.X + 10, Y2 = _center.Y,
            Stroke = Brushes.Gray, StrokeThickness = 1
        };
        var vLine = new Line
        {
            X1 = _center.X, Y1 = _center.Y - 10,
            X2 = _center.X, Y2 = _center.Y + 10,
            Stroke = Brushes.Gray, StrokeThickness = 1
        };
        Children.Add(hLine);
        Children.Add(vLine);

        // Draw draggable puck
        _puck = new Ellipse
        {
            Width = 16,
            Height = 16,
            Fill = Brushes.White,
            Stroke = Brushes.Black,
            StrokeThickness = 2,
            Cursor = Cursors.Hand
        };
        Children.Add(_puck);
    }

    private Brush CreateWheelGradient()
    {
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new GradientStop(Colors.White, 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 200, 200), 0.3));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 100, 100), 0.6));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(200, 100, 255), 0.8));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(100, 100, 255), 1.0));
        return brush;
    }

    private void UpdatePuckPosition()
    {
        if (_puck == null) return;

        // Convert red/blue values (-1 to 1) to position
        double x = _center.X + RedValue * _radius;
        double y = _center.Y + BlueValue * _radius;

        // Clamp to circle
        double dx = x - _center.X;
        double dy = y - _center.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance > _radius)
        {
            x = _center.X + (dx / distance) * _radius;
            y = _center.Y + (dy / distance) * _radius;
        }

        SetLeft(_puck, x - 8);
        SetTop(_puck, y - 8);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        CaptureMouse();
        UpdateFromMousePosition(e.GetPosition(this));
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging)
            UpdateFromMousePosition(e.GetPosition(this));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        ReleaseMouseCapture();
    }

    private void UpdateFromMousePosition(Point pos)
    {
        double dx = pos.X - _center.X;
        double dy = pos.Y - _center.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        // Clamp to wheel radius
        if (distance > _radius)
        {
            dx = (dx / distance) * _radius;
            dy = (dy / distance) * _radius;
            distance = _radius;
        }

        RedValue = Math.Clamp(dx / _radius, -1, 1);
        BlueValue = Math.Clamp(dy / _radius, -1, 1);

        UpdatePuckPosition();
        ColorChanged?.Invoke(this, new ColorWheelChangedEventArgs(RedValue, BlueValue));
    }

    private static void OnColorValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ColorWheelControl control)
            control.UpdatePuckPosition();
    }
}

public class ColorWheelChangedEventArgs : EventArgs
{
    public double Red { get; }
    public double Blue { get; }

    public ColorWheelChangedEventArgs(double red, double blue)
    {
        Red = red;
        Blue = blue;
    }
}
