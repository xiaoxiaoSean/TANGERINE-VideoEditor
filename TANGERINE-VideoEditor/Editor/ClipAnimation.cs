using System.Globalization;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Evaluates clip-relative keyframes for preview and emits equivalent FFmpeg
/// expressions for export. Property IDs are stable project-file identifiers.
/// The interpolation stored on the left keyframe controls its outgoing span.
/// </summary>
public static class ClipAnimation
{
    public static readonly IReadOnlyList<(string Id, string Name)> Properties =
    [
        ("PositionX", "位置 X"), ("PositionY", "位置 Y"),
        ("ScaleX", "水平缩放"), ("ScaleY", "垂直缩放"),
        ("RotationDegrees", "旋转角度"), ("Opacity", "不透明度"),
        ("Volume", "音量")
    ];

    public static double BaseValue(TimelineClip clip, string property) => property switch
    {
        "PositionX" => clip.Transform.PositionX,
        "PositionY" => clip.Transform.PositionY,
        "ScaleX" => clip.Transform.ScaleX,
        "ScaleY" => clip.Transform.ScaleY,
        "RotationDegrees" => clip.Transform.RotationDegrees,
        "Opacity" => clip.Opacity,
        "Volume" => clip.Volume,
        "Mask.CenterX" => clip.Mask?.CenterX ?? .5,
        "Mask.CenterY" => clip.Mask?.CenterY ?? .5,
        _ when property.StartsWith("Effect:", StringComparison.Ordinal) => EffectBaseValue(clip, property),
        _ => 0
    };

    private static double EffectBaseValue(TimelineClip clip, string property)
    {
        string[] parts = property.Split(':', 3);
        if (parts.Length != 3) return 0;
        ClipEffect? effect = clip.Effects.FirstOrDefault(item => item.Id == parts[1]);
        VisualEffectPipeline.Parameter? parameter = effect is null ? null :
            VisualEffectPipeline.ParametersFor(effect.PresetId).FirstOrDefault(item => item.Id == parts[2]);
        return effect is null || parameter is null ? 0 : VisualEffectPipeline.GetNumber(effect, parameter);
    }

    public static bool HasKeyframes(TimelineClip clip, string property) =>
        clip.Keyframes.Any(keyframe => keyframe.Property == property);

    public static double Evaluate(TimelineClip clip, string property, double timelineSeconds)
    {
        var keys = ValidKeys(clip, property);
        double time = Math.Clamp(timelineSeconds - clip.StartSeconds, 0, Math.Max(0, clip.DurationSeconds));
        return EvaluateAtLocal(keys, BaseValue(clip, property), time);
    }

    private static double EvaluateAtLocal(ClipKeyframe[] keys, double baseValue, double time)
    {
        if (keys.Length == 0) return baseValue;
        if (time <= keys[0].TimeSeconds) return keys[0].Value;
        for (int index = 1; index < keys.Length; index++)
        {
            ClipKeyframe right = keys[index];
            if (time >= right.TimeSeconds)
            {
                // The key at the exact boundary owns that frame, including
                // a Hold span. This matches the strict lt() export branch.
                if (time == right.TimeSeconds) return right.Value;
                continue;
            }
            ClipKeyframe left = keys[index - 1];
            double span = right.TimeSeconds - left.TimeSeconds;
            if (span <= 0) return right.Value;
            double fraction = Math.Clamp((time - left.TimeSeconds) / span, 0, 1);
            if (left.Interpolation == "Hold") return left.Value;
            if (left.Interpolation == "EaseInOut")
                fraction = fraction * fraction * (3 - 2 * fraction);
            else if (left.Interpolation == "Bezier")
                return CubicBezierValue(left, right, fraction);
            return left.Value + (right.Value - left.Value) * fraction;
        }
        return keys[^1].Value;
    }

