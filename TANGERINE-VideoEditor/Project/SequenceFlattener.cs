using System.Text.Json;
using TANGERINE_VideoEditor.Editor;

namespace TANGERINE_VideoEditor.Project;

/// <summary>
/// Expands local nested sequences into ordinary media tracks before export.
/// Every expansion is a copy, so the editable project remains untouched.
/// </summary>
public static class SequenceFlattener
{
    public static ProjectDocument Flatten(ProjectDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(source))
            ?? throw new ProjectFileException(ProjectStageCode.Parse, "无法复制项目序列。");
        var active = new ProjectSequenceWorkspace
        {
            Sequence = result.Sequence,
            Tracks = result.Tracks,
            Markers = result.Markers
        };
        Dictionary<string, ProjectSequenceWorkspace> all;
        try { all = result.Sequences.Append(active).ToDictionary(item => item.Sequence.Id, StringComparer.Ordinal); }
        catch (ArgumentException exception)
        { throw new ProjectFileException(ProjectStageCode.Validate, "项目中有重复的序列标识。", exception); }
        var expanded = new List<(int[] Order, TimelineTrack Track)>();
        Expand(active, [result.Sequence.Id], [], 0, 0, double.PositiveInfinity, 1);
        result.Tracks = expanded.OrderBy(item => item.Order, OrderComparer.Instance)
            .Select((item, index) => { item.Track.Order = index; return item.Track; }).ToList();
        return result;

        void Expand(ProjectSequenceWorkspace workspace, List<string> ancestry, int[] prefix,
            double placement, double sourceStart, double visibleDuration, double speed)
        {
            foreach (TimelineTrack track in workspace.Tracks.OrderBy(item => item.Order))
            {
                var output = new TimelineTrack
                {
                    Name = track.Name,
                    Kind = track.Kind,
                    IsHidden = track.IsHidden,
                    IsMuted = track.IsMuted,
                    IsLocked = track.IsLocked,
                    IsSyncLocked = track.IsSyncLocked
                };
                expanded.Add(([.. prefix, track.Order, 0], output));
                foreach (TimelineClip clip in track.Clips)
                {
                    double localStart = clip.StartSeconds;
                    double localEnd = clip.StartSeconds + clip.DurationSeconds;
                    double from = Math.Max(localStart, sourceStart);
                    double to = Math.Min(localEnd, sourceStart + visibleDuration);
                    if (to <= from) continue;
                    if (clip.Kind == ClipKind.Sequence)
                    {
                        if (string.IsNullOrWhiteSpace(clip.NestedSequenceId) ||
                            !all.TryGetValue(clip.NestedSequenceId, out ProjectSequenceWorkspace? child))
                            throw new ProjectFileException(ProjectStageCode.Validate,
                                $"嵌套序列“{clip.Name}”无法找到，请检查序列引用。");
                        if (ancestry.Contains(child.Sequence.Id, StringComparer.Ordinal))
                            throw new ProjectFileException(ProjectStageCode.Validate,
                                $"嵌套序列“{clip.Name}”形成循环引用。");
                        if (!double.IsFinite(clip.Speed) || clip.Speed <= 0 ||
                            !double.IsFinite(clip.SourceInSeconds) || clip.SourceInSeconds < 0)
                            throw new ProjectFileException(ProjectStageCode.Validate,
                                $"嵌套序列“{clip.Name}”的源范围或速度无效。");
                        // Child sequence time is converted into the current
                        // sequence's timebase, then again into the final export.
                        double childFrom = clip.SourceInSeconds + (from - localStart) * clip.Speed;
                        double childDuration = (to - from) * clip.Speed;
                        ExpandChild(child, [.. ancestry, child.Sequence.Id], [.. prefix, track.Order, 1],
                            placement + (from - sourceStart) / speed, childFrom, childDuration, speed * clip.Speed,
                            track.IsHidden, track.IsMuted);
                    }
                    else
                    {
                        TimelineClip copy = CloneClip(clip);
                        // A nested sequence may be placed more than once. Each
                        // occurrence needs its own clip identity: title image
                        // paths, crossfade holds, and export graph state are
                        // keyed by clip ID after expansion.
                        copy.Id = Guid.NewGuid().ToString("N");
                        double offset = from - localStart;
                        // ClipAnimation reads clip-local time; keep the child
                        // interval before changing the copy's timebase.
                        ClipAnimation.KeepRange(copy, offset, to - from);
                        if (copy.TimeRemapPoints.Count > 0)
                        {
                            ClipTimeMap.KeepRange(copy, offset, to - from);
                            foreach (ClipTimeRemapPoint point in copy.TimeRemapPoints)
                            {
                                point.TimeSeconds /= speed;
                                point.Speed *= speed;
                            }
                        }
                        else copy.SourceInSeconds +=
                            (copy.IsReversed ? clip.DurationSeconds - offset - (to - from) : offset) * clip.Speed;
                        copy.StartSeconds = placement + (from - sourceStart) / speed;
                        copy.DurationSeconds = (to - from) / speed;
                        copy.Speed *= speed;
                        foreach (ClipKeyframe keyframe in copy.Keyframes)
                            keyframe.TimeSeconds /= speed;
                        output.Clips.Add(copy);
                    }
                }
            }
        }

        void ExpandChild(ProjectSequenceWorkspace child, List<string> ancestry, int[] prefix,
            double placement, double sourceStart, double sourceDuration, double speed,
            bool parentHidden, bool parentMuted)
        {
            int start = expanded.Count;
            Expand(child, ancestry, prefix, placement, sourceStart, sourceDuration, speed);
            for (int index = start; index < expanded.Count; index++)
            {
                TimelineTrack track = expanded[index].Track;
                track.IsHidden |= parentHidden;
                track.IsMuted |= parentMuted;
            }
        }
    }

    private static TimelineClip CloneClip(TimelineClip clip) =>
        JsonSerializer.Deserialize<TimelineClip>(JsonSerializer.Serialize(clip))!;

    private sealed class OrderComparer : IComparer<int[]>
    {
        public static OrderComparer Instance { get; } = new();
        public int Compare(int[]? left, int[]? right)
        {
            if (left is null || right is null) return Comparer<int[]>.Default.Compare(left, right);
            for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
                if (left[index] != right[index]) return left[index].CompareTo(right[index]);
            return left.Length.CompareTo(right.Length);
        }
    }
}
