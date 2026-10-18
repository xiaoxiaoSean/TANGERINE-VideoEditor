using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Presents the sequence at its playhead using direct media playback or a
/// locally rendered composite frame, depending on the active visual layers.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch _playbackClock = new();
    private double _playbackClockStartSeconds;
    private bool _playbackTickSeeking;
    private bool _isPlaying;
    private int _playbackDirection = 1;
    private bool _globalMute;
    private string? _soloTrackId;
    // Mute and solo only change what the monitor plays, never the project, so
    // they must not bump the project revision (that would mark the project
    // dirty and clutter the save state). This counter invalidates the rendered
    // monitor window on its own.
    private long _monitorStateRevision;
    private long MonitorRevision => _projectRevision + _monitorStateRevision;
    private string? _previewSourcePath;
    private long _previewImageRequest;
    private string? _previewImageLoadingPath;
    private string? _previewImageFailedPath;
    private TimelineClip? _draggingTitleClip;
    private Canvas? _draggingTitleVisual;
    private Point _titleDragStart;
    private double _titleDisplayedStartX;
    private double _titleDisplayedStartY;
    private double _previewTitleScale = 1;
    private bool _titleDragChanged;
    private readonly Dictionary<string, Rectangle> _titleSelectionOutlines = [];
    private readonly Dictionary<string, Canvas> _titlePreviewVisuals = [];
    private readonly Dictionary<string, Canvas> _graphicPreviewVisuals = [];
    private double _playbackEndSeconds;
    private string? _lastTitleLayoutKey;
    private double _lastTitleRangeStart = double.NaN;
    private double _lastTitleRangeEnd = double.NaN;
    private string? _lastTitleClickClipId;
    private long _lastTitleClickTick;
    private Point _lastTitleClickPoint;
    private bool _previewSurfaceFirstClick;
    private long _previewSurfaceFirstClickTick;
    private Point _previewSurfaceFirstClickPoint;
    private bool _previewTextEditorPending;
    private TimelineClip? _activePreviewVisualClip;
    private string? _activePreviewVisualSourcePath;
    private TimelineClip? _draggingMediaClip;
    private Point _mediaDragStart;
    private bool _mediaDragChanged;
    private bool _catalogPlaybackUpdating;
    private readonly DispatcherTimer _catalogPlaybackTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    private static string FormatCatalogTime(TimeSpan value) =>
        $"{(int)value.TotalMinutes:00}:{value.Seconds:00}";

    private void InitializePreview()
    {
        // Sequence audio is mixed by the local timeline monitor, including
        // standalone audio tracks and each video's embedded sound.
        PreviewMediaElement.Volume = 0;
        CatalogPlaybackButton.Click += (_, _) =>
        {
            if (!_catalogPreviewActive) return;
            if (PreviewMediaElement.CanPause && PreviewMediaElement.IsLoaded)
            {
                if (PreviewMediaElement.Position >= PreviewMediaElement.NaturalDuration.TimeSpan &&
                    PreviewMediaElement.NaturalDuration.HasTimeSpan) PreviewMediaElement.Position = TimeSpan.Zero;
                if (CatalogPlaybackButton.Content?.ToString() == "❚❚")
                {
                    PreviewMediaElement.Pause();
                    CatalogPlaybackButton.Content = "▶";
                }
                else
                {
                    PreviewMediaElement.Play();
                    CatalogPlaybackButton.Content = "❚❚";
                }
            }
        };
        CatalogPlaybackSlider.ValueChanged += (_, _) =>
        {
            if (_catalogPlaybackUpdating || !_catalogPreviewActive || !CatalogPlaybackSlider.IsMouseOver) return;
            PreviewMediaElement.Position = TimeSpan.FromSeconds(CatalogPlaybackSlider.Value);
        };
        _catalogPlaybackTimer.Tick += (_, _) =>
        {
            if (!_catalogPreviewActive || !PreviewMediaElement.NaturalDuration.HasTimeSpan) return;
            TimeSpan position = PreviewMediaElement.Position;
            TimeSpan duration = PreviewMediaElement.NaturalDuration.TimeSpan;
            _catalogPlaybackUpdating = true;
            try
            {
                CatalogPlaybackSlider.Maximum = Math.Max(0.01, duration.TotalSeconds);
                CatalogPlaybackSlider.Value = Math.Clamp(position.TotalSeconds, 0, CatalogPlaybackSlider.Maximum);
                CatalogPlaybackTime.Text = $"{FormatCatalogTime(position)} / {FormatCatalogTime(duration)}";
            }
            finally { _catalogPlaybackUpdating = false; }
        };
        _catalogPlaybackTimer.Start();
        _playbackTimer.Tick += (_, _) =>
        {
            // Dispatcher ticks can be delayed by UI work. The monotonic clock
            // keeps sequence time aligned with monitored audio despite skips.
            double next = _playbackClockStartSeconds + _playbackDirection * _playbackClock.Elapsed.TotalSeconds;
            if ((_playbackDirection > 0 && next >= _playbackEndSeconds && _playbackEndSeconds > 0) ||
                (_playbackDirection < 0 && next <= 0)) { StopPlayback(); return; }
            _playbackTickSeeking = true;
            try { SeekPreview(next); }
            finally { _playbackTickSeeking = false; }
        };
        PlayPauseButton.Click += (_, _) => TogglePlayback();
        StopButton.Click += (_, _) => StopPlayback();
        BackButton.Click += (_, _) => SeekPreview(_project.Sequence.PlayheadSeconds - 1 / _project.Sequence.FramesPerSecond);
        ForwardButton.Click += (_, _) => SeekPreview(_project.Sequence.PlayheadSeconds + 1 / _project.Sequence.FramesPerSecond);
        PreviewMediaElement.MediaFailed += (_, args) =>
        {
            if (_catalogPreviewActive)
            {
                _catalogPreviewActive = false;
                CatalogPlaybackBar.Visibility = Visibility.Collapsed;
                StatusText.Text = $"示例预览失败 · {args.ErrorException?.Message ?? "无法打开示例视频"}";
                return;
            }
            if (_renderedPreviewClipId is not null) return;
            PreviewMediaElement.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewMessage.Text = "媒体无法预览，请检查源文件";
            StatusText.Text = $"预览失败 · PREVIEW-DECODE：{args.ErrorException?.Message ?? "未知媒体错误"}";
            _lastTitleLayoutKey = null;
            DrawTextOverlay();
        };
        PreviewMediaElement.MediaOpened += (_, _) =>
        {
            if (_catalogPreviewActive)
            {
                PreviewMediaElement.Position = TimeSpan.Zero;
                PreviewMediaElement.Play();
                CatalogPlaybackButton.Content = "❚❚";
                return;
            }
            if (_renderedPreviewClipId is not null)
            {
                PreviewMediaElement.Pause();
                PreviewMediaElement.Visibility = Visibility.Collapsed;
                return;
            }
            _lastMediaTransformKey = null;
            _lastTitleLayoutKey = null;
            DrawTextOverlay();
        };
        PreviewMediaElement.MediaEnded += (_, _) =>
        {
            if (!_catalogPreviewActive) return;
            CatalogPlaybackButton.Content = "▶";
            CatalogPlaybackSlider.Value = CatalogPlaybackSlider.Maximum;
        };
        PreviewCropButton.Click += (_, _) =>
        {
            if (_mediaCropMode && _draggingMediaClip is not null)
            {
                PreviewOverlay.ReleaseMouseCapture();
                FinishMediaDrag();
            }
            _mediaCropMode = !_mediaCropMode;
            PreviewCropButton.Content = _mediaCropMode ? "完成裁切" : "裁切";
            StatusText.Text = _mediaCropMode ? "已进入裁切：选择画面后拖动青色控制点" : "已退出裁切";
            _lastTitleLayoutKey = null;
            DrawTextOverlay();
        };
        PreviewOverlay.SizeChanged += (_, _) => DrawTextOverlay();
        PreviewOverlay.MouseLeftButtonDown += (sender, args) =>
        {
            if (args.Handled) return;
            Point clickPoint = args.GetPosition(PreviewOverlay);
            long clickTick = Environment.TickCount64;
            // Media mouse capture can make WPF deliver two single clicks
            // instead of one double click on the program monitor.
            bool doubleClick = args.ClickCount >= 2 ||
                _previewSurfaceFirstClick && clickTick - _previewSurfaceFirstClickTick <= 500 &&
                Math.Abs(clickPoint.X - _previewSurfaceFirstClickPoint.X) <= 6 &&
                Math.Abs(clickPoint.Y - _previewSurfaceFirstClickPoint.Y) <= 6;
            _previewSurfaceFirstClick = !doubleClick;
            _previewSurfaceFirstClickTick = clickTick;
            _previewSurfaceFirstClickPoint = clickPoint;
            if (!doubleClick)
            {
                StartMediaDrag(args);
                return;
            }
            // Handles retain their media action, while a double click on the
            // program monitor opens the selected visible title. A first click
            // may already have captured the monitor for a media move.
            MediaHandleKind handleKind = MediaHandleKind.Move;
            TimelineClip? interactiveMedia = InteractiveMediaClip();
            bool mediaHandle = interactiveMedia is not null && CanInteractWithMediaClip(interactiveMedia) &&
                TryMediaHandle(clickPoint, interactiveMedia,
                    out handleKind);
            if (_draggingMediaClip is not null)
            {
                PreviewOverlay.ReleaseMouseCapture();
                FinishMediaDrag();
            }
            if (mediaHandle && handleKind != MediaHandleKind.Move)
            { args.Handled = true; return; }
            // A click on a displayed title is handled by StartTitleDrag.
            // Empty monitor space still accepts a double-click for the
            // selected visible title or the topmost title.
            var visibleTitles = _project.Tracks.Where(track => !track.IsHidden && track.Kind != TrackKind.Audio)
                .OrderByDescending(track => track.Order)
                .SelectMany(track => track.Clips.Where(clip => clip.Kind == ClipKind.Text &&
                    _project.Sequence.PlayheadSeconds >= clip.StartSeconds &&
                    _project.Sequence.PlayheadSeconds < clip.StartSeconds + clip.DurationSeconds)
                    .OrderBy(item => item.StartSeconds))
                .ToList();
            TimelineClip? selected = _selectedClip is { Kind: ClipKind.Text } title &&
                visibleTitles.Contains(title) ? title : visibleTitles.LastOrDefault();
            if (selected is null)
            {
                if (mediaHandle) args.Handled = true;
                return;
            }
            OpenPreviewTextEditor(selected);
            args.Handled = true;
        };
        // A shape vertex resize captures the monitor, so it consumes the
        // pointer stream before the media move gesture sees it.
        PreviewOverlay.MouseMove += (_, args) =>
        {
            if (_vertexDragActive) { MoveShapeVertexDrag(args); return; }
            MoveMediaDrag(args);
        };
        PreviewOverlay.MouseLeftButtonUp += (_, args) =>
        {
            if (_vertexDragActive) { FinishShapeVertexDrag(); args.Handled = true; return; }
            EndMediaDrag(args);
        };
        PreviewOverlay.LostMouseCapture += (_, _) =>
        {
            if (_vertexDragActive) { FinishShapeVertexDrag(); return; }
            FinishMediaDrag();
        };
        PreviewViewport.PreviewMouseWheel += (_, args) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 ||
                _vertexDragActive || _draggingMediaClip is not null) return;
            PreviewZoomSlider.Value = Math.Clamp(PreviewZoomSlider.Value * Math.Pow(1.12, args.Delta / 120.0), .1, 4);
            args.Handled = true;
        };
    }

    private void SeekPreview(double seconds)
    {
        if (_catalogPreviewActive || _catalogPreviewPending) return;
        _project.Sequence.PlayheadSeconds = Math.Max(0, seconds);
        if (_isPlaying && !_playbackTickSeeking)
        {
            _playbackClockStartSeconds = _project.Sequence.PlayheadSeconds;
            _playbackClock.Restart();
        }
        UpdateMulticamPreviewControls(_project.Sequence.PlayheadSeconds);
        UpdateAudioPreview(_project.Sequence.PlayheadSeconds);
        CurrentTimeText.Text = FormatTime(_project.Sequence.PlayheadSeconds);
        // Clip visuals do not change while seeking. Move the existing playhead
        // instead of rebuilding every track on each playback frame.
        if (TimelineCanvas is not null) UpdatePlayheadVisual();
        if (_draggingMediaClip is not null || _draggingTitleClip is not null)
        {
            DrawTextOverlay();
            return;
        }
        if (TryShowCachedPreview(_project.Sequence.PlayheadSeconds)) return;
        // Track order follows the UI from top to bottom; the compositor
        // paints bottom tracks first. Later-starting clips on the same track
        // are painted last, so choose the same topmost source here.
        TimelineClip? clip = null;
        foreach (TimelineTrack track in _project.Tracks.Where(track => track.Kind != TrackKind.Audio && !track.IsHidden)
            .OrderByDescending(track => track.Order))
        {
            foreach (TimelineClip candidate in track.Clips.OrderBy(item => item.StartSeconds))
            {
                if (candidate.Kind is ClipKind.Image or ClipKind.Video && _project.Sequence.PlayheadSeconds >= candidate.StartSeconds &&
                    _project.Sequence.PlayheadSeconds < candidate.StartSeconds + candidate.DurationSeconds)
                    clip = candidate;
            }
        }
        _activePreviewVisualClip = clip;
        _activePreviewVisualSourcePath = clip is null ? null :
            _project.Media.FirstOrDefault(media => media.Id == clip.MediaAssetId)?.EffectivePreviewPath;
        DrawTextOverlay();
        bool hasText = _project.Tracks.Where(track => !track.IsHidden).SelectMany(track => track.Clips)
            .Any(candidate => candidate.Kind == ClipKind.Text &&
                _project.Sequence.PlayheadSeconds >= candidate.StartSeconds &&
                _project.Sequence.PlayheadSeconds < candidate.StartSeconds + candidate.DurationSeconds);
        if (clip is null)
        {
            ScheduleRenderedPreviewFrame(null);
            if (_renderedPreviewClipId is not null)
            {
                if (_previewSourcePath is not null || _previewImageLoadingPath is not null)
                {
                    _previewImageRequest++;
                    _previewSourcePath = null;
                    _previewImageLoadingPath = null;
                }
                PreviewMediaElement.Pause();
                if (PreviewMediaElement.Source is not null) PreviewMediaElement.Source = null;
                if (PreviewImage.Source is not null) PreviewImage.Source = null;
                PreviewMediaElement.Visibility = Visibility.Collapsed;
                PreviewImage.Visibility = Visibility.Collapsed;
                PreviewPlaceholder.Visibility = _renderedPreviewFailed ||
                    PreviewRenderedFrame.Visibility != Visibility.Visible
                    ? Visibility.Visible : Visibility.Collapsed;
                if (!_renderedPreviewFailed && PreviewPlaceholder.Visibility == Visibility.Visible)
                    PreviewMessage.Text = "正在渲染预览帧…";
                return;
            }
            _lastMediaTransformKey = null;
            if (_previewSourcePath is not null || _previewImageLoadingPath is not null)
            {
                _previewImageRequest++;
                _previewImageLoadingPath = null;
                _previewImageFailedPath = null;
                _previewSourcePath = null;
            }
            PreviewMediaElement.Pause();
            PreviewMediaElement.Visibility = Visibility.Collapsed;
            PreviewImage.Visibility = Visibility.Collapsed;
            if (PreviewMediaElement.Source is not null) PreviewMediaElement.Source = null;
            if (PreviewImage.Source is not null) PreviewImage.Source = null;
            PreviewPlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
            PreviewMessage.Text = "当前时间没有可预览的画面";
            return;
        }
        MediaAsset? asset = _project.Media.FirstOrDefault(media => media.Id == clip.MediaAssetId);
        if (asset is null || string.IsNullOrWhiteSpace(asset.EffectivePreviewPath))
        {
            ScheduleRenderedPreviewFrame(null);
            if (_renderedPreviewClipId is not null)
            {
                if (_previewSourcePath is not null || _previewImageLoadingPath is not null)
                {
                    _previewImageRequest++;
                    _previewSourcePath = null;
                    _previewImageLoadingPath = null;
                }
                PreviewMediaElement.Pause();
                if (PreviewMediaElement.Source is not null) PreviewMediaElement.Source = null;
                if (PreviewImage.Source is not null) PreviewImage.Source = null;
                PreviewMediaElement.Visibility = Visibility.Collapsed;
                PreviewImage.Visibility = Visibility.Collapsed;
                PreviewPlaceholder.Visibility = _renderedPreviewFailed ||
                    PreviewRenderedFrame.Visibility != Visibility.Visible
                    ? Visibility.Visible : Visibility.Collapsed;
                if (!_renderedPreviewFailed && PreviewPlaceholder.Visibility == Visibility.Visible)
                    PreviewMessage.Text = "正在渲染预览帧…";
                return;
            }
            _lastMediaTransformKey = null;
            if (_previewSourcePath is not null || _previewImageLoadingPath is not null)
            {
                _previewImageRequest++;
                _previewImageLoadingPath = null;
                _previewImageFailedPath = null;
                _previewSourcePath = null;
            }
            PreviewMediaElement.Pause();
            PreviewMediaElement.Visibility = Visibility.Collapsed;
            PreviewImage.Visibility = Visibility.Collapsed;
            if (PreviewMediaElement.Source is not null) PreviewMediaElement.Source = null;
            if (PreviewImage.Source is not null) PreviewImage.Source = null;
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewMessage.Text = "媒体文件缺失，请重新导入";
            return;
        }
        if (clip.Kind is ClipKind.Video or ClipKind.Image)
            ScheduleRenderedPreviewFrame(clip);
        else ScheduleRenderedPreviewFrame(null);
        if (_renderedPreviewClipId is not null)
        {
            // FFmpeg owns this frame. Keep the original image or video hidden
            // while the asynchronous result is pending, including after an
            // older image decode completes or the current render fails.
            if (_previewImageLoadingPath is not null)
            {
                _previewImageRequest++;
                _previewImageLoadingPath = null;
            }
            _previewImageFailedPath = null;
            _previewSourcePath = asset.EffectivePreviewPath;
            PreviewMediaElement.Pause();
            PreviewMediaElement.Visibility = Visibility.Collapsed;
            if (PreviewMediaElement.Source is not null) PreviewMediaElement.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            if (PreviewImage.Source is not null) PreviewImage.Source = null;
            PreviewPlaceholder.Visibility = _renderedPreviewFailed ||
                PreviewRenderedFrame.Visibility != Visibility.Visible
                ? Visibility.Visible : Visibility.Collapsed;
            if (!_renderedPreviewFailed && PreviewPlaceholder.Visibility == Visibility.Visible)
                PreviewMessage.Text = "正在渲染预览帧…";
            return;
        }
        // A previously composed frame leaves its WPF layout behind when we
        // return to an ordinary still. Force the native image's own fitted
        // geometry before making it visible.
        _lastMediaTransformKey = null;
        if (asset.Kind == MediaKind.Image)
        {
            // Returning to the same image after another source invalidated the
            // decode still needs a fresh load when the visual has no bitmap.
            PreviewMediaElement.Pause();
            PreviewMediaElement.Visibility = Visibility.Collapsed;
            if (PreviewMediaElement.Source is not null) PreviewMediaElement.Source = null;
            if (_previewSourcePath != asset.EffectivePreviewPath ||
                (PreviewImage.Source is null && _previewImageLoadingPath != asset.EffectivePreviewPath &&
                    _previewImageFailedPath != asset.EffectivePreviewPath))
            {
                PreviewImage.Visibility = Visibility.Collapsed;
                _previewSourcePath = asset.EffectivePreviewPath;
                _previewImageLoadingPath = asset.EffectivePreviewPath;
                _previewImageFailedPath = null;
                PreviewImage.Source = null;
                PreviewPlaceholder.Visibility = Visibility.Visible;
                PreviewMessage.Text = "正在加载图片…";
                _ = LoadPreviewImageAsync(asset.EffectivePreviewPath, ++_previewImageRequest);
            }
            else if (PreviewImage.Source is not null)
            {
                PreviewImage.Visibility = _renderedPreviewClipId is not null
                    ? Visibility.Collapsed : Visibility.Visible;
                PreviewPlaceholder.Visibility = _renderedPreviewClipId is not null &&
                    PreviewRenderedFrame.Visibility != Visibility.Visible
                    ? Visibility.Visible : Visibility.Collapsed;
                if (PreviewPlaceholder.Visibility == Visibility.Visible)
                    PreviewMessage.Text = "正在渲染预览帧…";
            }
            else if (_previewImageFailedPath == asset.EffectivePreviewPath)
            {
                PreviewImage.Visibility = Visibility.Collapsed;
                PreviewPlaceholder.Visibility = Visibility.Visible;
                PreviewMessage.Text = "图片无法预览，请检查源文件";
            }
            return;
        }
        if (asset.Kind == MediaKind.Audio)
        {
            _lastMediaTransformKey = null;
            if (_previewSourcePath is not null || _previewImageLoadingPath is not null)
            {
                _previewImageRequest++;
                _previewImageLoadingPath = null;
                _previewImageFailedPath = null;
                _previewSourcePath = null;
            }
            PreviewMediaElement.Pause();
            PreviewMediaElement.Visibility = Visibility.Collapsed;
            PreviewImage.Visibility = Visibility.Collapsed;
            if (PreviewMediaElement.Source is not null) PreviewMediaElement.Source = null;
            if (PreviewImage.Source is not null) PreviewImage.Source = null;
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewMessage.Text = "音频片段";
            return;
        }
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Collapsed;
        if (PreviewImage.Source is not null) PreviewImage.Source = null;
        PreviewMediaElement.Visibility = PreviewRenderedFrame.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        string previewPath = asset.EffectivePreviewPath;
        if (_previewSourcePath != previewPath || PreviewMediaElement.Source is null)
        {
            _previewImageRequest++;
            _previewImageLoadingPath = null;
            _previewImageFailedPath = null;
            try { PreviewMediaElement.Source = new Uri(previewPath); }
            catch (UriFormatException exception)
            {
                PreviewMediaElement.Visibility = Visibility.Collapsed;
                PreviewPlaceholder.Visibility = Visibility.Visible;
                PreviewMessage.Text = "媒体路径无效";
                StatusText.Text = $"预览失败 · PREVIEW-SOURCE：{exception.Message}";
                return;
            }
            _previewSourcePath = previewPath;
        }
        TimeSpan sourceTime = TimeSpan.FromSeconds(Math.Max(0,
            ClipTimeMap.SourceTime(clip, _project.Sequence.PlayheadSeconds)));
        if (Math.Abs((PreviewMediaElement.Position - sourceTime).TotalSeconds) > 0.15)
            PreviewMediaElement.Position = sourceTime;
        if (_isPlaying) PreviewMediaElement.Play();
        else PreviewMediaElement.Pause();
    }

    private async Task LoadPreviewImageAsync(string path, long request)
    {
        // Decode on a dedicated STA thread. BitmapImage can perform blocking
        // disk and network reads even when it is assigned to an Image later.
        var completion = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                // Load from a short-lived stream. UriSource may reuse a stale
                // decoder cache after a project archive is reopened, and an
                // extracted image can move while an old URI still exists.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                completion.TrySetResult(bitmap);
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        worker.IsBackground = true;
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        try
        {
            BitmapSource bitmap = await completion.Task;
            if (request != _previewImageRequest || _previewSourcePath != path) return;
            _previewImageLoadingPath = null;
            _previewImageFailedPath = null;
            PreviewImage.Source = bitmap;
            bool renderedFrameOwnsMonitor = _renderedPreviewClipId is not null;
            PreviewImage.Visibility = renderedFrameOwnsMonitor
                ? Visibility.Collapsed : Visibility.Visible;
            PreviewPlaceholder.Visibility = renderedFrameOwnsMonitor &&
                PreviewRenderedFrame.Visibility != Visibility.Visible
                ? Visibility.Visible : Visibility.Collapsed;
            if (PreviewPlaceholder.Visibility == Visibility.Visible)
                PreviewMessage.Text = "正在渲染预览帧…";
            _lastTitleLayoutKey = null;
            _lastMediaTransformKey = null;
            DrawTextOverlay();
        }
        catch (Exception exception)
        {
            if (request != _previewImageRequest || _previewSourcePath != path) return;
            _previewImageLoadingPath = null;
            _previewImageFailedPath = path;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewMessage.Text = "图片无法预览，请检查源文件";
            StatusText.Text = $"图片预览失败 · PREVIEW-DECODE：{exception.Message}";
            _lastTitleLayoutKey = null;
            DrawTextOverlay();
        }
    }

    private void DrawTextOverlay()
    {
        if (PreviewOverlay is null || _catalogPreviewActive || _catalogPreviewPending) return;
        if (_draggingTitleClip is not null) return;
        string layoutKey = $"{_projectGeneration}:{_projectRevision}:" +
            $"{PreviewOverlay.ActualWidth:0.0}:{PreviewOverlay.ActualHeight:0.0}:{_selectedClip?.Id}:{_activePreviewVisualClip?.Id}:{_mediaCropMode}";
        double currentTime = _project.Sequence.PlayheadSeconds;
        // Animated titles require a new layout for each playhead position;
        // animated media can reuse the title layout and update its transform.
        bool animatedTitle = _project.Tracks.Where(track => !track.IsHidden && track.Kind != TrackKind.Audio)
            .SelectMany(track => track.Clips).Any(clip => clip.Kind == ClipKind.Text &&
                currentTime >= clip.StartSeconds && currentTime < clip.StartSeconds + clip.DurationSeconds &&
                (clip.Keyframes.Count > 0 || DynamicTitlePreset.IsDynamic(clip)));
        if (layoutKey == _lastTitleLayoutKey &&
            _project.Sequence.PlayheadSeconds >= _lastTitleRangeStart &&
            _project.Sequence.PlayheadSeconds < _lastTitleRangeEnd && !animatedTitle)
        {
            // The monitor can leave a rendered composite while a source
            // becomes plain, or finish loading an image without changing the
            // title layout. Apply to the current visible visual each time.
            ApplyPreviewMediaTransform();
            DrawMediaSelectionOverlay();
            return;
        }
        _lastTitleLayoutKey = layoutKey;
        double now = _project.Sequence.PlayheadSeconds;
        double nextBoundary = double.PositiveInfinity;
        double rangeStart = 0;
        foreach (TimelineClip candidate in _project.Tracks.Where(track => !track.IsHidden && track.Kind != TrackKind.Audio)
            .SelectMany(track => track.Clips).Where(clip => clip.Kind is ClipKind.Text or ClipKind.Image or ClipKind.Video))
        {
            double start = candidate.StartSeconds;
            double end = start + candidate.DurationSeconds;
            if (start <= now && start > rangeStart) rangeStart = start;
            if (end <= now && end > rangeStart) rangeStart = end;
            if (start > now && start < nextBoundary) nextBoundary = start;
            if (end > now && end < nextBoundary) nextBoundary = end;
        }
        _lastTitleRangeStart = rangeStart;
        _lastTitleRangeEnd = nextBoundary;
        PreviewOverlay.Children.Clear();
        _titleSelectionOutlines.Clear();
        _titlePreviewVisuals.Clear();
        _graphicPreviewVisuals.Clear();
        double width = PreviewOverlay.ActualWidth;
        double height = PreviewOverlay.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            _lastTitleLayoutKey = null;
            return;
        }
        double scale = Math.Min(width / Math.Max(1, _project.Sequence.Width), height / Math.Max(1, _project.Sequence.Height));
        _previewTitleScale = scale;
        double frameLeft = (width - _project.Sequence.Width * scale) / 2;
        double frameTop = (height - _project.Sequence.Height * scale) / 2;
        double frameWidth = _project.Sequence.Width * scale;
        double frameHeight = _project.Sequence.Height * scale;
        bool visibleObjectOutsideFrame = false;
        // WPF initializes Width/Height to NaN (Auto). NaN comparisons never
        // exceed a threshold, which previously left media coordinates NaN.
        if (!double.IsFinite(PreviewVisualLayer.Width) ||
            Math.Abs(PreviewVisualLayer.Width - frameWidth) > .1)
            PreviewVisualLayer.Width = frameWidth;
        if (!double.IsFinite(PreviewVisualLayer.Height) ||
            Math.Abs(PreviewVisualLayer.Height - frameHeight) > .1)
            PreviewVisualLayer.Height = frameHeight;
        ApplyPreviewMediaTransform();
        foreach (TimelineClip clip in _project.Tracks.Where(track => !track.IsHidden && track.Kind != TrackKind.Audio)
            .OrderByDescending(track => track.Order).SelectMany(track => track.Clips)
            .Where(candidate => candidate.Kind == ClipKind.Text &&
                _project.Sequence.PlayheadSeconds >= candidate.StartSeconds &&
                _project.Sequence.PlayheadSeconds < candidate.StartSeconds + candidate.DurationSeconds))
        {
            double localTitleSeconds = Math.Clamp(now - clip.StartSeconds, 0, clip.DurationSeconds);
            // Stickers are stored as text clips for project compatibility, but
            // they are pure vector graphics. Do not create a TextBlock for
            // them: the text selection frame/placeholder would be rendered on
            // top of the shape and appear as an unwanted line through it.
            bool hasGraphic = clip.Text?.AdditionalData?.ContainsKey(GraphicShape.ExtensionKey) == true;
            if (hasGraphic)
            {
                DrawGraphicShapeOverlay(clip, scale, frameLeft, frameTop, frameWidth, frameHeight);
                continue;
            }
            ClipText style = DynamicTitlePreset.RenderStyle(clip, localTitleSeconds);
            double fontSize = Math.Max(1, style.FontSize * scale);
            var text = new TextBlock
            {
                Text = style.Content,
                FontFamily = new FontFamily(string.IsNullOrWhiteSpace(style.FontFamily)
                    ? "Microsoft YaHei UI" : style.FontFamily),
                FontSize = fontSize,
                FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal,
                Foreground = ParseBrush(style.ForegroundColor, Brushes.White),
                TextDecorations = style.Underline ? TextDecorations.Underline : null,
                // Text layers are single-line by default. Their width is the
                // measured content width and may extend beyond the sequence;
                // the preview frame clips what is outside its visible area.
                // Explicit newline characters still create additional lines.
                TextWrapping = TextWrapping.NoWrap,
                TextAlignment = style.HorizontalAlignment switch
                {
                    "Left" => TextAlignment.Left,
                    "Right" => TextAlignment.Right,
                    _ => TextAlignment.Center
                },
                Opacity = Math.Clamp(ClipAnimation.Evaluate(clip, "Opacity", now) *
                    DynamicTitlePreset.OpacityMultiplier(clip, localTitleSeconds), 0, 1)
            };
            text.LineHeight = Math.Max(fontSize, fontSize * style.LineHeight);
            if (style.ShadowEnabled)
            {
                bool glow = clip.TemplateId == "title.glow";
                text.Effect = new DropShadowEffect
                {
                    Color = ParseColor(glow ? style.GlowColor : style.ShadowColor, Colors.Black),
                    BlurRadius = Math.Max(0, (glow ? style.GlowRadius : style.ShadowBlur) * scale),
                    ShadowDepth = glow ? 0 : Math.Sqrt(style.ShadowOffsetX * style.ShadowOffsetX + style.ShadowOffsetY * style.ShadowOffsetY) * scale,
                    Direction = Math.Atan2(-style.ShadowOffsetY, style.ShadowOffsetX) * 180 / Math.PI,
                    Opacity = Math.Clamp(glow ? style.GlowOpacity : style.ShadowOpacity, 0, 1)
                };
            }
            // Measure the full text block as export does. The monitor canvas
            // clips overflow to the sequence frame after alignment/transform.
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            bool spacedText = Math.Abs(style.LetterSpacing) >= .001 &&
                !string.IsNullOrEmpty(style.Content);
            Size titleSize = spacedText ? LetterSpacingText.Measure(style, scale, 0) : text.DesiredSize;
            double animatedOpacity = text.Opacity;
            double positionX = (ClipAnimation.Evaluate(clip, "PositionX", now) +
                DynamicTitlePreset.HorizontalOffset(clip, localTitleSeconds, _project.Sequence.Width)) * scale;
            double positionY = (ClipAnimation.Evaluate(clip, "PositionY", now) +
                DynamicTitlePreset.VerticalOffset(clip, localTitleSeconds, _project.Sequence.Height) +
                DynamicTitlePreset.CreditsOffset(clip, localTitleSeconds, _project.Sequence.Height)) * scale;
            Point titleOrigin = TitleLayout.TextOrigin(style, frameWidth, frameHeight, titleSize);
            double x = frameLeft + titleOrigin.X + positionX;
            double y = frameTop + titleOrigin.Y + positionY;
            double visualWidth = Math.Max(28, titleSize.Width);
            double visualHeight = Math.Max(28, titleSize.Height);
            double visualScaleX = Math.Clamp(ClipAnimation.Evaluate(clip, "ScaleX", now), .05, 8) *
                DynamicTitlePreset.ScaleMultiplier(clip, localTitleSeconds);
            double visualScaleY = Math.Clamp(ClipAnimation.Evaluate(clip, "ScaleY", now), .05, 8) *
                DynamicTitlePreset.ScaleMultiplier(clip, localTitleSeconds);
            double pivotX = frameLeft + frameWidth / 2 + positionX;
            double pivotY = frameTop + frameHeight / 2 + positionY;
            double angle = ClipAnimation.Evaluate(clip, "RotationDegrees", now);
            foreach (Point corner in new[]
            {
                new Point(x, y), new Point(x + visualWidth, y),
                new Point(x + visualWidth, y + visualHeight), new Point(x, y + visualHeight)
            })
            {
                Point scaled = new(
                    pivotX + (corner.X - pivotX) * visualScaleX,
                    pivotY + (corner.Y - pivotY) * visualScaleY);
                Point transformed = RotatedPoint(pivotX, pivotY,
                    scaled.X - pivotX, scaled.Y - pivotY, angle);
                if (transformed.X < frameLeft - .5 || transformed.X > frameLeft + frameWidth + .5 ||
                    transformed.Y < frameTop - .5 || transformed.Y > frameTop + frameHeight + .5)
                {
                    visibleObjectOutsideFrame = true;
                    break;
                }
            }
            // Each text layer is hit-testable over its visible layout box, so
            // direct double-click works even when text is the only clip.
            bool showTitleHitArea = true;
            var titleVisual = new Canvas
            {
                Width = visualWidth,
                Height = visualHeight,
                Background = showTitleHitArea ? Brushes.Transparent : null,
                Opacity = animatedOpacity,
                IsHitTestVisible = showTitleHitArea,
                Cursor = Cursors.SizeAll,
                ToolTip = "在预览区拖动文字调整位置；双击编辑内容和样式",
                // Export rotates and scales the full transparent sequence frame.
                // Express that same sequence-center pivot in this text canvas's
                // local coordinates, including left/right and top/bottom titles.
                RenderTransformOrigin = new Point(
                    (frameLeft + frameWidth / 2 + positionX - x) / Math.Max(28, titleSize.Width),
                    (frameTop + frameHeight / 2 + positionY - y) / Math.Max(28, titleSize.Height)),
                RenderTransform = new TransformGroup
                {
                    Children = new TransformCollection
                    {
                        new ScaleTransform(
                            visualScaleX, visualScaleY),
                        new RotateTransform(angle)
                    }
                }
            };
            Border? titleBackground = TitleLayout.Background(style, titleSize, scale,
                ParseBrush(style.BackgroundColor, Brushes.Transparent));
            if (titleBackground is not null) titleVisual.Children.Add(titleBackground);
            if (style.OutlineEnabled && style.OutlineThickness > 0)
            {
                var outline = new TextBlock
                {
                    Text = text.Text,
                    FontFamily = text.FontFamily,
                    FontSize = text.FontSize,
                    FontWeight = text.FontWeight,
                    FontStyle = text.FontStyle,
                    Foreground = ParseBrush(style.OutlineColor, Brushes.Black),
                    TextAlignment = text.TextAlignment,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 1
                };
                double stroke = Math.Clamp(style.OutlineThickness, 0, 50) * scale;
                foreach (Point offset in new[]
                {
                    new Point(-stroke, 0), new Point(stroke, 0), new Point(0, -stroke), new Point(0, stroke),
                    new Point(-stroke, -stroke), new Point(stroke, -stroke), new Point(-stroke, stroke), new Point(stroke, stroke)
                })
                {
                    var copy = new TextBlock
                    {
                        Text = outline.Text, FontFamily = outline.FontFamily, FontSize = outline.FontSize,
                        FontWeight = outline.FontWeight, FontStyle = outline.FontStyle,
                        Foreground = outline.Foreground, TextAlignment = outline.TextAlignment,
                        TextWrapping = TextWrapping.NoWrap, LineHeight = text.LineHeight
                    };
                    if (spacedText)
                        LetterSpacingText.Draw(titleVisual, style, scale, 0,
                            offset.X, offset.Y, outline.Foreground, drawUnderline: false);
                    else
                    {
                        copy.IsHitTestVisible = false;
                        Canvas.SetLeft(copy, offset.X);
                        Canvas.SetTop(copy, offset.Y);
                        titleVisual.Children.Add(copy);
                    }
                }
            }
            if (spacedText)
                LetterSpacingText.Draw(titleVisual, style, scale, 0,
                    0, 0, text.Foreground, text.Effect);
            else
            {
                text.Opacity = 1;
                text.IsHitTestVisible = false;
                titleVisual.Children.Add(text);
            }
            var selection = new Rectangle
            {
                Width = titleVisual.Width + 6,
                Height = titleVisual.Height + 6,
                Stroke = BrushFromHex("#FF9957"),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Fill = Brushes.Transparent,
                Visibility = ReferenceEquals(clip, _selectedClip) ? Visibility.Visible : Visibility.Collapsed,
                IsHitTestVisible = false
            };

            // Add dimmed background overlay when selected
            if (ReferenceEquals(clip, _selectedClip))
            {
                var dimOverlay = new Rectangle
                {
                    Width = titleSize.Width + 6,
                    Height = titleSize.Height + 6,
                    Fill = new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)), // Dimmed background
                    Stroke = Brushes.Transparent,
                    StrokeThickness = 0,
                    RadiusX = 4,
                    RadiusY = 4,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(dimOverlay, -3);
                Canvas.SetTop(dimOverlay, -3);
                Canvas.SetZIndex(dimOverlay, -1);
                titleVisual.Children.Insert(0, dimOverlay); // Insert at bottom
            }

            Canvas.SetLeft(selection, -3);
            Canvas.SetTop(selection, -3);
            titleVisual.Children.Add(selection);
            _titleSelectionOutlines[clip.Id] = selection;
            titleVisual.PreviewMouseLeftButtonDown += (_, args) => { StartTitleDrag(clip, titleVisual, args); SelectClip(clip); ShowSelectedObjectSettings(); };
            titleVisual.MouseLeftButtonDown += (_, args) =>
            {
                if (!args.Handled) { StartTitleDrag(clip, titleVisual, args); SelectClip(clip); ShowSelectedObjectSettings(); }
            };
            titleVisual.MouseMove += (_, args) => MoveTitleDrag(args);
            titleVisual.MouseLeftButtonUp += (_, args) => EndTitleDrag(args);
            titleVisual.LostMouseCapture += (_, _) => FinishTitleDrag();
            Canvas.SetLeft(titleVisual, x);
            Canvas.SetTop(titleVisual, y);
            PreviewOverlay.Children.Add(titleVisual);
            _titlePreviewVisuals[clip.Id] = titleVisual;
        }
        // Apply the same overflow check to source media. MediaBounds uses the
        // actual loaded source aspect ratio and crop rectangle, so the check
        // follows the same geometry used by the preview transform handles.
        foreach (TimelineClip clip in _project.Tracks.Where(track => !track.IsHidden && track.Kind != TrackKind.Audio)
            .SelectMany(track => track.Clips)
            .Where(candidate => candidate.Kind is ClipKind.Image or ClipKind.Video &&
                now >= candidate.StartSeconds && now < candidate.StartSeconds + candidate.DurationSeconds))
        {
            var bounds = MediaBounds(clip);
            if (bounds is null) continue;
            double sx = Math.Clamp(ClipAnimation.Evaluate(clip, "ScaleX", now), .05, 8);
            double sy = Math.Clamp(ClipAnimation.Evaluate(clip, "ScaleY", now), .05, 8);
            if (IsOutsidePreviewFrame(bounds.Value.CenterX, bounds.Value.CenterY,
                    bounds.Value.Width, bounds.Value.Height, sx, sy, bounds.Value.Angle,
                    frameLeft, frameTop, frameWidth, frameHeight))
            {
                visibleObjectOutsideFrame = true;
                break;
            }
        }
        // Stickers are vector text clips. Their geometry is available even
        // when no media source is loaded, so include them separately.
        if (!visibleObjectOutsideFrame)
        {
            foreach (TimelineClip clip in _project.Tracks.Where(track => !track.IsHidden && track.Kind != TrackKind.Audio)
                .SelectMany(track => track.Clips)
                .Where(candidate => candidate.Kind == ClipKind.Text &&
                    candidate.Text?.AdditionalData?.ContainsKey(GraphicShape.ExtensionKey) == true &&
                    now >= candidate.StartSeconds && now < candidate.StartSeconds + candidate.DurationSeconds))
            {
                try
                {
                    GraphicShape? graphic = GraphicShape.FromClip(clip);
                    if (graphic is null) continue;
                    double local = now - clip.StartSeconds;
                    double shapeWidth = _project.Sequence.Width * graphic.WidthFraction * scale;
                    double shapeHeight = _project.Sequence.Height * graphic.HeightFraction * scale;
                    double cx = frameLeft + frameWidth / 2 +
                        (ClipAnimation.Evaluate(clip, "PositionX", now) +
                            DynamicTitlePreset.HorizontalOffset(clip, local, _project.Sequence.Width)) * scale;
                    double cy = frameTop + frameHeight / 2 +
                        (ClipAnimation.Evaluate(clip, "PositionY", now) +
                            DynamicTitlePreset.VerticalOffset(clip, local, _project.Sequence.Height)) * scale;
                    double sx = Math.Clamp(ClipAnimation.Evaluate(clip, "ScaleX", now), .05, 8) *
                        DynamicTitlePreset.ScaleMultiplier(clip, local);
                    double sy = Math.Clamp(ClipAnimation.Evaluate(clip, "ScaleY", now), .05, 8) *
                        DynamicTitlePreset.ScaleMultiplier(clip, local);
                    if (IsOutsidePreviewFrame(cx, cy, shapeWidth, shapeHeight, sx, sy,
                            ClipAnimation.Evaluate(clip, "RotationDegrees", now),
                            frameLeft, frameTop, frameWidth, frameHeight))
                    {
                        visibleObjectOutsideFrame = true;
                        break;
                    }
                }
                catch (GraphicShapeException) { }
            }
        }
        if (visibleObjectOutsideFrame)
        {
            // Keep the warning on the program monitor while the playhead is
            // over the overflowing title. The frame itself remains the
            // clipping boundary, so this outline does not alter the render.
            var frameOutline = new Rectangle
            {
                Width = frameWidth,
                Height = frameHeight,
                Stroke = Brushes.White,
                StrokeThickness = Math.Clamp(Math.Min(width, height) * .0025, 1, 3),
                Fill = Brushes.Transparent,
                IsHitTestVisible = false,
                ToolTip = "可见对象超出画面范围"
            };
            Canvas.SetLeft(frameOutline, frameLeft);
            Canvas.SetTop(frameOutline, frameTop);
            Canvas.SetZIndex(frameOutline, 1000);
            PreviewOverlay.Children.Add(frameOutline);
        }
        DrawMediaSelectionOverlay();
    }

    private static bool IsOutsidePreviewFrame(double centerX, double centerY,
        double width, double height, double scaleX, double scaleY, double angle,
        double frameLeft, double frameTop, double frameWidth, double frameHeight)
    {
        double halfWidth = Math.Abs(width * scaleX) / 2;
        double halfHeight = Math.Abs(height * scaleY) / 2;
        foreach (Point corner in new[]
        {
            new Point(-halfWidth, -halfHeight), new Point(halfWidth, -halfHeight),
            new Point(halfWidth, halfHeight), new Point(-halfWidth, halfHeight)
        })
        {
            Point transformed = RotatedPoint(centerX, centerY, corner.X, corner.Y, angle);
            if (transformed.X < frameLeft - .5 || transformed.X > frameLeft + frameWidth + .5 ||
                transformed.Y < frameTop - .5 || transformed.Y > frameTop + frameHeight + .5)
                return true;
        }
        return false;
    }

    private static Brush BrushFromHex(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;

    private void StartTitleDrag(TimelineClip clip, Canvas visual, MouseButtonEventArgs args)
    {
        Point clickPoint = args.GetPosition(PreviewOverlay);
        long clickTick = Environment.TickCount64;
        bool sameTitleDoubleClick = _lastTitleClickClipId == clip.Id &&
            clickTick - _lastTitleClickTick <= 500 &&
            Math.Abs(clickPoint.X - _lastTitleClickPoint.X) <= 6 &&
            Math.Abs(clickPoint.Y - _lastTitleClickPoint.Y) <= 6;
        _lastTitleClickClipId = sameTitleDoubleClick ? null : clip.Id;
        _lastTitleClickTick = clickTick;
        _lastTitleClickPoint = clickPoint;
        // Releasing mouse capture after the first click can cause WPF to
        // report two single clicks. Keep a same-title fallback for that case.
        if (args.ClickCount >= 2 || sameTitleDoubleClick)
        {
            if (_draggingTitleClip is not null)
            {
                _draggingTitleVisual?.ReleaseMouseCapture();
                FinishTitleDrag();
            }
            OpenPreviewTextEditor(clip);
            args.Handled = true;
            return;
        }
        if (_draggingTitleClip is not null) return;
        _previewSurfaceFirstClick = false;
        _selectedClip = clip;
        _selectedTrack = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
        _selectedClips.Clear();
        _selectedClips.Add(clip);
        DrawTimeline();
        foreach (var item in _titleSelectionOutlines)
            item.Value.Visibility = item.Key == clip.Id ? Visibility.Visible : Visibility.Collapsed;
        if (_selectedTrack?.IsLocked == true)
        {
            StatusText.Text = "轨道已锁定，无法移动文字";
            args.Handled = true;
            return;
        }
        if (_isPlaying)
        {
            _isPlaying = false;
            _playbackTimer.Stop();
            _playbackClock.Stop();
            PreviewMediaElement.Pause();
            PauseAudioPreview();
            PlayPauseButton.Content = "▶";
        }
        _draggingTitleClip = clip;
        _draggingTitleVisual = visual;
        _titleDragStart = clickPoint;
        // Position keyframes can differ from the base transform. Hold the
        // displayed position too, so a hand drag starts at the visible glyph.
        double now = _project.Sequence.PlayheadSeconds;
        _titleDisplayedStartX = ClipAnimation.Evaluate(clip, "PositionX", now);
        _titleDisplayedStartY = ClipAnimation.Evaluate(clip, "PositionY", now);
        _titleDragChanged = false;
        if (!visual.CaptureMouse())
        {
            _draggingTitleClip = null;
            _draggingTitleVisual = null;
            StatusText.Text = "无法捕获鼠标，文字拖动未开始 · PREVIEW-INPUT";
        }
        args.Handled = true;
    }

    private void MoveTitleDrag(MouseEventArgs args)
    {
        if (_draggingTitleClip is null || _draggingTitleVisual is null || args.LeftButton != MouseButtonState.Pressed) return;
        Point point = args.GetPosition(PreviewOverlay);
        double dx = point.X - _titleDragStart.X;
        double dy = point.Y - _titleDragStart.Y;
        if (!_titleDragChanged && Math.Abs(dx) + Math.Abs(dy) < 2) return;
        if (!_titleDragChanged)
        {
            SaveUndoState();
            _titleDragChanged = true;
            _lastTitleClickClipId = null;
        }
        double offsetX = Math.Clamp(_titleDisplayedStartX + dx / Math.Max(_previewTitleScale, .001),
            -_project.Sequence.Width * 4, _project.Sequence.Width * 4) - _titleDisplayedStartX;
        double offsetY = Math.Clamp(_titleDisplayedStartY + dy / Math.Max(_previewTitleScale, .001),
            -_project.Sequence.Height * 4, _project.Sequence.Height * 4) - _titleDisplayedStartY;
        double stepX = ShiftAnimatedVisualProperty(_draggingTitleClip, "PositionX", _titleDisplayedStartX + offsetX);
        double stepY = ShiftAnimatedVisualProperty(_draggingTitleClip, "PositionY", _titleDisplayedStartY + offsetY);
        MovePreviewTitleVisual(_draggingTitleVisual, stepX, stepY);
        if (_graphicPreviewVisuals.TryGetValue(_draggingTitleClip.Id, out Canvas? graphic) &&
            !ReferenceEquals(graphic, _draggingTitleVisual))
            MovePreviewTitleVisual(graphic, stepX, stepY);
        if (_titlePreviewVisuals.TryGetValue(_draggingTitleClip.Id, out Canvas? title) &&
            !ReferenceEquals(title, _draggingTitleVisual))
            MovePreviewTitleVisual(title, stepX, stepY);
        _titleDragStart = point;
        _titleDisplayedStartX += stepX;
        _titleDisplayedStartY += stepY;
        args.Handled = true;
    }

    private void MovePreviewTitleVisual(Canvas visual, double stepX, double stepY)
    {
        Canvas.SetLeft(visual, Canvas.GetLeft(visual) + stepX * _previewTitleScale);
        Canvas.SetTop(visual, Canvas.GetTop(visual) + stepY * _previewTitleScale);
    }

    private void EndTitleDrag(MouseButtonEventArgs args)
    {
        if (_draggingTitleVisual is null) return;
        _draggingTitleVisual.ReleaseMouseCapture();
        FinishTitleDrag();
        args.Handled = true;
    }

    private void FinishTitleDrag()
    {
        if (_draggingTitleClip is null) return;
        TimelineClip clip = _draggingTitleClip;
        bool changed = _titleDragChanged;
        if (changed)
        {
            // Mark before clearing drag state so a stale async frame from a
            // prior project revision cannot replace the released title.
            MarkProjectDirty();
            _lastTitleLayoutKey = null;
        }
        _draggingTitleClip = null;
        _draggingTitleVisual = null;
        _titleDragChanged = false;
        if (!changed) return;
        StatusText.Text = $"已移动文字：{clip.Name}  X {clip.Transform.PositionX:0}  Y {clip.Transform.PositionY:0}";
        SeekPreview(_project.Sequence.PlayheadSeconds);
    }

    private void OpenPreviewTextEditor(TimelineClip clip)
    {
        if (_previewTextEditorPending) return;
        _lastTitleClickClipId = null;
        _previewSurfaceFirstClick = false;
        _selectedClip = clip;
        _selectedTrack = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
        _selectedClips.Clear();
        _selectedClips.Add(clip);
        DrawTimeline();
        _lastTitleLayoutKey = null;
        DrawTextOverlay();
        if (_isPlaying) TogglePlayback();
        // Opening a modal window inside MouseLeftButtonDown can keep the
        // preview's mouse capture alive until the dialog closes. Wait until
        // the current click has finished dispatching before showing it.
        _previewTextEditorPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (_project.Tracks.Any(track => track.Clips.Contains(clip)))
                    ShowTextProperties(clip);
            }
            finally { _previewTextEditorPending = false; }
        }), DispatcherPriority.Background);
    }


    private static Brush ParseBrush(string? value, Brush fallback)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(value ?? string.Empty)!; }
        catch (Exception exception) when (exception is FormatException or ArgumentException or NotSupportedException)
        { return fallback; }
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(value ?? string.Empty); }
        catch (Exception exception) when (exception is FormatException or ArgumentException or NotSupportedException)
        { return fallback; }
    }

    private void TogglePlayback()
    {
        if (!_isPlaying && !_project.Tracks.Any(track => track.Clips.Count > 0))
        {
            StatusText.Text = "时间线中没有可播放的片段";
            return;
        }
        _isPlaying = !_isPlaying;
        PlayPauseButton.Content = _isPlaying ? "Ⅱ" : "▶";
        // Space and the play button always start forward. Without this, a J
        // reverse play followed by Space would keep running backwards.
        if (_isPlaying) _playbackDirection = 1;
        if (_isPlaying)
        {
            _playbackEndSeconds = _project.Tracks.SelectMany(track => track.Clips)
                .Select(clip => clip.StartSeconds + clip.DurationSeconds).DefaultIfEmpty(0).Max();
            _playbackClockStartSeconds = _project.Sequence.PlayheadSeconds;
            _playbackClock.Restart();
            _playbackTimer.Start();
            UpdateAudioPreview(_project.Sequence.PlayheadSeconds);
        }
        else { _playbackTimer.Stop(); _playbackClock.Stop(); PreviewMediaElement.Pause(); PauseAudioPreview(); StopCachedPreview(); SeekPreview(_project.Sequence.PlayheadSeconds); }
    }

    private void SetPlaybackDirection(int direction)
    {
        if (!_project.Tracks.Any(track => track.Clips.Count > 0))
        {
            StatusText.Text = "时间线中没有可播放的片段";
            return;
        }
        // Reverse play from the very start has nowhere to go; the next tick
        // would stop and rewind, which looks like the key did nothing.
        if (direction < 0 && _project.Sequence.PlayheadSeconds <= 0)
        {
            StatusText.Text = "已在时间线开头，无法倒放";
            return;
        }
        _playbackDirection = direction < 0 ? -1 : 1;
        if (_isPlaying) { _isPlaying = false; _playbackTimer.Stop(); _playbackClock.Stop(); PauseAudioPreview(); }
        _isPlaying = true;
        PlayPauseButton.Content = "Ⅱ";
        _playbackEndSeconds = _project.Tracks.SelectMany(track => track.Clips)
            .Select(clip => clip.StartSeconds + clip.DurationSeconds).DefaultIfEmpty(0).Max();
        _playbackClockStartSeconds = _project.Sequence.PlayheadSeconds;
        _playbackClock.Restart(); _playbackTimer.Start(); UpdateAudioPreview(_project.Sequence.PlayheadSeconds);
    }

    private void StopPlayback()
    {
        _isPlaying = false;
        _playbackTimer.Stop();
        _playbackClock.Stop();
        PreviewMediaElement.Stop();
        StopAudioPreview();
        PlayPauseButton.Content = "▶";
        SeekPreview(0);
    }

    private string FormatTime(double seconds)
    {
        int fps = Math.Max(1, (int)Math.Round(_project.Sequence.FramesPerSecond));
        int totalFrames = (int)Math.Max(0, Math.Round(seconds * fps));
        int frame = totalFrames % fps;
        TimeSpan time = TimeSpan.FromSeconds(totalFrames / fps);
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}:{frame:00}";
    }
}

