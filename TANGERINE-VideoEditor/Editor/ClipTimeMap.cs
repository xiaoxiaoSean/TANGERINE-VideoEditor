using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Integrates clip-relative speed segments into source time. The stored base
/// speed remains the first segment's default and preserves old projects.
/// </summary>
public static class ClipTimeMap
{
    public static double SourceOffset(TimelineClip clip, double localSeconds)
    {
        double end = Math.Clamp(localSeconds, 0, Math.Max(0, clip.DurationSeconds));
        if (clip.TimeRemapPoints.Count == 0) return end * clip.Speed;
        var knots = clip.TimeRemapPoints
            .Where(point => double.IsFinite(point.TimeSeconds) && double.IsFinite(point.Speed) &&
                point.Speed > 0 && point.TimeSeconds >= 0 &&
                point.TimeSeconds < clip.DurationSeconds)
            .OrderBy(point => point.TimeSeconds).ToArray();
        double previous = 0;
        double speed = clip.Speed;
        double offset = 0;
        foreach (ClipTimeRemapPoint knot in knots)
        {
            double boundary = Math.Clamp(knot.TimeSeconds, 0, clip.DurationSeconds);
            if (boundary > end) break;
            if (boundary > previous) offset += (boundary - previous) * speed;
            previous = Math.Max(previous, boundary);
            speed = knot.Speed;
        }
        if (end > previous) offset += (end - previous) * speed;
        return offset;
    }

    public static double SourceTime(TimelineClip clip, double timelineSeconds)
    {
        double local = Math.Clamp(timelineSeconds - clip.StartSeconds, 0, clip.DurationSeconds);
        double source = clip.SourceInSeconds + (clip.IsReversed
            ? SourceOffset(clip, clip.DurationSeconds) - SourceOffset(clip, local)
            : SourceOffset(clip, local));
        // A reversed clip's first timeline frame comes from the last frame
        // *inside* its source interval. Seeking exactly to the out boundary
        // can decode the next frame (or EOF) in a source preview.
        return clip.IsReversed && local < clip.DurationSeconds
            ? Math.Max(clip.SourceInSeconds, source - .000001)
            : source;
    }

    /// <summary>
    /// Finds the clip-local time that shows a given source time, or null when
    /// the clip never shows it. This is the inverse of <see cref="SourceTime"/>:
    /// speed knots make a closed form impractical, so it bisects. The mapping
    /// is monotonic (rising, or falling when reversed), which bisection needs.
    /// </summary>
    public static double? LocalTimeForSource(TimelineClip clip, double sourceSeconds)
    {
        if (!double.IsFinite(sourceSeconds) || clip.DurationSeconds <= 0) return null;
        double atStart = SourceTime(clip, clip.StartSeconds);
        double atEnd = SourceTime(clip, clip.StartSeconds + clip.DurationSeconds);
        double low = Math.Min(atStart, atEnd);
        double high = Math.Max(atStart, atEnd);
        if (sourceSeconds < low - .000001 || sourceSeconds > high + .000001) return null;
        bool rising = atEnd >= atStart;
        double lower = 0, upper = clip.DurationSeconds;
        for (int step = 0; step < 48; step++)
        {
            double middle = (lower + upper) / 2;
            double value = SourceTime(clip, clip.StartSeconds + middle);
            if (rising == (value < sourceSeconds)) lower = middle;
            else upper = middle;
        }
        return (lower + upper) / 2;
    }

    /// <summary>
    /// Retains a clip-local interval while preserving the source frame at its
    /// new left edge. All speed knots are translated into the new clock.
    /// </summary>
    public static void KeepRange(TimelineClip clip, double startSeconds, double durationSeconds)
    {
        if (clip.TimeRemapPoints.Count == 0) return;
        double start = startSeconds;
        double duration = Math.Max(0, durationSeconds);
        double end = start + duration;
        double oldTotal = SourceOffset(clip, clip.DurationSeconds);
        // Before local zero the unmodified base speed owns the source handle.
        // A knot at zero takes effect only when the old clip actually begins;
        // a negative boundary edit must preserve that transition at -start.
        double sourceStart = start < 0 ? start * clip.Speed : SourceOffset(clip, start);
        double sourceEnd = end < 0 ? end * clip.Speed : end > clip.DurationSeconds
            ? oldTotal + (end - clip.DurationSeconds) * SpeedAt(clip, clip.DurationSeconds)
            : SourceOffset(clip, end);
        double speedAtStart = start < 0 ? clip.Speed : SpeedAt(clip, start);
        var remaining = clip.TimeRemapPoints
            // An original knot exactly at the retained right boundary does
            // not belong to the resulting clip. KeepRange may be called
            // before its DurationSeconds property is updated.
            .Where(point => point.TimeSeconds >= 0 && point.TimeSeconds > start &&
                point.TimeSeconds < Math.Min(end, clip.DurationSeconds))
            .Select(point => new ClipTimeRemapPoint
            { TimeSeconds = point.TimeSeconds - start, Speed = point.Speed }).ToList();
        remaining.Insert(0, new ClipTimeRemapPoint { TimeSeconds = 0, Speed = speedAtStart });
        clip.TimeRemapPoints = remaining;
        clip.SourceInSeconds += clip.IsReversed ? oldTotal - sourceEnd : sourceStart;
    }

    public static double SpeedAt(TimelineClip clip, double localSeconds)
    {
        double speed = clip.Speed;
        foreach (ClipTimeRemapPoint point in clip.TimeRemapPoints
            .Where(point => double.IsFinite(point.TimeSeconds) && double.IsFinite(point.Speed) &&
                point.Speed > 0 && point.TimeSeconds >= 0 &&
                point.TimeSeconds < clip.DurationSeconds)
            .OrderBy(point => point.TimeSeconds))
        {
            if (point.TimeSeconds > localSeconds) break;
            speed = point.Speed;
        }
        return speed;
    }
}
