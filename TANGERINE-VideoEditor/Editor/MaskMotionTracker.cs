using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Estimates local translation by searching a small neighborhood around the
/// previous mask center. Normalized luminance patches are compared with sum
/// of absolute differences, allowing a manually corrected center to seed
/// forward or backward tracking without external services.
/// </summary>
internal static class MaskMotionTracker
{
    private sealed record Sample(int Width, int Height, byte[] Luma);

    public static Task<(double X, double Y)> TrackStepAsync(BitmapSource previous,
        BitmapSource current, double centerX, double centerY, double maskWidth, double maskHeight)
    {
        Sample first = Copy(previous);
        Sample second = Copy(current);
        return Task.Run(() => Search(first, second, centerX, centerY, maskWidth, maskHeight));
    }

    private static Sample Copy(BitmapSource source)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32 ? source :
            new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        if (width < 16 || height < 16)
            throw new InvalidOperationException("跟踪帧的画幅过小。");
        byte[] raw = new byte[width * height * 4];
        converted.CopyPixels(raw, width * 4, 0);
        byte[] luma = new byte[width * height];
        for (int pixel = 0; pixel < luma.Length; pixel++)
        {
            int color = pixel * 4;
            luma[pixel] = (byte)Math.Clamp((int)Math.Round(
                .0722 * raw[color] + .7152 * raw[color + 1] + .2126 * raw[color + 2]), 0, 255);
        }
        return new Sample(width, height, luma);
    }

    private static (double X, double Y) Search(Sample first, Sample second,
        double centerX, double centerY, double maskWidth, double maskHeight)
    {
        if (first.Width != second.Width || first.Height != second.Height)
            throw new InvalidOperationException("连续跟踪帧的画幅不一致。");
        int width = first.Width;
        int height = first.Height;
        int centerPixelX = (int)Math.Round(Math.Clamp(centerX, 0, 1) * (width - 1));
        int centerPixelY = (int)Math.Round(Math.Clamp(centerY, 0, 1) * (height - 1));
        int radiusX = Math.Clamp((int)Math.Round(maskWidth * width * .3), 8, Math.Max(8, width / 5));
        int radiusY = Math.Clamp((int)Math.Round(maskHeight * height * .3), 8, Math.Max(8, height / 5));
        int travelX = Math.Clamp(width / 14, 2, 28);
        int travelY = Math.Clamp(height / 14, 2, 28);
        int stride = Math.Max(2, Math.Min(radiusX, radiusY) / 12);
        long best = long.MaxValue;
        int bestX = 0, bestY = 0;
        for (int dy = -travelY; dy <= travelY; dy++)
        for (int dx = -travelX; dx <= travelX; dx++)
        {
            long difference = 0;
            int samples = 0;
            for (int py = -radiusY; py <= radiusY; py += stride)
            for (int px = -radiusX; px <= radiusX; px += stride)
            {
                int oldX = centerPixelX + px;
                int oldY = centerPixelY + py;
                int newX = oldX + dx;
                int newY = oldY + dy;
                if (oldX < 0 || oldX >= width || newX < 0 || newX >= width ||
                    oldY < 0 || oldY >= height || newY < 0 || newY >= height) continue;
                difference += Math.Abs(first.Luma[oldY * width + oldX] -
                    second.Luma[newY * width + newX]);
                samples++;
            }
            if (samples == 0) continue;
            // Penalize large jumps lightly; this stabilizes flat patches.
            long score = difference * 256 / samples + (Math.Abs(dx) + Math.Abs(dy)) * 2;
            if (score >= best) continue;
            best = score;
            bestX = dx;
            bestY = dy;
        }
        if (best == long.MaxValue)
            throw new InvalidOperationException("蒙版位于画面外，无法跟踪。");
        return (Math.Clamp((centerPixelX + bestX) / (double)(width - 1), 0, 1),
            Math.Clamp((centerPixelY + bestY) / (double)(height - 1), 0, 1));
    }
}
