using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

public enum PaperEditStageCode
{
    Validate,
    Read
}

public sealed class PaperEditException(PaperEditStageCode stageCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public PaperEditStageCode StageCode { get; } = stageCode;
}

public sealed record PaperEditPiece(TimelineClip SourceClip, double StartSeconds,
    double DurationSeconds, double LocalStartSeconds);

/// <summary>
/// Converts caption time ranges into non-destructive media edit decisions.
/// Captions are treated as a human-reviewed transcript: they supply time
/// boundaries, while the source clip supplies the actual audio or picture.
/// Disjoint intersections preserve gaps and overlapping caption ranges are
/// merged before any source offsets are calculated.
/// </summary>
public static class PaperEditPlanner
{
    public static IReadOnlyList<PaperEditPiece> Plan(TimelineTrack mediaTrack,
        IEnumerable<(double Start, double End)> captionRanges, bool keepSelected)
    {
        if (mediaTrack.Kind is not (TrackKind.Video or TrackKind.Audio))
            throw new PaperEditException(PaperEditStageCode.Validate, "文本剪辑需要视频或音频轨道。");
        List<(double Start, double End)> ranges = Merge(captionRanges);
        if (ranges.Count == 0)
            throw new PaperEditException(PaperEditStageCode.Validate, "请先选择至少一条有效字幕。");

        var pieces = new List<PaperEditPiece>();
        foreach (TimelineClip clip in mediaTrack.Clips.Where(item =>
            item.Kind is ClipKind.Video or ClipKind.Audio).OrderBy(item => item.StartSeconds))
        {
            if (mediaTrack.Kind == TrackKind.Video && clip.Kind != ClipKind.Video ||
                mediaTrack.Kind == TrackKind.Audio && clip.Kind != ClipKind.Audio) continue;
            if (!double.IsFinite(clip.StartSeconds) || clip.StartSeconds < 0 ||
                !double.IsFinite(clip.DurationSeconds) || clip.DurationSeconds <= 0 ||
                !double.IsFinite(clip.SourceInSeconds) || clip.SourceInSeconds < 0 ||
                !double.IsFinite(clip.Speed) || clip.Speed <= 0 ||
                clip.TimeRemapPoints is null ||
                clip.TimeRemapPoints.Any(point => !double.IsFinite(point.TimeSeconds) ||
                    !double.IsFinite(point.Speed) || point.Speed < .05 || point.Speed > 32 ||
                    point.TimeSeconds < 0 || point.TimeSeconds >= clip.DurationSeconds))
                throw new PaperEditException(PaperEditStageCode.Validate,
                    "文本剪辑的媒体时间或速度节点无效。");
            ClipTimeRemapPoint[] nodes = clip.TimeRemapPoints.OrderBy(point => point.TimeSeconds).ToArray();
            if (nodes.Zip(nodes.Skip(1),
                    (left, right) => right.TimeSeconds - left.TimeSeconds)
                .Any(distance => distance < .000001))
                throw new PaperEditException(PaperEditStageCode.Validate,
                    "文本剪辑的媒体速度节点重叠。");
            if (!double.IsFinite(ClipTimeMap.SourceOffset(clip, clip.DurationSeconds)) ||
                !double.IsFinite(clip.StartSeconds + clip.DurationSeconds))
                throw new PaperEditException(PaperEditStageCode.Validate,
                    "文本剪辑的媒体源时间范围无效。");
            double clipEnd = clip.StartSeconds + clip.DurationSeconds;
            double cursor = clip.StartSeconds;
            foreach ((double start, double end) in ranges)
            {
                if (end <= clip.StartSeconds || start >= clipEnd) continue;
                double selectedStart = Math.Max(start, clip.StartSeconds);
                double selectedEnd = Math.Min(end, clipEnd);
                if (!keepSelected && selectedStart > cursor)
                    AddPiece(clip, cursor, selectedStart, pieces);
                if (keepSelected) AddPiece(clip, selectedStart, selectedEnd, pieces);
                cursor = Math.Max(cursor, selectedEnd);
            }
            if (!keepSelected && cursor < clipEnd)
                AddPiece(clip, cursor, clipEnd, pieces);
        }
        if (keepSelected) pieces = pieces.OrderBy(piece => piece.StartSeconds).ToList();
        if (pieces.Count > 10000)
            throw new PaperEditException(PaperEditStageCode.Validate,
                "文本剪辑将生成超过 10000 个片段，请减少所选字幕范围。");
        return pieces;
    }

    private static List<(double Start, double End)> Merge(IEnumerable<(double Start, double End)> values)
    {
        var ordered = values.OrderBy(value => value.Start).ToArray();
        var result = new List<(double Start, double End)>();
        foreach ((double start, double end) in ordered)
        {
            if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start)
                throw new PaperEditException(PaperEditStageCode.Validate, "字幕时间范围无效。");
            if (result.Count > 0 && start <= result[^1].End)
            {
                (double oldStart, double oldEnd) = result[^1];
                result[^1] = (oldStart, Math.Max(oldEnd, end));
            }
            else result.Add((start, end));
        }
        return result;
    }

    private static void AddPiece(TimelineClip clip, double start, double end, List<PaperEditPiece> pieces)
    {
        if (end - start < .001) return;
        // KeepRange later translates this local interval through fixed speed,
        // speed nodes, or reverse playback before the clone is repositioned.
        pieces.Add(new PaperEditPiece(clip, start, end - start,
            start - clip.StartSeconds));
    }
}
