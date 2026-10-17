using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

public sealed record MusicRemixSegment(double StartSeconds, double SourceInSeconds,
    double DurationSeconds, double FadeInSeconds, double FadeOutSeconds);

/// <summary>
/// Builds a deterministic offline arrangement from musical beat boundaries.
/// Repeated sections use a short linear crossfade on overlapping clips,
/// preserving the original pitch because no time stretching is performed.
/// </summary>
public static class MusicRemixPlanner
{
    private const int MaximumSegments = 500;

    public static IReadOnlyList<MusicRemixSegment> Plan(double timelineStart, double sourceIn,
        double sourceDuration, double targetDuration, double bpm)
    {
        if (!double.IsFinite(timelineStart) || timelineStart < 0 ||
            !double.IsFinite(sourceIn) || sourceIn < 0 ||
            !double.IsFinite(sourceDuration) || sourceDuration <= sourceIn ||
            !double.IsFinite(targetDuration) || targetDuration <= 0 || targetDuration > 24 * 3600 ||
            !double.IsFinite(bpm) || bpm < 40 || bpm > 240)
            throw new AudioOperationException(AudioStageCode.Validate, "音乐重定时参数无效。");

        double beat = 60 / bpm;
        double sourceRemaining = sourceDuration - sourceIn;
        if (timelineStart + targetDuration > 24 * 3600)
            throw new AudioOperationException(AudioStageCode.Validate,
                "音乐重定时结果超过时间线最大位置。");
        if (targetDuration <= sourceRemaining)
            return [new MusicRemixSegment(timelineStart, sourceIn, targetDuration, 0, 0)];
        double fade = Math.Min(.12, beat / 4);
        if (sourceRemaining + .000001 < 2 * beat + 2 * fade)
            throw new AudioOperationException(AudioStageCode.Validate, "音乐源剩余长度不足两个节拍。");

        // Whole bars are preferable for phrase continuity. Short recordings
        // fall back to the longest available span aligned to a full beat.
        double phrase = Math.Min(16 * beat,
            Math.Floor((sourceRemaining - 2 * fade + .000001) / beat) * beat);
        phrase = Math.Max(2 * beat, phrase);
        // A requested extension may be longer than one source phrase. Each
        // repeated phrase below is short enough to remain inside the source.
        var lengths = new List<double>();
        double cursor = 0;
        while (cursor < targetDuration - .000001)
        {
            if (lengths.Count >= MaximumSegments)
                throw new AudioOperationException(AudioStageCode.Validate,
                    "目标时长需要超过 500 个音乐片段，请使用较长的源音乐或缩短目标时长。");
            double length = Math.Min(phrase, targetDuration - cursor);
            if (length <= 0) break;
            lengths.Add(length);
            cursor += length;
        }
        if (lengths.Count > 0 && cursor < targetDuration)
            lengths[^1] += targetDuration - cursor;

        var segments = new List<MusicRemixSegment>(lengths.Count);
        cursor = 0;
        for (int index = 0; index < lengths.Count; index++)
        {
            double length = lengths[index];
            // Each join is centered on its nominal beat boundary. Both clips
            // contribute half the overlap, so neither fade can exceed a very
            // short final segment's available audio.
            double incoming = index == 0 ? 0 : Math.Min(fade,
                Math.Min(lengths[index - 1], length) / 4);
            double outgoing = index == lengths.Count - 1 ? 0 : Math.Min(fade,
                Math.Min(length, lengths[index + 1]) / 4);
            double leading = incoming / 2;
            double trailing = outgoing / 2;
            double safeRange = Math.Max(0, sourceRemaining - phrase - 2 * fade);
            double sourceOffset = safeRange < beat ? 0 :
                (index * phrase) % ((Math.Floor(safeRange / beat) + 1) * beat);
            double sourceStart = sourceIn + sourceOffset;
            // A short target tail may need to borrow a few samples from the
            // preceding phrase. Keep the trim within the recording.
            if (sourceStart + length + leading + trailing > sourceDuration)
                sourceStart = Math.Max(sourceIn, sourceDuration - length - leading - trailing);
            double clipLength = length + leading + trailing;
            segments.Add(new MusicRemixSegment(timelineStart + cursor - leading,
                sourceStart, clipLength, incoming, outgoing));
            cursor += length;
        }
        return segments;
    }
}
