using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Estimates a subject center from local frame edges. It does not identify a
/// person or upload media; the editor samples source frames and turns the
/// estimated centers into ordinary editable position keyframes.
/// </summary>
internal static class AutoReframeAnalyzer
{
    public static Task<(double X, double Y)> AnalyzeAsync(BitmapSource frame)
    {
        // WPF image conversion remains on the caller's dispatcher. Only the
        // copied, immutable byte array is processed on a background thread.
        BitmapSource rgba = frame.Format == PixelFormats.Bgra32 ? frame :
            new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int width = rgba.PixelWidth;
        int height = rgba.PixelHeight;
        if (width < 1 || height < 1)
            throw new InvalidOperationException("无法分析空白帧。");
        byte[] pixels = new byte[width * height * 4];
        rgba.CopyPixels(pixels, width * 4, 0);
        return Task.Run(() => Analyze(pixels, width, height));
    }

    private static (double X, double Y) Analyze(byte[] pixels, int width, int height)
    {
        if (width < 5 || height < 5) return (.5, .5);
        double weightSum = 0;
        double xSum = 0;
        double ySum = 0;
        int step = Math.Max(1, Math.Min(width, height) / 160);
        for (int y = step; y < height - step; y += step)
        for (int x = step; x < width - step; x += step)
        {
            double center = Luma(x, y);
            double edge = Math.Abs(center - Luma(x + step, y)) +
                Math.Abs(center - Luma(x, y + step));
            // Favor coherent detail near the middle over isolated borders.
            double nx = (x + .5) / width;
            double ny = (y + .5) / height;
            double prior = 1 - .35 * Math.Abs(nx - .5) - .35 * Math.Abs(ny - .5);
            double weight = Math.Max(0, edge - 12) * prior;
            weightSum += weight;
            xSum += weight * nx;
            ySum += weight * ny;
        }
        if (weightSum < 1) return (.5, .5);
        return (Math.Clamp(xSum / weightSum, .05, .95),
            Math.Clamp(ySum / weightSum, .05, .95));

        double Luma(int x, int y)
        {
            int index = (y * width + x) * 4;
            return .0722 * pixels[index] + .7152 * pixels[index + 1] +
                .2126 * pixels[index + 2];
        }
    }
}
