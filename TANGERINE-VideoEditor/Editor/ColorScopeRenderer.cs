using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Samples the actual composed preview surface. Scopes never alter source
/// media or a project file and are refreshed only when the user requests it.
/// </summary>
public static class ColorScopeRenderer
{
    /// <summary>
    /// Computes approximate luma and chroma averages for a locally rendered
    /// frame. The statistics are descriptive; the caller decides whether to
    /// translate them into a color correction on another clip.
    /// </summary>
    public static Task<(double Luma, double Red, double Blue)> AverageColorAsync(BitmapSource frame)
    {
        BitmapSource converted = frame.Format == PixelFormats.Bgra32 ? frame :
            new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        if (width < 1 || height < 1)
            throw new InvalidOperationException("当前没有可分析的视频帧。");
        byte[] pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        return Task.Run(() =>
        {
            double luma = 0, red = 0, blue = 0;
            int count = 0;
            int step = Math.Max(1, Math.Min(width, height) / 192);
            for (int y = 0; y < height; y += step)
            for (int x = 0; x < width; x += step)
            {
                int index = (y * width + x) * 4;
                double b = pixels[index] / 255.0;
                double g = pixels[index + 1] / 255.0;
                double r = pixels[index + 2] / 255.0;
                luma += .2126 * r + .7152 * g + .0722 * b;
                red += r;
                blue += b;
                count++;
            }
            if (count == 0) return (0.0, 0.0, 0.0);
            return (luma / count, red / count, blue / count);
        });
    }

    /// <summary>Analyzes a frozen or UI-owned bitmap without opening a media stream.</summary>
    public static Task<BitmapSource> RenderBitmapAsync(BitmapSource frame, int mode)
    {
        int width = 512;
        int height = 256;
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
            context.DrawImage(frame, new Rect(0, 0, width, height));
        target.Render(drawing);
        target.Freeze();
        return RenderScopeAsync(target, mode);
    }

    public static async Task<BitmapSource> RenderCurrentFrameAsync(Visual mediaLayer, Visual overlay, int mode)
    {
        int width = 512;
        int height = 256;
        var source = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        double layerWidth = mediaLayer is FrameworkElement element ? element.ActualWidth : 0;
        double layerHeight = mediaLayer is FrameworkElement layer ? layer.ActualHeight : 0;
        if (layerWidth <= 0 || layerHeight <= 0)
            throw new InvalidOperationException("当前没有可分析的视频帧。");
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
        {
            var brush = new VisualBrush(mediaLayer) { Stretch = Stretch.Fill };
            context.DrawRectangle(brush, null, new Rect(0, 0, width, height));
            // The overlay is sampled with its native transparent background;
            // this includes visible title and graphics layers in the scopes.
            if (overlay is FrameworkElement overlayElement && overlayElement.ActualWidth > 0 && overlayElement.ActualHeight > 0)
            {
                var titleBrush = new VisualBrush(overlay) { Stretch = Stretch.Fill };
                context.DrawRectangle(titleBrush, null, new Rect(0, 0, width, height));
            }
        }
        source.Render(drawing);
        source.Freeze();
        return await RenderScopeAsync(source, mode);
    }

    private static async Task<BitmapSource> RenderScopeAsync(BitmapSource source, int mode)
    {
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        byte[] pixels = new byte[width * height * 4];
        source.CopyPixels(pixels, width * 4, 0);
        return await Task.Run(() =>
        {
            var counts = new int[width * height * 4];
            var histogram = new int[256];
            if (mode == 3)
            {
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    int luma = Math.Clamp((int)Math.Round(.2126 * pixels[i + 2] + .7152 * pixels[i + 1] + .0722 * pixels[i]), 0, 255);
                    histogram[luma]++;
                }
            }
            int step = 2;
            for (int y = 0; y < height; y += step)
            for (int x = 0; x < width; x += step)
            {
                int input = (y * width + x) * 4;
                int b = pixels[input], g = pixels[input + 1], r = pixels[input + 2];
                int luma = Math.Clamp((int)Math.Round(.2126 * r + .7152 * g + .0722 * b), 0, 255);
                int outputX = x;
                int outputY = 255 - luma;
                switch (mode)
                {
                    case 1:
                        Add(outputX, 255 - r, 0); Add(outputX, 255 - g, 1); Add(outputX, 255 - b, 2);
                        continue;
                    case 2:
                        outputX = Math.Clamp((int)Math.Round(256 + (b - luma) * .85), 0, 511);
                        outputY = Math.Clamp((int)Math.Round(128 - (r - luma) * .75), 0, 255);
                        break;
                    case 3: continue;
                }
                Add(outputX, outputY, 3);
            }
            if (mode == 3)
            {
                int maximum = Math.Max(1, histogram.Max());
                for (int x = 0; x < width; x++)
                {
                    int bin = x / 2;
                    int bar = (int)Math.Round(Math.Sqrt(histogram[bin] / (double)maximum) * 245);
                    for (int y = height - 1; y >= height - bar; y--) Add(x, y, 3);
                }
            }
            var output = new byte[pixels.Length];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bool grid = y % 64 == 0 || x % 128 == 0;
                int background = grid ? 34 : 14;
                int mono = Math.Min(255, counts[i + 3] * 25);
                output[i] = (byte)Math.Max(background, Math.Max(mono, Math.Min(255, counts[i + 2] * 27)));
                output[i + 1] = (byte)Math.Max(background, Math.Max(mono, Math.Min(255, counts[i + 1] * 27)));
                output[i + 2] = (byte)Math.Max(background, Math.Max(mono, Math.Min(255, counts[i] * 27)));
                output[i + 3] = 255;
            }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, output, width * 4);
            bitmap.Freeze();
            return bitmap;

            void Add(int x, int y, int channel)
            {
                int index = (y * width + x) * 4 + channel;
                counts[index]++;
            }
        });
    }
}
