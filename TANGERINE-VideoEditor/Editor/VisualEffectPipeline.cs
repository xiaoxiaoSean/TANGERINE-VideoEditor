using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Defines editable local effects and translates their persisted values into
/// FFmpeg filters. The same filter chain is used for still preview and export.
/// Parameters are always bounded before they reach a filter graph.
/// </summary>
public static class VisualEffectPipeline
{
    public sealed record Parameter(string Id, string Name, double Default, double Minimum, double Maximum,
        bool Animatable = false);

    public static IReadOnlyList<Parameter> ParametersFor(string presetId) => presetId switch
    {
        "effect.brightness" => [new("amount", "亮度", .12, -1, 1, true)],
        "effect.contrast" => [new("amount", "对比度", 1.3, 0, 3, true)],
        "effect.saturation" => [new("amount", "饱和度", 1.4, 0, 3, true)],
        "effect.temperature" => [new("amount", "色温", .15, -.5, .5)],
        "effect.tint" => [new("amount", "色调", .1, -.5, .5)],
        "effect.exposure" => [new("amount", "曝光级数", .25, -3, 3, true)],
        "effect.highlights" => [new("amount", "高光", -.2, -.9, .9, true)],
        "effect.shadows" => [new("amount", "阴影", .25, -.9, .9, true)],
        "effect.vignette" => [new("amount", "暗角", .25, 0, 1)],
        "effect.glow" =>
            [new("amount", "光晕强度", 2, 0, 20), new("radius", "光效范围", 8, 0, 40)],
        "effect.blur" or "effect.motion-blur" =>
            [new("amount", "强度", presetId == "effect.blur" ? 5 : 2, 0, 20)],
        "effect.sharpen" => [new("amount", "锐化", 1.2, 0, 3)],
        "effect.grain" or "effect.noise" =>
            [new("amount", "颗粒", presetId == "effect.grain" ? 12 : 25, 0, 100)],
        "effect.posterize" => [new("levels", "色阶数量", 16, 2, 128)],
        "effect.mosaic" => [new("block", "块大小", 16, 2, 80)],
        "effect.chromatic" or "effect.glitch" => [new("amount", "偏移像素", 5, 0, 40)],
        "effect.key" => [new("similarity", "相似度", .12, .01, 1), new("feather", "边缘柔化", .04, 0, 1)],
        "effect.lut" => [new("strength", "LUT 强度", 1, 0, 1)],
        "effect.vintage" => [new("amount", "复古强度", .75, 0, 1, true), new("grain", "颗粒范围", 18, 0, 100)],
        "effect.cinematic" => [new("amount", "电影对比度", 1.35, 0, 3, true)],
        "effect.dream" or "effect.soft-focus" => [new("amount", "柔焦强度", 3, 0, 20)],
        "effect.color-pop" => [new("amount", "色彩强度", 1.6, 0, 3, true)],
        "effect.teal-orange" => [new("amount", "青橙强度", .18, 0, .5, true)],
        "effect.hsl" => [new("hue", "色相角度", 0, -180, 180), new("saturation", "局部饱和度", 1, 0, 3),
            new("target", "目标色相角度", 0, 0, 360), new("range", "色相范围", 45, 1, 180)],
        "effect.curves" => [new("shadows", "暗部", 0, -.5, .5), new("midtones", "中间调", 0, -.5, .5),
            new("highlights", "亮部", 0, -.5, .5)],
        "effect.wheels" => [new("shadowRed", "阴影红", 0, -.5, .5), new("shadowBlue", "阴影蓝", 0, -.5, .5),
            new("highlightRed", "高光红", 0, -.5, .5), new("highlightBlue", "高光蓝", 0, -.5, .5)],
        _ => []
    };

