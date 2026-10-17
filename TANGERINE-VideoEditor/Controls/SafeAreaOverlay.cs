using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TANGERINE_VideoEditor.Controls;

/// <summary>
/// Safe area and guideline overlay for preview monitor.
/// Shows title-safe, action-safe, and custom guidelines for professional framing.
/// </summary>
public class SafeAreaOverlay : Canvas
{
    public static readonly DependencyProperty ShowTitleSafeProperty =
        DependencyProperty.Register(nameof(ShowTitleSafe), typeof(bool), typeof(SafeAreaOverlay),
            new PropertyMetadata(false, OnVisibilityChanged));

    public static readonly DependencyProperty ShowActionSafeProperty =
        DependencyProperty.Register(nameof(ShowActionSafe), typeof(bool), typeof(SafeAreaOverlay),
            new PropertyMetadata(false, OnVisibilityChanged));

    public static readonly DependencyProperty ShowCenterCrossProperty =
        DependencyProperty.Register(nameof(ShowCenterCross), typeof(bool), typeof(SafeAreaOverlay),
            new PropertyMetadata(false, OnVisibilityChanged));

    public static readonly DependencyProperty ShowGridProperty =
        DependencyProperty.Register(nameof(ShowGrid), typeof(bool), typeof(SafeAreaOverlay),
            new PropertyMetadata(false, OnVisibilityChanged));

    public bool ShowTitleSafe
    {
        get => (bool)GetValue(ShowTitleSafeProperty);
        set => SetValue(ShowTitleSafeProperty, value);
    }

    public bool ShowActionSafe
    {
        get => (bool)GetValue(ShowActionSafeProperty);
        set => SetValue(ShowActionSafeProperty, value);
    }

    public bool ShowCenterCross
    {
        get => (bool)GetValue(ShowCenterCrossProperty);
        set => SetValue(ShowCenterCrossProperty, value);
    }

    public bool ShowGrid
    {
        get => (bool)GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public SafeAreaOverlay()
    {
        IsHitTestVisible = false;
        SizeChanged += (_, _) => RedrawOverlay();
    }

    private void RedrawOverlay()
    {
        Children.Clear();

        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        if (ShowGrid)
            DrawGrid();

        if (ShowActionSafe)
            DrawActionSafe();

        if (ShowTitleSafe)
            DrawTitleSafe();

        if (ShowCenterCross)
            DrawCenterCross();
    }

    private void DrawTitleSafe()
    {
        // Title-safe area: 90% of frame (5% margin each side)
        double margin = 0.05;
        DrawRectangle(
            ActualWidth * margin,
            ActualHeight * margin,
            ActualWidth * (1 - 2 * margin),
            ActualHeight * (1 - 2 * margin),
            Colors.Yellow, 2, true);
    }

    private void DrawActionSafe()
    {
        // Action-safe area: 95% of frame (2.5% margin each side)
        double margin = 0.025;
        DrawRectangle(
            ActualWidth * margin,
            ActualHeight * margin,
            ActualWidth * (1 - 2 * margin),
            ActualHeight * (1 - 2 * margin),
            Colors.Cyan, 2, true);
    }

    private void DrawCenterCross()
    {
        double cx = ActualWidth / 2;
        double cy = ActualHeight / 2;
        double size = 20;

        // Horizontal line
        var hLine = new Line
        {
            X1 = cx - size, Y1 = cy,
            X2 = cx + size, Y2 = cy,
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 1
        };
        Children.Add(hLine);

        // Vertical line
        var vLine = new Line
        {
            X1 = cx, Y1 = cy - size,
            X2 = cx, Y2 = cy + size,
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 1
        };
        Children.Add(vLine);
    }

    private void DrawGrid()
    {
        // Rule of thirds grid (3x3)
        var brush = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255));

        // Vertical lines
        for (int i = 1; i < 3; i++)
        {
            double x = ActualWidth * i / 3;
            var line = new Line
            {
                X1 = x, Y1 = 0,
                X2 = x, Y2 = ActualHeight,
                Stroke = brush,
                StrokeThickness = 1
            };
            Children.Add(line);
        }

        // Horizontal lines
        for (int i = 1; i < 3; i++)
        {
            double y = ActualHeight * i / 3;
            var line = new Line
            {
                X1 = 0, Y1 = y,
                X2 = ActualWidth, Y2 = y,
                Stroke = brush,
                StrokeThickness = 1
            };
            Children.Add(line);
        }
    }

    private void DrawRectangle(double x, double y, double width, double height, Color color, double thickness, bool dashed)
    {
        var rect = new Rectangle
        {
            Width = width,
            Height = height,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness,
            Fill = Brushes.Transparent
        };

        if (dashed)
            rect.StrokeDashArray = new DoubleCollection { 5, 5 };

        SetLeft(rect, x);
        SetTop(rect, y);
        Children.Add(rect);
    }

    private static void OnVisibilityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SafeAreaOverlay overlay)
            overlay.RedrawOverlay();
    }
}
