using System.Globalization;
using System.IO;
using System.Text.Json;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Analyzes timeline segments to determine if they can be stream-copied
/// instead of re-encoded. Smart render dramatically speeds up exports when
/// source clips match output codec/resolution/framerate and no effects are applied.
/// </summary>
public static class SmartRenderAnalyzer
{
    public sealed record SegmentAnalysis(
        bool CanStreamCopy,
        string? Reason,
        string? SourceCodec,
        int? SourceWidth,
        int? SourceHeight,
        double? SourceFramerate,
        bool HasEffects,
        bool HasTransforms,
        bool HasTransitions
    );

    /// <summary>
    /// Determines if a clip segment can be stream-copied without re-encoding.
    /// </summary>
    public static SegmentAnalysis AnalyzeClip(
        TimelineClip clip,
        MediaAsset? asset,
        int targetWidth,
        int targetHeight,
        double targetFramerate,
        string targetCodec = "h264")
    {
        // Only video/audio clips from media files can be stream-copied
        if (clip.Kind is not (ClipKind.Video or ClipKind.Audio))
            return new(false, "非媒体片段", null, null, null, null, false, false, false);

        if (asset is null || string.IsNullOrWhiteSpace(asset.SourcePath))
            return new(false, "缺少源素材", null, null, null, null, false, false, false);

        // Check for effects that require re-encoding
        bool hasEffects = clip.Effects.Count > 0 ||
                         (clip.BlendMode != null && clip.BlendMode != "Normal");

        if (hasEffects)
            return new(false, "应用了效果/蒙版/混合模式", null, null, null, null, true, false, false);

        // Check for transform/animation requiring re-encoding
        bool hasTransforms = clip.Keyframes.Count > 0 ||
                            Math.Abs(clip.Transform.ScaleX - 1.0) > 0.001 ||
                            Math.Abs(clip.Transform.ScaleY - 1.0) > 0.001 ||
                            Math.Abs(clip.Transform.RotationDegrees) > 0.001 ||
                            Math.Abs(clip.Transform.PositionX) > 0.1 ||
                            Math.Abs(clip.Transform.PositionY) > 0.1 ||
                            clip.Transform.CropLeft > 0 || clip.Transform.CropRight > 0 ||
                            clip.Transform.CropTop > 0 || clip.Transform.CropBottom > 0;

        if (hasTransforms)
            return new(false, "应用了变换/裁切/关键帧", null, null, null, null, false, true, false);

        // Check speed/time remapping
        if (Math.Abs(clip.Speed - 1.0) > 0.001 || clip.TimeRemapPoints.Count > 0)
            return new(false, "应用了速度调节", null, null, null, null, false, false, false);

        // Check opacity
        if (Math.Abs(clip.Opacity - 1.0) > 0.001)
            return new(false, "不透明度非 100%", null, null, null, null, false, false, false);

        // Parse source media properties from cached metadata
        string? sourceCodec = asset.Metadata.GetValueOrDefault("VideoCodec") as string;
        int? sourceWidth = null;
        int? sourceHeight = null;
        double? sourceFramerate = null;

        if (asset.Metadata.TryGetValue("Width", out var widthObj))
            sourceWidth = Convert.ToInt32(widthObj);
        if (asset.Metadata.TryGetValue("Height", out var heightObj))
            sourceHeight = Convert.ToInt32(heightObj);
        if (asset.Metadata.TryGetValue("Framerate", out var fpsObj))
            sourceFramerate = Convert.ToDouble(fpsObj);

        // Verify codec match
        if (sourceCodec == null || !sourceCodec.Equals(targetCodec, StringComparison.OrdinalIgnoreCase))
            return new(false, $"编码格式不匹配 ({sourceCodec} ≠ {targetCodec})",
                sourceCodec, sourceWidth, sourceHeight, sourceFramerate, false, false, false);

        // Verify resolution match
        if (sourceWidth != targetWidth || sourceHeight != targetHeight)
            return new(false, $"分辨率不匹配 ({sourceWidth}x{sourceHeight} ≠ {targetWidth}x{targetHeight})",
                sourceCodec, sourceWidth, sourceHeight, sourceFramerate, false, false, false);

        // Verify framerate match (allow 0.01 fps tolerance for 23.976/29.97/59.94)
        if (sourceFramerate == null || Math.Abs(sourceFramerate.Value - targetFramerate) > 0.01)
            return new(false, $"帧率不匹配 ({sourceFramerate:F2} ≠ {targetFramerate:F2})",
                sourceCodec, sourceWidth, sourceHeight, sourceFramerate, false, false, false);

        // All checks passed - can stream copy
        return new(true, null, sourceCodec, sourceWidth, sourceHeight, sourceFramerate, false, false, false);
    }

