using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Video stabilization using motion analysis and compensating transforms.
/// Analyzes camera motion and generates smoothing keyframes to reduce shake.
/// </summary>
public static class WarpStabilizer
{
    public enum StabilizationMethod
    {
        Position,          // Stabilize position only
        PositionScale,     // Stabilize position and scale
        PositionScaleRotation, // Full stabilization (smoothest)
        Perspective        // Subspace warp (not implemented)
    }

    public enum SmoothingLevel
    {
        Low = 10,
        Medium = 30,
        High = 60
    }

    public sealed record StabilizationResult(
        List<ClipKeyframe> PositionXKeyframes,
        List<ClipKeyframe> PositionYKeyframes,
        List<ClipKeyframe> ScaleKeyframes,
        List<ClipKeyframe> RotationKeyframes,
        double CropPercentage,
        int AnalyzedFrames
    );

    /// <summary>
    /// Analyzes clip motion and generates stabilization keyframes.
    /// </summary>
    public static async Task<StabilizationResult> StabilizeClipAsync(
        TimelineClip clip,
        MediaAsset asset,
        StabilizationMethod method = StabilizationMethod.PositionScaleRotation,
        SmoothingLevel smoothing = SmoothingLevel.Medium,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (clip.Kind is not (ClipKind.Video or ClipKind.Image))
            throw new ArgumentException("Only video clips can be stabilized");

        if (asset?.SourcePath == null || !File.Exists(asset.SourcePath))
            throw new FileNotFoundException("Source media not found");

        // Use FFmpeg vidstabdetect and vidstabtransform filters
        string ffmpeg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (!File.Exists(ffmpeg))
            throw new FileNotFoundException("FFmpeg not found", ffmpeg);

        string transformFile = Path.Combine(Path.GetTempPath(), $"stabilize-{Guid.NewGuid():N}.trf");

        try
        {
            // Step 1: Analyze motion
            progress?.Report(0.1);
            await AnalyzeMotionAsync(ffmpeg, asset.SourcePath, transformFile,
                clip.SourceInSeconds, clip.SourceInSeconds + clip.DurationSeconds,
                (int)smoothing, cancellationToken);

            progress?.Report(0.6);

            // Step 2: Parse transform data and generate keyframes
            var result = await GenerateStabilizationKeyframesAsync(
                transformFile, clip, method, smoothing, cancellationToken);

            progress?.Report(1.0);

            return result;
        }
        finally
        {
            try { File.Delete(transformFile); } catch { }
        }
    }

    private static async Task AnalyzeMotionAsync(
        string ffmpegPath,
        string inputPath,
        string transformFile,
        double startSeconds,
        double endSeconds,
        int shakiness,
        CancellationToken cancellationToken)
    {
        double duration = endSeconds - startSeconds;
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        // A comma-decimal locale would otherwise write "-ss 12,500", which the
        // encoder rejects, so the whole feature would fail there.
        string seek = startSeconds.ToString("0.###", invariant);
        string span = duration.ToString("0.###", invariant);
        // Backslash and colon are filter-syntax characters; an unescaped drive
        // path such as C:\Temp\x.trf is not parsed as a path inside -vf.
        string resultPath = transformFile.Replace("\\", "/").Replace(":", "\\:");

        ProcessResult result = await ExternalProcessRunner.RunAsync(ffmpegPath,
            $"-ss {seek} -t {span} -i \"{inputPath}\" " +
            $"-vf \"vidstabdetect=shakiness={shakiness}:accuracy=15:result='{resultPath}'\" " +
            "-f null -", cancellationToken);

        if (!result.Succeeded || !File.Exists(transformFile))
            throw new InvalidOperationException(
                "运动分析失败：" + (result.ErrorTail().Length > 0 ? result.ErrorTail() : "编码器没有输出任何信息"));
    }

