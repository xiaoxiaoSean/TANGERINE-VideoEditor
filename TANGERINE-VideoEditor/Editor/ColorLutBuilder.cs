using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Creates small local 3D LUTs for selective HSL correction and strength
/// blending. Generated files live in the system temporary directory; only
/// the original LUT path and numeric controls are stored in the project.
/// </summary>
internal static class ColorLutBuilder
{
    private const int SelectiveSize = 33;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string SelectiveHsl(double hueShift, double saturation, double targetHue, double range)
    {
        string identity = string.Join("|", "hsl-v1", Format(hueShift), Format(saturation),
            Format(targetHue), Format(range));
        return Cached(identity, () =>
        {
            var lines = new StringBuilder("TITLE \"Tangerine local HSL\"\nLUT_3D_SIZE 33\n");
            for (int blue = 0; blue < SelectiveSize; blue++)
            for (int green = 0; green < SelectiveSize; green++)
            for (int red = 0; red < SelectiveSize; red++)
            {
                double r = red / (double)(SelectiveSize - 1);
                double g = green / (double)(SelectiveSize - 1);
                double b = blue / (double)(SelectiveSize - 1);
                (double currentHue, double currentSat, double lightness) = RgbToHsl(r, g, b);
                double distance = Math.Abs(currentHue - targetHue % 360);
                distance = Math.Min(distance, 360 - distance);
                // Desaturated pixels carry no meaningful hue. Feather the
                // selection edge to avoid a hard band around the chosen hue.
                double proximity = 1 - Math.Clamp(distance / Math.Max(1, range), 0, 1);
                double weight = proximity * proximity * (3 - 2 * proximity) *
                    Math.Clamp(currentSat * 6, 0, 1);
                (double changedR, double changedG, double changedB) = HslToRgb(
                    currentHue + hueShift, Math.Clamp(currentSat * saturation, 0, 1), lightness);
                AppendTriplet(lines, r + (changedR - r) * weight,
                    g + (changedG - g) * weight, b + (changedB - b) * weight);
            }
            return lines.ToString();
        });
    }

    public static string WithStrength(string originalPath, double strength)
    {
        string fullPath = Path.GetFullPath(originalPath);
        var source = new FileInfo(fullPath);
        string identity = string.Join("|", "mix-v1", fullPath, source.Length,
            source.LastWriteTimeUtc.Ticks, Format(strength));
        return Cached(identity, () =>
        {
            string[] lines = File.ReadAllLines(fullPath);
            int size = 0;
            int count = 0;
            var output = new StringBuilder();
            foreach (string sourceLine in lines)
            {
                string line = sourceLine.Split('#', 2)[0].Trim();
                if (line.Length == 0) { output.AppendLine(sourceLine); continue; }
                string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields[0].Equals("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase))
                {
                    if (fields.Length != 2 || !int.TryParse(fields[1], NumberStyles.Integer, Invariant, out size) ||
                        size < 2 || size > 65)
                        throw InvalidLut("LUT_3D_SIZE 必须在 2 到 65 之间。");
                    output.AppendLine(sourceLine);
                }
                else if (fields[0].Equals("LUT_1D_SIZE", StringComparison.OrdinalIgnoreCase))
                    throw InvalidLut("目前仅支持 3D .cube 文件。");
                else if (fields[0].Equals("DOMAIN_MIN", StringComparison.OrdinalIgnoreCase) ||
                    fields[0].Equals("DOMAIN_MAX", StringComparison.OrdinalIgnoreCase))
                    throw InvalidLut("强度混合仅支持默认 0 到 1 输入范围的 3D LUT。");
                else if (fields[0].Equals("TITLE", StringComparison.OrdinalIgnoreCase))
                    output.AppendLine(sourceLine);
                else if (fields.Length == 3 && size > 0)
                {
                    double[] mapped = ParseTriplet(fields);
                    int red = count % size;
                    int green = count / size % size;
                    int blue = count / (size * size);
                    if (blue >= size) throw InvalidLut("LUT 数据行超过声明的尺寸。");
                    double[] grid =
                    [
                        red / (double)(size - 1),
                        green / (double)(size - 1),
                        blue / (double)(size - 1)
                    ];
                    AppendTriplet(output,
                        grid[0] + (mapped[0] - grid[0]) * strength,
                        grid[1] + (mapped[1] - grid[1]) * strength,
                        grid[2] + (mapped[2] - grid[2]) * strength);
                    count++;
                }
                else throw InvalidLut($"不支持的 LUT 文件行：{fields[0]}");
            }
            if (size == 0 || count != size * size * size)
                throw InvalidLut("LUT 数据数量与声明的尺寸不符。");
            return output.ToString();
        });
    }

    private static string Cached(string identity, Func<string> create)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        string directory = Path.Combine(Path.GetTempPath(), "TangerineVideoEditor", "ColorLuts");
        string target = Path.Combine(directory, $"{Convert.ToHexString(digest)}.cube");
        if (File.Exists(target)) return target;
        try
        {
            string content = create();
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, content, new UTF8Encoding(false));
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
            }
            return target;
        }
        catch (ExportException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ExportException(ExportStageCode.Prepare, "无法准备本地调色 LUT。", exception);
        }
    }

    private static double[] ParseTriplet(string[] fields)
    {
        if (fields.Length != 3 || fields.Any(field =>
            !double.TryParse(field, NumberStyles.Float, Invariant, out double value) || !double.IsFinite(value)))
            throw InvalidLut("LUT 颜色值不是有效的三个数字。");
        return fields.Select(field => double.Parse(field, NumberStyles.Float, Invariant)).ToArray();
    }

    private static ExportException InvalidLut(string message) =>
        new(ExportStageCode.Validate, message);

    private static void AppendTriplet(StringBuilder result, double r, double g, double b) =>
        result.Append(Format(r)).Append(' ').Append(Format(g)).Append(' ')
            .Append(Format(b)).Append('\n');

    private static string Format(double value) => value.ToString("0.########", Invariant);

    private static (double Hue, double Saturation, double Lightness) RgbToHsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double lightness = (max + min) / 2;
        if (delta < .0000001) return (0, 0, lightness);
        double saturation = delta / Math.Max(.0000001, 1 - Math.Abs(2 * lightness - 1));
        double hue = max == r ? ((g - b) / delta) % 6 :
            max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
        hue = (hue * 60 + 360) % 360;
        return (hue, saturation, lightness);
    }

    private static (double Red, double Green, double Blue) HslToRgb(double hue, double saturation, double lightness)
    {
        hue = (hue % 360 + 360) % 360;
        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double scaled = hue / 60;
        double second = chroma * (1 - Math.Abs(scaled % 2 - 1));
        (double r, double g, double b) = scaled switch
        {
            < 1 => (chroma, second, 0.0),
            < 2 => (second, chroma, 0.0),
            < 3 => (0.0, chroma, second),
            < 4 => (0.0, second, chroma),
            < 5 => (second, 0.0, chroma),
            _ => (chroma, 0.0, second)
        };
        double offset = lightness - chroma / 2;
        return (r + offset, g + offset, b + offset);
    }
}
