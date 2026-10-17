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
    double? EndSeconds = null);

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
        CancellationToken cancellationToken = default, VideoExportSettings? settings = null)
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
        string encoder = ResolveEncoderPath();
        string temporaryOutput = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(fullOutput)}.{Guid.NewGuid():N}.mp4");
        string scriptPath = Path.Combine(Path.GetTempPath(), $"tangerine-{Guid.NewGuid():N}.ffgraph");
        try
        {
            var inputs = new List<InputClip>();
            var arguments = new List<string>
            {
                "-hide_banner", "-y", "-nostdin", "-loglevel", "error",
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
                    double sourceSpan = clip.DurationSeconds * clip.Speed;
                    filter = $"trim=start={Number(clip.SourceInSeconds)}:duration={Number(sourceSpan)}" +
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
                bool transitionRotation = false;
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
                bool dissolveBoundary = incomingCrossfade &&
                    BoundaryCrossfade.TryGetTransition(item.Track, clip,
                        project.Sequence.FramesPerSecond, out _, out ClipTransition? boundaryTransition,
                        out _) && boundaryTransition?.PresetId == "transition.crossfade";
                if (dissolveBoundary)
                    filter += $",fade=t=in:st=0:d={Number(crossfadeDuration)}:alpha=1";
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
                foreach (ClipTransition transition in clip.Transitions)
                {
                    if (transition.Edge.Equals("Out", StringComparison.OrdinalIgnoreCase) &&
                        outgoingDurations.ContainsKey(clip.Id)) continue;
                    if (transition.PresetId is "transition.crossfade" or "transition.dissolve" &&
                        transition.Edge.Equals("In", StringComparison.OrdinalIgnoreCase) &&
                        dissolveBoundary) continue;
                    double transitionDuration = Math.Min(clip.DurationSeconds / 2, Math.Max(0, transition.DurationSeconds));
                    if (transitionDuration < .001) continue;
                    bool outgoing = transition.Edge.Equals("Out", StringComparison.OrdinalIgnoreCase);
                    double startTime = outgoing ? clip.DurationSeconds - transitionDuration : 0;
                    string transitionProgress = outgoing
                        ? $"({Number(clip.DurationSeconds)}-N/{fpsText})/{Number(transitionDuration)}"
                        : $"(N/{fpsText})/{Number(transitionDuration)}";
                    double strength = TransitionSettings.Strength(transition);
                    string expression = transition.PresetId switch
                    {
                        "transition.wipe-left" => $"lte(X/W,clip({transitionProgress},0,1))",
                        "transition.wipe-right" => $"gte(X/W,1-clip({transitionProgress},0,1))",
                        "transition.wipe-up" => $"lte(Y/H,clip({transitionProgress},0,1))",
                        "transition.wipe-down" => $"gte(Y/H,1-clip({transitionProgress},0,1))",
                        "transition.circle" or "transition.circle-open" => $"lte(hypot(X-W/2,Y-H/2)/hypot(W/2,H/2),clip({transitionProgress},0,1))",
                        "transition.diamond" => $"lte((abs(X-W/2)/(W/2)+abs(Y-H/2)/(H/2))/2,clip({transitionProgress},0,1))",
                        "transition.split" => $"lte(abs(X-W/2)/(W/2),clip({transitionProgress},0,1))",
                        "transition.page" => $"lte(X/W+{Number(.18 * strength)}*sin(PI*Y/H),clip({transitionProgress},0,1)*{Number(1 + .18 * strength)})",
                        "transition.glitch" => $"lte(X/W,clip(({transitionProgress})+{Number(.12 * strength)}*sin(31*Y/H+18*N/{fpsText}),0,1))",
                        "transition.film" or "transition.shake" => $"lte(Y/H,clip(({transitionProgress})+{Number(.08 * strength)}*sin(10*X/W+N/{fpsText}*16),0,1))",
                        _ => string.Empty
                    };
                    if (expression.Length > 0)
                    {
                        filter += $",geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*{VisualEffectPipeline.EscapeExpression(expression)}'";
                    }
                    else if (transition.PresetId is "transition.slide-left" or "transition.slide-right" or
                        "transition.push-up" or "transition.push-down")
                    {
                        string position = $"min(max((t-{Number(clip.StartSeconds + startTime)})/{Number(transitionDuration)},0),1)";
                        if (outgoing) position = $"(1-{position})";
                        switch (transition.PresetId)
                        {
                            case "transition.slide-left": overlayX = $"{overlayX}+(1-{position})*W"; break;
                            case "transition.slide-right": overlayX = $"{overlayX}-(1-{position})*W"; break;
                            case "transition.push-up": overlayY = $"{overlayY}+(1-{position})*H"; break;
                            case "transition.push-down": overlayY = $"{overlayY}-(1-{position})*H"; break;
                        }
                    }
                    else if (transition.PresetId is "transition.zoom-in" or "transition.zoom-out" or "transition.zoom")
                    {
                        // Scale evaluates at each clip-local frame. The alpha
                        // fade exposes the neighboring clip while the picture
                        // grows or shrinks around its existing center.
                        string zoomProgress = outgoing
                            ? $"min(max(({Number(clip.DurationSeconds)}-t)/{Number(transitionDuration)},0),1)"
                            : $"min(max(t/{Number(transitionDuration)},0),1)";
                        double zoomRange = .5 * strength;
                        string zoom = transition.PresetId is "transition.zoom-in" or "transition.zoom"
                            ? $"({Number(1 - zoomRange)}+{Number(zoomRange)}*{zoomProgress})"
                            : $"({Number(1 + zoomRange)}-{Number(zoomRange)}*{zoomProgress})";
                        filter += $",scale=w='max(1,iw*{zoom})':h='max(1,ih*{zoom})':eval=frame";
                        filter += $",fade=t={(outgoing ? "out" : "in")}:st={Number(startTime)}:d={Number(transitionDuration)}:alpha=1";
                    }
                    else if (transition.PresetId is "transition.spin" or "transition.rotate")
                    {
                        string transitionPosition = outgoing
                            ? $"min(max(({Number(clip.DurationSeconds)}-t)/{Number(transitionDuration)},0),1)"
                            : $"min(max(t/{Number(transitionDuration)},0),1)";
                        rotationAngle += $"+(1-{transitionPosition})*{Number(2 * TransitionSettings.Turns(transition))}*PI";
                        transitionRotation = true;
                        filter += $",fade=t={(outgoing ? "out" : "in")}:st={Number(startTime)}:d={Number(transitionDuration)}:alpha=1";
                    }
                    else if (transition.PresetId is "transition.blur" or "transition.blur-dissolve")
                    {
                        // gblur sigma is a numeric filter option rather than
                        // a per-frame expression. Its enabled interval gives
                        // this transition a visibly softened boundary.
                        string blurWindow = $"between(t,{Number(startTime)},{Number(startTime + transitionDuration)})";
                        if (strength >= .01)
                            filter += $",gblur=sigma={Number(9 * strength)}:enable='{VisualEffectPipeline.EscapeExpression(blurWindow)}'";
                        filter += $",fade=t={(outgoing ? "out" : "in")}:st={Number(startTime)}:d={Number(transitionDuration)}:alpha=1";
                    }
                    else if (transition.PresetId is "transition.flash" or "transition.light-leak" or
                        "transition.dip-black" or "transition.dip-white")
                    {
                        string transitionPosition = outgoing
                            ? $"min(max(({Number(clip.DurationSeconds)}-t)/{Number(transitionDuration)},0),1)"
                            : $"min(max(t/{Number(transitionDuration)},0),1)";
                        double amount = transition.PresetId switch
                        {
                            "transition.flash" => Math.Min(1, .85 * strength),
                            "transition.light-leak" => Math.Min(1, .55 * strength),
                            "transition.dip-white" => 1, _ => -1
                        };
                        filter += $",eq=brightness='(1-{transitionPosition})*{Number(amount)}':eval=frame";
                        filter += $",fade=t={(outgoing ? "out" : "in")}:st={Number(startTime)}:d={Number(transitionDuration)}:alpha=1";
                    }
                    else
                    {
                        filter += $",fade=t={(outgoing ? "out" : "in")}:st={Number(startTime)}:d={Number(transitionDuration)}:alpha=1";
                    }
                }
                if (animatedRotation || transitionRotation || Math.Abs(transform.RotationDegrees) > .001)
                {
                    // Rotate after transition modulation. A fixed diagonal
                    // output canvas keeps animated angles compositable.
                    filter += animatedRotation || transitionRotation
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
                    graph.AppendLine($"[{local}]split=2[{original}][{held}];");
                    overlaySource = original;
                    string hold = $"vholdframe{videoCount}";
                    double frameSeconds = 1 / project.Sequence.FramesPerSecond;
                    double lastFrame = Math.Max(0, clip.StartSeconds + clip.DurationSeconds - frameSeconds);
                    // The transparent, fully processed outgoing picture is
                    // cloned into the incoming interval. Its placement uses
                    // the same overlay coordinates as the ordinary clip.
                    graph.AppendLine($"[{held}]trim=start={Number(lastFrame)}:duration={Number(frameSeconds)}," +
                        $"setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration={Number(holdDuration)}," +
                        $"trim=duration={Number(holdDuration)},setpts=PTS+{Number(clip.StartSeconds + clip.DurationSeconds)}/TB[{hold}];");
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
            foreach (InputClip item in inputs.Where(item => item.HasAudio && !item.Track.IsMuted &&
                (item.Track.Kind == TrackKind.Audio || !item.Track.IsHidden) && !item.Clip.IsMuted))
            {
                TimelineClip clip = item.Clip;
                string label = $"a{audioCount++}";
                string sourceLabel = $"{item.InputIndex}:a";
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
            // The last graph stage trims the fully composited clock, then
            // re-bases audio and video timestamps to zero for the MP4 muxer.
            graph.AppendLine($"[{videoLabel}]{outputColorPrefix}" +
                $"trim=start={Number(rangeStart)}:end={Number(rangeEnd)},setpts=PTS-STARTPTS" +
                $"{outputScale},fps={outputFpsText},format=yuv420p[videoout];");
            graph.AppendLine($"[audioout]atrim=start={Number(rangeStart)}:end={Number(rangeEnd)},asetpts=PTS-STARTPTS[audiorange]");
            await File.WriteAllTextAsync(scriptPath, graph.ToString(), new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            arguments.AddRange(["-filter_complex_script", scriptPath, "-map", "[videoout]", "-map", "[audiorange]",
                "-t", Number(exportDuration), "-r", outputFpsText, "-c:v", "libx264", "-preset", "medium",
                "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
            if (settings.VideoBitrateKbps is int bitrate)
                arguments.AddRange(["-b:v", $"{bitrate}k", "-maxrate", $"{bitrate * 13 / 10}k",
                    "-bufsize", $"{bitrate * 2}k"]);
            else arguments.AddRange(["-crf", "20"]);
            arguments.Add(temporaryOutput);

            await RunEncoderAsync(encoder, arguments, exportDuration, progress, cancellationToken)
                .ConfigureAwait(false);
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
            throw new ExportException(ExportStageCode.Encode, $"视频编码失败。\n{errors.ToString().Trim()}");
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
        string mediaType = audio ? "a" : "v";
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

    private static string TempoFilters(double speed)
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
