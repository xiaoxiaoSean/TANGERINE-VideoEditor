using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

public enum ExportStageCode
{
    Validate,
    Prepare,
    EncoderMissing,
    Encode,
    Finalize
}

public sealed class ExportException(ExportStageCode stageCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ExportStageCode StageCode { get; } = stageCode;
}

/// <summary>
/// Defines the local encoding choices for one queue item. Null output dimensions
/// and frame rate preserve the sequence settings, including fractional frame rates.
/// The time range is expressed on the sequence clock rather than source clocks.
/// </summary>
public sealed record VideoExportSettings(
    int? Width = null,
    int? Height = null,
    double? FramesPerSecond = null,
    int? VideoBitrateKbps = null,
    double StartSeconds = 0,
    double? EndSeconds = null, string VideoCodec = "H264", int BitDepth = 8);

/// <summary>
/// Builds an FFmpeg filter graph from the versioned timeline model. Each clip is
/// trimmed and placed on the common sequence clock, then visible video tracks
/// are composited in track order while audible tracks are mixed independently.
/// The encoder is bundled with the application and never edits source media.
/// </summary>
public static class TimelineExporter
{
    private sealed record InputClip(TimelineTrack Track, TimelineClip Clip, MediaAsset? Asset, int InputIndex, bool HasAudio);