    /// <summary>Maps supported project color spaces to explicit FFmpeg metadata.</summary>
    public static string BuildInputColorConversion(MediaAsset? asset, ProjectSequence sequence)
    {
        string input = asset?.InputColorSpace ?? "Auto";
        string working = sequence.WorkingColorSpace;
        if (input == "Auto" || input.Equals(working, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        string? inputName = ColorSpaceName(input);
        string? outputName = ColorSpaceName(working);
        if (inputName is null || outputName is null) return string.Empty;
        return $",colorspace=iall={inputName}:all={outputName}:format=yuv420p";
    }

    public static string BuildOutputColorConversion(ProjectSequence sequence)
    {
        if (sequence.WorkingColorSpace.Equals(sequence.OutputColorSpace, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        string? inputName = ColorSpaceName(sequence.WorkingColorSpace);
        string? outputName = ColorSpaceName(sequence.OutputColorSpace);
        return inputName is null || outputName is null ? string.Empty :
            $",colorspace=iall={inputName}:all={outputName}:format=yuv420p";
    }

    private static string? ColorSpaceName(string value) => value switch
    {
        "Rec709" or "BT709" => "bt709",
        "Rec601" or "BT601" => "bt601-6-625",
        "Rec2020" or "BT2020" => "bt2020",
        _ => null
    };

    public static double GetNumber(ClipEffect effect, Parameter parameter)
    {
        if (effect.Parameters.TryGetValue(parameter.Id, out JsonElement element) &&
            element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double value) && double.IsFinite(value))
            return Math.Clamp(value, parameter.Minimum, parameter.Maximum);
        return parameter.Default;
    }

    public static void SetNumber(ClipEffect effect, Parameter parameter, double value) =>
        effect.Parameters[parameter.Id] = JsonSerializer.SerializeToElement(Math.Clamp(value, parameter.Minimum, parameter.Maximum));

    public static string? GetString(ClipEffect effect, string key) =>
        effect.Parameters.TryGetValue(key, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() : null;

    public static void SetString(ClipEffect effect, string key, string value) =>
        effect.Parameters[key] = JsonSerializer.SerializeToElement(value);

    public static string ParameterProperty(ClipEffect effect, Parameter parameter) =>
        $"Effect:{effect.Id}:{parameter.Id}";

    /// <summary>
    /// Produces a comma-prefixed chain so the caller can append it after a
    /// trim/scale filter. Every supported effect has a concrete local result.
    /// </summary>
    public static string BuildFfmpegFilters(TimelineClip clip, ProjectSequence sequence,
        double? previewLocalSeconds = null)
    {
        var result = new StringBuilder();
        foreach (ClipEffect effect in clip.Effects.Where(item => item.IsEnabled))
        {
            string id = effect.PresetId;

            // Get intensity multiplier (0-200% -> 0.0-2.0)
            double intensity = 1.0;
            if (effect.Parameters.TryGetValue("intensity", out var intensityValue) &&
                intensityValue.ValueKind == JsonValueKind.Number &&
                intensityValue.TryGetDouble(out double storedIntensity) && double.IsFinite(storedIntensity))
            {
                intensity = Math.Clamp(storedIntensity / 100.0, 0, 2);
            }

            // Skip if intensity is zero
            if (intensity <= 0.001) continue;

            double p(string name)
            {
                Parameter parameter = ParametersFor(id).First(item => item.Id == name);
                return Math.Clamp(GetNumber(effect, parameter) * intensity, parameter.Minimum, parameter.Maximum);
            }
            string a(string name)
            {
                Parameter parameter = ParametersFor(id).First(item => item.Id == name);
                string property = ParameterProperty(effect, parameter);
                if (!parameter.Animatable || !ClipAnimation.HasKeyframes(clip, property))
                    return Number(p(name));
                if (previewLocalSeconds is double local)
                    return Number(Math.Clamp(ClipAnimation.Evaluate(clip, property, clip.StartSeconds + local) * intensity,
                        parameter.Minimum, parameter.Maximum));
                string expression = ClipAnimation.FfmpegExpression(clip, property, "t");
                return $"clip(({expression})*{Number(intensity)},{Number(parameter.Minimum)},{Number(parameter.Maximum)})";
            }
            string filter = id switch
            {
                "effect.brightness" => $"eq=brightness='{a("amount")}':eval=frame",
                "effect.contrast" => $"eq=contrast='{a("amount")}':eval=frame",
                "effect.saturation" => $"eq=saturation='{a("amount")}':eval=frame",
                "effect.temperature" => $"colorbalance=rs={Number(p("amount"))}:bs={Number(-p("amount"))}",
                "effect.tint" => $"colorbalance=gs={Number(-p("amount"))}:rs={Number(p("amount") / 2)}:bs={Number(p("amount") / 2)}",
                "effect.exposure" => $"eq=brightness='clip(({a("amount")})/6,-1,1)':eval=frame",
                "effect.highlights" => $"eq=gamma='1+({a("amount")})':eval=frame",
                "effect.shadows" => $"eq=gamma='1+({a("amount")})':eval=frame",
                "effect.vignette" => $"vignette=angle={Number((1 - p("amount") * .8) * Math.PI / 2)}",
                "effect.glow" => $"gblur=sigma={Number(p("radius"))},eq=brightness='{Number(.02 * p("amount"))}':eval=frame",
                "effect.blur" => $"gblur=sigma={Number(p("amount"))}",
                "effect.motion-blur" => $"tmix=frames={Math.Clamp((int)Math.Round(p("amount")), 1, 8)}",
                "effect.sharpen" => $"unsharp=5:5:{Number(p("amount"))}",
                "effect.grain" or "effect.noise" => $"noise=alls={Number(p("amount"))}:allf=t",
                "effect.vintage" => $"{BuildSepia(p("amount"))},noise=alls={Number(p("grain"))}:allf=t",
                "effect.cinematic" => $"eq=contrast='{a("amount")}':eval=frame,eq=saturation='0.86':eval=frame",
                "effect.dream" or "effect.soft-focus" => $"gblur=sigma={Number(p("amount"))},eq=brightness='0.06':eval=frame",
                "effect.color-pop" => $"eq=saturation='{a("amount")}':eval=frame,eq=contrast='1.08':eval=frame",
                "effect.teal-orange" => $"colorbalance=gs={Number(-p("amount"))}:bs={Number(p("amount"))}:rs={Number(p("amount"))}:rh={Number(p("amount"))}:gh={Number(-p("amount"))}",
                "effect.black-white" => intensity >= 0.5 ? "hue=s=0" : $"hue=s={Number(1.0 - intensity * 2.0)}",
                "effect.sepia" => BuildSepia(intensity),
                "effect.invert" => intensity >= 0.5 ? "negate" : "",
                "effect.posterize" => BuildPosterize(p("levels")),
                "effect.chromatic" => $"chromashift=cbh={Number(p("amount"))}:crh={Number(-p("amount"))}",
                "effect.glitch" => $"rgbashift=rh={Number(p("amount"))}:bh={Number(-p("amount"))}",
                "effect.mosaic" => BuildMosaic(p("block")),
                "effect.mirror" => intensity >= 0.5 ? "hflip" : "",
                "effect.kaleidoscope" => intensity >= 0.5 ? "vflip,hflip" : "",
                "effect.key" => $"chromakey=color={NormalizeColor(GetString(effect, "color") ?? "#00FF00")}:similarity={Number(p("similarity"))}:blend={Number(p("feather"))}",
                "effect.curves" => BuildCurves(p("shadows"), p("midtones"), p("highlights")),
                "effect.wheels" => $"colorbalance=rs={Number(p("shadowRed"))}:bs={Number(p("shadowBlue"))}:rh={Number(p("highlightRed"))}:bh={Number(p("highlightBlue"))}",
                "effect.hsl" => BuildHsl(p("hue"), p("saturation"), p("target"), p("range")),
                "effect.lut" => BuildLut(effect, intensity),
                _ => string.Empty
            };
            if (filter.Length > 0) result.Append(',').Append(filter);
        }
        return result.ToString();
    }

    private static string BuildCurves(double shadows, double midtones, double highlights)
    {
        double low = Math.Clamp(.25 + shadows, .01, .45);
        double middle = Math.Clamp(.5 + midtones, low + .01, .74);
        double high = Math.Clamp(.75 + highlights, middle + .01, .99);
        return $"curves=all='0/0 0.25/{Number(low)} 0.5/{Number(middle)} 0.75/{Number(high)} 1/1'";
    }

    private static string BuildPosterize(double levels)
    {
        int count = Math.Clamp((int)Math.Round(levels), 2, 128);
        double step = 255.0 / (count - 1);
        string expression = $"round(val/{Number(step)})*{Number(step)}";
        return $"lutrgb=r='{expression}':g='{expression}':b='{expression}'";
    }

    private static string BuildMosaic(double block)
    {
        int pixels = Math.Clamp((int)Math.Round(block), 2, 80);
        // The second scale multiplies the reduced dimensions. Division
        // round-off can leave a one-pixel edge on unusual source sizes;
        // downstream overlay centers the resulting layer.
        return $"scale=w='max(1,iw/{pixels})':h='max(1,ih/{pixels})':flags=neighbor," +
            $"scale=w=iw*{pixels}:h=ih*{pixels}:flags=neighbor";
    }

    private static string BuildHsl(double hue, double saturation, double target, double range)
    {
        if (Math.Abs(hue) < .000001 && Math.Abs(saturation - 1) < .000001)
            return string.Empty;
        // A generated 3D LUT adjusts only the selected hue interval. It keeps
        // colors outside that interval intact in both preview and export.
        return LutFilter(ColorLutBuilder.SelectiveHsl(hue, saturation, target, range));
    }

    private static string BuildSepia(double intensity)
    {
        intensity = Math.Clamp(intensity, 0, 1);
        // Name the RGB coefficients explicitly: positional options also
        // contain alpha entries, so nine positional numbers corrupt channels.
        // Keep the identity matrix at zero so a following noise filter still
        // has a valid input chain, and format numbers independently of locale.
        double r = 0.393 * intensity + (1 - intensity);
        double g = 0.686 * intensity + (1 - intensity);
        double b = 0.131 * intensity + (1 - intensity);
        return $"colorchannelmixer=rr={Number(r)}:rg={Number(.769 * intensity)}:rb={Number(.189 * intensity)}:" +
               $"gr={Number(.349 * intensity)}:gg={Number(g)}:gb={Number(.168 * intensity)}:" +
               $"br={Number(.272 * intensity)}:bg={Number(.534 * intensity)}:bb={Number(b)}";
    }

    private static string BuildLut(ClipEffect effect, double intensity)
    {
        string? path = GetString(effect, "path");
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (!File.Exists(path))
            throw new ExportException(ExportStageCode.Validate, $"找不到 LUT 文件：{path}");

        // Get base strength from effect parameter
        double baseStrength = GetNumber(effect, ParametersFor("effect.lut")[0]);
        // Apply intensity multiplier
        double finalStrength = Math.Clamp(baseStrength * intensity, 0, 1);

        if (finalStrength <= .000001) return string.Empty;
        string selectedPath = finalStrength >= .999999 ? path : ColorLutBuilder.WithStrength(path, finalStrength);
        return LutFilter(selectedPath);
    }

    private static string LutFilter(string path)
    {
        // FFmpeg parses the path inside filter syntax even when the complete
        // graph is passed through a separate script file.
        string escaped = Path.GetFullPath(path).Replace("\\", "/", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
        return $"lut3d=file='{escaped}'";
    }

    /// <summary>Converts normalized rectangle, ellipse, or pen path to clip alpha.</summary>
    public static string BuildMaskAlpha(ClipMask mask, TimelineClip? clip = null,
        double? previewLocalSeconds = null, double framesPerSecond = 30)
    {
        string x = MaskValue(mask.CenterX, "Mask.CenterX", 0, 1);
        string y = MaskValue(mask.CenterY, "Mask.CenterY", 0, 1);
        if (!double.IsFinite(mask.Width) || mask.Width is <= 0 or > 1 ||
            !double.IsFinite(mask.Height) || mask.Height is <= 0 or > 1 ||
            !double.IsFinite(mask.Feather) || mask.Feather is < 0 or > .5)
            throw new ExportException(ExportStageCode.Validate, "蒙版尺寸或羽化范围无效。");
        double width = Math.Max(.001, mask.Width);
        double height = Math.Max(.001, mask.Height);
        double feather = mask.Feather;
        string value;
        if (mask.Shape.Equals("Path", StringComparison.OrdinalIgnoreCase))
        {
            value = BuildPathMask(mask, x, y, feather);
        }
        else
        {
            string distance = mask.Shape.Equals("Ellipse", StringComparison.OrdinalIgnoreCase)
                ? $"sqrt(pow((X/W-({x}))/({Number(width / 2)}),2)+pow((Y/H-({y}))/({Number(height / 2)}),2))"
                : $"max(abs(X/W-({x}))/({Number(width / 2)}),abs(Y/H-({y}))/({Number(height / 2)}))";
            value = feather <= .00001
                ? $"lte({distance},1)"
                : $"clip((1+{Number(feather)}-{distance})/{Number(feather * 2)},0,1)";
        }
        if (mask.Invert) value = $"(1-({value}))";
        return $",format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*({EscapeExpression(value)})'";

        string MaskValue(double baseValue, string property, double minimum, double maximum)
        {
            if (!double.IsFinite(baseValue))
                throw new ExportException(ExportStageCode.Validate, "蒙版中心坐标无效。");
            if (clip is null || !ClipAnimation.HasKeyframes(clip, property))
                return Number(Math.Clamp(baseValue, minimum, maximum));
            if (previewLocalSeconds is double local)
                return Number(Math.Clamp(ClipAnimation.Evaluate(clip, property, clip.StartSeconds + local), minimum, maximum));
            // geq's documented N expression denotes the filter input frame
            // index. Convert it to clip seconds using the sequence timebase;
            // runtime compatibility still requires verification with the
            // packaged FFmpeg build.
            string localTime = $"(N/{Number(Math.Max(1, framesPerSecond))})";
            return $"clip({ClipAnimation.FfmpegExpression(clip, property, localTime)},{Number(minimum)},{Number(maximum)})";
        }
    }

    /// <summary>
    /// Builds a closed even-odd polygon test for the pen mask. The query point
    /// is offset by animated mask-center keyframes, so manually tracked paths
    /// retain their edited vertex geometry in preview and export alike.
    /// Each non-horizontal edge contributes one ray crossing; optional
    /// feathering uses the nearest distance to a finite edge segment.
    /// </summary>
    private static string BuildPathMask(ClipMask mask, string centerX, string centerY, double feather)
    {
        if (mask.Points is null || mask.Points.Count is < 3 or > 32 ||
            mask.Points.Any(point => point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                point.X is < 0 or > 1 || point.Y is < 0 or > 1))
            throw new ExportException(ExportStageCode.Validate, "自由路径蒙版需要 3 到 32 个画幅内顶点。");
        if (mask.Points.Count == 4 && mask.Points[0].X == .25 && mask.Points[0].Y == .25 &&
            mask.Points[1].X == .75 && mask.Points[1].Y == .25 &&
            mask.Points[2].X == .75 && mask.Points[2].Y == .75 &&
            mask.Points[3].X == .25 && mask.Points[3].Y == .75)
        {
            // Common initial path: use a compact expression until the user
            // edits its vertices. This keeps preview interaction responsive.
            string cx = $"(.5+(({centerX})-{Number(mask.CenterX)}))";
            string cy = $"(.5+(({centerY})-{Number(mask.CenterY)}))";
            string distance = $"max(abs(X/W-{cx})/.25,abs(Y/H-{cy})/.25)";
            return feather <= .00001 ? $"lte({distance},1)" :
                $"clip((1+{Number(feather)}-{distance})/{Number(2 * feather)},0,1)";
        }
        string px = $"(X/W-(({centerX})-{Number(mask.CenterX)}))";
        string py = $"(Y/H-(({centerY})-{Number(mask.CenterY)}))";
        var crossings = new List<string>();
        var distances = new List<string>();
        for (int index = 0; index < mask.Points.Count; index++)
        {
            MaskPoint first = mask.Points[index];
            MaskPoint second = mask.Points[(index + 1) % mask.Points.Count];
            double dx = second.X - first.X;
            double dy = second.Y - first.Y;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared < 1e-12) continue;
            string x1 = Number(first.X);
            string y1 = Number(first.Y);
            string dxText = Number(dx);
            string dyText = Number(dy);
            if (Math.Abs(dy) > 1e-9)
                crossings.Add($"(gte({py},{Number(Math.Min(first.Y, second.Y))})*" +
                    $"lt({py},{Number(Math.Max(first.Y, second.Y))})*" +
                    $"lt({px},{x1}+{dxText}*({py}-{y1})/{dyText}))");
            if (feather > .00001)
            {
                string projection = $"clip((({px}-{x1})*{dxText}+({py}-{y1})*{dyText})/{Number(lengthSquared)},0,1)";
                distances.Add($"hypot({px}-({x1}+{dxText}*{projection})," +
                    $"{py}-({y1}+{dyText}*{projection}))");
            }
        }
        if (crossings.Count == 0)
            throw new ExportException(ExportStageCode.Validate, "自由路径蒙版的顶点不能共线。");
        string inside = $"mod(({string.Join("+", crossings)}),2)";
        if (feather <= .00001) return inside;
        string nearest = distances.Aggregate((left, right) => $"min({left},{right})");
        return $"clip(0.5+((2*{inside})-1)*{nearest}/{Number(feather)},0,1)";
    }

    public static string BlendModeToFfmpeg(string? blendMode) => blendMode switch
    {
        "Multiply" => "multiply", "Screen" => "screen", "Overlay" => "overlay",
        "Add" => "addition", "Difference" => "difference", "Darken" => "darken",
        "Lighten" => "lighten", "SoftLight" => "softlight", _ => "normal"
    };

    public static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Protects expression commas from FFmpeg's filtergraph parser.</summary>
    public static string EscapeExpression(string expression) =>
        expression.Replace(",", "\\,", StringComparison.Ordinal);

    private static string NormalizeColor(string value)
    {
        string hex = value.Trim().TrimStart('#');
        return hex.Length == 6 && hex.All(Uri.IsHexDigit) ? $"0x{hex}" : "0x00FF00";
    }
}