    /// <summary>
    /// Analyzes entire timeline and estimates potential time savings from smart render.
    /// </summary>
    public static (int StreamCopySegments, int ReEncodeSegments, double StreamCopyDuration, double ReEncodeDuration)
        AnalyzeTimeline(
            ProjectDocument project,
            double startSeconds,
            double endSeconds,
            int targetWidth,
            int targetHeight,
            double targetFramerate)
    {
        int streamCopyCount = 0;
        int reEncodeCount = 0;
        double streamCopyDuration = 0;
        double reEncodeDuration = 0;

        foreach (var track in project.Tracks.Where(t => t.Kind == TrackKind.Video && !t.IsHidden))
        {
            foreach (var clip in track.Clips)
            {
                // Only analyze clips within export range
                double clipEnd = clip.StartSeconds + clip.DurationSeconds;
                if (clipEnd < startSeconds || clip.StartSeconds > endSeconds)
                    continue;

                var asset = project.Media.FirstOrDefault(m => m.Id == clip.MediaAssetId);
                var analysis = AnalyzeClip(clip, asset, targetWidth, targetHeight, targetFramerate);

                if (analysis.CanStreamCopy)
                {
                    streamCopyCount++;
                    streamCopyDuration += clip.DurationSeconds;
                }
                else
                {
                    reEncodeCount++;
                    reEncodeDuration += clip.DurationSeconds;
                }
            }
        }

        return (streamCopyCount, reEncodeCount, streamCopyDuration, reEncodeDuration);
    }

    /// <summary>
    /// Probes the first VIDEO stream of a file for codec, size and frame rate.
    /// Only the video stream is requested: files often list audio first, and
    /// reading "the first codec_name" would report AAC as the video codec, so
    /// Smart Render would see a mismatch and never apply.
    /// </summary>
    public static async Task<Dictionary<string, object?>> ProbeMediaFileAsync(
        string ffprobePath,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, object?>();
        try
        {
            ProcessResult run = await ExternalProcessRunner.RunAsync(ffprobePath,
                "-v error -select_streams v:0 -print_format json -show_streams " +
                $"\"{filePath}\"", cancellationToken).ConfigureAwait(false);
            if (!run.Succeeded) return result;

            using JsonDocument document = JsonDocument.Parse(run.StandardOutput);
            if (!document.RootElement.TryGetProperty("streams", out JsonElement streams) ||
                streams.ValueKind != JsonValueKind.Array || streams.GetArrayLength() == 0)
                return result;
            JsonElement video = streams[0];

            if (video.TryGetProperty("codec_name", out JsonElement codec) &&
                codec.ValueKind == JsonValueKind.String)
                result["VideoCodec"] = codec.GetString();
            if (video.TryGetProperty("width", out JsonElement width) && width.TryGetInt32(out int w))
                result["Width"] = w;
            if (video.TryGetProperty("height", out JsonElement height) && height.TryGetInt32(out int h))
                result["Height"] = h;

            // "30000/1001" is a rational. A "0/0" means unknown, so it is skipped
            // rather than turned into a bogus rate.
            if (video.TryGetProperty("r_frame_rate", out JsonElement rate) &&
                rate.GetString() is { } text && text.Split('/') is { Length: 2 } parts &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) &&
                denominator > 0 && numerator > 0)
                result["Framerate"] = numerator / denominator;
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            // The probe could not run or its output was unreadable. An empty
            // result means "unknown", which Smart Render treats as not eligible.
        }
        return result;
    }
}
