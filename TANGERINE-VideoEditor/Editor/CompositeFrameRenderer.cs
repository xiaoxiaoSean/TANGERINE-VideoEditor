using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Builds a single local sequence frame from visible picture tracks. The
/// short-lived FFmpeg graph is evaluated at the playhead, including masks,
/// blending, and adjustment layers. WPF title overlays remain in their
/// existing interactive preview layer above the returned bitmap.
/// </summary>
public static class CompositeFrameRenderer
{
    private sealed record Layer(TimelineClip Clip, MediaAsset? Asset, int Input,
        bool HeldPrior, double SampleTime, bool MovingTail);

    public static async Task<BitmapSource> RenderFrameAsync(ProjectDocument project, double timelineSeconds,
        int previewWidth, int previewHeight, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? titleImagePaths = null,
        string previewGpuMode = "自动（GPU 优先）")
    {
        // The preview normally stays below 4096 pixels, but the same renderer
        // also writes a full-resolution still frame for 4K and 8K sequences.
        if (!double.IsFinite(timelineSeconds) || timelineSeconds < 0 || previewWidth < 1 || previewHeight < 1 ||
            previewWidth > 8192 || previewHeight > 8192)
            throw new ClipFrameException(ClipFrameStageCode.Validate, "合成预览画幅或时间无效。");
        string[] arguments = await Task.Run(() => Prepare(project, timelineSeconds, previewWidth, previewHeight,
                titleImagePaths),
            cancellationToken).ConfigureAwait(false);
        var start = new ProcessStartInfo(TimelineExporter.ResolveEncoderPath())
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        // Hardware decode is selected before the first media input. The
        // automatic mode lets FFmpeg choose a supported device and falls back
        // to software decoding when the driver has no usable accelerator.
        int firstInput = start.ArgumentList.IndexOf("-i");
        if (firstInput >= 0)
            foreach (string argument in PreviewHardwareAcceleration.Arguments(previewGpuMode).Reverse())
                start.ArgumentList.Insert(firstInput, argument);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new ClipFrameException(ClipFrameStageCode.EncoderMissing,
                "无法启动本地合成预览编码器。");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new ClipFrameException(ClipFrameStageCode.EncoderMissing, "找不到本地 FFmpeg。", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new ClipFrameException(ClipFrameStageCode.EncoderMissing, "无法启动本地 FFmpeg。", exception);
        }
        using var cancellation = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var output = new MemoryStream();
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            string errorText = await errorTask;
            if (process.ExitCode != 0 || output.Length == 0)
                throw new ClipFrameException(ClipFrameStageCode.Filter,
                    $"画面合成失败：{errorText.Trim()}");
            byte[] bytes = output.ToArray();
            var decoded = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    using var stream = new MemoryStream(bytes, writable: false);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    decoded.TrySetResult(bitmap);
                }
                catch (Exception exception) { decoded.TrySetException(exception); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await decoded.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is not ClipFrameException)
        {
            throw new ClipFrameException(ClipFrameStageCode.Decode, "无法读取合成预览帧。", exception);
        }
    }

    private static string[] Prepare(ProjectDocument project, double time,
        int width, int height, IReadOnlyDictionary<string, string>? titleImagePaths)
    {
        try
        {
            var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
            string background = (project.Sequence.BackgroundColor ?? "000000").Trim().TrimStart('#');
            if (background.Length != 6 || !background.All(Uri.IsHexDigit)) background = "000000";
            int sequenceWidth = project.Sequence.Width;
            int sequenceHeight = project.Sequence.Height;
            if (sequenceWidth < 1 || sequenceHeight < 1 ||
                sequenceWidth > 8192 || sequenceHeight > 8192)
                throw new ClipFrameException(ClipFrameStageCode.Validate, "序列画幅无效。");
            // Compose at sequence resolution before the final high-quality downsample.
            // Fitting each layer to the monitor first loses detail when a clip
            // is enlarged, and gives effects different pixel units from export.
            int canvasWidth = Math.Max(sequenceWidth, width);
            int canvasHeight = Math.Max(sequenceHeight, height);
            double positionScaleX = canvasWidth / (double)sequenceWidth;
            double positionScaleY = canvasHeight / (double)sequenceHeight;
            arguments.AddRange(["-f", "lavfi", "-i",
                $"color=c=0x{background}:s={canvasWidth}x{canvasHeight}:r=1:d=1"]);
            int input = 1;
            var layers = new List<Layer>();
            var heldPriorIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (TimelineTrack candidateTrack in project.Tracks.Where(item =>
                !item.IsHidden && item.Kind != TrackKind.Audio))
            foreach (TimelineClip incoming in candidateTrack.Clips)
                if (BoundaryCrossfade.TryGetPrior(candidateTrack, incoming,
                    project.Sequence.FramesPerSecond, out TimelineClip? prior, out double length) &&
                    prior is not null && time >= incoming.StartSeconds &&
                    time < incoming.StartSeconds + length)
                    heldPriorIds.Add(prior.Id);
            foreach (var visual in project.Tracks.Where(item => !item.IsHidden && item.Kind != TrackKind.Audio)
                .SelectMany(track => track.Clips.Select(clip => new { track, clip }))
                .OrderBy(item => item.clip.Kind == ClipKind.Text ? 1 : 0)
                .ThenByDescending(item => item.track.Order)
                .ThenBy(item => item.clip.StartSeconds))
            {
                TimelineTrack track = visual.track;
                TimelineClip clip = visual.clip;
                if (!(
                (time >= clip.StartSeconds && time < clip.StartSeconds + clip.DurationSeconds ||
                    heldPriorIds.Contains(clip.Id)) &&
                (clip.Kind is (ClipKind.Video or ClipKind.Image or ClipKind.Color or ClipKind.Adjustment) ||
                    (clip.Kind == ClipKind.Text && titleImagePaths?.ContainsKey(clip.Id) == true)))) continue;
                MediaAsset? asset = project.Media.FirstOrDefault(item => item.Id == clip.MediaAssetId);
                bool heldPrior = heldPriorIds.Contains(clip.Id) &&
                    time >= clip.StartSeconds + clip.DurationSeconds;
                bool movingTail = heldPrior && TransitionRenderPlan.HeldBoundary(track, clip, project.Sequence.FramesPerSecond) is { } boundary &&
                    BoundaryCrossfade.TryGetPrior(track, track.Clips.First(c => c.StartSeconds == boundary.Start), project.Sequence.FramesPerSecond, out _, out double tail) &&
                    TransitionHandles.CanMove(clip, asset, tail);
                double sampleTime = heldPrior && !movingTail
                    ? clip.StartSeconds + clip.DurationSeconds - 1 / project.Sequence.FramesPerSecond
                    : time;
                if (clip.Kind == ClipKind.Adjustment)
                {
                    layers.Add(new Layer(clip, null, -1, false, time, false));
                    continue;
                }
                if (clip.Kind == ClipKind.Color)
                {
                    string hex = clip.Color?.Trim().TrimStart('#') ?? "000000";
                    string color = hex.Length == 6 && hex.All(Uri.IsHexDigit) ? $"0x{hex}" : "black";
                    arguments.AddRange(["-f", "lavfi", "-i", $"color=c={color}:s={canvasWidth}x{canvasHeight}:r=1:d=1"]);
                }
                else
                {
                    string? path = null;
                    if (clip.Kind == ClipKind.Text)
                    {
                        if (titleImagePaths is not null) titleImagePaths.TryGetValue(clip.Id, out path);
                    }
                    else path = asset?.EffectivePreviewPath;
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                        throw new ClipFrameException(ClipFrameStageCode.Validate, $"找不到片段“{clip.Name}”的预览素材。");
                    if (clip.Kind == ClipKind.Video)
                    {
                        arguments.Add("-ss");
                        arguments.Add(VisualEffectPipeline.Number(movingTail ? TransitionHandles.Sample(clip, time) : ClipTimeMap.SourceTime(clip, sampleTime)));
                    }
                    else arguments.AddRange(["-loop", "1", "-framerate", "1"]);
                    arguments.AddRange(["-i", path]);
                }
                layers.Add(new Layer(clip, asset, input++, heldPrior, sampleTime, movingTail));
            }
            var graph = new StringBuilder("[0:v]format=rgba[base0];\n");
            string current = "base0";
            int index = 0;
            foreach (Layer layer in layers)
            {
                TimelineClip clip = layer.Clip;
                string next = $"base{++index}";
                string local = $"local{index}";
                double sampleTime = layer.SampleTime;
                double localSeconds = sampleTime - clip.StartSeconds;
                if (clip.Kind == ClipKind.Adjustment)
                {
                    string original = $"original{index}";
                    string processing = $"processing{index}";
                    graph.AppendLine($"[{current}]split=2[{original}][{processing}];");
                    string adjustmentFilters = "format=rgba" + VisualEffectPipeline.BuildFfmpegFilters(clip, project.Sequence, localSeconds);
                    if (clip.Mask is not null) adjustmentFilters += VisualEffectPipeline.BuildMaskAlpha(clip.Mask, clip, localSeconds);
                    adjustmentFilters += $",colorchannelmixer=aa={VisualEffectPipeline.Number(Math.Clamp(ClipAnimation.Evaluate(clip, "Opacity", sampleTime), 0, 1))}";
                    graph.AppendLine($"[{processing}]{adjustmentFilters}[{local}];");
                    graph.AppendLine($"[{original}][{local}]overlay=format=auto[{next}];");
                    current = next;
                    continue;
                }
                string filters = clip.Kind is (ClipKind.Color or ClipKind.Text) ? $"scale={canvasWidth}:{canvasHeight}" :
                    $"scale={canvasWidth}:{canvasHeight}:force_original_aspect_ratio=decrease:flags=lanczos";
                filters += ",format=rgba" + VisualEffectPipeline.BuildInputColorConversion(layer.Asset, project.Sequence) +
                    VisualEffectPipeline.BuildFfmpegFilters(clip, project.Sequence, localSeconds) + ",format=rgba";
                if (clip.Mask is not null) filters += VisualEffectPipeline.BuildMaskAlpha(clip.Mask, clip, localSeconds);
                ClipTransform transform = clip.Transform;
                if (transform.CropLeft + transform.CropRight > 0 || transform.CropTop + transform.CropBottom > 0)
                    filters += $",crop=w='max(1,iw*{VisualEffectPipeline.Number(1 - transform.CropLeft - transform.CropRight)})'" +
                        $":h='max(1,ih*{VisualEffectPipeline.Number(1 - transform.CropTop - transform.CropBottom)})'" +
                        $":x='iw*{VisualEffectPipeline.Number(transform.CropLeft)}':y='ih*{VisualEffectPipeline.Number(transform.CropTop)}'";
                double sx = ClipAnimation.Evaluate(clip, "ScaleX", sampleTime);
                double sy = ClipAnimation.Evaluate(clip, "ScaleY", sampleTime);
                double croppedWidth = canvasWidth;
                double croppedHeight = canvasHeight;
                // FFmpeg scale=decrease retains source aspect ratio. Use the
                // fitted source dimensions when translating an authored pivot.
                if (clip.Kind is ClipKind.Video or ClipKind.Image &&
                    layer.Asset?.Width is > 0 && layer.Asset?.Height is > 0)
                {
                    double fit = Math.Min(canvasWidth / (double)layer.Asset.Width.Value,
                        canvasHeight / (double)layer.Asset.Height.Value);
                    croppedWidth = layer.Asset.Width.Value * fit;
                    croppedHeight = layer.Asset.Height.Value * fit;
                }
                croppedWidth *= Math.Max(.01, 1 - transform.CropLeft - transform.CropRight);
                croppedHeight *= Math.Max(.01, 1 - transform.CropTop - transform.CropBottom);
                double anchorX = Math.Clamp(transform.AnchorX, 0, 1);
                double anchorY = Math.Clamp(transform.AnchorY, 0, 1);
                double pivotDeltaX = (.5 - anchorX) * croppedWidth * Math.Clamp(sx, .05, 8);
                double pivotDeltaY = (.5 - anchorY) * croppedHeight * Math.Clamp(sy, .05, 8);
                filters += $",scale=w='max(1,iw*{VisualEffectPipeline.Number(Math.Clamp(sx, .05, 8))})'" +
                    $":h='max(1,ih*{VisualEffectPipeline.Number(Math.Clamp(sy, .05, 8))})':flags=lanczos";
                double rotation = ClipAnimation.Evaluate(clip, "RotationDegrees", sampleTime) * Math.PI / 180;
                double opacity = Math.Clamp(ClipAnimation.Evaluate(clip, "Opacity", sampleTime), 0, 1);
                TimelineTrack transitionTrack = project.Tracks.First(track => track.Clips.Contains(clip));
                int transitionIndex = 0;
                foreach (ClipTransition transition in TransitionRenderPlan.ForClip(transitionTrack, clip,
                    project.Sequence.FramesPerSecond))
                {
                    if (layer.HeldPrior && transition.Edge.Equals("Out", StringComparison.OrdinalIgnoreCase)) continue;
                    filters += TransitionRenderPlan.BuildFilters(transition, clip.DurationSeconds,
                        VisualEffectPipeline.Number(localSeconds), $"stilltr{index}x{transitionIndex++}", canvasWidth);
                }
                if (layer.HeldPrior && TransitionRenderPlan.HeldBoundary(transitionTrack, clip,
                    project.Sequence.FramesPerSecond) is { } heldBoundary)
                    filters += TransitionRenderPlan.BuildHeldFilters(heldBoundary.Transition,
                        heldBoundary.ClipDuration, VisualEffectPipeline.Number(time - heldBoundary.Start));
                if (Math.Abs(rotation) > .00001)
                {
                    string angle = VisualEffectPipeline.Number(rotation);
                    filters += $",rotate=a={angle}:ow=rotw({angle}):oh=roth({angle}):c=none";
                }
                double pivotOffsetX = pivotDeltaX * Math.Cos(rotation) - pivotDeltaY * Math.Sin(rotation);
                double pivotOffsetY = pivotDeltaX * Math.Sin(rotation) + pivotDeltaY * Math.Cos(rotation);
                if (opacity < 1) filters += $",colorchannelmixer=aa={VisualEffectPipeline.Number(opacity)}";
                graph.AppendLine($"[{layer.Input}:v]{filters}[{local}];");
                string x = $"(W-w)/2+{VisualEffectPipeline.Number(ClipAnimation.Evaluate(clip, "PositionX", sampleTime) * positionScaleX + pivotOffsetX)}";
                string y = $"(H-h)/2+{VisualEffectPipeline.Number(ClipAnimation.Evaluate(clip, "PositionY", sampleTime) * positionScaleY + pivotOffsetY)}";
                // Title bitmaps are centered; this compositor applies their
                // animated placement once, as in the full MP4 export.
                string mode = VisualEffectPipeline.BlendModeToFfmpeg(clip.BlendMode);
                if (mode == "normal")
                    graph.AppendLine($"[{current}][{local}]overlay=x='{x}':y='{y}':format=auto[{next}];");
                else
                {
                    string transparent = $"transparent{index}";
                    graph.AppendLine($"color=c=black@0.0:s={canvasWidth}x{canvasHeight}:r=1:d=1,format=rgba[{transparent}];");
                    graph.AppendLine($"[{transparent}][{local}]overlay=x='{x}':y='{y}':format=auto,split=2[placed{index}][alpha{index}];");
                    graph.AppendLine($"[{current}]split=2[blendbase{index}][outputbase{index}];");
                    graph.AppendLine($"[placed{index}][blendbase{index}]blend=all_mode={mode}[color{index}];");
                    graph.AppendLine($"[alpha{index}]alphaextract[matte{index}];");
                    graph.AppendLine($"[color{index}][matte{index}]alphamerge[restored{index}];");
                    graph.AppendLine($"[outputbase{index}][restored{index}]overlay=format=auto[{next}];");
                }
                current = next;
            }
            string outputConversion = VisualEffectPipeline.BuildOutputColorConversion(project.Sequence);
            graph.AppendLine($"[{current}]{(outputConversion.Length == 0 ? "null" : outputConversion.TrimStart(','))}," +
                $"scale={width}:{height}:flags=lanczos,format=rgba[output]");
            // Keep the PNG pipe format used by still-frame export so its
            // transparency and WPF decode path remain identical.
            arguments.AddRange(["-filter_complex", graph.ToString(), "-map", "[output]",
                "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "pipe:1"]);
            return arguments.ToArray();
        }
        catch (ExportException exception)
        {
            throw new ClipFrameException(ClipFrameStageCode.Filter, "合成预览效果配置无效。", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            throw new ClipFrameException(ClipFrameStageCode.Validate, "无法读取合成预览素材。", exception);
        }
    }
}
