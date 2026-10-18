using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Renders effect previews on demand and cancels obsolete requests when the
/// playhead moves. Plain clips continue to use WPF media playback directly.
/// </summary>
public partial class MainWindow
{
    private CancellationTokenSource? _renderedPreviewCancellation;
    private string? _renderedPreviewKey;
    private string? _renderedPreviewClipId;
    private string? _renderedPreviewSourcePath;
    private long _renderedPreviewGeneration;
    private bool _renderedPreviewIsComposite;
    private bool _renderedPreviewFailed;
    private string? _renderedPreviewFailedKey;
    private bool _renderedPreviewInFlight;
    private bool _renderedPreviewPending;
    private string? _renderedPreviewRunningKey;

    private void InvalidateCompositedPreview()
    {
        _renderedPreviewKey = null;
        _renderedPreviewCancellation?.Cancel();
        _renderedPreviewCancellation = null;
        _renderedPreviewGeneration = -1;
        _renderedPreviewClipId = null;
        _renderedPreviewSourcePath = null;
        PreviewRenderedFrame.Source = null;
        PreviewRenderedFrame.Visibility = Visibility.Collapsed;
        _renderedPreviewIsComposite = false;
        _renderedPreviewFailed = false;
        _renderedPreviewFailedKey = null;
        _renderedPreviewPending = false;
        _renderedPreviewInFlight = false;
        _renderedPreviewRunningKey = null;
        _lastMediaTransformKey = null;
        SeekPreview(_project.Sequence.PlayheadSeconds);
    }

