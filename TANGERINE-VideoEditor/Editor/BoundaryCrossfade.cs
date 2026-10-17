using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Finds an adjacent same-track pair that requests a two-source dissolve.
/// The outgoing source is held at its last edited frame when the timeline has
/// no overlapping media handles; the incoming source continues normally.
/// </summary>
public static class BoundaryCrossfade
{
    public static bool TryGetTransition(TimelineTrack track, TimelineClip incoming, double fps,
        out TimelineClip? prior, out ClipTransition? transition, out double duration)
    {
        transition = null;
        prior = null;
        duration = 0;
        if (incoming.Kind is not (ClipKind.Video or ClipKind.Image or ClipKind.Color) ||
            !double.IsFinite(fps) || fps <= 0) return false;
        prior = track.Clips.Where(candidate => candidate.Id != incoming.Id &&
                candidate.Kind is (ClipKind.Video or ClipKind.Image or ClipKind.Color) &&
                Math.Abs(candidate.StartSeconds + candidate.DurationSeconds - incoming.StartSeconds) <= .5 / fps)
            .OrderByDescending(candidate => candidate.StartSeconds).FirstOrDefault();
        if (prior is null) return false;
        transition = incoming.Transitions.FirstOrDefault(item =>
            item.Edge.Equals("In", StringComparison.OrdinalIgnoreCase)) ??
            prior.Transitions.FirstOrDefault(item =>
                item.Edge.Equals("Out", StringComparison.OrdinalIgnoreCase));
        if (transition is null || !double.IsFinite(transition.DurationSeconds)) return false;
        duration = Math.Min(transition.DurationSeconds,
            Math.Min(prior.DurationSeconds / 2, incoming.DurationSeconds / 2));
        return duration > 1 / fps;
    }

    public static bool TryGetPrior(TimelineTrack track, TimelineClip incoming, double fps,
        out TimelineClip? prior, out double duration)
    {
        return TryGetTransition(track, incoming, fps, out prior, out _, out duration);
    }

    public static bool IsActive(ProjectDocument project, double time) =>
        project.Tracks.Where(track => !track.IsHidden && track.Kind != TrackKind.Audio)
            .Any(track => track.Clips.Any(incoming =>
                TryGetPrior(track, incoming, project.Sequence.FramesPerSecond,
                    out _, out double duration) &&
                time >= incoming.StartSeconds && time < incoming.StartSeconds + duration));
}
