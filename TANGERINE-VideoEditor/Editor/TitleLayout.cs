using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Sequence-relative title layout shared by the live monitor and PNG export.
/// Insets reserve a safe area at an aligned edge; background padding expands
/// around the measured glyphs without changing the text alignment anchor.
/// </summary>
public static class TitleLayout
{
    private static double Finite(double value, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, maximum) : 0;

    public static Point TextOrigin(ClipText style, double width, double height, Size textSize)
    {
        double insetX = width * Finite(style.HorizontalInset, .5);
        double insetY = height * Finite(style.VerticalInset, .5);
        return new Point(style.HorizontalAlignment switch
        {
            "Left" => insetX,
            "Right" => width - textSize.Width - insetX,
            _ => (width - textSize.Width) / 2
        }, style.VerticalAlignment switch
        {
            "Top" => insetY,
            "Bottom" => height - textSize.Height - insetY,
            _ => (height - textSize.Height) / 2
        });
    }

    public static Border? Background(ClipText style, Size textSize, double scale, Brush brush)
    {
        if (string.IsNullOrWhiteSpace(style.BackgroundColor)) return null;
        double paddingX = Finite(style.BackgroundPaddingX, 500) * scale;
        double paddingY = Finite(style.BackgroundPaddingY, 500) * scale;
        var background = new Border
        {
            Width = Math.Max(0, textSize.Width + paddingX * 2),
            Height = Math.Max(0, textSize.Height + paddingY * 2),
            Background = brush,
            Opacity = double.IsFinite(style.BackgroundOpacity) ? Math.Clamp(style.BackgroundOpacity, 0, 1) : 1,
            CornerRadius = new CornerRadius(Finite(style.BackgroundCornerRadius, 500) * scale),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(background, -paddingX);
        Canvas.SetTop(background, -paddingY);
        return background;
    }
}