    private void ScheduleRenderedPreviewFrame(TimelineClip? clip)
    {
        if (_catalogPreviewActive || _catalogPreviewPending) return;
        double time = _project.Sequence.PlayheadSeconds;
        TimelineClip[] activeVisuals = _project.Tracks.Where(track => !track.IsHidden && track.Kind != TrackKind.Audio)
            .SelectMany(track => track.Clips)
            .Where(item => item.Kind is (ClipKind.Video or ClipKind.Image or ClipKind.Color or ClipKind.Adjustment) &&
                time >= item.StartSeconds && time < item.StartSeconds + item.DurationSeconds)
            .ToArray();
        if (activeVisuals.Length == 0 && clip is null)
        {
            ClearRenderedPreviewFrame();
            return;
        }
        bool composite = BoundaryCrossfade.IsActive(_project, time) ||
            activeVisuals.Length > 1 || activeVisuals.Any(item =>
            item.Kind is (ClipKind.Color or ClipKind.Adjustment) ||
            VisualEffectPipeline.BlendModeToFfmpeg(item.BlendMode) != "normal" ||
            item.Transitions.Any(transition =>
                (transition.Edge.Equals("In", StringComparison.OrdinalIgnoreCase) &&
                    time < item.StartSeconds + transition.DurationSeconds) ||
                (transition.Edge.Equals("Out", StringComparison.OrdinalIgnoreCase) &&
                    time >= item.StartSeconds + item.DurationSeconds - transition.DurationSeconds)));
        if (clip is null && !composite && activeVisuals.Length == 1 &&
            activeVisuals[0].Kind is (ClipKind.Video or ClipKind.Image))
            clip = activeVisuals[0];
        if (!composite && clip is not null && activeVisuals.Length == 1 && activeVisuals[0].Id != clip.Id)
            clip = activeVisuals[0];
        if (_renderedPreviewClipId == "composite" && !composite && clip is not null)
            ClearRenderedPreviewFrame();
        bool sourceTimeMapped = clip is not null &&
            (clip.IsReversed || clip.TimeRemapPoints.Count > 0 || Math.Abs(clip.Speed - 1) > .000001);
        // A filter pipeline is needed only when WPF cannot depict the source
        // result. For clean clips, keep native frame playback responsive.
        if (!composite && (clip is null || clip.Kind is not (ClipKind.Image or ClipKind.Video) ||
            clip.Effects.All(effect => !effect.IsEnabled) && clip.Mask is null &&
            !sourceTimeMapped))
        {
            ClearRenderedPreviewFrame();
            return;
        }
        MediaAsset? asset = clip is null ? null : _project.Media.FirstOrDefault(item => item.Id == clip.MediaAssetId);
        if (!composite && asset is null)
        {
            ClearRenderedPreviewFrame();
            return;
        }
        double seconds = time;
        // DrawTextOverlay assigns the sequence canvas size before WPF has
        // necessarily completed another layout pass. ActualWidth can still be
        // zero here, producing a one-pixel frame for imported stills.
        double canvasWidth = PreviewVisualLayer.Width;
        double canvasHeight = PreviewVisualLayer.Height;
        if (!double.IsFinite(canvasWidth) || !double.IsFinite(canvasHeight) ||
            canvasWidth < 1 || canvasHeight < 1)
        {
            ClearRenderedPreviewFrame();
            return;
        }
        // WPF layout uses device-independent pixels. Render to the actual
        // monitor pixels occupied by the letterboxed sequence canvas, then
        // apply the user's preview quality ratio. Export resolution is never
        // used for interactive frames.
        System.Windows.Media.Matrix device = PresentationSource.FromVisual(PreviewVisualLayer)
            ?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
        double requestedWidth = canvasWidth * device.M11 * Math.Max(1, PreviewZoomSlider.Value) * PreviewResolutionRatio;
        double requestedHeight = canvasHeight * device.M22 * Math.Max(1, PreviewZoomSlider.Value) * PreviewResolutionRatio;
        double limit = Math.Min(1, 4096 / Math.Max(requestedWidth, requestedHeight));
        int width = Math.Max(1, (int)Math.Round(requestedWidth * limit));
        int height = Math.Max(1, (int)Math.Round(requestedHeight * limit));
        // Frames within the same sequence tick have identical source content.
        // A stable frame key also avoids restarting an expensive FFmpeg graph
        // for every 33 ms UI timer tick on a 24/25 fps sequence.
        double frameTime = Math.Floor(seconds * Math.Max(1, _project.Sequence.FramesPerSecond) + 1e-7) /
            Math.Max(1, _project.Sequence.FramesPerSecond);
        // A still with no animated properties or active transition has the
        // same pixels at every playhead position. Reuse its decoded frame
        // instead of starting FFmpeg on every playback tick.
        if (!composite && clip?.Kind == ClipKind.Image && clip.Keyframes.Count == 0 &&
            clip.Transitions.All(transition => transition.DurationSeconds <= 0 ||
                transition.Edge.Equals("In", StringComparison.OrdinalIgnoreCase) &&
                    time >= clip.StartSeconds + transition.DurationSeconds ||
                transition.Edge.Equals("Out", StringComparison.OrdinalIgnoreCase) &&
                    time < clip.StartSeconds + clip.DurationSeconds - transition.DurationSeconds))
            frameTime = 0;
        string key = composite
            ? $"{_projectGeneration}:{_projectRevision}:composite:{frameTime:0.000000}:{width}:{height}"
            : $"{_projectGeneration}:{_projectRevision}:{clip!.Id}:{frameTime:0.000000}:{width}:{height}:{asset!.EffectivePreviewPath}";
        // Keep the native top source visible after a failed effect render.
        // A new playhead position, edit or canvas size gets a fresh attempt.
        if (key == _renderedPreviewFailedKey) return;
        // A frame from another source must never cover the current source
        // while its new effect frame is being decoded.
        string? clipId = composite ? "composite" : clip!.Id;
        string? sourcePath = composite ? "composite" : asset!.EffectivePreviewPath;
        bool sourceChanged = _renderedPreviewClipId != clipId ||
            _renderedPreviewSourcePath != sourcePath;
        if (sourceChanged)
        {
            PreviewRenderedFrame.Source = null;
            PreviewRenderedFrame.Visibility = Visibility.Collapsed;
            _renderedPreviewIsComposite = false;
            _renderedPreviewFailed = false;
            _lastMediaTransformKey = null;
            _renderedPreviewCancellation?.Cancel();
            _renderedPreviewKey = null;
        }
        _renderedPreviewClipId = clipId;
        _renderedPreviewSourcePath = sourcePath;
        if (key == _renderedPreviewKey) return;
        if (_renderedPreviewInFlight && _isPlaying && !sourceChanged && !_scrubbingPlayhead)
        {
            // Let the current render finish. Canceling it on each playback
            // tick can starve previews indefinitely on slower machines.
            _renderedPreviewPending = true;
            return;
        }
        if (_renderedPreviewInFlight && _scrubbingPlayhead)
        {
            // During pointer scrubbing the newest frame is more valuable than
            // completing an older request. Cancel immediately and start the
            // current frame below.
            _renderedPreviewCancellation?.Cancel();
            _renderedPreviewInFlight = false;
            _renderedPreviewPending = false;
            _renderedPreviewRunningKey = null;
        }
        // A new source starts immediately. During playback, later requests
        // coalesce behind the current render so the decoder can make progress.
        _renderedPreviewFailed = false;
        _renderedPreviewKey = key;
        _renderedPreviewGeneration = _projectGeneration;
        _renderedPreviewCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _renderedPreviewCancellation = cancellation;
        _renderedPreviewInFlight = true;
        _renderedPreviewPending = false;
        _renderedPreviewRunningKey = key;
        _ = RenderPreviewFrameAsync(clip, asset, frameTime, width, height, key, composite, cancellation.Token);
    }

