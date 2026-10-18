using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>Extends outgoing video reads into available source handles without shifting authored cuts.</summary>
public static class TransitionHandles
{
    public static double Available(TimelineClip clip, MediaAsset? asset)
    {
        if (clip.Kind != ClipKind.Video || clip.TimeRemapPoints.Count > 0 || asset?.DurationSeconds is not > 0 || clip.Speed <= 0) return 0;
        return Math.Max(0, (clip.IsReversed ? clip.SourceInSeconds : asset.DurationSeconds.Value - clip.SourceInSeconds - clip.DurationSeconds * clip.Speed) / clip.Speed);
    }
    public static bool CanMove(TimelineClip clip, MediaAsset? asset, double duration) => Available(clip, asset) + .000001 >= duration;
    public static double Sample(TimelineClip clip, double time) => clip.IsReversed
        ? clip.SourceInSeconds - Math.Max(0, time - clip.StartSeconds - clip.DurationSeconds) * clip.Speed
        : clip.SourceInSeconds + (time - clip.StartSeconds) * clip.Speed;
    public static string Describe(TimelineClip clip, MediaAsset? asset, double duration)
    {
        double available = Available(clip, asset);
        return CanMove(clip, asset, duration) ? $"双源连续运动：使用前段 {duration:0.###} 秒源余量（切点后混合）" :
            $"前段源余量仅 {available:0.###} 秒，当前转场需要 {duration:0.###} 秒；将明确回退为尾帧保持。可缩短转场或修剪源出点。";
    }
}
