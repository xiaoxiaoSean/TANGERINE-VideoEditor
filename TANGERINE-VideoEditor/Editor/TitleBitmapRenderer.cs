using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Rasterizes editable title data to a transparent frame for the video encoder.
/// Rendering happens on a dedicated STA worker so the exported glyphs use
/// WPF's text layout engine without blocking the editor dispatcher.
/// </summary>
public static class TitleBitmapRenderer
{
    public static void Render(TimelineClip clip, ProjectSequence sequence, string outputPath,
        double? localSeconds = null)
    {
        ClipText style = localSeconds is double time
            ? DynamicTitlePreset.RenderStyle(clip, time)
            : clip.Text ?? new ClipText { Content = clip.Name };
        int width = sequence.Width;
        int height = sequence.Height;
        var canvas = new Canvas { Width = width, Height = height, Background = Brushes.Transparent };
        GraphicShape? graphic = GraphicShape.FromClip(clip);
        if (graphic is not null)
        {
            Shape shape = graphic.CreateVisual(width, height, 1);
            double left = (width - shape.Width) / 2;
            double top = (height - shape.Height) / 2;
            Canvas.SetLeft(shape, left);
            Canvas.SetTop(shape, top);
            canvas.Children.Add(shape);
        }
        var face = new FontFamily(string.IsNullOrWhiteSpace(style.FontFamily) ? "Microsoft YaHei UI" : style.FontFamily);
        var foreground = ParseBrush(style.ForegroundColor, Brushes.White);
        var outlineBrush = ParseBrush(style.OutlineColor, Brushes.Black);
        double fontSize = Math.Max(1, style.FontSize);

        TextBlock CreateText(Brush color, bool underline = true) => new()
        {
            Text = style.Content,
            FontFamily = face,
            FontSize = fontSize,
            FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = color,
            TextDecorations = underline && style.Underline ? TextDecorations.Underline : null,
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = style.HorizontalAlignment switch
            {
                "Left" => TextAlignment.Left,
                "Right" => TextAlignment.Right,
                _ => TextAlignment.Center
            },
            LineHeight = Math.Max(1, style.LineHeight) * fontSize,
        };

        TextBlock title = CreateText(foreground);
        title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        bool spaced = Math.Abs(style.LetterSpacing) >= .001 && !string.IsNullOrEmpty(style.Content);
        Size titleSize = spaced ? LetterSpacingText.Measure(style, 1, 0) : title.DesiredSize;
        double x = style.HorizontalAlignment switch
        {
            "Left" => 0,
            "Right" => width - titleSize.Width,
            _ => (width - titleSize.Width) / 2
        };
        double y = style.VerticalAlignment switch
        {
            "Top" => 0,
            "Bottom" => height - titleSize.Height,
            _ => (height - titleSize.Height) / 2
        };
        // The complete transparent title is positioned by the shared FFmpeg
        // overlay. Keeping this bitmap centered lets position keyframes apply
        // once and gives scale/rotation the same sequence-center anchor.

        if (!string.IsNullOrWhiteSpace(style.BackgroundColor))
        {
            var background = new Border
            {
                Background = ParseBrush(style.BackgroundColor, Brushes.Transparent),
                Width = titleSize.Width,
                Height = titleSize.Height
            };
            Canvas.SetLeft(background, x);
            Canvas.SetTop(background, y);
            canvas.Children.Add(background);
        }

        if (style.OutlineEnabled && style.OutlineThickness > 0)
        {
            double radius = Math.Clamp(style.OutlineThickness, 0, 50);
            foreach (Point offset in new[]
            {
                new Point(-radius, 0), new Point(radius, 0), new Point(0, -radius), new Point(0, radius),
                new Point(-radius, -radius), new Point(radius, -radius), new Point(-radius, radius), new Point(radius, radius)
            })
            {
                if (spaced)
                {
                    LetterSpacingText.Draw(canvas, style, 1, 0,
                        x + offset.X, y + offset.Y, outlineBrush, drawUnderline: false);
                    continue;
                }
                TextBlock copy = CreateText(outlineBrush, underline: false);
                Canvas.SetLeft(copy, x + offset.X);
                Canvas.SetTop(copy, y + offset.Y);
                canvas.Children.Add(copy);
            }
        }
        if (style.ShadowEnabled)
        {
            title.Effect = new DropShadowEffect
            {
                Color = ParseColor(style.ShadowColor, Colors.Black),
                BlurRadius = Math.Max(0, style.ShadowBlur),
                ShadowDepth = Math.Sqrt(style.ShadowOffsetX * style.ShadowOffsetX + style.ShadowOffsetY * style.ShadowOffsetY),
                Direction = Math.Atan2(-style.ShadowOffsetY, style.ShadowOffsetX) * 180 / Math.PI,
                Opacity = Math.Clamp(style.ShadowOpacity, 0, 1)
            };
        }
        if (spaced)
            LetterSpacingText.Draw(canvas, style, 1, 0, x, y, foreground,
                style.ShadowEnabled ? title.Effect : null);
        else
        {
            Canvas.SetLeft(title, x);
            Canvas.SetTop(title, y);
            canvas.Children.Add(title);
        }
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        canvas.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    private static Brush ParseBrush(string? value, Brush fallback)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(value ?? string.Empty)!; }
        catch (Exception exception) when (exception is FormatException or ArgumentException or NotSupportedException)
        { return fallback; }
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(value ?? string.Empty); }
        catch (Exception exception) when (exception is FormatException or ArgumentException or NotSupportedException)
        { return fallback; }
    }
}