    private void ClearRenderedPreviewFrame()
    {
        _renderedPreviewCancellation?.Cancel();
        _renderedPreviewCancellation = null;
        _renderedPreviewGeneration = -1;
        _renderedPreviewKey = null;
        _renderedPreviewClipId = null;
        _renderedPreviewSourcePath = null;
        _renderedPreviewIsComposite = false;
        _renderedPreviewFailed = false;
        _renderedPreviewInFlight = false;
        _renderedPreviewPending = false;
        _renderedPreviewRunningKey = null;
        PreviewRenderedFrame.Source = null;
        PreviewRenderedFrame.Visibility = Visibility.Collapsed;
        _lastMediaTransformKey = null;
    }

    private async Task RenderPreviewFrameAsync(TimelineClip? clip, MediaAsset? asset, double seconds,
        int width, int height, string key, bool composite, CancellationToken cancellationToken)
    {
        try
        {
            BitmapSource bitmap = composite
                ? await CompositeFrameRenderer.RenderFrameAsync(_project, seconds, width, height, cancellationToken,
                    previewGpuMode: _previewGpuMode)
                : await ClipFrameRenderer.RenderClipFrameAsync(clip!, asset!,
                    _project.Sequence, seconds, width, height, cancellationToken,
                    previewGpuMode: _previewGpuMode);
            if (_catalogPreviewActive || _catalogPreviewPending || cancellationToken.IsCancellationRequested || key != _renderedPreviewKey ||
                _renderedPreviewGeneration != _projectGeneration ||
                !composite && _activePreviewVisualClip?.Id != clip?.Id) return;
            PreviewRenderedFrame.Source = bitmap;
            PreviewRenderedFrame.Visibility = Visibility.Visible;
            _renderedPreviewIsComposite = composite;
            _renderedPreviewFailed = false;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewMediaElement.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            _lastMediaTransformKey = null;
            ApplyPreviewMediaTransform();
            DrawMediaSelectionOverlay();
        }
        catch (OperationCanceledException) { }
        catch (ClipFrameException exception)
        {
            if (key != _renderedPreviewKey) return;
            StatusText.Text = $"效果预览失败 · {exception.StageCode}";
            FallBackFromRenderFailure(key);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException)
        {
            if (key != _renderedPreviewKey) return;
            StatusText.Text = $"效果预览失败 · PREVIEW-RENDER：{exception.Message}";
            FallBackFromRenderFailure(key);
        }
        finally
        {
            if (key == _renderedPreviewRunningKey)
            {
                _renderedPreviewInFlight = false;
                _renderedPreviewRunningKey = null;
                if (_renderedPreviewPending && key == _renderedPreviewKey)
                {
                    _renderedPreviewPending = false;
                    // The playhead advanced while this frame was rendering.
                    // Release the completed key so the newest frame can be
                    // requested instead of treating it as already scheduled.
                    _renderedPreviewKey = null;
                    ScheduleRenderedPreviewFrame(_activePreviewVisualClip);
                }
            }
        }
    }

    private void FallBackFromRenderFailure(string key)
    {
        ClearRenderedPreviewFrame();
        _renderedPreviewFailedKey = key;
        _renderedPreviewFailed = true;
        // SeekPreview picks the highest visible image/video layer and loads it
        // through WPF. This avoids leaving a fully black monitor when one
        // optional composition filter cannot render.
        SeekPreview(_project.Sequence.PlayheadSeconds);
    }
}