    /// <summary>
    /// Re-bases animation to a retained clip-local interval. Boundary values
    /// are sampled before removing outside keys so a trimmed animation does
    /// not jump to the next remaining keyframe.
    /// </summary>
    public static void KeepRange(TimelineClip clip, double startSeconds, double durationSeconds)
    {
        if (clip.Keyframes.Count == 0) return;
        double start = startSeconds;
        double end = start + Math.Max(0, durationSeconds);
        var retained = new List<ClipKeyframe>();
        foreach (string property in clip.Keyframes.Select(key => key.Property).Distinct(StringComparer.Ordinal))
        {
            var keys = ValidKeys(clip, property);
            if (keys.Length == 0) continue;
            var propertyKeys = new List<ClipKeyframe>();
            // Some callers have already assigned the new duration before
            // trimming. Sample the original key curve at the requested local
            // boundary rather than clamping through Evaluate(clip,...).
            double leftValue = EvaluateAtLocal(keys, BaseValue(clip, property), start);
            double rightValue = EvaluateAtLocal(keys, BaseValue(clip, property), end);
            ClipKeyframe? leftOriginal = keys.LastOrDefault(key => key.TimeSeconds <= start);
            ClipKeyframe? exactStart = keys.FirstOrDefault(key =>
                Math.Abs(key.TimeSeconds - start) < .000001);
            propertyKeys.Add(exactStart is null ? new ClipKeyframe
            {
                Property = property, TimeSeconds = 0, Value = leftValue,
                Interpolation = leftOriginal?.Interpolation == "Bezier" ? "Linear" :
                    leftOriginal?.Interpolation ?? "Linear"
            } : CloneKey(exactStart, 0));
            propertyKeys.AddRange(keys.Where(key => key.TimeSeconds > start && key.TimeSeconds < end)
                .Select(key => CloneKey(key, key.TimeSeconds - start)));
            if (durationSeconds > 0 && keys.Any(key => key.TimeSeconds >= end))
            {
                ClipKeyframe? exactEnd = keys.FirstOrDefault(key =>
                    Math.Abs(key.TimeSeconds - end) < .000001);
                propertyKeys.Add(exactEnd is null ? new ClipKeyframe
                {
                    Property = property, TimeSeconds = durationSeconds,
                    Value = rightValue, Interpolation = "Linear"
                } : CloneKey(exactEnd, durationSeconds));
            }
            PreserveBezierSubspans(keys, propertyKeys, start);
            retained.AddRange(propertyKeys);
        }
        clip.Keyframes = retained;
    }

    private static ClipKeyframe CloneKey(ClipKeyframe key, double time) => new()
    {
        Id = key.Id, Property = key.Property, TimeSeconds = time,
        Value = key.Value, Interpolation = key.Interpolation,
        BezierOutTime = key.BezierOutTime, BezierOutValue = key.BezierOutValue,
        BezierInTime = key.BezierInTime, BezierInValue = key.BezierInValue,
        AdditionalData = key.AdditionalData
    };