    public static string ResolveEncoderPath()
    {
        foreach (string baseDirectory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(TimelineExporter).Assembly.Location) ?? string.Empty })
        {
            string packaged = Path.Combine(baseDirectory, "ffmpeg", "ffmpeg.exe");
            if (File.Exists(packaged)) return packaged;
            packaged = Path.Combine(baseDirectory, "Resources", "ffmpeg.exe");
            if (File.Exists(packaged)) return packaged;
        }
        return "ffmpeg";
    }

    public static async Task ExportMp4Async(ProjectDocument project, string outputPath,
        IReadOnlyDictionary<string, string> titleImagePaths, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default, VideoExportSettings? settings = null,
        PreviewEncodingProfile? previewEncoding = null, string? encoderPath = null)
    {
        settings ??= new VideoExportSettings();
        if (project.Sequence.Width <= 0 || project.Sequence.Height <= 0 ||
            !double.IsFinite(project.Sequence.FramesPerSecond) || project.Sequence.FramesPerSecond <= 0 ||
            string.IsNullOrWhiteSpace(outputPath) ||
            !string.Equals(Path.GetExtension(outputPath), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ExportException(ExportStageCode.Validate, "导出设置无效，请选择 MP4 文件并检查项目分辨率及帧率。");
        if (project.Sequence.AudioSampleRate is < 8000 or > 192000)
            throw new ExportException(ExportStageCode.Validate, "序列音频采样率无效。");
        if (!double.IsFinite(project.Sequence.FramesPerSecond) || project.Sequence.FramesPerSecond > 120)
            throw new ExportException(ExportStageCode.Validate, "序列帧率无效。");

        double duration = project.Tracks.SelectMany(track => track.Clips)
            .Where(clip => clip.DurationSeconds > 0 && double.IsFinite(clip.StartSeconds) && double.IsFinite(clip.DurationSeconds))
            .Select(clip => clip.StartSeconds + clip.DurationSeconds).DefaultIfEmpty(0).Max();
        if (duration <= 0) throw new ExportException(ExportStageCode.Validate, "时间线上没有可导出的片段。");
        if (duration > 24 * 3600) throw new ExportException(ExportStageCode.Validate, "导出时长超过 24 小时，请检查时间线片段。");
        double rangeStart = settings.StartSeconds;
        double rangeEnd = settings.EndSeconds ?? duration;
        double exportDuration = rangeEnd - rangeStart;
        int outputWidth = settings.Width ?? project.Sequence.Width;
        int outputHeight = settings.Height ?? project.Sequence.Height;
        double outputFps = settings.FramesPerSecond ?? project.Sequence.FramesPerSecond;
        if (!double.IsFinite(rangeStart) || !double.IsFinite(rangeEnd) || rangeStart < 0 ||
            rangeEnd > duration + .000001 || exportDuration <= 0 ||
            outputWidth < 16 || outputHeight < 16 || outputWidth > 8192 || outputHeight > 8192 ||
            outputWidth % 2 != 0 || outputHeight % 2 != 0 ||
            !double.IsFinite(outputFps) || outputFps < 1 || outputFps > 120 ||
            settings.VideoBitrateKbps is < 250 or > 200000)
            throw new ExportException(ExportStageCode.Validate, "导出范围、画幅、帧率或码率无效。");
        if (settings.VideoCodec is not ("H264" or "HEVC" or "AV1") || settings.BitDepth is not (8 or 10) ||
            settings.VideoCodec == "H264" && settings.BitDepth == 10)
            throw new ExportException(ExportStageCode.Validate, "编码格式应为 H264/HEVC/AV1；10位请选择HEVC或AV1。");
        // Some legacy FFmpeg filters only accept 8-bit input. Never silently
        // quantize an advertised ten-bit render through one of those filters.
        string[] precisionSafe = ["effect.blur", "effect.sharpen", "effect.motion-blur", "effect.mirror", "effect.curves", "effect.wheels", "effect.temperature", "effect.tint", "effect.lut", "effect.sepia", "effect.invert"];
        if (settings.BitDepth == 10 && project.Tracks.SelectMany(t => t.Clips).SelectMany(c => c.Effects).Any(e => e.IsEnabled && !precisionSafe.Contains(e.PresetId)))
            throw new ExportException(ExportStageCode.Validate, "当前效果尚未通过10位处理精度验证。请使用8位导出，或关闭该效果后进行10位导出，避免静默降低精度。");
        string fullOutput = Path.GetFullPath(outputPath);
        if (!Path.GetExtension(fullOutput).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ExportException(ExportStageCode.Validate, "导出目标必须是 MP4 文件。");
        if (project.Media.Any(media =>
            string.Equals(SafeFullPath(media.SourcePath), fullOutput, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(SafeFullPath(media.ProxyPath), fullOutput, StringComparison.OrdinalIgnoreCase)))
            throw new ExportException(ExportStageCode.Validate, "导出目标不能覆盖项目中的源文件或代理文件。");

        string? directory = Path.GetDirectoryName(fullOutput);
        if (directory is null || !Directory.Exists(directory))
            throw new ExportException(ExportStageCode.Validate, "导出目录不存在。");
        string encoder = encoderPath ?? ResolveEncoderPath();
        string temporaryOutput = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(fullOutput)}.{Guid.NewGuid():N}.mp4");
        string scriptPath = Path.Combine(Path.GetTempPath(), $"tangerine-{Guid.NewGuid():N}.ffgraph");
        try
        {
            var inputs = new List<InputClip>();
            var arguments = new List<string>
            {
                "-hide_banner", "-y", "-nostdin", "-loglevel", "error",
                // Bound per-filter queues on timelines with many image inputs;
                // decoding and video encoding remain independently threaded.
                "-filter_complex_threads", "4",
                "-progress", "pipe:1"
            };
            int width = project.Sequence.Width;
            int height = project.Sequence.Height;
            if (width % 2 != 0 || height % 2 != 0)
                throw new ExportException(ExportStageCode.Validate, "H.264 导出要求画布宽度和高度都是偶数。");
            string fpsText = Number(project.Sequence.FramesPerSecond);
            string outputFpsText = Number(outputFps);
            string durationText = Number(duration);
            arguments.AddRange(["-f", "lavfi", "-i", $"color=c={NormalizeColor(project.Sequence.BackgroundColor)}:s={width}x{height}:r={fpsText}:d={durationText}"]);
            arguments.AddRange(["-f", "lavfi", "-i", $"anullsrc=channel_layout=stereo:sample_rate={project.Sequence.AudioSampleRate}"]);
            int nextInput = 2;
            var audioProbeCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (TimelineTrack track in project.Tracks
                .Where(track => track.Kind == TrackKind.Audio ? !track.IsMuted : !track.IsHidden)
                .OrderBy(track => track.Order))
            {
                foreach (TimelineClip clip in track.Clips.OrderBy(clip => clip.StartSeconds))
                {
                    if (!double.IsFinite(clip.StartSeconds) || clip.StartSeconds < 0 || !double.IsFinite(clip.DurationSeconds) ||
                        clip.DurationSeconds <= 0 || !double.IsFinite(clip.Speed) || clip.Speed <= 0)
                        throw new ExportException(ExportStageCode.Validate, $"片段“{clip.Name}”的时间或速度无效。");
                    MediaAsset? asset = project.Media.FirstOrDefault(media => media.Id == clip.MediaAssetId);
                    string? inputPath = clip.Kind == ClipKind.Text && titleImagePaths.TryGetValue(clip.Id, out string? title)
                        ? title : asset?.EffectiveExportPath;
                    bool changingTitle = clip.Kind == ClipKind.Text &&
                        DynamicTitlePreset.HasChangingText(clip);
                    if (clip.Kind == ClipKind.Color)
                    {
                        arguments.AddRange(["-f", "lavfi", "-i", $"color=c={NormalizeColor(clip.Color)}:s={width}x{height}:r={fpsText}:d={Number(clip.DurationSeconds)}"]);
                    }
                    else if (clip.Kind == ClipKind.Text || clip.Kind == ClipKind.Image || clip.Kind == ClipKind.Video || clip.Kind == ClipKind.Audio)
                    {
                        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                            throw new ExportException(ExportStageCode.Validate, $"找不到片段“{clip.Name}”的源文件，请重新链接媒体。");
                        if (changingTitle)
                            arguments.AddRange(["-safe", "0", "-f", "concat"]);
                        else if (clip.Kind is ClipKind.Text or ClipKind.Image)
                            arguments.AddRange(["-loop", "1", "-framerate", fpsText]);
                        // Default decoder thread counts multiply by every
                        // simultaneously open input on large multiclip edits.
                        arguments.AddRange(["-threads", "2"]);
                        arguments.AddRange(["-i", inputPath]);
                    }
                    else
                    {
                        continue;
                    }
                    // Probe the actual export input once per path. Project
                    // metadata can be stale after relinking, and a proxy may
                    // omit audio even when the original source has it.
                    bool hasAudio = clip.Kind == ClipKind.Audio;
                    if (clip.Kind == ClipKind.Video && asset is not null && inputPath is not null)
                    {
                        if (!audioProbeCache.TryGetValue(inputPath, out bool probed))
                        {
                            probed = await ProbeHasAudioAsync(encoder, inputPath, cancellationToken)
                                .ConfigureAwait(false);
                            audioProbeCache[inputPath] = probed;
                        }
                        hasAudio = probed;
                    }
                    inputs.Add(new InputClip(track, clip, asset, nextInput++, hasAudio));
                }
            }

            var graph = new StringBuilder();
            graph.AppendLine($"[0:v]format=rgba[base0];");
            string videoLabel = "base0";
            int videoCount = 0;
            var outgoingFrames = new Dictionary<string, (string Label, string X, string Y)>(StringComparer.Ordinal);
            var outgoingDurations = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (TimelineTrack track in project.Tracks.Where(track => !track.IsHidden &&
                track.Kind != TrackKind.Audio))
            foreach (TimelineClip incoming in track.Clips)
                if (BoundaryCrossfade.TryGetPrior(track, incoming,
                    project.Sequence.FramesPerSecond, out TimelineClip? prior, out double length) &&
                    prior is not null)
                    outgoingDurations[prior.Id] = Math.Max(
                        outgoingDurations.GetValueOrDefault(prior.Id), length);
            // Adjustment layers do not have an input file. Insert them into the
            // same bottom-to-top walk so their filters affect only layers that
            // have already been composited below the adjustment track.
            var visualItems = inputs.Where(item => item.Track.Kind != TrackKind.Audio && !item.Track.IsHidden &&
                    item.Clip.Kind is not (ClipKind.Audio or ClipKind.Adjustment))
                .Concat(project.Tracks.Where(track => track.Kind != TrackKind.Audio && !track.IsHidden)
                    .SelectMany(track => track.Clips.Where(clip => clip.Kind == ClipKind.Adjustment)
                        .Select(clip => new InputClip(track, clip, null, -1, false))));
            // The interactive title layer is above the picture canvas. Keep
            // that stacking order in the encoded graph, including titles on
            // the same track as their background picture.
            foreach (InputClip item in visualItems
                .OrderBy(item => item.Clip.Kind == ClipKind.Text ? 1 : 0)
                .ThenByDescending(item => item.Track.Order).ThenBy(item => item.Clip.StartSeconds))
            {
                string local = $"v{videoCount}";
                string output = $"base{videoCount + 1}";
                TimelineClip clip = item.Clip;
                ValidateVisualTransform(clip);
                if (clip.Kind == ClipKind.Adjustment)
                {
                    // Trim one copy of the lower composite to the adjustment
                    // interval. Overlaying that processed copy preserves the
                    // untouched lower composite before and after the interval.
                    string original = $"adjustoriginal{videoCount}";
                    string source = $"adjustsource{videoCount}";
                    string chain = VisualEffectPipeline.BuildFfmpegFilters(clip, project.Sequence);
                    graph.AppendLine($"[{videoLabel}]split=2[{original}][{source}];");
                    string adjustmentFilter = $"trim=start={Number(clip.StartSeconds)}:duration={Number(clip.DurationSeconds)},setpts=PTS-STARTPTS" +
                        chain + ",format=rgba";
                    if (clip.Mask is not null)
                        adjustmentFilter += VisualEffectPipeline.BuildMaskAlpha(clip.Mask, clip,
                            framesPerSecond: project.Sequence.FramesPerSecond);
                    if (ClipAnimation.HasKeyframes(clip, "Opacity"))
                    {
                        string opacityExpression = ClipAnimation.FfmpegExpression(clip, "Opacity", $"(N/{fpsText})");
                        string alphaExpression = $"clip({opacityExpression},0,1)";
                        adjustmentFilter += $",geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*{VisualEffectPipeline.EscapeExpression(alphaExpression)}'";
                    }
                    else if (clip.Opacity < 1)
                        adjustmentFilter += $",colorchannelmixer=aa={Number(clip.Opacity)}";
                    adjustmentFilter += $",setpts=PTS+{Number(clip.StartSeconds)}/TB";
                    graph.AppendLine($"[{source}]{adjustmentFilter}[{local}];");
                    graph.AppendLine($"[{original}][{local}]overlay=eof_action=pass:repeatlast=0:format=auto[{output}];");
                    videoLabel = output;
                    videoCount++;
                    continue;
                }
                double movingTail = outgoingDurations.TryGetValue(clip.Id, out double requestedTail) &&
                    TransitionHandles.CanMove(clip, item.Asset, requestedTail) ? requestedTail : 0;
                bool still = clip.Kind is ClipKind.Text or ClipKind.Image or ClipKind.Color;
                bool changingTitle = clip.Kind == ClipKind.Text &&
                    DynamicTitlePreset.HasChangingText(clip);
                string sourceLabel = $"{item.InputIndex}:v";
                string filter;
                if (clip.FrameInterpolation is not ("Sampling" or "Blend" or "OpticalFlow"))
                    throw new ExportException(ExportStageCode.Validate,
                        $"片段“{clip.Name}”的帧插值模式无效。");
                if (still)
                    filter = $"trim=duration={Number(clip.DurationSeconds)},setpts=PTS-STARTPTS";
                else if (clip.TimeRemapPoints.Count > 0)
                {
                    sourceLabel = BuildRetimedInput(graph, item.InputIndex, clip,
                        item.InputIndex, audio: false);
                    filter = "null";
                }
                else
                {
                    double sourceSpan = (clip.DurationSeconds + movingTail) * clip.Speed;
                    double sourceIn = clip.SourceInSeconds - (clip.IsReversed ? movingTail * clip.Speed : 0);
                    filter = $"trim=start={Number(sourceIn)}:duration={Number(sourceSpan)}" +
                        (clip.IsReversed ? ",reverse" : string.Empty) +
                        $",setpts=(PTS-STARTPTS)/{Number(clip.Speed)}";
                }
                if (!still && clip.FrameInterpolation == "Blend")
                    filter += $",framerate=fps={fpsText}:interp_start=0:interp_end=255";
                else if (!still && clip.FrameInterpolation == "OpticalFlow")
                    filter += $",minterpolate=fps={fpsText}:mi_mode=mci";
                // Match the preview's source-sized layer. Fitting the complete
                // frame first keeps crop percentages relative to the original
                // visible image and prevents cropping from zooming it to fill
                // the entire sequence. Transparent pixels are introduced only
                // by rotation, rather than by a full-canvas pad around media.
                filter += clip.Kind is ClipKind.Text or ClipKind.Color
                    ? $",fps={fpsText},scale={width}:{height},format=rgba"
                    : $",fps={fpsText},scale={width}:{height}:force_original_aspect_ratio=decrease,format=rgba";
                // Concat demuxer entries carry their own durations. After
                // sampling onto the sequence rate, trim again to avoid the
                // final repeated state extending the clip by one frame.
                if (changingTitle)
                    filter += $",trim=duration={Number(clip.DurationSeconds)}";
                filter += VisualEffectPipeline.BuildInputColorConversion(item.Asset, project.Sequence);
                filter += VisualEffectPipeline.BuildFfmpegFilters(clip, project.Sequence);
                // Some effect filters negotiate a pixel format without alpha.
                // Restore RGBA before rotation so uncovered corners remain
                // transparent when the clip is composited over lower tracks.
                filter += ",format=rgba";
                if (clip.Mask is not null) filter += VisualEffectPipeline.BuildMaskAlpha(clip.Mask, clip,
                    framesPerSecond: project.Sequence.FramesPerSecond);
                ClipTransform transform = clip.Transform;
                if (transform.CropLeft > 0 || transform.CropRight > 0 || transform.CropTop > 0 || transform.CropBottom > 0)
                {
                    // FFmpeg evaluates these expressions after source fitting,
                    // so image and video use the same proportional crop even
                    // when their dimensions differ from the sequence canvas.
                    double horizontalKeep = 1 - transform.CropLeft - transform.CropRight;
                    double verticalKeep = 1 - transform.CropTop - transform.CropBottom;
                    filter += $",crop=w='max(1,iw*{Number(horizontalKeep)})':h='max(1,ih*{Number(verticalKeep)})'" +
                        $":x='iw*{Number(transform.CropLeft)}':y='ih*{Number(transform.CropTop)}'";
                }
                double scaleX = transform.ScaleX;
                double scaleY = transform.ScaleY;
                bool animatedScale = ClipAnimation.HasKeyframes(clip, "ScaleX") || ClipAnimation.HasKeyframes(clip, "ScaleY");
                if (animatedScale || Math.Abs(scaleX - 1) > .001 || Math.Abs(scaleY - 1) > .001)
                {
                    string scaleExpressionX = ClipAnimation.FfmpegExpression(clip, "ScaleX", "t");
                    string scaleExpressionY = ClipAnimation.FfmpegExpression(clip, "ScaleY", "t");
                    filter += $",scale=w='max(1,iw*({scaleExpressionX}))':h='max(1,ih*({scaleExpressionY}))'" +
                        (animatedScale ? ":eval=frame" : string.Empty);
                }
                string titleScale = DynamicTitlePreset.FfmpegScale(clip, "t");
                if (titleScale.Length > 0)
                    filter += $",scale=w='max(1,iw*{titleScale})':h='max(1,ih*{titleScale})':eval=frame";
                bool animatedRotation = ClipAnimation.HasKeyframes(clip, "RotationDegrees");
                string rotationAngle = $"({ClipAnimation.FfmpegExpression(clip, "RotationDegrees", "t")})*PI/180";
                double opacity = clip.Opacity;
                if (ClipAnimation.HasKeyframes(clip, "Opacity"))
                {
                    string opacityExpression = ClipAnimation.FfmpegExpression(clip, "Opacity", $"(N/{fpsText})");
                    string alphaExpression = $"clip({opacityExpression},0,1)";
                    filter += $",geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*{VisualEffectPipeline.EscapeExpression(alphaExpression)}'";
                }
                else if (opacity < 1) filter += $",colorchannelmixer=aa={Number(opacity)}";
                filter += DynamicTitlePreset.FfmpegFade(clip);
                bool incomingCrossfade = BoundaryCrossfade.TryGetPrior(item.Track, clip,
                    project.Sequence.FramesPerSecond, out TimelineClip? previousClip,
                    out double crossfadeDuration);
                // Text titles are already laid out and positioned in a full-frame transparent PNG.
                string localOverlayTime = $"(t-{Number(clip.StartSeconds)})";
                string overlayX = $"(W-w)/2+({ClipAnimation.FfmpegExpression(clip, "PositionX", localOverlayTime)})";
                string overlayY = $"(H-h)/2+({ClipAnimation.FfmpegExpression(clip, "PositionY", localOverlayTime)})";
                // The anchor is normalized inside the cropped visible image.
                // FFmpeg's overlay coordinates use the transformed bounding
                // box, so rotate the center-to-anchor displacement first.
                // iw/ih at overlay time describe the output of crop, scale,
                // and rotate; use the fitted media size as the source basis.
                if (clip.Kind is ClipKind.Video or ClipKind.Image &&
                    (Math.Abs(transform.AnchorX - .5) > .000001 ||
                     Math.Abs(transform.AnchorY - .5) > .000001))
                {
                    double baseWidth = width;
                    double baseHeight = height;
                    if (item.Asset?.Width is > 0 && item.Asset?.Height is > 0)
                    {
                        double fit = Math.Min(width / (double)item.Asset.Width.Value,
                            height / (double)item.Asset.Height.Value);
                        baseWidth = item.Asset.Width.Value * fit;
                        baseHeight = item.Asset.Height.Value * fit;
                    }
                    baseWidth *= Math.Max(.01, 1 - transform.CropLeft - transform.CropRight);
                    baseHeight *= Math.Max(.01, 1 - transform.CropTop - transform.CropBottom);
                    string animatedX = ClipAnimation.FfmpegExpression(clip, "ScaleX", localOverlayTime);
                    string animatedY = ClipAnimation.FfmpegExpression(clip, "ScaleY", localOverlayTime);
                    string radians = $"({ClipAnimation.FfmpegExpression(clip, "RotationDegrees", localOverlayTime)})*PI/180";
                    string pivotX = $"({Number(.5 - transform.AnchorX)}*{Number(baseWidth)}*({animatedX}))";
                    string pivotY = $"({Number(.5 - transform.AnchorY)}*{Number(baseHeight)}*({animatedY}))";
                    overlayX += $"+({pivotX}*cos({radians})-{pivotY}*sin({radians}))";
                    overlayY += $"+({pivotX}*sin({radians})+{pivotY}*cos({radians}))";
                }
                string titleOffset = DynamicTitlePreset.FfmpegHorizontalOffset(clip,
                    width, localOverlayTime);
                if (titleOffset.Length > 0)
                    overlayX += $"+({titleOffset})";
                string titleOffsetY = DynamicTitlePreset.FfmpegVerticalOffset(clip,
                    height, localOverlayTime);
                if (titleOffsetY.Length > 0)
                    overlayY += $"+({titleOffsetY})";
                string creditsOffset = DynamicTitlePreset.FfmpegCreditsOffset(clip, height, localOverlayTime);
                if (creditsOffset.Length > 0)
                    overlayY += $"+({creditsOffset})";
                int transitionIndex = 0;
                foreach (ClipTransition transition in TransitionRenderPlan.ForClip(item.Track, clip,
                    project.Sequence.FramesPerSecond))
                {
                    if (transition.Edge.Equals("Out", StringComparison.OrdinalIgnoreCase) &&
                        outgoingDurations.ContainsKey(clip.Id)) continue;
                    filter += TransitionRenderPlan.BuildFilters(transition, clip.DurationSeconds,
                        "T", $"exporttr{videoCount}x{transitionIndex++}", width, settings.BitDepth == 10 ? 65535 : 255);
                }
                if (animatedRotation || Math.Abs(transform.RotationDegrees) > .001)
                {
                    // Rotate after transition modulation. A fixed diagonal
                    // output canvas keeps animated angles compositable.
                    filter += animatedRotation
                        ? $",rotate=a='{rotationAngle}':ow='hypot(iw,ih)':oh='hypot(iw,ih)':c=none"
                        : $",rotate=a='{rotationAngle}':ow=rotw({rotationAngle}):oh=roth({rotationAngle}):c=none";
                }
                filter += $",setpts=PTS+{Number(clip.StartSeconds)}/TB";
                graph.AppendLine($"[{sourceLabel}]{filter}[{local}];");
                string overlaySource = local;
                if (outgoingDurations.TryGetValue(clip.Id, out double holdDuration))
                {
                    string original = $"voriginal{videoCount}";
                    string held = $"vheld{videoCount}";
                    graph.AppendLine($"[{local}]split=2[{original}untrimmed][{held}];");
                    graph.AppendLine($"[{original}untrimmed]trim=end={Number(clip.StartSeconds + clip.DurationSeconds)}[{original}];");
                    overlaySource = original;
                    string hold = $"vholdframe{videoCount}";
                    double frameSeconds = 1 / project.Sequence.FramesPerSecond;
                    double lastFrame = Math.Max(0, clip.StartSeconds + clip.DurationSeconds - frameSeconds);
                    // Timestamp shifts are rounded to the stream time base.
                    // A one-frame wall-clock trim can therefore contain zero
                    // frames (fractional starts plus a framesync transition
                    // exposed this). FFmpeg 4.4 tpad then clones a null frame
                    // and crashes in av_frame_ref. Keep a bounded tail, reverse
                    // only that tail and take its actual last decoded frame.
                    // Never reverse the entire clip: long sources would buffer
                    // unbounded video in memory just to hold their last frame.
                    string heldMotion = TransitionRenderPlan.HeldBoundary(item.Track, clip,
                        project.Sequence.FramesPerSecond) is { } heldBoundary
                        ? TransitionRenderPlan.BuildHeldFilters(heldBoundary.Transition, heldBoundary.ClipDuration, "T")
                        : string.Empty;
                    if (movingTail > 0)
                        graph.AppendLine($"[{held}]trim=start={Number(clip.StartSeconds + clip.DurationSeconds)}:duration={Number(holdDuration)}," +
                            $"setpts=PTS-STARTPTS{heldMotion},setpts=PTS+{Number(clip.StartSeconds + clip.DurationSeconds)}/TB[{hold}];");
                    else
                    graph.AppendLine($"[{held}]trim=start={Number(Math.Max(0, lastFrame - 2 * frameSeconds))},reverse,trim=end_frame=1," +
                        $"setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration={Number(holdDuration)}," +
                        $"trim=duration={Number(holdDuration)}{heldMotion},setpts=PTS+{Number(clip.StartSeconds + clip.DurationSeconds)}/TB[{hold}];");
                    double holdTime = clip.StartSeconds + clip.DurationSeconds - frameSeconds;
                    string priorX = $"(W-w)/2+{Number(ClipAnimation.Evaluate(clip, "PositionX", holdTime))}";
                    string priorY = $"(H-h)/2+{Number(ClipAnimation.Evaluate(clip, "PositionY", holdTime))}";
                    outgoingFrames[clip.Id] = (hold, priorX, priorY);
                }
                if (incomingCrossfade && previousClip is not null &&
                    outgoingFrames.TryGetValue(previousClip.Id, out var heldFrame))
                {
                    string withPrior = $"vwithprior{videoCount}";
                    graph.AppendLine($"[{videoLabel}][{heldFrame.Label}]overlay=x='{heldFrame.X}':y='{heldFrame.Y}':" +
                        $"eof_action=pass:repeatlast=0:format=auto[{withPrior}];");
                    videoLabel = withPrior;
                }
                string blendMode = VisualEffectPipeline.BlendModeToFfmpeg(clip.BlendMode);
                if (blendMode == "normal")
                    graph.AppendLine($"[{videoLabel}][{overlaySource}]overlay=x='{overlayX}':y='{overlayY}':eof_action=pass:repeatlast=0:format=auto[{output}];");
                else
                {
                    // Place the upper frame on a transparent full-sized
                    // canvas. Preserve its alpha as a separate matte while
                    // blending color channels against the lower composite.
                    string placed = $"placed{videoCount}";
                    string color = $"color{videoCount}";
                    string alpha = $"alpha{videoCount}";
                    string baseForBlend = $"baseblend{videoCount}";
                    string baseForOutput = $"baseoutput{videoCount}";
                    string restored = $"restored{videoCount}";
                    // A fresh graph source is required for each blended clip:
                    // one filter output pad cannot be consumed by two branches.
                    string transparent = $"transparent{videoCount}";
                    graph.AppendLine($"color=c=black@0.0:s={width}x{height}:r={fpsText}:d={durationText},format=rgba[{transparent}];");
                    graph.AppendLine($"[{transparent}][{overlaySource}]overlay=x='{overlayX}':y='{overlayY}':eof_action=pass:repeatlast=0:format=auto,split=2[{placed}][{alpha}];");
                    graph.AppendLine($"[{videoLabel}]split=2[{baseForBlend}][{baseForOutput}];");
                    graph.AppendLine($"[{placed}][{baseForBlend}]blend=all_mode={blendMode}[{color}];");
                    graph.AppendLine($"[{alpha}]alphaextract[{alpha}matte];");
                    graph.AppendLine($"[{color}][{alpha}matte]alphamerge[{restored}];");
                    graph.AppendLine($"[{baseForOutput}][{restored}]overlay=eof_action=pass:repeatlast=0:format=auto[{output}];");
                }
                videoLabel = output;
                videoCount++;
            }

            var audioLabels = new List<string>();
            int audioCount = 0;
            foreach (InputClip item in inputs.Where(item => item.HasAudio && !item.Clip.AudioDetached && !item.Track.IsMuted &&
                (item.Track.Kind == TrackKind.Audio || !item.Track.IsHidden) && !item.Clip.IsMuted))
            {
                TimelineClip clip = item.Clip;
                string label = $"a{audioCount++}";
                string sourceLabel = $"{item.InputIndex}:a:{clip.SourceAudioStreamIndex}";
                string filter;
                if (clip.TimeRemapPoints.Count > 0)
                {
                    sourceLabel = BuildRetimedInput(graph, item.InputIndex, clip,
                        item.InputIndex, audio: true);
                    filter = "anull";
                }
                else
                {
                    double sourceSpan = clip.DurationSeconds * clip.Speed;
                    filter = $"atrim=start={Number(clip.SourceInSeconds)}:duration={Number(sourceSpan)}" +
                        (clip.IsReversed ? ",areverse" : string.Empty) + ",asetpts=PTS-STARTPTS";
                    filter += TempoFilters(clip.Speed);
                }
                string volume = ClipAnimation.HasKeyframes(clip, "Volume")
                    ? $"volume='{ClipAnimation.FfmpegExpression(clip, "Volume", "t")}':eval=frame"
                    : $"volume={Number(Math.Clamp(clip.Volume, 0, 4))}";
                filter += $",atrim=duration={Number(clip.DurationSeconds)},{volume}";
                filter += AudioProcessing.BuildClipFilters(clip);
                filter += AudioProcessing.BuildRemixFadeFilters(clip);
                filter += AudioProcessing.BuildTrackFilters(item.Track);
                filter += MusicDucking.BuildFilter(project, clip);
                filter += $",atrim=duration={Number(clip.DurationSeconds)}";
                int delay = Math.Max(0, (int)Math.Round(clip.StartSeconds * 1000));
                filter += $",adelay={delay}:all=1";
                graph.AppendLine($"[{sourceLabel}]{filter}[{label}];");
                audioLabels.Add(label);
            }
            if (audioLabels.Count > 0)
            {
                graph.AppendLine($"[1:a]{string.Concat(audioLabels.Select(label => $"[{label}]"))}amix=inputs={audioLabels.Count + 1}:duration=longest:dropout_transition=0:normalize=0,atrim=duration={durationText}[audioout];");
            }
            else graph.AppendLine($"[1:a]atrim=duration={durationText}[audioout];");
            string outputColor = VisualEffectPipeline.BuildOutputColorConversion(project.Sequence).TrimStart(',');
            string outputScale = outputWidth == width && outputHeight == height
                ? string.Empty : $",scale={outputWidth}:{outputHeight}:force_original_aspect_ratio=increase:flags=lanczos," +
                    $"crop={outputWidth}:{outputHeight}";
            string outputColorPrefix = outputColor.Length > 0 ? outputColor + "," : string.Empty;
            // A sub-frame selection can fall between every source timestamp.
            // Sample its containing sequence frame rather than feeding an
            // empty stream into clone padding (FFmpeg 4.4 cannot clone null).
            bool subFrameRange = exportDuration < 1 / project.Sequence.FramesPerSecond - .000001;
            double videoRangeStart = subFrameRange
                ? Math.Floor(rangeStart * project.Sequence.FramesPerSecond) / project.Sequence.FramesPerSecond : rangeStart;
            double videoRangeEnd = subFrameRange ? videoRangeStart + 1 / project.Sequence.FramesPerSecond : rangeEnd;
            // The last graph stage trims the fully composited clock, then
            // re-bases audio and video timestamps to zero for the MP4 muxer.
            graph.AppendLine($"[{videoLabel}]{outputColorPrefix}" +
                (subFrameRange ? $"tpad=stop_mode=clone:stop_duration={Number(1 / project.Sequence.FramesPerSecond)}," : string.Empty) +
                $"trim=start={Number(videoRangeStart)}:end={Number(videoRangeEnd)},setpts=PTS-STARTPTS" +
                // Framesync can signal EOF at the last frame's PTS. Extend the
                // last real frame before fps sampling, then bound the result by
                // frame count so neither EOF rounding nor padding changes length.
                $"{outputScale},tpad=stop_mode=clone:stop_duration={Number(1 / outputFps)}," +
                $"fps={outputFpsText}:eof_action=pass,trim=end_frame={Math.Max(1, (long)Math.Ceiling(exportDuration * outputFps - .000001))},format=yuv420p[videoout];");
            graph.AppendLine($"[audioout]atrim=start={Number(rangeStart)}:end={Number(rangeEnd)},asetpts=PTS-STARTPTS[audiorange]");
            string graphText = graph.ToString();
            if (settings.BitDepth == 10)
            {
                // Negotiate high precision before any layer scaling or mixing,
                // then quantize only the final delivery frame to ten bits.
                graphText = graphText.Replace("format=rgba", "format=gbrap16le")
                    .Replace("format=yuv420p", "format=yuv420p10le")
                    .Replace(":format=auto", ":format=yuv420p10");
            }
            await File.WriteAllTextAsync(scriptPath, graphText, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            arguments.AddRange(["-filter_complex_script", scriptPath, "-map", "[videoout]", "-map", "[audiorange]",
                "-t", Number(exportDuration), "-r", outputFpsText,
                "-pix_fmt", settings.BitDepth == 10 ? "yuv420p10le" : "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
            if (previewEncoding is null)
            {
                string codec = settings.VideoCodec switch { "HEVC" => "libx265", "AV1" => "libaom-av1", _ => "libx264" };
                arguments.AddRange(["-c:v", codec, "-threads", "4"]);
                if (settings.VideoCodec == "AV1") arguments.AddRange(["-cpu-used", "8", "-row-mt", "1"]);
                else arguments.AddRange(["-preset", settings.VideoCodec == "HEVC" ? "fast" : "medium"]);
                if (settings.VideoCodec == "HEVC") arguments.AddRange(["-tag:v", "hvc1", "-x265-params", "pools=4:frame-threads=2:log-level=error"]);
                string colors = project.Sequence.OutputColorSpace == "Rec2020" ? "bt2020" : "bt709";
                arguments.AddRange(["-color_primaries", colors, "-color_trc", "bt709", "-colorspace", colors == "bt2020" ? "bt2020nc" : "bt709"]);
                if (settings.VideoBitrateKbps is int bitrate)
                    arguments.AddRange(["-b:v", $"{bitrate}k", "-maxrate", $"{bitrate * 13 / 10}k",
                        "-bufsize", $"{bitrate * 2}k"]);
                else arguments.AddRange(["-crf", settings.VideoCodec == "AV1" ? "28" : "20"]);
                arguments.Add(temporaryOutput);
                await RunEncoderAsync(encoder, arguments, exportDuration, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                // Only library demos opt into the low-latency profile. Keep
                // the filter graph identical when falling back: a hardware
                // session rejection must never change the effect itself.
                List<string> previewArguments = [.. arguments, .. previewEncoding.VideoArguments, temporaryOutput];
                try
                {
                    await RunEncoderAsync(encoder, previewArguments, exportDuration, progress, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (ExportException exception) when (exception.StageCode == ExportStageCode.Encode &&
                    previewEncoding.IsHardwareAccelerated && !cancellationToken.IsCancellationRequested)
                {
                    Debug.WriteLine($"PREVIEW-GPU-FALLBACK {previewEncoding.EncoderName}: {exception.Message}");
                    previewArguments = [.. arguments, .. PreviewEncodingProfile.Software.VideoArguments, temporaryOutput];
                    await RunEncoderAsync(encoder, previewArguments, exportDuration, progress, cancellationToken)
                        .ConfigureAwait(false);
                    // A malformed graph also fails on software and should
                    // not poison the cached hardware capability result.
                    previewEncoding.MarkUnavailable();
                }
            }
            try { File.Move(temporaryOutput, fullOutput, overwrite: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { throw new ExportException(ExportStageCode.Finalize, "视频已渲染，但无法写入目标文件。", exception); }
            progress?.Report(1);
        }
        catch (ExportException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ExportException(ExportStageCode.Prepare, "准备视频导出时发生文件错误。", exception);
        }
        finally
        {
            // This API may be awaited directly from an interactive window.
            // Keep temp cleanup off its captured dispatcher as well.
            await Task.Run(() =>
            {
                try { File.Delete(scriptPath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { Debug.WriteLine($"EXPORT-CLEANUP: {exception}"); }
                try { File.Delete(temporaryOutput); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { Debug.WriteLine($"EXPORT-CLEANUP: {exception}"); }
            }).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ProbeHasAudioAsync(string encoder, string path, CancellationToken cancellationToken)
    {
        string probe = Path.Combine(Path.GetDirectoryName(encoder) ?? string.Empty, "ffprobe.exe");
        if (!File.Exists(probe)) probe = "ffprobe";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(probe)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            } };
            process.StartInfo.ArgumentList.Add("-v"); process.StartInfo.ArgumentList.Add("error");
            process.StartInfo.ArgumentList.Add("-select_streams"); process.StartInfo.ArgumentList.Add("a");
            process.StartInfo.ArgumentList.Add("-show_entries"); process.StartInfo.ArgumentList.Add("stream=index");
            process.StartInfo.ArgumentList.Add("-of"); process.StartInfo.ArgumentList.Add("csv=p=0");
            process.StartInfo.ArgumentList.Add(path);
            if (!process.Start())
                throw new ExportException(ExportStageCode.EncoderMissing, "无法启动 FFprobe 音频流检查工具。");
            using var stopProcess = timeout.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception or NotSupportedException) { }
            });
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string output = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { throw new ExportException(ExportStageCode.Prepare, "检查视频音轨超时，请检查源文件。"); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            throw new ExportException(ExportStageCode.EncoderMissing,
                "无法启动 FFprobe 音频流检查工具。", exception);
        }
    }

    private static async Task RunEncoderAsync(string encoder, IReadOnlyList<string> arguments, double duration,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(encoder)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        } };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start())
                throw new ExportException(ExportStageCode.EncoderMissing, "无法启动 FFmpeg 编码器。");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        { throw new ExportException(ExportStageCode.EncoderMissing, "找不到 FFmpeg 编码器。请修复安装，或将 ffmpeg.exe 添加到 PATH。", exception); }
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or NotSupportedException) { }
        });
        var errors = new StringBuilder();
        Task errorReader = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (errors.Length > 6000) errors.Remove(0, errors.Length - 4000);
                errors.AppendLine(line);
            }
        }, cancellationToken);
        while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("out_time_ms=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan("out_time_ms=".Length), out long microseconds))
                progress?.Report(Math.Clamp(microseconds / (duration * 1_000_000), 0, .99));
        }
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await errorReader.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string code = $"0x{unchecked((uint)process.ExitCode):X8}";
            string? diagnostic = await SaveEncoderFailureAsync(encoder, arguments, process.ExitCode, errors.ToString()).ConfigureAwait(false);
            string detail = errors.ToString().Trim();
            if (detail.Length == 0) detail = process.ExitCode == unchecked((int)0xC0000005)
                ? "FFmpeg 发生原生内存访问冲突。" : "编码器异常退出，未输出错误文本。";
            throw new ExportException(ExportStageCode.Encode,
                $"视频编码失败（退出码 {process.ExitCode} / {code}）。\n{detail}" +
                (diagnostic is null ? "" : $"\n诊断记录：{diagnostic}"));
        }
    }

    private static async Task<string?> SaveEncoderFailureAsync(string encoder, IReadOnlyList<string> arguments,
        int exitCode, string errors)
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TANGERINE-VideoEditor", "diagnostics", "export");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
            int graphIndex = arguments.ToList().IndexOf("-filter_complex_script");
            string graph = graphIndex >= 0 && graphIndex + 1 < arguments.Count && File.Exists(arguments[graphIndex + 1])
                ? await File.ReadAllTextAsync(arguments[graphIndex + 1]).ConfigureAwait(false) : "";
            await File.WriteAllTextAsync(path, $"Encoder: {encoder}\nExit code: {exitCode} / 0x{unchecked((uint)exitCode):X8}\n" +
                "Arguments (JSON): " + System.Text.Json.JsonSerializer.Serialize(arguments) +
                "\n\nFFmpeg stderr:\n" + errors + "\n\nFilter graph:\n" + graph).ConfigureAwait(false);
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Diagnostic persistence is best effort; never replace the real
            // encoder failure with a secondary log-directory failure.
            Debug.WriteLine("EXPRT-DIAGNOSTIC: " + exception.Message); return null;
        }
    }

    /// <summary>
    /// Splits one input into speed intervals on the clip clock and joins them
    /// again before visual or audio effects are applied. SourceOffset is the
    /// same integral used by the preview, including reverse playback.
    /// </summary>
    private static string BuildRetimedInput(StringBuilder graph, int inputIndex, TimelineClip clip,
        int serial, bool audio)
    {
        string prefix = audio ? "ra" : "rv";
        string mediaType = audio ? $"a:{clip.SourceAudioStreamIndex}" : "v";
        string result = $"{prefix}{serial}joined";
        var boundaries = new List<double> { 0 };
        double previousPoint = double.NegativeInfinity;
        if (clip.TimeRemapPoints.Any(point => !double.IsFinite(point.TimeSeconds) ||
            !double.IsFinite(point.Speed)))
            throw new ExportException(ExportStageCode.Validate, $"片段“{clip.Name}”的时间重映射节点无效。");
        foreach (ClipTimeRemapPoint point in clip.TimeRemapPoints.OrderBy(point => point.TimeSeconds))
        {
            if (!double.IsFinite(point.TimeSeconds) || !double.IsFinite(point.Speed) ||
                point.TimeSeconds < 0 || point.TimeSeconds >= clip.DurationSeconds ||
                point.Speed < .05 || point.Speed > 32)
                throw new ExportException(ExportStageCode.Validate, $"片段“{clip.Name}”的时间重映射节点无效。");
            if (point.TimeSeconds - previousPoint < .000001)
                throw new ExportException(ExportStageCode.Validate, $"片段“{clip.Name}”存在重叠的速度节点。");
            previousPoint = point.TimeSeconds;
            if (point.TimeSeconds > 0 && point.TimeSeconds > boundaries[^1] + .000001)
                boundaries.Add(point.TimeSeconds);
        }
        boundaries.Add(clip.DurationSeconds);
        var labels = new List<string>();
        var splitLabels = new List<string>();
        if (boundaries.Count > 2)
        {
            for (int index = 0; index < boundaries.Count - 1; index++)
                splitLabels.Add($"{prefix}{serial}input{index}");
            graph.AppendLine($"[{inputIndex}:{mediaType}]{(audio ? "asplit" : "split")}" +
                $"={splitLabels.Count}{string.Concat(splitLabels.Select(label => $"[{label}]"))};");
        }
        for (int index = 0; index < boundaries.Count - 1; index++)
        {
            double localStart = boundaries[index];
            double localEnd = boundaries[index + 1];
            double speed = ClipTimeMap.SpeedAt(clip, localStart);
            double sourceStart = clip.SourceInSeconds + (clip.IsReversed
                ? ClipTimeMap.SourceOffset(clip, clip.DurationSeconds) - ClipTimeMap.SourceOffset(clip, localEnd)
                : ClipTimeMap.SourceOffset(clip, localStart));
            double sourceSpan = (localEnd - localStart) * speed;
            string label = $"{prefix}{serial}s{index}";
            if (!double.IsFinite(sourceStart) || !double.IsFinite(sourceSpan) ||
                sourceStart < 0 || sourceSpan <= 0)
                throw new ExportException(ExportStageCode.Validate,
                    $"片段“{clip.Name}”的时间重映射超出有效源范围。");
            string filter = audio
                ? $"atrim=start={Number(sourceStart)}:duration={Number(sourceSpan)}" +
                    (clip.IsReversed ? ",areverse" : string.Empty) + ",asetpts=PTS-STARTPTS" + TempoFilters(speed)
                : $"trim=start={Number(sourceStart)}:duration={Number(sourceSpan)}" +
                    (clip.IsReversed ? ",reverse" : string.Empty) +
                    $",setpts=(PTS-STARTPTS)/{Number(speed)}";
            // Leave the segment timestamps at zero for concat. A final clip
            // trim after effects bounds any filter tail to the edit duration.
            filter += audio ? ",asetpts=PTS-STARTPTS" : ",setpts=PTS-STARTPTS";
            string inputLabel = splitLabels.Count > 0 ? splitLabels[index] : $"{inputIndex}:{mediaType}";
            graph.AppendLine($"[{inputLabel}]{filter}[{label}];");
            labels.Add(label);
        }
        if (labels.Count == 1)
            graph.AppendLine($"[{labels[0]}]{(audio ? "anull" : "null")}[{result}];");
        else graph.AppendLine($"{string.Concat(labels.Select(label => $"[{label}]"))}" +
            $"concat=n={labels.Count}:v={(audio ? 0 : 1)}:a={(audio ? 1 : 0)}[{result}];");
        return result;
    }

    public static string TempoFilters(double speed)
    {
        var filters = new StringBuilder();
        while (speed > 2) { filters.Append(",atempo=2"); speed /= 2; }
        while (speed < .5) { filters.Append(",atempo=0.5"); speed *= 2; }
        filters.Append(",atempo=").Append(Number(speed));
        return filters.ToString();
    }

    private static void ValidateVisualTransform(TimelineClip clip)
    {
        ClipTransform? transform = clip.Transform;
        if (transform is null ||
            !double.IsFinite(transform.PositionX) || !double.IsFinite(transform.PositionY) ||
            !double.IsFinite(transform.ScaleX) || transform.ScaleX < .05 || transform.ScaleX > 8 ||
            !double.IsFinite(transform.ScaleY) || transform.ScaleY < .05 || transform.ScaleY > 8 ||
            !double.IsFinite(transform.RotationDegrees) || Math.Abs(transform.RotationDegrees) > 36000 ||
            !double.IsFinite(transform.CropLeft) || transform.CropLeft < 0 || transform.CropLeft > .95 ||
            !double.IsFinite(transform.CropRight) || transform.CropRight < 0 || transform.CropRight > .95 ||
            !double.IsFinite(transform.CropTop) || transform.CropTop < 0 || transform.CropTop > .95 ||
            !double.IsFinite(transform.CropBottom) || transform.CropBottom < 0 || transform.CropBottom > .95 ||
            transform.CropLeft + transform.CropRight >= 1 || transform.CropTop + transform.CropBottom >= 1 ||
            !double.IsFinite(clip.Opacity) || clip.Opacity < 0 || clip.Opacity > 1)
            throw new ExportException(ExportStageCode.Validate, $"片段“{clip.Name}”的画面变换或裁切范围无效。");
    }

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static string? SafeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }
    private static string NormalizeColor(string? color)
    {
        string value = color?.Trim().TrimStart('#') ?? "000000";
        return value.Length is 6 or 8 && value.All(Uri.IsHexDigit) ? $"0x{value}" : "black";
    }
}