    private static async Task<StabilizationResult> GenerateStabilizationKeyframesAsync(
        string transformFile,
        TimelineClip clip,
        StabilizationMethod method,
        SmoothingLevel smoothing,
        CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(transformFile, cancellationToken);

        var posXKeys = new List<ClipKeyframe>();
        var posYKeys = new List<ClipKeyframe>();
        var scaleKeys = new List<ClipKeyframe>();
        var rotKeys = new List<ClipKeyframe>();

        double maxAbsX = 0, maxAbsY = 0, maxAbsRotation = 0;

        // Parse transform data
        // Format: frame dx dy da zoom (frame, x-shift, y-shift, rotation, zoom)
        var transforms = new List<(int frame, double dx, double dy, double angle, double zoom)>();

        foreach (var line in lines)
        {
            if (line.StartsWith("Camera") || string.IsNullOrWhiteSpace(line))
                continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;

            if (int.TryParse(parts[0], out int frame) &&
                double.TryParse(parts[1], out double dx) &&
                double.TryParse(parts[2], out double dy) &&
                double.TryParse(parts[3], out double angle) &&
                double.TryParse(parts[4], out double zoom))
            {
                transforms.Add((frame, dx, dy, angle, zoom));
                maxAbsX = Math.Max(maxAbsX, Math.Abs(dx));
                maxAbsY = Math.Max(maxAbsY, Math.Abs(dy));
                maxAbsRotation = Math.Max(maxAbsRotation, Math.Abs(angle));
            }
        }

        if (transforms.Count == 0)
            throw new InvalidOperationException("No motion data found");

        // Apply smoothing and generate keyframes
        double fps = 30.0; // Assume 30fps, should get from asset metadata
        double smoothRadius = (int)smoothing / fps;

        foreach (var (frame, dx, dy, angle, zoom) in transforms)
        {
            double timeSeconds = frame / fps;
            if (timeSeconds > clip.DurationSeconds)
                break;

            // Invert transforms to stabilize (opposite motion compensates shake)
            if (method >= StabilizationMethod.Position)
            {
                posXKeys.Add(new ClipKeyframe
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Property = "PositionX",
                    TimeSeconds = timeSeconds,
                    Value = -dx,
                    Interpolation = "Linear"
                });

                posYKeys.Add(new ClipKeyframe
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Property = "PositionY",
                    TimeSeconds = timeSeconds,
                    Value = -dy,
                    Interpolation = "Linear"
                });
            }

            if (method >= StabilizationMethod.PositionScale)
            {
                // Apply inverse zoom to compensate
                double inverseZoom = 1.0 / Math.Max(0.5, zoom);
                scaleKeys.Add(new ClipKeyframe
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Property = "ScaleX",
                    TimeSeconds = timeSeconds,
                    Value = inverseZoom,
                    Interpolation = "Linear"
                });

                scaleKeys.Add(new ClipKeyframe
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Property = "ScaleY",
                    TimeSeconds = timeSeconds,
                    Value = inverseZoom,
                    Interpolation = "Linear"
                });
            }

            if (method >= StabilizationMethod.PositionScaleRotation)
            {
                rotKeys.Add(new ClipKeyframe
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Property = "RotationDegrees",
                    TimeSeconds = timeSeconds,
                    Value = -angle * (180.0 / Math.PI), // Convert radians to degrees
                    Interpolation = "Linear"
                });
            }
        }

        // Calculate crop percentage needed (edges may be black after stabilization)
        double cropPercentage = Math.Max(maxAbsX, maxAbsY) * 100.0 / 1920.0; // Rough estimate
        cropPercentage = Math.Min(15.0, cropPercentage); // Cap at 15%

        return new StabilizationResult(
            posXKeys,
            posYKeys,
            scaleKeys,
            rotKeys,
            cropPercentage,
            transforms.Count
        );
    }

    /// <summary>
    /// Applies stabilization result to a clip by merging keyframes.
    /// </summary>
    public static void ApplyStabilization(TimelineClip clip, StabilizationResult result)
    {
        // Remove existing transform keyframes
        clip.Keyframes.RemoveAll(k => k.Property is "PositionX" or "PositionY" or "ScaleX" or "ScaleY" or "RotationDegrees");

        // Add stabilization keyframes
        clip.Keyframes.AddRange(result.PositionXKeyframes);
        clip.Keyframes.AddRange(result.PositionYKeyframes);
        clip.Keyframes.AddRange(result.ScaleKeyframes);
        clip.Keyframes.AddRange(result.RotationKeyframes);

        // Apply crop if needed
        if (result.CropPercentage > 0.5)
        {
            double scale = 1.0 + (result.CropPercentage / 100.0);
            clip.Transform.ScaleX *= scale;
            clip.Transform.ScaleY *= scale;
        }
    }
}