    /// <summary>
    /// Reparameterize every cropped Bezier span with its original cubic's
    /// endpoint derivatives. This keeps the retained curve and tangent
    /// handles editable after a split or edge trim.
    /// </summary>
    private static void PreserveBezierSubspans(ClipKeyframe[] originals,
        List<ClipKeyframe> retained, double removedStart)
    {
        for (int index = 0; index + 1 < retained.Count; index++)
        {
            ClipKeyframe first = retained[index];
            ClipKeyframe last = retained[index + 1];
            double sourceStart = first.TimeSeconds + removedStart;
            double sourceEnd = last.TimeSeconds + removedStart;
            ClipKeyframe? originalFirst = originals.LastOrDefault(key =>
                key.TimeSeconds <= sourceStart + .000001);
            if (originalFirst is null || originalFirst.Interpolation != "Bezier") continue;
            ClipKeyframe? originalLast = originals.FirstOrDefault(key =>
                key.TimeSeconds > originalFirst.TimeSeconds + .000001);
            if (originalLast is null || sourceEnd > originalLast.TimeSeconds + .000001)
                continue;
            double oldSpan = originalLast.TimeSeconds - originalFirst.TimeSeconds;
            if (oldSpan <= .000001 || sourceEnd - sourceStart <= .000001) continue;
            // An unchanged span already has exact authored handles.
            if (Math.Abs(sourceStart - originalFirst.TimeSeconds) < .000001 &&
                Math.Abs(sourceEnd - originalLast.TimeSeconds) < .000001) continue;
            double delta = originalLast.Value - originalFirst.Value;
            double scale = Math.Abs(delta) < 1e-9 ? 1 : delta;
            double x1 = originalFirst.BezierOutTime;
            double x2 = Math.Max(x1, originalLast.BezierInTime);
            double y0 = originalFirst.Value;
            double y1 = y0 + scale * originalFirst.BezierOutValue;
            double y3 = originalLast.Value;
            double y2 = y3 + scale * (originalLast.BezierInValue - 1);
            double u0 = BezierParameter((sourceStart - originalFirst.TimeSeconds) / oldSpan,
                x1, x2);
            double u1 = BezierParameter((sourceEnd - originalFirst.TimeSeconds) / oldSpan,
                x1, x2);
            if (u1 <= u0 + 1e-9) continue;
            double x0 = Cubic(x1, x2, u0), x3 = Cubic(x1, x2, u1);
            double firstValue = CubicValue(y0, y1, y2, y3, u0);
            double lastValue = CubicValue(y0, y1, y2, y3, u1);
            double xControl1 = x0 + (u1 - u0) / 3 * CubicDerivative(0, x1, x2, 1, u0);
            double xControl2 = x3 - (u1 - u0) / 3 * CubicDerivative(0, x1, x2, 1, u1);
            double yControl1 = firstValue + (u1 - u0) / 3 * CubicDerivative(y0, y1, y2, y3, u0);
            double yControl2 = lastValue - (u1 - u0) / 3 * CubicDerivative(y0, y1, y2, y3, u1);
            double newSpan = x3 - x0;
            if (newSpan <= 1e-9) continue;
            first.Value = firstValue;
            last.Value = lastValue;
            double newDelta = lastValue - firstValue;
            double newScale = Math.Abs(newDelta) < 1e-9 ? 1 : newDelta;
            first.Interpolation = "Bezier";
            first.BezierOutTime = Math.Clamp((xControl1 - x0) / newSpan, 0, 1);
            first.BezierOutValue = Math.Clamp((yControl1 - firstValue) / newScale, -4, 4);
            last.BezierInTime = Math.Clamp((xControl2 - x0) / newSpan,
                first.BezierOutTime, 1);
            last.BezierInValue = Math.Clamp(1 + (yControl2 - lastValue) / newScale, -4, 4);
        }
    }

    private static double BezierParameter(double normalizedTime, double x1, double x2)
    {
        double low = 0, high = 1;
        for (int iteration = 0; iteration < 40; iteration++)
        {
            double middle = (low + high) / 2;
            if (Cubic(x1, x2, middle) < normalizedTime) low = middle;
            else high = middle;
        }
        return (low + high) / 2;
    }

    private static double CubicDerivative(double first, double second,
        double third, double last, double time)
    {
        double remaining = 1 - time;
        return 3 * remaining * remaining * (second - first) +
            6 * remaining * time * (third - second) +
            3 * time * time * (last - third);
    }

    /// <summary>
    /// The caller supplies either clip-local t/T or a global expression such
    /// as (t-start). Quoting is left to the FFmpeg filter argument builder.
    /// </summary>
    public static string FfmpegExpression(TimelineClip clip, string property, string timeExpression)
    {
        var keys = ValidKeys(clip, property);
        if (keys.Length == 0) return Number(BaseValue(clip, property));
        string result = Number(keys[^1].Value);
        for (int index = keys.Length - 2; index >= 0; index--)
        {
            ClipKeyframe left = keys[index];
            ClipKeyframe right = keys[index + 1];
            double span = right.TimeSeconds - left.TimeSeconds;
            string value;
            if (span <= 0) value = Number(right.Value);
            else if (left.Interpolation == "Hold") value = Number(left.Value);
            else
            {
                string fraction = $"(({timeExpression}-{Number(left.TimeSeconds)})/{Number(span)})";
                if (left.Interpolation == "EaseInOut")
                    fraction = $"({fraction}*{fraction}*(3-2*{fraction}))";
                if (left.Interpolation == "Bezier")
                    value = FfmpegBezierValue(left, right, fraction);
                else
                    value = $"({Number(left.Value)}+({Number(right.Value - left.Value)})*{fraction})";
            }
            result = $"if(lt({timeExpression},{Number(right.TimeSeconds)}),{value},{result})";
        }
        return $"if(lt({timeExpression},{Number(keys[0].TimeSeconds)}),{Number(keys[0].Value)},{result})";
    }

