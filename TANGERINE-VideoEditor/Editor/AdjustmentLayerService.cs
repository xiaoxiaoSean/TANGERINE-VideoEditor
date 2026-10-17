using System;
using System.Collections.Generic;
using System.Linq;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Adjustment layers apply effects to all clips beneath them on the timeline.
/// Unlike normal clips, they have no source media and act as effect containers.
/// </summary>
public static class AdjustmentLayerService
{
    /// <summary>
    /// Creates a new adjustment layer spanning the specified time range.
    /// </summary>
    public static TimelineClip CreateAdjustmentLayer(double startSeconds, double durationSeconds)
    {
        return new TimelineClip
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "调整图层",
            Kind = ClipKind.Adjustment,
            StartSeconds = startSeconds,
            DurationSeconds = durationSeconds,
            SourceInSeconds = 0,
            Speed = 1.0,
            Opacity = 1.0
        };
    }

    /// <summary>
    /// Checks if a clip is an adjustment layer.
    /// </summary>
    public static bool IsAdjustmentLayer(TimelineClip clip)
    {
        return clip.Kind == ClipKind.Adjustment;
    }

    /// <summary>
    /// Gets all clips affected by an adjustment layer (clips below it in the timeline).
    /// </summary>
    public static List<TimelineClip> GetAffectedClips(
        TimelineClip adjustmentLayer,
        TimelineTrack adjustmentTrack,
        ProjectDocument project)
    {
        if (!IsAdjustmentLayer(adjustmentLayer))
            return new List<TimelineClip>();

        var affected = new List<TimelineClip>();

        // Find all tracks below the adjustment layer track
        var tracksBelow = project.Tracks
            .Where(t => t.Kind == TrackKind.Video && t.Order < adjustmentTrack.Order)
            .OrderByDescending(t => t.Order);

        double adjStart = adjustmentLayer.StartSeconds;
        double adjEnd = adjustmentLayer.StartSeconds + adjustmentLayer.DurationSeconds;

        foreach (var track in tracksBelow)
        {
            foreach (var clip in track.Clips)
            {
                double clipStart = clip.StartSeconds;
                double clipEnd = clip.StartSeconds + clip.DurationSeconds;

                // Check if clip overlaps with adjustment layer
                if (clipEnd > adjStart && clipStart < adjEnd)
                {
                    affected.Add(clip);
                }
            }
        }

        return affected;
    }

    /// <summary>
    /// Applies adjustment layer effects to the FFmpeg filter graph during export.
    /// </summary>
    public static string BuildAdjustmentLayerFilters(
        TimelineClip adjustmentLayer,
        ProjectSequence sequence,
        double? previewLocalSeconds = null)
    {
        if (!IsAdjustmentLayer(adjustmentLayer))
            return string.Empty;

        // Adjustment layers use the same effect pipeline as regular clips
        return VisualEffectPipeline.BuildFfmpegFilters(adjustmentLayer, sequence, previewLocalSeconds);
    }
}
