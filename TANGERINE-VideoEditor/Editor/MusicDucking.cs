using System.Globalization;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Computes a deterministic offline music envelope from manually classified
/// dialogue clips. Ducking is timeline based and is evaluated in both export
/// and short-window audio monitoring, so the same edit remains audible in
/// either path without a hosted speech-detection service.
/// </summary>
public static class MusicDucking
{
    public static string BuildFilter(ProjectDocument project, TimelineClip musicClip,
        double localStart = 0, double? durationSeconds = null)
    {
        AudioSettings music = AudioSettings.FromClip(musicClip);
        if (!music.AutoDuckingEnabled) return string.Empty;
        double duration = durationSeconds ?? musicClip.DurationSeconds;
        if (!double.IsFinite(localStart) || localStart < 0 ||
            !double.IsFinite(duration) || duration <= 0 ||
            !double.IsFinite(musicClip.StartSeconds) || musicClip.StartSeconds < 0 ||
            !double.IsFinite(musicClip.DurationSeconds) || musicClip.DurationSeconds <= 0)
            throw new AudioOperationException(AudioStageCode.Validate, "音乐闪避时间范围无效。");

        double windowStart = musicClip.StartSeconds + localStart;
        double windowEnd = windowStart + duration;
        var dialogue = new List<(double Start, double End)>();
        foreach (TimelineTrack track in project.Tracks.Where(track =>
            (track.Kind is TrackKind.Audio or TrackKind.Video) && !track.IsMuted &&
            (track.Kind == TrackKind.Audio || !track.IsHidden)))
        {
            foreach (TimelineClip clip in track.Clips.Where(clip =>
                (clip.Kind is ClipKind.Audio or ClipKind.Video) && !clip.IsMuted &&
                !ReferenceEquals(clip, musicClip)))
            {
                MediaAsset? asset = project.Media.FirstOrDefault(item => item.Id == clip.MediaAssetId);
                if (asset is null || (clip.Kind == ClipKind.Video && !asset.HasAudio) ||
                    AudioSettings.FromClip(clip).Category != "对白") continue;
                if (!double.IsFinite(clip.StartSeconds) || !double.IsFinite(clip.DurationSeconds) ||
                    clip.DurationSeconds <= 0) continue;
                double start = Math.Max(musicClip.StartSeconds, clip.StartSeconds);
                double end = Math.Min(musicClip.StartSeconds + musicClip.DurationSeconds,
                    clip.StartSeconds + clip.DurationSeconds);
                if (end + music.DuckingFadeSeconds <= windowStart ||
                    start - music.DuckingFadeSeconds >= windowEnd || end <= start) continue;
                dialogue.Add((start - windowStart, end - windowStart));
            }
        }
        if (dialogue.Count == 0) return string.Empty;
        if (dialogue.Count > 1000)
            throw new AudioOperationException(AudioStageCode.Validate,
                "对白片段过多，无法生成音乐闪避包络。");
        dialogue.Sort((left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<(double Start, double End)>();
        foreach ((double start, double end) in dialogue)
        {
            // When two ramps touch, sustain the ducked level through their
            // narrow gap instead of multiplying two partial gain envelopes.
            if (merged.Count > 0 && start - music.DuckingFadeSeconds * 2 <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            else merged.Add((start, end));
        }
        if (merged.Count > 128)
            throw new AudioOperationException(AudioStageCode.Validate,
                "对白区间过多，无法生成音乐闪避包络。");
        string number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        string gain = number(music.DuckingGain);
        double fade = music.DuckingFadeSeconds;
        var envelopes = new List<string>(merged.Count);
        foreach ((double start, double end) in merged)
        {
            double attackStart = start - fade;
            double releaseEnd = end + fade;
            string expression = fade < .000001
                ? $"if(between(t,{number(start)},{number(end)}),{gain},1)"
                : $"if(lt(t,{number(attackStart)}),1," +
                    $"if(lt(t,{number(start)}),1-(1-{gain})*" +
                    $"(t-{number(attackStart)})/{number(Math.Max(.000001, start - attackStart))}," +
                    $"if(lt(t,{number(end)}),{gain}," +
                    $"if(lt(t,{number(releaseEnd)}),{gain}+(1-{gain})*" +
                    $"(t-{number(end)})/{number(Math.Max(.000001, releaseEnd - end))},1))))";
            envelopes.Add(expression);
        }
        // Separate volume stages multiply distant envelopes. Nearby ramps
        // were merged above, so their active regions never overlap.
        return string.Concat(envelopes.Select(expression => $",volume='{expression}':eval=frame"));
    }
}