    private static ClipKeyframe[] ValidKeys(TimelineClip clip, string property) =>
        clip.Keyframes.Where(keyframe => keyframe.Property == property &&
            double.IsFinite(keyframe.TimeSeconds) && double.IsFinite(keyframe.Value))
            .OrderBy(keyframe => keyframe.TimeSeconds).ThenBy(keyframe => keyframe.Id).ToArray();

    /// <summary>
    /// Sample a monotone cubic into twenty-four straight spans. The preview
    /// uses the same table as the FFmpeg expression, so rendered frames and
    /// interactive values agree even for asymmetric user tangents.
    /// </summary>
    private static double CubicBezierValue(ClipKeyframe left, ClipKeyframe right, double time)
    {
        var samples = BezierSamples(left, right);
        for (int index = 1; index < samples.Length; index++)
        {
            if (time > samples[index].Time && index < samples.Length - 1) continue;
            double span = samples[index].Time - samples[index - 1].Time;
            double mix = span <= 0 ? 1 : Math.Clamp((time - samples[index - 1].Time) / span, 0, 1);
            double value = samples[index - 1].Value +
                (samples[index].Value - samples[index - 1].Value) * mix;
            return value;
        }
        return right.Value;
    }

    private static double Cubic(double first, double second, double time)
    {
        double remaining = 1 - time;
        return 3 * remaining * remaining * time * first +
            3 * remaining * time * time * second + time * time * time;
    }

    /// <summary>
    /// Emit a compact piecewise expression over the same sample table. The
    /// expression stays linear in sample count instead of growing with the
    /// number of bisection steps.
    /// </summary>
    private static string FfmpegBezierValue(ClipKeyframe left, ClipKeyframe right, string time)
    {
        var samples = BezierSamples(left, right);
        string result = Number(right.Value);
        for (int index = samples.Length - 1; index >= 1; index--)
        {
            double start = samples[index - 1].Time;
            double end = samples[index].Time;
            string actual;
            if (end - start <= 1e-9)
                actual = Number(samples[index].Value);
            else
            {
                double startValue = samples[index - 1].Value;
                double deltaValue = samples[index].Value - startValue;
                actual = $"({Number(startValue)}+({time}-{Number(start)})/" +
                    $"{Number(end - start)}*{Number(deltaValue)})";
            }
            result = $"if(lt({time},{Number(end)}),{actual},{result})";
        }
        return result;
    }

    private static (double Time, double Value)[] BezierSamples(ClipKeyframe left, ClipKeyframe right)
    {
        const int count = 24;
        double firstTime = Math.Clamp(left.BezierOutTime, 0, 1);
        double secondTime = Math.Clamp(right.BezierInTime, firstTime, 1);
        // A zero-height span still supports a hump when the user moves a
        // value handle. The neutral handles remain flat at 0 and 1.
        double delta = right.Value - left.Value;
        double scale = Math.Abs(delta) < 1e-9 ? 1 : delta;
        double outgoing = left.Value + scale * left.BezierOutValue;
        double incoming = right.Value + scale * (right.BezierInValue - 1);
        var samples = new (double Time, double Value)[count + 1];
        for (int index = 0; index <= count; index++)
        {
            double parameter = index / (double)count;
            samples[index] = (Cubic(firstTime, secondTime, parameter),
                CubicValue(left.Value, outgoing, incoming, right.Value, parameter));
        }
        return samples;
    }

    private static double CubicValue(double first, double firstControl,
        double secondControl, double last, double time)
    {
        double remaining = 1 - time;
        return remaining * remaining * remaining * first +
            3 * remaining * remaining * time * firstControl +
            3 * remaining * time * time * secondControl +
            time * time * time * last;
    }

    private static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
}
