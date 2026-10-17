using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Draws and edits the track canvas. Timeline coordinates are calculated from
/// seconds and the zoom value rather than persisted as screen pixels.
/// </summary>
public partial class MainWindow
{
    private const double HeaderSpace = 140;
    private const double RulerSpace = 30;
    private const double TrackSpace = 74;
    private const double BasePixelsPerSecond = 54;
    private TimelineClip? _selectedClip;
    private ClipKeyframe? _selectedKeyframe;
    private ClipTransition? _selectedTransition;
    private TimelineClip? _selectedTransitionIncoming;
    private TimelineClip? _selectedTransitionPrior;
    private TimelineTrack? _selectedTransitionTrack;
    private TimelineTrack? _selectedTrack;
    private TimelineClip? _clipboardClip;
    private readonly HashSet<TimelineClip> _selectedClips = [];
    private readonly Dictionary<TimelineClip, double> _moveCompanionOriginalStarts = [];
    private readonly Stack<string> _undoStates = new();
    private readonly Stack<string> _redoStates = new();
    private bool _historyOperationInProgress;
    private bool _snapEnabled = true;
    private bool _movingClip;
    private bool _trimmingStart;
    private bool _trimmingEnd;
    private readonly System.Windows.Threading.DispatcherTimer _clipEdgeScrollTimer = new()
    { Interval = TimeSpan.FromMilliseconds(35) };
    private Point _moveOrigin;
    private double _moveOriginalStart;
    private double _moveOriginalDuration;
    private double _moveOriginalSourceIn;
    private List<ClipTimeRemapPoint>? _moveOriginalTimeRemap;
    private List<ClipKeyframe>? _moveOriginalKeyframes;
    private double _moveOriginalMulticamLocalStart;
    private TimelineTrack? _moveOriginalTrack;
    private string? _moveOriginalProjectJson;
    private TimelineTrack? _contextTrack;
    private bool _scrubbingPlayhead;
    private double _playheadGrabOffset;
    private DateTime _lastScrubPreviewUtc = DateTime.MinValue;
    private Rectangle? _playheadHitTarget;
    private Rectangle? _playheadGrip;
    private Line? _playheadVisualLine;
    private TimelineTrack? _draggingTrack;
    private Point _trackDragOrigin;
    private bool _trackDragStarted;
    // While a track header is dragged, the track itself is hidden but still
    // occupies a slot. The predicted drop index opens a gap, and the floating
    // block follows the pointer outside the canvas layout.
    private int _trackDragTargetIndex = -1;
    private Border? _trackDragBlock;
    private DispatcherTimer? _trackDragScrollTimer;
    private double _trackDragScrollStep;
    private Point _trackDragPointer;
    private Border? _dropGhost;
    private Point _timelineContextPoint;
    // A dropped file can reach the timeline before ffprobe has returned its
    // duration. Keep only the provisional clips from that gesture so a late
    // probe never changes clips that the user created or trimmed manually.
    private readonly List<PendingDropBatch> _pendingDropBatches = [];

    private sealed class PendingDropBatch(ProjectDocument project, long generation)
    {
        public ProjectDocument Project { get; } = project;
        public long Generation { get; } = generation;
        public List<PendingDropClip> Clips { get; } = [];
    }

    private sealed class PendingDropClip(TimelineClip clip, double expectedStart)
    {
        public TimelineClip Clip { get; } = clip;
        public double ExpectedStart { get; set; } = expectedStart;
        public double ExpectedDuration { get; set; } = clip.DurationSeconds;
        public bool AwaitsDuration { get; set; } = clip.Kind is ClipKind.Video or ClipKind.Audio;
    }

    private double PixelsPerSecond => BasePixelsPerSecond * TimelineZoomSlider.Value;

    private void InitializeTimeline()
    {
        // Keep trimming while the pointer rests at either visible edge.
        _clipEdgeScrollTimer.Tick += (_, _) =>
        {
            if ((!_trimmingStart && !_trimmingEnd && !_movingClip) ||
                Mouse.LeftButton != MouseButtonState.Pressed)
            { _clipEdgeScrollTimer.Stop(); return; }
            if (ScrollTimelineDuringClipGesture())
                TimelineCanvas_MouseMove(TimelineCanvas,
                    new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount));
        };
        TimelineZoomSlider.ValueChanged += (_, _) => RefreshTimeline();
        TimelineScrollViewer.PreviewMouseWheel += TimelineScrollViewer_PreviewMouseWheel;
        TimelineScrollViewer.SizeChanged += (_, _) => RefreshTimeline();
        TimelineScrollViewer.ScrollChanged += (_, args) =>
        {
            if (args.HorizontalChange != 0 || args.VerticalChange != 0) DrawTimeline();
        };
        TimelineCanvas.SizeChanged += (_, _) => DrawTimeline();
        TimelineCanvas.MouseLeftButtonDown += TimelineCanvas_MouseLeftButtonDown;
        TimelineCanvas.PreviewMouseRightButtonDown += (_, args) =>
        {
            _timelineContextPoint = args.GetPosition(TimelineCanvas);
            if (IsTransitionMarker(args.OriginalSource as DependencyObject)) return;
            // The playhead hit target is drawn above every clip. Resolve a
            // right click against the clip at this point before WPF chooses
            // the canvas context menu; otherwise a clip under the orange
            // line exposes only track commands.
            if (ReferenceEquals(args.OriginalSource, _playheadHitTarget))
            {
                Point hit = args.GetPosition(TimelineCanvas);
                TimelineTrack? hitTrack = TrackAt(hit.Y);
                TimelineClip? hitClip = hitTrack?.Clips
                    .Where(item => hit.X >= HeaderSpace + item.StartSeconds * PixelsPerSecond &&
                        hit.X <= HeaderSpace + (item.StartSeconds + item.DurationSeconds) * PixelsPerSecond)
                    .OrderByDescending(item => item.StartSeconds).FirstOrDefault();
                if (hitClip is not null)
                {
                    var menu = CreateClipContextMenu(hitClip);
                    menu.PlacementTarget = TimelineCanvas;
                    menu.IsOpen = true;
                    args.Handled = true;
                    return;
                }
            }
            _contextTrack = TrackAt(args.GetPosition(TimelineCanvas).Y);
            _selectedTrack = _contextTrack;
        };
        TimelineCanvas.MouseMove += TimelineCanvas_MouseMove;
        TimelineCanvas.MouseLeftButtonUp += TimelineCanvas_MouseLeftButtonUp;
        TimelineCanvas.PreviewMouseLeftButtonDown += TimelineCanvas_TrackPreviewMouseLeftButtonDown;
        InitializeTimelineControls();
        TimelineCanvas.LostMouseCapture += (_, _) =>
        {
            // Another control can take capture before mouse-up arrives.
            // Commit a clip gesture as well as the preview, otherwise losing
            // capture drops the undo entry for edits already visible on the
            // mutable project graph.
            if (_rubberBandDrag is not null) FinishRubberBandDrag();
            if (_bezierHandleDrag is not null) FinishBezierHandleDrag();
            if (_draggingTrack is not null)
            {
                // Losing capture abandons the move, so clear the floating block
                // and the edge-scroll timer or they would outlive the gesture.
                _draggingTrack = null;
                _trackDragStarted = false;
                _trackDragTargetIndex = -1;
                StopTrackDragEdgeScroll();
                HideTrackDragBlock();
                DrawTimeline();
            }
            if (_movingClip || _trimmingStart || _trimmingEnd)
                FinishClipGesture();
            if (!_scrubbingPlayhead) return;
            _scrubbingPlayhead = false;
            SeekPreview(_project.Sequence.PlayheadSeconds);
        };
        TimelineCanvas.DragOver += TimelineCanvas_DragOver;
        TimelineCanvas.Drop += TimelineCanvas_Drop;
        AddTrackButton.Click += (_, _) => PromptAddTrack();
        AddVideoTrackMenuItem.Click += (_, _) => AddTrack(TrackKind.Video);
        AddAudioTrackMenuItem.Click += (_, _) => AddTrack(TrackKind.Audio);
        TimelineAddTrackMenuItem.Click += (_, _) => PromptAddTrack();
        SplitButton.Click += (_, _) => SplitSelectedClip();
        SplitMenuItem.Click += (_, _) => SplitSelectedClip();
        TimelineSplitContextItem.Click += (_, _) => SplitSelectedClip();
        DeleteButton.Click += (_, _) => DeleteSelectedClip();
        PropertiesButton.Click += (_, _) => EditSelectedClipProperties();
        DeleteMenuItem.Click += (_, _) => DeleteSelectedClip();
        RippleDeleteMenuItem.Click += (_, _) => RippleDeleteSelectedClip();
        TimelineRippleDeleteItem.Click += (_, _) => RippleDeleteSelectedClip();
        AddClipMenuCommands();
        DuplicateMenuItem.Click += (_, _) => DuplicateSelectedClip();
        TimelineDuplicateItem.Click += (_, _) => DuplicateSelectedClip();
        UndoButton.Click += (_, _) => Undo();
        UndoMenuItem.Click += (_, _) => Undo();
        RedoButton.Click += (_, _) => Redo();
        RedoMenuItem.Click += (_, _) => Redo();
        SnapButton.Click += (_, _) =>
        {
            _snapEnabled = !_snapEnabled;
            SnapButton.Content = _snapEnabled ? "⊞ 吸附" : "□ 吸附";
            StatusText.Text = _snapEnabled ? "时间线吸附已开启" : "时间线吸附已关闭";
        };
        ZoomInMenuItem.Click += (_, _) => TimelineZoomSlider.Value = Math.Min(TimelineZoomSlider.Maximum, TimelineZoomSlider.Value * 1.25);
        ZoomOutMenuItem.Click += (_, _) => TimelineZoomSlider.Value = Math.Max(TimelineZoomSlider.Minimum, TimelineZoomSlider.Value / 1.25);
        FitTimelineMenuItem.Click += (_, _) => FitTimeline();
        TimelineZoomFitItem.Click += (_, _) => FitTimeline();
        TimelinePasteMenuItem.Click += (_, _) => PasteClipboardClip();
        SpeedRampMenuItem.Click += (_, _) => EditSelectedTimeRemapping();
        TimelineLockTrackItem.Click += (_, _) => ChangeContextTrack(track => track.IsLocked = !track.IsLocked);
        TimelineMuteTrackItem.Click += (_, _) => ChangeContextTrack(track => track.IsMuted = !track.IsMuted);
        TimelineHideTrackItem.Click += (_, _) => ChangeContextTrack(track => track.IsHidden = !track.IsHidden);
        TimelineRemoveTrackItem.Click += (_, _) => RemoveContextTrack();
        DeleteEmptyTracksMenuItem.Click += (_, _) => RemoveEmptyTracks();
        TimelineRemoveEmptyTracksItem.Click += (_, _) => RemoveEmptyTracks();
        MoveTrackUpMenuItem.Click += (_, _) => MoveContextTrack(-1);
        MoveTrackDownMenuItem.Click += (_, _) => MoveContextTrack(1);
        TimelineMoveTrackUpItem.Click += (_, _) => MoveContextTrack(-1);
        TimelineMoveTrackDownItem.Click += (_, _) => MoveContextTrack(1);
        // Commands are also available when the user right-clicks blank
        // timeline space, where no clip-specific menu is present.
        if (TimelineCanvas.ContextMenu is ContextMenu emptyMenu)
        {
            AddContextCommand(emptyMenu, "插入剪贴板片段（推开后续）", InsertClipboardAtPlayhead);
            AddContextCommand(emptyMenu, "覆盖粘贴（替换范围）", OverwriteClipboardAtPlayhead);
            AddContextCommand(emptyMenu, "从所选视频创建多机位…", () =>
                _ = ShowCreateMulticamDialogAsync());
        }
        InitializeMulticamCommands();
        InitializeGeneratedClipCommands();
    }

    private void AddClipMenuCommands()
    {
        if (FindMainMenu() is not Menu menu || menu.Items.OfType<MenuItem>().Any(item =>
            string.Equals(item.Header?.ToString(), "本地剪辑工具(_L)", StringComparison.Ordinal))) return;
        var local = new MenuItem { Header = "本地剪辑工具(_L)" };
        local.Items.Add(ContextCommand("提取选中片段（关闭空隙）", ExtractSelectedClip));
        local.Items.Add(ContextCommand("匹配帧到源监视器", MatchFrameSelectedClip));
        local.Items.Add(ContextCommand("打开序列索引", ShowSequenceIndex));
        menu.Items.Insert(Math.Max(0, menu.Items.Count - 1), local);
    }

    /// <summary>
    /// Clears pointers into the active sequence when a different sequence or
    /// project replaces its track collection. The project commands call this
    /// after switching, and history restoration calls it after deserializing.
    /// </summary>
    private void ClearTimelineSelection()
    {
        ClearTransitionSelection();
        _selectedClip = null;
        _selectedKeyframe = null;
        _selectedTrack = null;
        _contextTrack = null;
        _selectedClips.Clear();
        _moveCompanionOriginalStarts.Clear();
        if (SettingsHost is not null) CloseSettingsTabs();
    }

    private void PromptAddTrack()
    {
        ProjectPickerItem? choice = PromptChoice("添加轨道", "选择轨道类型",
            [new("video", "视频轨道"), new("audio", "音频轨道")], "video");
        if (choice?.Id == "audio") AddTrack(TrackKind.Audio);
        else if (choice?.Id == "video") AddTrack(TrackKind.Video);
    }

    private void RefreshTimeline()
    {
        if (TimelineCanvas is null || TimelineScrollViewer is null) return;
        double end = Math.Max(60, _project.Tracks.SelectMany(track => track.Clips)
            .Select(clip => clip.StartSeconds + clip.DurationSeconds).DefaultIfEmpty(0).Max() + 20);
        TimelineCanvas.Width = Math.Max(0, Math.Max(TimelineScrollViewer.ViewportWidth, HeaderSpace + end * PixelsPerSecond));
        TimelineCanvas.Height = Math.Max(0, Math.Max(TimelineScrollViewer.ViewportHeight,
            RulerSpace + Math.Max(5, _project.Tracks.Count) * TrackSpace));
        DrawTimeline();
        TotalTimeText.Text = FormatTime(Math.Max(0, end - 20));
    }

    private void DrawTimeline()
    {
        TimelineCanvas.Children.Clear();
        double width = TimelineCanvas.Width;
        double height = TimelineCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        double visibleLeft = Math.Max(HeaderSpace, TimelineScrollViewer.HorizontalOffset - PixelsPerSecond);
        double visibleRight = Math.Min(width, TimelineScrollViewer.HorizontalOffset +
            Math.Max(TimelineScrollViewer.ViewportWidth, 1) + PixelsPerSecond);
        AddRectangle(0, 0, width, RulerSpace, "#22252D");
        int tickSeconds = Math.Max(1, (int)Math.Ceiling(70 / PixelsPerSecond));
        int firstTick = Math.Max(0, (int)Math.Floor((visibleLeft - HeaderSpace) / PixelsPerSecond / tickSeconds) * tickSeconds);
        for (int second = firstTick; HeaderSpace + second * PixelsPerSecond < visibleRight; second += tickSeconds)
        {
            double x = HeaderSpace + second * PixelsPerSecond;
            AddLine(x, 0, x, TimelineCanvas.Height, second % (tickSeconds * 5) == 0 ? "#30343D" : "#252932", 1);
            AddText(FormatRuler(second), x + 4, 5, "#A5AAB6", 10);
        }

        AddRectangle(0, 0, HeaderSpace, height, "#1D2027");
        AddRectangle(0, 0, HeaderSpace, RulerSpace, "#22252D");

        for (int index = 0; index < _project.Tracks.Count; index++)
        {
            TimelineTrack track = _project.Tracks[index];
            double y = RulerSpace + TrackSlotIndex(index) * TrackSpace;
            if (y + TrackSpace < TimelineScrollViewer.VerticalOffset - TrackSpace ||
                y > TimelineScrollViewer.VerticalOffset +
                    Math.Max(1, TimelineScrollViewer.ViewportHeight) + TrackSpace)
                continue;
            // The dragged track leaves an empty lane at the predicted drop
            // position. Its own row is skipped: the floating block shows it.
            if (ReferenceEquals(track, _draggingTrack) && _trackDragStarted)
            {
                AddRectangle(0, y, width, TrackSpace, "#101218");
                AddDashedDropLane(y, width);
                continue;
            }
            AddRectangle(0, y, width, TrackSpace, index % 2 == 0 ? "#151820" : "#191C24");
            AddRectangle(0, y, HeaderSpace, TrackSpace - 1, "#23262E");
            AddTrackHeader(track, y);
            DrawTrackTarget(track, y);
            AddTrackToggle(track, track.IsLocked ? "锁" : "开", 10, y + 33, () =>
            {
                SaveUndoState(); track.IsLocked = !track.IsLocked; MarkProjectDirty(); RefreshTimeline();
            });
            AddTrackToggle(track, track.IsMuted ? "静" : "声", 43, y + 33, () =>
            {
                SaveUndoState(); track.IsMuted = !track.IsMuted; MarkProjectDirty(); RefreshTimeline();
            });
            if (track.Kind != TrackKind.Audio)
                AddTrackToggle(track, track.IsHidden ? "隐" : "显", 76, y + 33, () =>
                {
                    SaveUndoState(); track.IsHidden = !track.IsHidden; MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
                });
            AddTrackToggle(track, track.IsSyncLocked ? "联" : "独", 106, y + 33, () =>
            {
                SaveUndoState(); track.IsSyncLocked = !track.IsSyncLocked;
                MarkProjectDirty(); RefreshTimeline();
                StatusText.Text = track.IsSyncLocked ? "轨道已加入同步编辑" : "轨道已脱离同步编辑";
            });
            AddLine(0, y + TrackSpace - 1, width, y + TrackSpace - 1, "#353943", 1);
            foreach (TimelineClip clip in track.Clips.OrderBy(c => c.StartSeconds))
            {
                double clipLeft = HeaderSpace + clip.StartSeconds * PixelsPerSecond;
                double clipRight = clipLeft + Math.Max(12, clip.DurationSeconds * PixelsPerSecond);
                if (clipRight >= visibleLeft && clipLeft <= visibleRight) DrawClip(track, clip, y);
            }
            foreach (TimelineClip incoming in track.Clips)
                if (BoundaryCrossfade.TryGetTransition(track, incoming,
                    _project.Sequence.FramesPerSecond, out TimelineClip? prior,
                    out ClipTransition? transition, out double duration) &&
                    transition is not null && prior is not null)
                {
                    double x = HeaderSpace + incoming.StartSeconds * PixelsPerSecond;
                    if (x >= visibleLeft && x <= visibleRight)
                        DrawTransitionMarker(track, incoming, prior, transition, duration, x, y);
                }
        }

        // Sequence markers are visible in the ruler as well as available to
        // the magnetic alignment calculation during a pointer edit.
        foreach (TimelineMarker marker in _project.Markers)
        {
            double markerX = HeaderSpace + marker.TimeSeconds * PixelsPerSecond;
            if (markerX < visibleLeft || markerX > visibleRight) continue;
            var markerGlyph = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(0, 0), new Point(RulerSpace * .31, 0),
                    new Point(RulerSpace * .155, RulerSpace * .3)
                },
                Fill = SafeMarkerBrush(marker.Color),
                Stroke = Brushes.White,
                StrokeThickness = 1,
                ToolTip = $"{marker.Name}\n{marker.Notes}\n{FormatRuler(marker.TimeSeconds)}",
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(markerGlyph, markerX - RulerSpace * .155);
            Canvas.SetTop(markerGlyph, 0);
            markerGlyph.MouseLeftButtonDown += (_, args) =>
            {
                SeekPreview(marker.TimeSeconds);
                args.Handled = true;
            };
            TimelineCanvas.Children.Add(markerGlyph);
        }

        double playheadX = HeaderSpace + _project.Sequence.PlayheadSeconds * PixelsPerSecond;
        _playheadVisualLine = new Line
        {
            X1 = playheadX, X2 = playheadX, Y1 = 0, Y2 = height,
            Stroke = Brush("#FF9957"), StrokeThickness = 2,
            IsHitTestVisible = false
        };
        TimelineCanvas.Children.Add(_playheadVisualLine);
        double gripSize = RulerSpace / 3;
        _playheadGrip = new Rectangle
        {
            Width = gripSize, Height = gripSize,
            Fill = Brush("#FF9957"), IsHitTestVisible = false
        };
        Canvas.SetLeft(_playheadGrip, playheadX - gripSize / 2);
        Canvas.SetTop(_playheadGrip, 0);
        TimelineCanvas.Children.Add(_playheadGrip);
        // A transparent target makes the narrow playhead easy to grab
        // without hiding the underlying clips or changing its appearance.
        _playheadHitTarget = new Rectangle
        {
            Width = RulerSpace * 0.4,
            Height = height,
            Fill = Brushes.Transparent,
            Cursor = Cursors.SizeWE,
            ToolTip = "拖动播放头预览时间线",
            IsHitTestVisible = true
        };
        Canvas.SetLeft(_playheadHitTarget, playheadX - _playheadHitTarget.Width / 2);
        Canvas.SetTop(_playheadHitTarget, 0);
        TimelineCanvas.Children.Add(_playheadHitTarget);
    }

    private void AddTrackToggle(TimelineTrack track, string text, double x, double y, Action action)
    {
        var button = new Button
        {
            Content = text,
            ToolTip = $"{track.Name}：{text}",
            Padding = new Thickness(4, 1, 4, 1),
            Foreground = Brushes.White,
            Background = Brush("#3C414C"),
            BorderThickness = new Thickness(0)
        };
        button.Click += (_, _) => action();
        Canvas.SetLeft(button, x);
        Canvas.SetTop(button, y);
        TimelineCanvas.Children.Add(button);
    }

    /// <summary>
    /// Maps a track's list index onto the row it should occupy on screen.
    /// During a header drag the dragged track takes the predicted drop slot and
    /// the tracks it passes shift by one, which opens the gap the pointer is
    /// heading for without touching the project until the drop commits.
    /// </summary>
    private int TrackSlotIndex(int listIndex)
    {
        if (_draggingTrack is null || !_trackDragStarted || _trackDragTargetIndex < 0)
            return listIndex;
        int from = _project.Tracks.IndexOf(_draggingTrack);
        if (from < 0) return listIndex;
        int to = Math.Clamp(_trackDragTargetIndex, 0, _project.Tracks.Count - 1);
        if (listIndex == from) return to;
        // Tracks between the origin and the target slide one row toward the
        // vacated lane; everything outside that span keeps its row.
        if (from < to && listIndex > from && listIndex <= to) return listIndex - 1;
        if (from > to && listIndex >= to && listIndex < from) return listIndex + 1;
        return listIndex;
    }

    /// <summary>Marks the lane that will receive the dragged track.</summary>
    private void AddDashedDropLane(double y, double width)
    {
        var lane = new Rectangle
        {
            Width = Math.Max(1, width),
            Height = Math.Max(1, TrackSpace - 4),
            Stroke = Brush("#FF9957"),
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Fill = Brush("#1C1F27"),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(lane, 0);
        Canvas.SetTop(lane, y + 2);
        TimelineCanvas.Children.Add(lane);
    }

    private void AddTrackHeader(TimelineTrack track, double y)
    {
        var header = new Border
        {
            Width = HeaderSpace,
            Height = TrackSpace * 0.42,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeNS,
            ToolTip = "拖动轨道名称上下调整顺序；右键可上移、下移或删除轨道",
            Tag = track,
            Child = new TextBlock
            {
                Text = "⋮⋮  " + track.Name,
                Foreground = Brushes.White,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(8, 7, 5, 0)
            }
        };
        Canvas.SetLeft(header, 0);
        Canvas.SetTop(header, y);
        TimelineCanvas.Children.Add(header);
    }

    private static Border? FindTrackHeader(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Border border && border.Tag is TimelineTrack) return border;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void TimelineCanvas_TrackPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (FindTrackHeader(args.OriginalSource as DependencyObject)?.Tag is not TimelineTrack track) return;
        if (_scrubbingPlayhead || _movingClip || _trimmingStart || _trimmingEnd ||
            _rubberBandDrag is not null || _bezierHandleDrag is not null) return;
        _selectedTrack = track;
        _contextTrack = track;
        _selectedClip = null;
        _selectedClips.Clear();
        CloseSettingsTabs();
        _draggingTrack = track;
        _trackDragOrigin = args.GetPosition(TimelineCanvas);
        _trackDragStarted = false;
        DrawTimeline();
        TimelineCanvas.CaptureMouse();
        args.Handled = true;
    }

    private void DrawClip(TimelineTrack track, TimelineClip clip, double trackY)
    {
        double x = HeaderSpace + clip.StartSeconds * PixelsPerSecond;
        double width = Math.Max(12, clip.DurationSeconds * PixelsPerSecond);
        string color = clip.Kind switch
        {
            ClipKind.Text => "#675CB0",
            ClipKind.Audio => "#357C70",
            ClipKind.Image => "#396C9E",
            ClipKind.Sequence => "#866AB5",
            _ => "#9A6845"
        };
        bool selected = ReferenceEquals(clip, _selectedClip) || _selectedClips.Contains(clip);
        if (clip.MulticamGroupId is not null) color = "#5D5EA2";
        if (selected)
        {
            color = clip.Kind switch
            {
                ClipKind.Text => "#3B3566",
                ClipKind.Audio => "#214E47",
                ClipKind.Image => "#254766",
                _ => "#5B3B29"
            };
        }
        var border = new Border
        {
            Background = Brush(color),
            BorderBrush = Brush(selected ? "#FFFFFF" : "#9BA3AF"),
            BorderThickness = new Thickness(selected ? 2 : 1),
            CornerRadius = new CornerRadius(4),
            Child = new TextBlock
            {
                Text = (clip.MulticamGroupId is not null ? "▧  " :
                    clip.Kind == ClipKind.Text ? "T  " : "▣  ") + clip.Name,
                Foreground = Brushes.White,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(8, 7, 8, 0)
            },
            Tag = (track, clip),
            ContextMenu = CreateClipContextMenu(clip)
        };
        Canvas.SetLeft(border, x);
        Canvas.SetTop(border, trackY + 6);
        border.Width = width;
        border.Height = TrackSpace - 13;
        TimelineCanvas.Children.Add(border);
        DrawClipWaveform(clip, x, trackY + 10, width);
        // Edge handles participate in the same canvas pointer gesture as the
        // clip body. Their tags keep the gesture target stable during redraw.
        DrawClipRubberBand(track, clip, x, trackY + 6, width);
        DrawClipKeyframeMarkers(track, clip, x, trackY + 6, width);
        DrawClipEffectMarkers(track, clip, x, trackY + 6, width);
        AddTrimHandle(track, clip, x, trackY + 6, true);
        AddTrimHandle(track, clip, x + width - TrackSpace * .095, trackY + 6, false);
        if (clip.Kind == ClipKind.Video &&
            (clip.SpeedAdjustmentApplied || Math.Abs(clip.Speed - 1) > .000001 || clip.TimeRemapPoints.Count > 0))
            DrawSpeedIndicator(clip, x, trackY, width);
    }

    private void DrawSpeedIndicator(TimelineClip clip, double left, double trackY, double clipWidth)
    {
        double badgeWidth = Math.Min(58, Math.Max(38, clipWidth - 8));
        string label = clip.TimeRemapPoints.Count > 0
            ? "变速"
            : $"{clip.Speed:0.##}×";
        var badge = new Border
        {
            Width = badgeWidth,
            Height = 19,
            Background = Brush("#FF9957"),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Cursor = Cursors.Hand,
            ToolTip = "速度提示：点击打开视频调速设置",
            Tag = clip,
            Child = new TextBlock
            {
                Text = label,
                Foreground = Brushes.Black,
                FontWeight = FontWeights.SemiBold,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };
        Canvas.SetLeft(badge, left + Math.Max(4, clipWidth - badgeWidth - 5));
        Canvas.SetTop(badge, trackY + 8);
        badge.MouseLeftButtonDown += (_, args) =>
        {
            args.Handled = true;
            SelectClip(clip);
            OpenObjectToolPage(clip, "视频调速", BuildSpeedTool);
        };
        TimelineCanvas.Children.Add(badge);
    }

    private void DrawClipWaveform(TimelineClip clip, double left, double top, double width)
    {
        if (clip.Kind is not (ClipKind.Video or ClipKind.Audio) || width < 24) return;
        MediaAsset? asset = clip.MediaAssetId is null ? null :
            _project.Media.FirstOrDefault(item => item.Id == clip.MediaAssetId);
        if (clip.Kind == ClipKind.Video && asset?.HasAudio != true) return;
        int samples = Math.Clamp((int)(width / 10), 8, 120);
        double center = top + (TrackSpace - 24) * .46;
        var random = new Random(StringComparer.Ordinal.GetHashCode(clip.Id));
        for (int index = 0; index < samples; index++)
        {
            double px = left + 5 + (width - 10) * index / Math.Max(1, samples - 1);
            double amplitude = (TrackSpace - 27) * (.16 + random.NextDouble() * .27);
            var line = new Line
            {
                X1 = px, X2 = px, Y1 = center - amplitude, Y2 = center + amplitude,
                Stroke = Brush("#A7D8D2"), StrokeThickness = 1,
                IsHitTestVisible = false, Opacity = .8
            };
            TimelineCanvas.Children.Add(line);
        }
    }

    private void AddTrimHandle(TimelineTrack track, TimelineClip clip, double x, double y, bool atStart)
    {
        var handle = new Border
        {
            Width = TrackSpace * .095,
            Height = TrackSpace - 13,
            Background = Brush("#EDEFF5"),
            Opacity = 0.72,
            CornerRadius = new CornerRadius(3),
            Cursor = Cursors.SizeWE,
            Tag = (track, clip, atStart)
        };
        Canvas.SetLeft(handle, x);
        Canvas.SetTop(handle, y);
        TimelineCanvas.Children.Add(handle);
    }

    private void DrawTransitionMarker(TimelineTrack track, TimelineClip incoming,
        TimelineClip prior, ClipTransition transition, double duration, double x, double y)
    {
        var menu = new ContextMenu { Background = Brush("#24262C"), Foreground = Brushes.White };
        AddContextCommand(menu, "删除转场", () => RemoveBoundaryTransition(track, incoming, prior, transition));
        AddContextCommand(menu, "编辑转场参数…", () => EditBoundaryTransition(track, incoming, prior, transition));
        bool selected = ReferenceEquals(_selectedTransition, transition);
        var marker = new Border
        {
            Width = Math.Clamp(duration * PixelsPerSecond, TrackSpace * .38, TrackSpace * 1.25),
            Height = TrackSpace * .38,
            CornerRadius = new CornerRadius(4),
            Background = Brush("#6CC7B8"),
            BorderBrush = Brush(selected ? "#FF9C59" : "#FFFFFF"),
            BorderThickness = new Thickness(selected ? 3 : 1),
            ToolTip = $"{transition.Name} · {duration:0.##} 秒\n右键删除；双击打开设置",
            ContextMenu = menu,
            Tag = transition,
            Child = new TextBlock { Text = "◀▶ " + transition.Name,
                Foreground = Brushes.Black, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 3, 0) }
        };
        marker.MouseLeftButtonDown += (_, args) =>
        {
            SelectTransition(track, incoming, prior, transition, duration);
            if (args.ClickCount == 2) EditBoundaryTransition(track, incoming, prior, transition);
            args.Handled = true;
        };
        marker.PreviewMouseRightButtonDown += (_, _) =>
        {
            // Select before WPF opens the menu. Rebuilding the timeline from
            // ContextMenu.Opened would detach its placement target.
            _selectedTransition = transition;
            _selectedTransitionIncoming = incoming;
            _selectedTransitionPrior = prior;
            _selectedTransitionTrack = track;
            _selectedClip = null;
            _selectedClips.Clear();
            _selectedTrack = track;
        };
        Canvas.SetLeft(marker, x - marker.Width / 2);
        Canvas.SetTop(marker, y + (TrackSpace - marker.Height) / 2);
        Canvas.SetZIndex(marker, 20);
        TimelineCanvas.Children.Add(marker);
    }

    private void SelectTransition(TimelineTrack track, TimelineClip incoming,
        TimelineClip prior, ClipTransition transition, double duration)
    {
        _selectedTransition = transition;
        _selectedTransitionIncoming = incoming;
        _selectedTransitionPrior = prior;
        _selectedTransitionTrack = track;
        _selectedClip = null;
        _selectedClips.Clear();
        _selectedTrack = track;
        DrawTimeline();
        SeekPreview(incoming.StartSeconds + duration / 2);
        StatusText.Text = $"已选中转场：{transition.Name} · Delete 删除";
    }

    private void EditBoundaryTransition(TimelineTrack track, TimelineClip incoming,
        TimelineClip prior, ClipTransition transition)
    {
        if (track.IsLocked) { StatusText.Text = "轨道已锁定 · VIS-TRANSITION-LOCKED"; return; }

        // Store transition info for editing in left upper tab
        _selectedTransition = transition;
        _selectedTransitionIncoming = incoming;
        _selectedTransitionPrior = prior;
        _selectedTransitionTrack = track;

        // Show transition settings in left upper tab
        ShowTransitionSettings();
    }

    private void ShowTransitionSettings()
    {
        if (_selectedTransition == null || _selectedTransitionIncoming == null ||
            _selectedTransitionPrior == null || _selectedTransitionTrack == null) return;

        var panel = new StackPanel { Margin = new Thickness(0) };
        panel.Children.Add(SettingsHeader("转场设置", () => CloseSettingsTabs()));

        panel.Children.Add(new TextBlock
        {
            Text = $"{_selectedTransitionPrior.Name} → {_selectedTransitionIncoming.Name}",
            Foreground = Brush("#9CA3AF"),
            Margin = new Thickness(0, 0, 0, 15)
        });

        panel.Children.Add(new TextBlock { Text = "转场类型", Margin = new Thickness(0, 0, 0, 5) });
        var preset = new ComboBox
        {
            ItemsSource = BuiltinCatalog.Transitions.Select(item => item.Name).ToArray(),
            SelectedIndex = Math.Max(0, BuiltinCatalog.Transitions.ToList().FindIndex(item => item.Id == _selectedTransition.PresetId)),
            Margin = new Thickness(0, 0, 0, 15)
        };
        panel.Children.Add(preset);

        panel.Children.Add(new TextBlock { Text = "时长（秒）", Margin = new Thickness(0, 0, 0, 5) });
        var duration = new TextBox
        {
            Text = _selectedTransition.DurationSeconds.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture),
            Margin = new Thickness(0, 0, 0, 15)
        };
        panel.Children.Add(duration);

        // Strength / turn count of the transition's current type. They apply
        // as soon as they are entered; changing the type rebuilds this tab.
        ClipTransition editedTransition = _selectedTransition;
        TimelineClip editedIncoming = _selectedTransitionIncoming;
        AddTransitionParameterRows(panel, editedTransition, new Thickness(0, 0, 0, 15), message =>
        {
            RefreshTimeline();
            SeekPreview(editedIncoming.StartSeconds + Math.Min(editedTransition.DurationSeconds,
                editedIncoming.DurationSeconds / 2) / 2);
            StatusText.Text = message;
        });

        var applyButton = new Button
        {
            Content = "应用",
            Padding = new Thickness(20, 8, 20, 8),
            Margin = new Thickness(0, 5, 0, 0)
        };
        applyButton.Click += (_, _) =>
        {
            if (!double.TryParse(duration.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.CurrentCulture, out double seconds) ||
                !double.IsFinite(seconds) || seconds <= 1 / _project.Sequence.FramesPerSecond ||
                seconds > Math.Min(30, Math.Min(_selectedTransitionPrior.DurationSeconds, _selectedTransitionIncoming.DurationSeconds) / 2))
            { StatusText.Text = "转场时长超出相邻片段可用范围 · VIS-TRANSITION-DURATION"; return; }
            if (!_selectedTransitionTrack.Clips.Contains(_selectedTransitionIncoming) ||
                !_selectedTransitionTrack.Clips.Contains(_selectedTransitionPrior) ||
                !_selectedTransitionIncoming.Transitions.Contains(_selectedTransition) &&
                !_selectedTransitionPrior.Transitions.Contains(_selectedTransition))
            { StatusText.Text = "转场已变化 · VIS-TRANSITION-STATE"; return; }
            SaveUndoState();
            BuiltinPreset choice = BuiltinCatalog.Transitions[Math.Clamp(preset.SelectedIndex, 0,
                BuiltinCatalog.Transitions.Count - 1)];
            _selectedTransition.PresetId = choice.Id;
            _selectedTransition.Name = choice.Name;
            _selectedTransition.DurationSeconds = seconds;
            MarkProjectDirty(); RefreshTimeline();
            SeekPreview(_selectedTransitionIncoming.StartSeconds + Math.Min(seconds, _selectedTransitionIncoming.DurationSeconds / 2) / 2);
            StatusText.Text = "已应用转场设置";
            // The type may now support different extra fields.
            ShowTransitionSettings();
        };
        panel.Children.Add(applyButton);

        OpenSettingsCategory("转场设置", panel);
    }

    private void RemoveBoundaryTransition(TimelineTrack track, TimelineClip incoming,
        TimelineClip prior, ClipTransition transition)
    {
        if (track.IsLocked) { StatusText.Text = "轨道已锁定 · EDIT-TRANSITION-LOCKED"; return; }
        if (!incoming.Transitions.Contains(transition) && !prior.Transitions.Contains(transition)) return;
        SaveUndoState();
        if (!incoming.Transitions.Remove(transition)) prior.Transitions.Remove(transition);
        ClearTransitionSelection();
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = "已删除转场";
    }

    private void ClearTransitionSelection()
    {
        _selectedTransition = null;
        _selectedTransitionIncoming = null;
        _selectedTransitionPrior = null;
        _selectedTransitionTrack = null;
    }

    private ContextMenu CreateClipContextMenu(TimelineClip clip)
    {
        var menu = new ContextMenu();
        menu.Background = Brush("#24262C");
        menu.Foreground = Brushes.White;
        menu.Opened += (_, _) =>
        {
            // Keep an existing multi-selection when right-clicking one of its
            // members; otherwise make the clicked clip the edit target. Avoid
            // rebuilding the canvas while its context menu is opening.
            _selectedClip = clip;
            _selectedTrack = _project.Tracks.FirstOrDefault(track => track.Clips.Contains(clip));
            if (!_selectedClips.Contains(clip))
            {
                _selectedClips.Clear();
                _selectedClips.Add(clip);
            }
            _contextTrack = _selectedTrack;
            ShowSelectedObjectSettings(); // 立即更新Tab显示
        };
        if (clip.Kind == ClipKind.Text)
            AddContextCommand(menu, "编辑文字 / 字幕…", () => { SelectClip(clip); EditSelectedClipProperties(); });
        if (clip.Kind == ClipKind.Audio)
        {
            AddContextCommand(menu, "音频属性…", () => { SelectClip(clip); ShowAudioClipProperties(clip); });
            AddContextCommand(menu, "音乐重定时…", () => { SelectClip(clip); ShowMusicRemixDialog(clip); });
        }
        if (clip.Kind is ClipKind.Audio or ClipKind.Video)
        {
            AddContextCommand(menu, "按音频波形同步选中片段", () => { _ = SyncSelectedAudioAsync(); });
            AddContextCommand(menu, "按源时间码同步选中片段", SyncSelectedAudioByTimecode);
        }
        if (clip.Kind == ClipKind.Sequence)
            AddContextCommand(menu, "打开嵌套序列", () =>
            {
                if (clip.NestedSequenceId is not null) SwitchSequence(clip.NestedSequenceId);
            });
        if (clip.Kind is ClipKind.Video or ClipKind.Image or ClipKind.Text or ClipKind.Color or ClipKind.Adjustment)
            AddContextCommand(menu, "效果控件…", () => { SelectClip(clip); EditSelectedEffects(); });
        if (clip.Kind is ClipKind.Image or ClipKind.Video)
            AddContextCommand(menu, "画面变换 / 裁切…", () => { SelectClip(clip); EditMediaTransform(clip); });
        if (clip.MulticamGroupId is null)
            AddContextCommand(menu, "速度 / 时长…", () => { SelectClip(clip); EditSelectedSpeed(); });
        if (clip.Kind is ClipKind.Video or ClipKind.Audio)
        {
            if (clip.MulticamGroupId is null)
            {
                AddContextCommand(menu, "时间重映射…", () => { SelectClip(clip); EditSelectedTimeRemapping(); });
                AddContextCommand(menu, "反向播放", () => { SelectClip(clip); ToggleSelectedReverse(); });
            }
        }
        AddContextCommand(menu, "在播放头处分割", () => { SelectClip(clip); SplitSelectedClip(); });
        AddContextCommand(menu, "复制片段", () => { SelectClip(clip); DuplicateSelectedClip(); });
        AddContextCommand(menu, "删除片段", () => { SelectClip(clip); DeleteSelectedClip(); });
        AddContextCommand(menu, "波纹删除", () => { SelectClip(clip); RippleDeleteSelectedClip(); });
        AddLocalEditingCommands(menu, clip);
        AddContextCommand(menu, "复制到剪贴板", () => { SelectClip(clip); CopySelectedClip(); });
        AddContextCommand(menu, "粘贴片段", PasteClipboardClip);
        AddContextCommand(menu, "插入剪贴板片段（推开后续）", () => { SelectClip(clip); InsertClipboardAtPlayhead(); });
        AddContextCommand(menu, "覆盖粘贴（替换范围）", () => { SelectClip(clip); OverwriteClipboardAtPlayhead(); });
        if (clip.Kind is ClipKind.Video or ClipKind.Audio)
        {
            AddContextCommand(menu, "滑移源素材…", () => { SelectClip(clip); SlipSelectedClip(); });
            AddContextCommand(menu, "滚动下一切点…", () => { SelectClip(clip); RollSelectedCut(); });
            AddContextCommand(menu, "滑动片段…", () => { SelectClip(clip); SlideSelectedClip(); });
        }
        AddContextCommand(menu, "编组选中片段", () => GroupSelectedClips(false));
        AddContextCommand(menu, "链接选中片段", () => GroupSelectedClips(true));
        AddContextCommand(menu, "取消编组", () => UngroupSelectedClips(false));
        AddContextCommand(menu, "解除链接", () => UngroupSelectedClips(true));
        AddContextCommand(menu, "将入点裁到播放头", () => { SelectClip(clip); TrimSelectedStart(); });
        AddContextCommand(menu, "将出点裁到播放头", () => { SelectClip(clip); TrimSelectedEnd(); });
        AddContextCommand(menu, "波纹修剪入点到播放头", () => { SelectClip(clip); RippleTrimStartToPlayhead(); });
        AddContextCommand(menu, "波纹修剪出点到播放头", () => { SelectClip(clip); RippleTrimEndToPlayhead(); });
        AddContextCommand(menu, "添加标记…", AddSequenceMarker);
        AddContextCommand(menu, "在此处添加关键帧", () => AddKeyframeAtPoint(_timelineContextPoint));
        AddContextCommand(menu, "打开关键帧编辑器…", EditSelectedKeyframes);
        AddRubberBandContextCommands(menu, clip);
        AddMulticamContextCommands(menu, clip);
        return menu;
    }

    private static void AddContextCommand(ContextMenu menu, string label, Action action)
    {
        var item = new MenuItem { Header = label, Background = Brush("#24262C"),
            Foreground = Brushes.White };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void TimelineCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        Point point = args.GetPosition(TimelineCanvas);
        // Ruler clicks jump to a frame; dragging the orange line retains
        // the pointer offset so the playhead does not jump on mouse-down.
        bool nearPlayheadLine = Math.Abs(point.X - (HeaderSpace + _project.Sequence.PlayheadSeconds * PixelsPerSecond)) <= RulerSpace * 0.2;
        bool lineOnEmptyTrack = nearPlayheadLine && point.X >= HeaderSpace &&
            FindClipBorder(args.OriginalSource as DependencyObject) is null &&
            FindTrimHandle(args.OriginalSource as DependencyObject) is null &&
            !IsTransitionMarker(args.OriginalSource as DependencyObject);
        if (ReferenceEquals(args.OriginalSource, _playheadHitTarget) ||
            (lineOnEmptyTrack && point.Y >= RulerSpace) ||
            (point.Y >= 0 && point.Y < RulerSpace && point.X >= HeaderSpace))
        {
            if (_isPlaying) TogglePlayback();
            _scrubbingPlayhead = true;
            _playheadGrabOffset = (ReferenceEquals(args.OriginalSource, _playheadHitTarget) ||
                (nearPlayheadLine && point.Y >= RulerSpace))
                ? point.X - (HeaderSpace + _project.Sequence.PlayheadSeconds * PixelsPerSecond) : 0;
            _lastScrubPreviewUtc = DateTime.MinValue;
            TimelineCanvas.CaptureMouse();
            ScrubPlayheadTo(point.X - _playheadGrabOffset, true);
            args.Handled = true;
            return;
        }
        if (FindTrimHandle(args.OriginalSource as DependencyObject) is { Tag: ValueTuple<TimelineTrack, TimelineClip, bool> trimData })
        {
            ClearTransitionSelection();
            _selectedTrack = trimData.Item1;
            _contextTrack = _selectedTrack;
            _selectedClip = trimData.Item2;
            _selectedClips.Clear();
            _selectedClips.Add(trimData.Item2);
            ShowSelectedObjectSettings();
            if (_selectedTrack.IsLocked) return;
            _trimmingStart = trimData.Item3;
            _trimmingEnd = !trimData.Item3;
            _clipEdgeScrollTimer.Start();
            _moveOrigin = point;
            _moveOriginalStart = _selectedClip.StartSeconds;
            _moveOriginalDuration = _selectedClip.DurationSeconds;
            _moveOriginalSourceIn = _selectedClip.SourceInSeconds;
            _moveOriginalTimeRemap = _selectedClip.TimeRemapPoints.Select(point =>
                new ClipTimeRemapPoint { TimeSeconds = point.TimeSeconds, Speed = point.Speed }).ToList();
            _moveOriginalKeyframes = CloneTimelineKeyframes(_selectedClip.Keyframes);
            _moveOriginalMulticamLocalStart = _selectedClip.MulticamLocalStartSeconds;
            _moveOriginalTrack = _selectedTrack;
            _moveOriginalProjectJson = JsonSerializer.Serialize(_project);
            TimelineCanvas.CaptureMouse();
            DrawTimeline();
            args.Handled = true;
            return;
        }
        if (FindClipBorder(args.OriginalSource as DependencyObject) is { Tag: ValueTuple<TimelineTrack, TimelineClip> data })
        {
            if (args.ClickCount == 2 && data.Item2.Kind == ClipKind.Text)
            {
                SelectClip(data.Item2);
                EditSelectedClipProperties();
                args.Handled = true;
                return;
            }
            ClearTransitionSelection();
            _selectedTrack = data.Item1;
            _contextTrack = _selectedTrack;
            _selectedClip = data.Item2;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                if (!_selectedClips.Add(data.Item2)) _selectedClips.Remove(data.Item2);
                DrawTimeline();
                if (_selectedClips.Contains(data.Item2)) ShowSelectedObjectSettings();
                else CloseSettingsTabs();
                args.Handled = true;
                return;
            }
            if (!_selectedClips.Contains(data.Item2))
            {
                _selectedClips.Clear();
                _selectedClips.Add(data.Item2);
            }
            if (_selectedTrack.IsLocked)
            {
                SeekPreview(_selectedClip.StartSeconds);
                DrawTimeline();
                ShowSelectedObjectSettings();
                return;
            }
            _movingClip = true;
            _clipEdgeScrollTimer.Start();
            _moveOrigin = point;
            _moveOriginalStart = _selectedClip.StartSeconds;
            _moveOriginalDuration = _selectedClip.DurationSeconds;
            _moveOriginalSourceIn = _selectedClip.SourceInSeconds;
            _moveOriginalTimeRemap = _selectedClip.TimeRemapPoints.Select(point =>
                new ClipTimeRemapPoint { TimeSeconds = point.TimeSeconds, Speed = point.Speed }).ToList();
            _moveOriginalKeyframes = CloneTimelineKeyframes(_selectedClip.Keyframes);
            _moveOriginalMulticamLocalStart = _selectedClip.MulticamLocalStartSeconds;
            _moveOriginalTrack = _selectedTrack;
            _moveOriginalProjectJson = JsonSerializer.Serialize(_project);
            CaptureMoveCompanions();
            TimelineCanvas.CaptureMouse();
            DrawTimeline();
            ShowSelectedObjectSettings();
            SeekPreview(Math.Max(_selectedClip.StartSeconds, _project.Sequence.PlayheadSeconds));
            return;
        }
        ClearTransitionSelection();
        _selectedClip = null;
        _selectedKeyframe = null;
        _selectedClips.Clear();
        _selectedTrack = TrackAt(point.Y);
        _contextTrack = _selectedTrack;
        CloseSettingsTabs();
        SeekPreview(Math.Max(0, (point.X - HeaderSpace) / PixelsPerSecond));
        DrawTimeline();
    }

    private static Border? FindClipBorder(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Border border && border.Tag is ValueTuple<TimelineTrack, TimelineClip>) return border;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private static Border? FindTrimHandle(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Border border && border.Tag is ValueTuple<TimelineTrack, TimelineClip, bool>) return border;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private static bool IsTransitionMarker(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Border { Tag: ClipTransition }) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void TimelineCanvas_MouseMove(object sender, MouseEventArgs args)
    {
        if (_bezierHandleDrag is not null)
        {
            MoveBezierHandleDrag(args);
            return;
        }
        if (_rubberBandDrag is not null)
        {
            MoveRubberBandDrag(args);
            return;
        }
        if (_draggingTrack is not null)
        {
            if (args.LeftButton != MouseButtonState.Pressed) { FinishTrackDrag(args.GetPosition(TimelineCanvas)); return; }
            Point trackPoint = args.GetPosition(TimelineCanvas);
            bool wasDragging = _trackDragStarted;
            _trackDragStarted |= Math.Abs(trackPoint.Y - _trackDragOrigin.Y) >= SystemParameters.MinimumVerticalDragDistance;
            if (_trackDragStarted)
            {
                _trackDragPointer = args.GetPosition(TimelineScrollViewer);
                UpdateTrackDragPrediction(trackPoint, wasDragging);
                UpdateTrackDragEdgeScroll();
            }
            args.Handled = true;
            return;
        }
        if (_scrubbingPlayhead)
        {
            if (args.LeftButton == MouseButtonState.Pressed)
                ScrubPlayheadTo(args.GetPosition(TimelineCanvas).X - _playheadGrabOffset, false);
            else
                FinishPlayheadScrub();
            return;
        }
        if ((!_movingClip && !_trimmingStart && !_trimmingEnd) || _selectedClip is null || args.LeftButton != MouseButtonState.Pressed) return;
        ScrollTimelineDuringClipGesture();
        Point point = args.GetPosition(TimelineCanvas);
        if (_trimmingStart || _trimmingEnd)
        {
            double change = (point.X - _moveOrigin.X) / PixelsPerSecond;
            double minimum = 1 / Math.Max(1, _project.Sequence.FramesPerSecond);
            // MouseMove runs repeatedly. Evaluate each trim against the
            // gesture's original source range and speed curve; the previous
            // pointer event may already have cropped the live clip.
            _selectedClip.StartSeconds = _moveOriginalStart;
            _selectedClip.DurationSeconds = _moveOriginalDuration;
            _selectedClip.SourceInSeconds = _moveOriginalSourceIn;
            _selectedClip.TimeRemapPoints = CloneTimeRemap(_moveOriginalTimeRemap);
            _selectedClip.Keyframes = CloneTimelineKeyframes(_moveOriginalKeyframes);
            _selectedClip.MulticamLocalStartSeconds = _moveOriginalMulticamLocalStart;
            MediaAsset? source = _project.Media.FirstOrDefault(asset => asset.Id == _selectedClip.MediaAssetId);
            double usedSource = ClipTimeMap.SourceOffset(_selectedClip, _moveOriginalDuration);
            if (_trimmingStart)
            {
                double minStart = _selectedClip.MulticamGroupId is null ? 0 :
                    Math.Max(0, _moveOriginalStart - _moveOriginalMulticamLocalStart);
                if (source is not null && (_selectedClip.Kind is ClipKind.Video or ClipKind.Audio))
                {
                    double firstSpeed = ClipTimeMap.SpeedAt(_selectedClip, 0);
                    double availableHandle = _selectedClip.IsReversed
                        ? source.DurationSeconds is double sourceDurationAtStart
                            ? sourceDurationAtStart - _moveOriginalSourceIn - usedSource
                            : double.PositiveInfinity
                        : _moveOriginalSourceIn;
                    if (firstSpeed > 0 && double.IsFinite(availableHandle))
                        minStart = Math.Max(minStart, _moveOriginalStart -
                            Math.Max(0, availableHandle) / firstSpeed);
                }
                minStart = Math.Min(minStart, _moveOriginalStart + _moveOriginalDuration - minimum);
                double newStart = Math.Clamp(_moveOriginalStart + change, minStart,
                    _moveOriginalStart + _moveOriginalDuration - minimum);
                if (_snapEnabled && _selectedTrack?.IsSnapping != false) newStart = Math.Clamp(SnapEditTime(newStart, _selectedClip), minStart,
                    _moveOriginalStart + _moveOriginalDuration - minimum);
                double shifted = newStart - _moveOriginalStart;
                double newDuration = _moveOriginalDuration - shifted;
                if (Math.Abs(shifted) < 1e-9) { DrawTimeline(); return; }
                _selectedClip.StartSeconds = newStart;
                ClipAnimation.KeepRange(_selectedClip, shifted, newDuration);
                AdvanceMulticamLocalStart(_selectedClip, shifted);
                if (_selectedClip.TimeRemapPoints.Count > 0)
                    ClipTimeMap.KeepRange(_selectedClip, shifted, newDuration);
                else if (!_selectedClip.IsReversed)
                    _selectedClip.SourceInSeconds += shifted * _selectedClip.Speed;
                _selectedClip.DurationSeconds = newDuration;
            }
            else
            {
                double newDuration = Math.Max(minimum, _moveOriginalDuration + change);
                newDuration = _snapEnabled && _selectedTrack?.IsSnapping != false
                    ? Math.Max(minimum, SnapEditTime(_moveOriginalStart + newDuration, _selectedClip) - _moveOriginalStart)
                    : newDuration;
                if (source is not null && (_selectedClip.Kind is ClipKind.Video or ClipKind.Audio))
                {
                    double lastSpeed = ClipTimeMap.SpeedAt(_selectedClip, _moveOriginalDuration);
                    double availableHandle = _selectedClip.IsReversed ? _moveOriginalSourceIn :
                        source.DurationSeconds is double sourceDurationAtEnd
                            ? sourceDurationAtEnd - _moveOriginalSourceIn - usedSource : double.PositiveInfinity;
                    if (lastSpeed > 0 && double.IsFinite(availableHandle))
                        newDuration = Math.Min(newDuration, Math.Max(minimum,
                            _moveOriginalDuration + Math.Max(0, availableHandle) / lastSpeed));
                }
                if (_selectedClip.MulticamGroupId is not null &&
                    _project.MulticamGroups.FirstOrDefault(group =>
                        group.Id == _selectedClip.MulticamGroupId) is { } multicamGroup)
                    newDuration = Math.Min(newDuration,
                        multicamGroup.DurationSeconds - _moveOriginalMulticamLocalStart);
                newDuration = Math.Max(minimum, newDuration);
                if (Math.Abs(newDuration - _moveOriginalDuration) < 1e-9)
                { DrawTimeline(); return; }
                ClipAnimation.KeepRange(_selectedClip, 0, newDuration);
                if (_selectedClip.TimeRemapPoints.Count > 0)
                    ClipTimeMap.KeepRange(_selectedClip, 0, newDuration);
                else if (_selectedClip.IsReversed)
                    _selectedClip.SourceInSeconds +=
                        (_moveOriginalDuration - newDuration) * _selectedClip.Speed;
                _selectedClip.DurationSeconds = newDuration;
            }
            // The source monitor's Out point is an import selection, not a
            // hard media boundary. Editing can recover handles on either
            // side, but may never decode beyond the actual file duration.
            if (_selectedClip.SourceInSeconds < 0 ||
                (source?.DurationSeconds is double available &&
                 _selectedClip.SourceInSeconds + ClipTimeMap.SourceOffset(_selectedClip,
                     _selectedClip.DurationSeconds) > available + 0.001))
            {
                _selectedClip.StartSeconds = _moveOriginalStart;
                _selectedClip.DurationSeconds = _moveOriginalDuration;
                _selectedClip.SourceInSeconds = _moveOriginalSourceIn;
                _selectedClip.TimeRemapPoints = CloneTimeRemap(_moveOriginalTimeRemap);
                _selectedClip.Keyframes = CloneTimelineKeyframes(_moveOriginalKeyframes);
                _selectedClip.MulticamLocalStartSeconds = _moveOriginalMulticamLocalStart;
            }
            DrawTimeline();
            return;
        }
        double minimumDelta = -Math.Min(_moveOriginalStart,
            _moveCompanionOriginalStarts.Values.DefaultIfEmpty(_moveOriginalStart).Min());
        double next = Math.Max(_moveOriginalStart + minimumDelta,
            _moveOriginalStart + (point.X - _moveOrigin.X) / PixelsPerSecond);
        if (_snapEnabled && _selectedTrack?.IsSnapping != false) next = Math.Max(_moveOriginalStart + minimumDelta,
            SnapMovingClipStart(next));
        _selectedClip.StartSeconds = next;
        double moveDelta = next - _moveOriginalStart;
        foreach (var companion in _moveCompanionOriginalStarts)
            companion.Key.StartSeconds = Math.Max(0, companion.Value + moveDelta);
        TimelineTrack? target = TrackAt(point.Y);
        if (target is not null && _selectedTrack is not null && target != _selectedTrack && !target.IsLocked && IsCompatible(target, _selectedClip))
        {
            _selectedTrack.Clips.Remove(_selectedClip);
            target.Clips.Add(_selectedClip);
            _selectedTrack = target;
            _contextTrack = target;
        }
        DrawTimeline();
    }

    private void TimelineCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs args)
    {
        if (_bezierHandleDrag is not null)
        {
            FinishBezierHandleDrag();
            args.Handled = true;
            return;
        }
        if (_rubberBandDrag is not null)
        {
            FinishRubberBandDrag();
            args.Handled = true;
            return;
        }
        if (_draggingTrack is not null)
        {
            FinishTrackDrag(args.GetPosition(TimelineCanvas));
            args.Handled = true;
            return;
        }
        if (_scrubbingPlayhead)
        {
            ScrubPlayheadTo(args.GetPosition(TimelineCanvas).X - _playheadGrabOffset, true);
            FinishPlayheadScrub();
            args.Handled = true;
            return;
        }
        if (!_movingClip && !_trimmingStart && !_trimmingEnd) return;
        FinishClipGesture();
    }

    private void FinishClipGesture()
    {
        _clipEdgeScrollTimer.Stop();
        _movingClip = false;
        _trimmingStart = false;
        _trimmingEnd = false;
        if (TimelineCanvas.IsMouseCaptured) TimelineCanvas.ReleaseMouseCapture();
        if (_selectedClip is null) return;
        bool changed = Math.Abs(_selectedClip.StartSeconds - _moveOriginalStart) > 0.001 ||
            Math.Abs(_selectedClip.DurationSeconds - _moveOriginalDuration) > 0.001 ||
            Math.Abs(_selectedClip.SourceInSeconds - _moveOriginalSourceIn) > 0.001 ||
            Math.Abs(_selectedClip.MulticamLocalStartSeconds - _moveOriginalMulticamLocalStart) > 0.001 ||
            _selectedTrack != _moveOriginalTrack ||
            _moveCompanionOriginalStarts.Any(item => Math.Abs(item.Key.StartSeconds - item.Value) > 0.001);
        if (changed)
        {
            if (_moveOriginalProjectJson is not null)
            {
                _undoStates.Push(_moveOriginalProjectJson);
                TrimHistoryCapacity();
                _redoStates.Clear();
            }
            MarkProjectDirty();
            StatusText.Text = $"已更新片段：{_selectedClip.Name}";
            RefreshTimeline();
            SeekPreview(_project.Sequence.PlayheadSeconds);
        }
        else DrawTimeline();
        _moveOriginalProjectJson = null;
        _moveOriginalTimeRemap = null;
        _moveCompanionOriginalStarts.Clear();
    }

    private bool ScrollTimelineDuringClipGesture()
    {
        double viewport = TimelineScrollViewer.ViewportWidth;
        if (viewport <= 0) return false;
        double pointer = Mouse.GetPosition(TimelineScrollViewer).X;
        double margin = Math.Min(48, viewport * .12);
        double direction = pointer < margin ? -1 : pointer > viewport - margin ? 1 : 0;
        if (direction == 0) return false;
        // A clip can grow as its right edge is dragged. Refresh the extent
        // before scrolling so the gesture never stalls at the old endpoint.
        if (direction > 0) RefreshTimeline();
        double oldOffset = TimelineScrollViewer.HorizontalOffset;
        TimelineScrollViewer.ScrollToHorizontalOffset(oldOffset + direction * Math.Max(8, PixelsPerSecond * .18));
        return Math.Abs(TimelineScrollViewer.HorizontalOffset - oldOffset) > .1;
    }

    private void ScrubPlayheadTo(double canvasX, bool forcePreview)
    {
        double maximum = Math.Max(0, (TimelineCanvas.Width - HeaderSpace) / PixelsPerSecond);
        double seconds = SnapTime(Math.Clamp((canvasX - HeaderSpace) / PixelsPerSecond, 0, maximum));
        _project.Sequence.PlayheadSeconds = seconds;
        CurrentTimeText.Text = FormatTime(seconds);
        StatusText.Text = $"播放头：{FormatTime(seconds)}";
        UpdatePlayheadVisual();

        // Keep the latest pointer position responsive. The render scheduler
        // cancels the obsolete FFmpeg request, so a slow old frame never
        // blocks the frame currently under the pointer.
        DateTime now = DateTime.UtcNow;
        if (forcePreview || now - _lastScrubPreviewUtc >= TimeSpan.FromMilliseconds(16))
        {
            _lastScrubPreviewUtc = now;
            SeekPreview(seconds);
        }
    }

    private void UpdatePlayheadVisual()
    {
        double visualX = HeaderSpace + _project.Sequence.PlayheadSeconds * PixelsPerSecond;
        if (_playheadVisualLine is not null)
        {
            _playheadVisualLine.X1 = visualX;
            _playheadVisualLine.X2 = visualX;
        }
        if (_playheadGrip is not null)
            Canvas.SetLeft(_playheadGrip, visualX - _playheadGrip.Width / 2);
        if (_playheadHitTarget is not null)
            Canvas.SetLeft(_playheadHitTarget, visualX - _playheadHitTarget.Width / 2);
    }

    private void FinishPlayheadScrub()
    {
        if (!_scrubbingPlayhead) return;
        _scrubbingPlayhead = false;
        TimelineCanvas.ReleaseMouseCapture();
        // The mouse-up path already sought the exact frame. Capture loss
        // reaches this method without mouse-up and still needs one seek.
        SeekPreview(_project.Sequence.PlayheadSeconds);
    }

    private void TimelineCanvas_DragOver(object sender, DragEventArgs args)
    {
        try
        {
            args.Effects = args.Data.GetDataPresent(typeof(LibraryItem)) || args.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            if (args.Effects != DragDropEffects.None && args.Data.GetData(typeof(LibraryItem)) is LibraryItem item)
                ShowDropGhost(args.GetPosition(TimelineCanvas), item);
        }
        catch (System.Runtime.InteropServices.COMException) { args.Effects = DragDropEffects.None; }
        args.Handled = true;
    }

    private async void TimelineCanvas_Drop(object sender, DragEventArgs args)
    {
        RemoveDropGhost();
        Point point = args.GetPosition(TimelineCanvas);
        double time = Math.Max(0, (point.X - HeaderSpace) / PixelsPerSecond);
        int trackIndex = (int)Math.Floor((point.Y - RulerSpace) / TrackSpace);
        try
        {
            if (args.Data.GetData(typeof(LibraryItem)) is LibraryItem item)
            {
                if (item.Source is MediaAsset source)
                {
                    MediaAsset? current = _project.Media.FirstOrDefault(asset => asset.Id == source.Id);
                    if (current is null) { StatusText.Text = "所拖媒体已从当前项目移除"; return; }
                    item = item with { Source = current };
                }
                if (item.Category is 2 or 3 or 5) ApplyPresetToClipAt(item, point);
                else AddLibraryItemToTimeline(item, time, trackIndex);
            }
            else if (args.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                ProjectDocument targetProject = _project;
                long targetGeneration = _projectGeneration;
                IReadOnlyList<MediaAsset> assets = await ImportMediaPathsAsync(paths);
                if (ReferenceEquals(targetProject, _project) && targetGeneration == _projectGeneration)
                    await AddImportedAssetsToTimelineAsync(assets, time, trackIndex);
            }
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            StatusText.Text = $"媒体拖放失败 · MED-DROP：{exception.Message}";
        }
        args.Handled = true;
    }

    private void ShowDropGhost(Point point, LibraryItem item)
    {
        if (_dropGhost is null)
        {
            _dropGhost = new Border { Padding = new Thickness(8, 4, 8, 4),
                Background = Brush("#D925252D"), BorderBrush = Brush("#FF9957"),
                BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(3),
                IsHitTestVisible = false, Opacity = .9 };
            TimelineCanvas.Children.Add(_dropGhost);
        }
        string prefix = item.Source is MediaAsset ? "▣" : "✦";
        _dropGhost.Child = new TextBlock { Text = $"{prefix} {item.Name}", Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold };
        Canvas.SetLeft(_dropGhost, point.X + 12); Canvas.SetTop(_dropGhost, point.Y + 12);
    }

    private void RemoveDropGhost()
    {
        if (_dropGhost is null) return;
        TimelineCanvas.Children.Remove(_dropGhost); _dropGhost = null;
    }

    /// <summary>
    /// Adds a dropped batch with one timeline undo step and one redraw. The
    /// imported assets remain in the project media bin for later reuse.
    /// </summary>
    private async Task AddImportedAssetsToTimelineAsync(IReadOnlyList<MediaAsset> assets, double time, int trackIndex)
    {
        if (assets.Count == 0) return;
        ProjectDocument targetProject = _project;
        long targetGeneration = _projectGeneration;
        _pendingDropBatches.RemoveAll(batch => !ReferenceEquals(batch.Project, targetProject) ||
            batch.Generation != targetGeneration);
        var dropBatch = new PendingDropBatch(targetProject, targetGeneration);
        var currentAssets = new Dictionary<string, MediaAsset>(StringComparer.Ordinal);
        foreach (MediaAsset current in _project.Media) currentAssets.TryAdd(current.Id, current);
        if (!assets.Any(asset => currentAssets.ContainsKey(asset.Id))) return;
        SaveUndoState();
        double start = time;
        double? previousEnd = null;
        int inserted = 0;
        foreach (MediaAsset importedAsset in assets)
        {
            if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration) return;
            if (!currentAssets.TryGetValue(importedAsset.Id, out MediaAsset? asset)) continue;
            ClipKind kind = asset.Kind switch
            {
                MediaKind.Video => ClipKind.Video,
                MediaKind.Audio => ClipKind.Audio,
                _ => ClipKind.Image
            };
            TimelineTrack? track = ResolveDropTarget(kind, trackIndex);
            if (track is null)
            {
                track = new TimelineTrack
                {
                    Name = kind == ClipKind.Audio ? $"音频 {_project.Tracks.Count(t => t.Kind == TrackKind.Audio) + 1}" :
                        $"视频 {_project.Tracks.Count(t => t.Kind == TrackKind.Video) + 1}",
                    Kind = kind == ClipKind.Audio ? TrackKind.Audio : TrackKind.Video,
                    Order = _project.Tracks.Count
                };
                _project.Tracks.Add(track);
            }
            var clip = new TimelineClip
            {
                Name = asset.Name,
                Kind = kind,
                MediaAssetId = asset.Id,
                // Snap only the gesture's first clip. The rest of the batch
                // must stay contiguous even if a later boundary is close to
                // another clip or the playhead.
                StartSeconds = previousEnd ?? (_snapEnabled && track.IsSnapping ? SnapNewClipStart(track, SnapEditTime(start)) : start),
                SourceInSeconds = asset.SourceInSeconds,
                DurationSeconds = asset.Kind != MediaKind.Image &&
                    (asset.SourceOutSeconds ?? asset.DurationSeconds) is double batchOut &&
                    batchOut - asset.SourceInSeconds is > 0 and < 86400
                    ? batchOut - asset.SourceInSeconds : 5
            };
            track.Clips.Add(clip);
            if (asset.Kind != MediaKind.Image && asset.SourceOutSeconds is null &&
                !(asset.DurationSeconds is > 0 and < 86400))
                dropBatch.Clips.Add(new PendingDropClip(clip, clip.StartSeconds));
            else if (dropBatch.Clips.Count > 0)
                dropBatch.Clips.Add(new PendingDropClip(clip, clip.StartSeconds) { AwaitsDuration = false });
            inserted++;
            previousEnd = clip.StartSeconds + clip.DurationSeconds;
            _selectedClip = clip;
            _selectedTrack = track;
            _contextTrack = track;
            start += clip.DurationSeconds;
            // Let input and paint run between large insertion batches.
            if (inserted % 200 == 0)
            {
                MarkProjectDirty();
                StatusText.Text = $"正在加入时间线：{inserted} / {assets.Count}";
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration) return;
        if (inserted == 0) return;
        if (dropBatch.Clips.Any(item => item.AwaitsDuration))
        {
            _pendingDropBatches.Add(dropBatch);
            // A probe may have completed while a large batch was yielding to
            // input. Reconcile after all of its clips have been inserted.
            ReconcilePendingDropDurations();
        }
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(time);
        StatusText.Text = $"已将 {inserted} 个媒体加入时间线";
    }

    private bool ReconcilePendingDropDurations()
    {
        bool changed = false;
        bool hasPending = _pendingDropBatches.Any(batch => ReferenceEquals(batch.Project, _project) &&
            batch.Generation == _projectGeneration && batch.Clips.Any(item => item.AwaitsDuration));
        // Metadata for an unrelated source arrives through the same callback.
        // Avoid scanning a large timeline when no dropped clip awaits a probe.
        if (!hasPending)
        {
            _pendingDropBatches.Clear();
            return false;
        }
        var presentClips = _project.Tracks.SelectMany(track => track.Clips).ToHashSet();
        var assetsById = new Dictionary<string, MediaAsset>(StringComparer.Ordinal);
        foreach (MediaAsset asset in _project.Media) assetsById.TryAdd(asset.Id, asset);
        foreach (PendingDropBatch batch in _pendingDropBatches.ToArray())
        {
            if (!ReferenceEquals(batch.Project, _project) || batch.Generation != _projectGeneration)
            {
                _pendingDropBatches.Remove(batch);
                continue;
            }
            for (int index = 0; index < batch.Clips.Count; index++)
            {
                PendingDropClip pending = batch.Clips[index];
                if (!pending.AwaitsDuration) continue;
                TimelineClip clip = pending.Clip;
                bool stillPresent = presentClips.Contains(clip);
                if (!stillPresent || Math.Abs(clip.StartSeconds - pending.ExpectedStart) > 0.001 ||
                    Math.Abs(clip.DurationSeconds - pending.ExpectedDuration) > 0.001 ||
                    clip.Speed != 1)
                {
                    pending.AwaitsDuration = false;
                    continue;
                }
                MediaAsset? asset = clip.MediaAssetId is string id && assetsById.TryGetValue(id, out MediaAsset? found)
                    ? found : null;
                if (!(asset?.DurationSeconds is > 0 and < 86400)) continue;
                double duration = (asset.SourceOutSeconds ?? asset.DurationSeconds.Value) - clip.SourceInSeconds;
                if (!double.IsFinite(duration) || duration <
                    1 / Math.Max(1, _project.Sequence.FramesPerSecond))
                {
                    pending.AwaitsDuration = false;
                    StatusText.Text = $"源素材区间无效：{asset.Name} · MED-DURATION";
                    continue;
                }
                double delta = duration - clip.DurationSeconds;
                pending.AwaitsDuration = false;
                if (Math.Abs(delta) <= 0.001) continue;
                clip.DurationSeconds = duration;
                pending.ExpectedDuration = duration;
                changed = true;
                // Preserve the arrangement made by the one drop gesture.
                // If a later clip has been moved, leave it and the rest of
                // that user-edited chain where the editor placed them.
                for (int following = index + 1; following < batch.Clips.Count; following++)
                {
                    PendingDropClip next = batch.Clips[following];
                    if (!presentClips.Contains(next.Clip) ||
                        Math.Abs(next.Clip.StartSeconds - next.ExpectedStart) > 0.001 ||
                        Math.Abs(next.Clip.DurationSeconds - next.ExpectedDuration) > 0.001 ||
                        next.Clip.Speed != 1) break;
                    next.Clip.StartSeconds = Math.Max(0, next.Clip.StartSeconds + delta);
                    next.ExpectedStart = next.Clip.StartSeconds;
                }
            }
            if (batch.Clips.All(item => !item.AwaitsDuration)) _pendingDropBatches.Remove(batch);
        }
        if (changed)
        {
            MarkProjectDirty();
            RefreshTimeline();
            SeekPreview(_project.Sequence.PlayheadSeconds);
        }
        return changed;
    }

    private void AddLibraryItemToTimeline(LibraryItem item, double time, int trackIndex)
    {
        bool isSticker = item.Category == 4;
        ClipKind kind = item.Category is 1 or 4 ? ClipKind.Text : ((MediaAsset)item.Source).Kind switch
        {
            MediaKind.Video => ClipKind.Video,
            MediaKind.Audio => ClipKind.Audio,
            _ => ClipKind.Image
        };
        // Text, shapes and stickers go on ordinary video tracks. A library
        // double-click names no track, so pick a free video track on top of the
        // picture (or add one) rather than stacking the text onto media.
        TimelineTrack? track = kind == ClipKind.Text && trackIndex < 0
            ? null
            : ResolveDropTarget(kind, trackIndex);
        // Capture the state before a missing destination track is created, so undo
        // removes both the new clip and its automatically created track.
        SaveUndoState();
        if (kind == ClipKind.Text && track is null)
            track = GetOrCreateTextTrack(Math.Max(0, time), 5);
        else if (track is null)
        {
            track = new TimelineTrack
            {
                Name = kind == ClipKind.Audio ? $"音频 {_project.Tracks.Count(t => t.Kind == TrackKind.Audio) + 1}" :
                    $"视频 {_project.Tracks.Count(t => t.Kind == TrackKind.Video) + 1}",
                Kind = kind == ClipKind.Audio ? TrackKind.Audio : TrackKind.Video,
                Order = _project.Tracks.Count
            };
            _project.Tracks.Add(track);
        }
        var clip = new TimelineClip
        {
            Name = item.Name,
            Kind = kind,
            TemplateId = item.Category is 1 or 4 ? item.Id : null,
            StartSeconds = _snapEnabled && track.IsSnapping ? SnapNewClipStart(track, SnapEditTime(time)) : time,
            SourceInSeconds = item.Source is MediaAsset rangeAsset ? rangeAsset.SourceInSeconds : 0,
            DurationSeconds = item.Source is MediaAsset media && media.Kind != MediaKind.Image &&
                (media.SourceOutSeconds ?? media.DurationSeconds) is double sourceOut &&
                sourceOut - media.SourceInSeconds is > 0 and < 86400 ? sourceOut - media.SourceInSeconds : 5,
            MediaAssetId = item.Source is MediaAsset asset ? asset.Id : null,
            Text = kind == ClipKind.Text ? CreateTitleStyle(isSticker ? "sticker" : item.Id) : null
        };
        if (isSticker && item.Source is BuiltinPreset stickerPreset)
        {
            string shapeKind = stickerPreset.Id switch
            {
                "sticker.circle" => "Circle",
                "sticker.ellipse" => "Ellipse",
                "sticker.triangle" => "Triangle",
                "sticker.pentagon" => "Pentagon",
                "sticker.hexagon" => "Hexagon",
                "sticker.decagon" => "Decagon",
                "sticker.parallelogram" => "Parallelogram",
                "sticker.quadrilateral" => "Quadrilateral",
                _ => "Rectangle"
            };
            var shape = new GraphicShape { Kind = shapeKind, WidthFraction = .35, HeightFraction = .35,
                FillColor = stickerPreset.AccentHex, StrokeColor = "#FFFFFFFF", StrokeFraction = .006 };
            if (shapeKind == "Quadrilateral")
                shape.Points = [new Point(.12, .18), new Point(.86, .12), new Point(.92, .82), new Point(.18, .9)];
            shape.Save(clip);
            clip.Text!.Content = string.Empty;
        }
        track.Clips.Add(clip);
        if (item.Source is MediaAsset source && source.Kind != MediaKind.Image && source.SourceOutSeconds is null &&
            !(source.DurationSeconds is > 0 and < 86400))
        {
            var pending = new PendingDropBatch(_project, _projectGeneration);
            pending.Clips.Add(new PendingDropClip(clip, clip.StartSeconds));
            _pendingDropBatches.Add(pending);
        }
        _selectedClip = clip;
        _selectedTrack = track;
        _contextTrack = track;
        ShowSelectedObjectSettings(); // 立即更新Tab显示
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(clip.StartSeconds);
        StatusText.Text = $"已添加 {clip.Name}";
    }

    /// <summary>
    /// The one place a title preset becomes a style. Both dropping a preset on
    /// the timeline and the catalog's demonstration read it, so the sample the
    /// user watches is the style they will actually get.
    /// </summary>
    internal static ClipText CreateTitleStyle(string presetId)
    {
        var style = new ClipText { Content = "在预览区双击编辑文字" };
        switch (presetId)
        {
            case "title.typewriter":
                style.Motion = DynamicTitlePreset.DefaultMotionFor(presetId);
                break;
            case "title.subtitle":
                style.FontSize = 56; style.VerticalAlignment = "Bottom";
                style.OutlineEnabled = true; style.OutlineThickness = 3;
                break;
            case "title.caption-bar":
                style.FontSize = 54; style.VerticalAlignment = "Bottom";
                style.BackgroundColor = "#CC181A20";
                break;
            case "title.lower-third":
                style.HorizontalAlignment = "Left"; style.VerticalAlignment = "Bottom";
                style.Bold = true; style.FontSize = 68;
                break;
            case "title.opening":
                style.Bold = true; style.FontSize = 128;
                style.ShadowEnabled = true;
                break;
            case "title.ending":
                style.FontSize = 64; style.VerticalAlignment = "Bottom";
                break;
            case "title.glow":
                style.ForegroundColor = "#53C5EC"; style.OutlineEnabled = true;
                style.OutlineColor = "#1B76B8"; style.OutlineThickness = 4;
                style.ShadowEnabled = true; style.ShadowColor = "#53C5EC";
                style.ShadowBlur = 24;
                break;
            case "title.outline":
                style.Bold = true; style.OutlineEnabled = true;
                style.OutlineThickness = 5;
                break;
            case "title.shadow":
                style.Bold = true; style.ShadowEnabled = true;
                style.ShadowBlur = 18;
                break;
            case "title.quote":
                style.Italic = true; style.FontSize = 62;
                style.BackgroundColor = "#CC24262C";
                break;
            case "title.counter":
                style.Bold = true; style.FontSize = 112;
                // The editable range is interpreted by DynamicTitlePreset.
                // Users can change either endpoint in the text properties.
                style.Content = "0 → 100";
                break;
            case "title.date":
                style.FontSize = 58; style.HorizontalAlignment = "Left";
                style.VerticalAlignment = "Bottom";
                break;
            case "title.modern":
                style.FontSize = 82; style.Bold = true; style.ShadowEnabled = true;
                style.ShadowBlur = 10; style.ShadowOpacity = .65;
                break;
            case "title.social":
                style.FontSize = 74; style.Bold = true; style.VerticalAlignment = "Bottom";
                style.BackgroundColor = "#D9181A20"; style.OutlineEnabled = true;
                style.OutlineThickness = 2;
                break;
            case "title.handwritten":
                style.FontSize = 76; style.Italic = true; style.ForegroundColor = "#FFE6A3";
                style.ShadowEnabled = true; style.ShadowBlur = 12;
                break;
            case "title.callout":
                style.FontSize = 58; style.Bold = true; style.HorizontalAlignment = "Left";
                style.BackgroundColor = "#EAF2A65A"; style.OutlineEnabled = true;
                style.OutlineColor = "#FFFFFFFF"; style.OutlineThickness = 2;
                break;
            case "title.kinetic":
                style.FontSize = 96; style.Bold = true; style.ForegroundColor = "#FFFFFFFF";
                style.OutlineEnabled = true; style.OutlineThickness = 4;
                style.Motion = new TitleMotion { PopSeconds = .45, PopStartScale = .45, PopOvershoot = .16 };
                break;
            case "title.credits":
                style.FontSize = 54; style.VerticalAlignment = "Bottom";
                style.HorizontalAlignment = "Center"; style.ShadowEnabled = true;
                break;
        }
        return style;
    }

    private void ApplyPresetToSelectedClip(LibraryItem item)
    {
        if (_selectedClip is null)
        {
            StatusText.Text = "请先选择一个时间线片段";
            return;
        }
        if (_selectedTrack is null || _selectedTrack.IsLocked ||
            !_selectedTrack.Clips.Contains(_selectedClip))
        {
            StatusText.Text = "目标轨道已锁定或片段已移除 · EDIT-PRESET-STATE";
            return;
        }
        if (item.Category == 3)
        {
            if (!TryFindTransitionBoundary(_selectedTrack, _selectedClip, null,
                out TimelineClip? incoming) || incoming is null)
            {
                StatusText.Text = "转场需要同一轨道上紧邻的两段视频或图片 · EDIT-TRANSITION-BOUNDARY";
                return;
            }
            SaveUndoState();
            incoming.Transitions.RemoveAll(transition => transition.Edge == "In");
            var addedTransition = new ClipTransition { PresetId = item.Id, Name = item.Name, Edge = "In" };
            incoming.Transitions.Add(addedTransition);
            TimelineClip prior = _selectedTrack.Clips.First(candidate =>
                candidate.Id != incoming.Id &&
                Math.Abs(candidate.StartSeconds + candidate.DurationSeconds - incoming.StartSeconds) <=
                    .5 / Math.Max(1, _project.Sequence.FramesPerSecond));
            _selectedTransition = addedTransition;
            _selectedTransitionIncoming = incoming;
            _selectedTransitionPrior = prior;
            _selectedTransitionTrack = _selectedTrack;
            _selectedClip = null;
            _selectedClips.Clear();
        }
        else
        {
            SaveUndoState();
            _selectedClip.Effects.Add(new ClipEffect { PresetId = item.Id, Name = item.Name });
        }
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(_project.Sequence.PlayheadSeconds);
        if (_selectedTransition is not null)
            ShowTransitionSettings();
        else if (_selectedClip is not null)
            OpenObjectToolPage(_selectedClip, "效果列表", BuildEffectControls);
        StatusText.Text = $"已添加 {item.Name}";
    }

    private void ApplyPresetToClipAt(LibraryItem item, Point point)
    {
        TimelineTrack? track = TrackAt(point.Y);
        double time = Math.Max(0, (point.X - HeaderSpace) / PixelsPerSecond);
        TimelineClip? clip = track?.Clips.OrderByDescending(candidate => candidate.StartSeconds)
            .FirstOrDefault(candidate => time >= candidate.StartSeconds &&
                time < candidate.StartSeconds + candidate.DurationSeconds);
        if (item.Category == 3 && track is not null &&
            TryFindTransitionBoundary(track, clip, time, out TimelineClip? incoming))
            clip = incoming;
        if (clip is null)
        {
            StatusText.Text = "请将特效或转场放在片段上";
            return;
        }
        SelectClip(clip);
        ApplyPresetToSelectedClip(item);
    }

    private bool TryFindTransitionBoundary(TimelineTrack track, TimelineClip? selected,
        double? dropTime, out TimelineClip? incoming)
    {
        double tolerance = .5 / Math.Max(1, _project.Sequence.FramesPerSecond);
        var pairs = from next in track.Clips
            where next.Kind is ClipKind.Video or ClipKind.Image or ClipKind.Color
            from previous in track.Clips
            where previous.Id != next.Id &&
                previous.Kind is ClipKind.Video or ClipKind.Image or ClipKind.Color &&
                Math.Abs(previous.StartSeconds + previous.DurationSeconds - next.StartSeconds) <= tolerance
            select new { Next = next, Previous = previous };
        var choice = dropTime is double time
            ? pairs.OrderBy(pair => Math.Abs(pair.Next.StartSeconds - time)).FirstOrDefault(pair =>
                Math.Abs(pair.Next.StartSeconds - time) <= Math.Max(.35, 30 / PixelsPerSecond))
            : pairs.FirstOrDefault(pair => pair.Next == selected) ??
              pairs.FirstOrDefault(pair => pair.Previous == selected);
        incoming = choice?.Next;
        return incoming is not null;
    }

    private void SelectClip(TimelineClip clip)
    {
        ClearTransitionSelection();
        _selectedKeyframe = null;
        _selectedClip = clip;
        _selectedTrack = _project.Tracks.FirstOrDefault(track => track.Clips.Contains(clip));
        _contextTrack = _selectedTrack;
        _selectedClips.Clear();
        _selectedClips.Add(clip);
        DrawTimeline();
        DrawTextOverlay();
        ShowSelectedObjectSettings();
    }

    private void SplitSelectedClip()
    {
        if (_selectedClip is null || _selectedTrack is null) return;
        if (_selectedTrack.IsLocked) { StatusText.Text = "轨道已锁定"; return; }
        double split = SnapTime(_project.Sequence.PlayheadSeconds);
        double frame = 1 / Math.Max(1, _project.Sequence.FramesPerSecond);
        if (split + .000001 < _selectedClip.StartSeconds + frame ||
            split - .000001 > _selectedClip.StartSeconds + _selectedClip.DurationSeconds - frame) return;
        SaveUndoState();
        var right = CloneClip(_selectedClip);
        right.Id = Guid.NewGuid().ToString("N");
        right.StartSeconds = split;
        double leftLength = split - _selectedClip.StartSeconds;
        double rightLength = _selectedClip.DurationSeconds - leftLength;
        ClipAnimation.KeepRange(right, leftLength, rightLength);
        AdvanceMulticamLocalStart(right, leftLength);
        ClipAnimation.KeepRange(_selectedClip, 0, leftLength);
        if (_selectedClip.TimeRemapPoints.Count > 0)
        {
            ClipTimeMap.KeepRange(right, leftLength, rightLength);
            ClipTimeMap.KeepRange(_selectedClip, 0, leftLength);
        }
        else if (_selectedClip.IsReversed)
            _selectedClip.SourceInSeconds += rightLength * _selectedClip.Speed;
        else
            right.SourceInSeconds += leftLength * _selectedClip.Speed;
        _selectedClip.DurationSeconds = leftLength;
        right.DurationSeconds = rightLength;
        right.GroupId = null;
        right.LinkGroupId = null;
        _selectedTrack.Clips.Add(right);
        SelectClip(right);
        MarkProjectDirty();
        RefreshTimeline();
        StatusText.Text = "已分割片段";
    }

    private void DeleteSelectedClip()
    {
        if (_selectedKeyframe is not null && _selectedClip is not null && _selectedTrack is not null)
        {
            ClipKeyframe key = _selectedKeyframe;
            RemoveSelectedKeyframe(_selectedTrack, _selectedClip, key);
            return;
        }
        if (_selectedTransition is not null && _selectedTransitionTrack is not null &&
            _selectedTransitionIncoming is not null && _selectedTransitionPrior is not null)
        {
            RemoveBoundaryTransition(_selectedTransitionTrack, _selectedTransitionIncoming,
                _selectedTransitionPrior, _selectedTransition);
            return;
        }
        if (_selectedClip is null || _selectedTrack is null) return;
        if (_selectedTrack.IsLocked) { StatusText.Text = "轨道已锁定"; return; }
        SaveUndoState();
        _selectedTrack.Clips.Remove(_selectedClip);
        _selectedClip = null;
        _selectedKeyframe = null;
        _selectedClips.Clear();
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = "已删除片段";
    }

    private void RippleDeleteSelectedClip() => RemoveSelectedClipAndCloseGap("已波纹删除片段");

    /// <summary>
    /// Removes the selected clip and closes the gap on its track and on every
    /// sync-locked track, moving markers and the playhead with it. Ripple
    /// delete and extract are the same edit, so they share this one body.
    /// </summary>
    private void RemoveSelectedClipAndCloseGap(string completedMessage)
    {
        if (_selectedClip is null || _selectedTrack is null) return;
        if (_selectedTrack.IsLocked) { StatusText.Text = "轨道已锁定"; return; }
        SaveUndoState();
        double end = _selectedClip.StartSeconds + _selectedClip.DurationSeconds;
        double start = _selectedClip.StartSeconds;
        TimelineTrack selectedTrack = _selectedTrack;
        selectedTrack.Clips.Remove(_selectedClip);
        foreach (TimelineTrack track in _project.Tracks.Where(track => !track.IsLocked &&
            (ReferenceEquals(track, selectedTrack) || track.IsSyncLocked)))
            RemoveTimelineInterval(track, start, end);
        foreach (TimelineMarker marker in _project.Markers)
            if (marker.TimeSeconds >= end) marker.TimeSeconds -= end - start;
            else if (marker.TimeSeconds > start) marker.TimeSeconds = start;
        _project.Sequence.PlayheadSeconds = Math.Max(0,
            _project.Sequence.PlayheadSeconds >= end
                ? _project.Sequence.PlayheadSeconds - (end - start)
                : Math.Min(_project.Sequence.PlayheadSeconds, start));
        _selectedClip = null;
        _selectedClips.Clear();
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = completedMessage;
    }

    private void DuplicateSelectedClip()
    {
        if (_selectedClip is null || _selectedTrack is null) return;
        if (_selectedTrack.IsLocked) { StatusText.Text = "轨道已锁定"; return; }
        SaveUndoState();
        var duplicate = CloneClip(_selectedClip);
        duplicate.Id = Guid.NewGuid().ToString("N");
        duplicate.GroupId = null;
        duplicate.LinkGroupId = null;
        duplicate.StartSeconds += duplicate.DurationSeconds;
        _selectedTrack.Clips.Add(duplicate);
        SelectClip(duplicate);
        MarkProjectDirty();
        RefreshTimeline();
        StatusText.Text = "已复制片段";
    }

    private void PasteClipboardClip()
    {
        if (_clipboardClip is null) return;
        TimelineTrack? track = ResolveCommandTarget(_clipboardClip);
        if (track is null || track.IsLocked || !IsCompatible(track, _clipboardClip)) return;
        SaveUndoState();
        var pasted = CloneClip(_clipboardClip);
        pasted.Id = Guid.NewGuid().ToString("N");
        pasted.GroupId = null;
        pasted.LinkGroupId = null;
        pasted.StartSeconds = _project.Sequence.PlayheadSeconds;
        track.Clips.Add(pasted);
        SelectClip(pasted);
        MarkProjectDirty();
        RefreshTimeline();
        StatusText.Text = "已粘贴片段";
    }

    private void EditSelectedSpeed()
    {
        if (_selectedClip is null) return;
        if (_selectedClip.Kind is ClipKind.Video or ClipKind.Audio)
        {
            OpenObjectToolPage(_selectedClip, _selectedClip.Kind == ClipKind.Video ? "视频调速" : "音频调速", BuildSpeedTool);
            return;
        }
        if (_selectedTrack?.IsLocked != false) { StatusText.Text = "轨道已锁定"; return; }
        if (_selectedClip.MulticamGroupId is not null)
        {
            StatusText.Text = "多机位切段需保持原速 · MULTICAM-EDIT";
            return;
        }
        TimelineClip edited = _selectedClip;
        var dialog = new Window
        {
            Title = "速度 / 时长",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            Background = Brush("#191B20"),
            Foreground = Brushes.White
        };
        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var speedLabel = new TextBlock { Text = "播放速度", Margin = new Thickness(0, 4, 12, 8) };
        var speedBox = new TextBox { Text = _selectedClip.Speed.ToString("0.##"), MinWidth = SystemParameters.PrimaryScreenWidth * .09, Margin = new Thickness(0, 0, 0, 8) };
        var durationLabel = new TextBlock { Text = "时长（秒）", Margin = new Thickness(0, 4, 12, 8) };
        var durationBox = new TextBox { Text = _selectedClip.DurationSeconds.ToString("0.##"), MinWidth = SystemParameters.PrimaryScreenWidth * .09, Margin = new Thickness(0, 0, 0, 8) };
        if (_selectedClip.Kind is not (ClipKind.Video or ClipKind.Audio))
        {
            speedBox.IsEnabled = false;
            speedBox.Text = "1";
        }
        Grid.SetRow(speedLabel, 0); Grid.SetColumn(speedLabel, 0); grid.Children.Add(speedLabel);
        Grid.SetRow(speedBox, 0); Grid.SetColumn(speedBox, 1); grid.Children.Add(speedBox);
        Grid.SetRow(durationLabel, 1); Grid.SetColumn(durationLabel, 0); grid.Children.Add(durationLabel);
        Grid.SetRow(durationBox, 1); Grid.SetColumn(durationBox, 1); grid.Children.Add(durationBox);
        bool hasFiniteSource = (_selectedClip.Kind is ClipKind.Video or ClipKind.Audio) &&
            _project.Media.Any(asset => asset.Id == _selectedClip.MediaAssetId &&
                asset.DurationSeconds is > 0);
        var preserveSource = new CheckBox
        {
            Content = "变速时保持源画面范围",
            IsChecked = false,
            IsEnabled = hasFiniteSource,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(preserveSource, 2); Grid.SetColumnSpan(preserveSource, 2);
        grid.Children.Add(preserveSource);
        double originalSourceSpan = ClipTimeMap.SourceOffset(_selectedClip, _selectedClip.DurationSeconds);
        bool originalReverse = edited.IsReversed;
        double originalSourceIn = edited.SourceInSeconds;
        double originalDuration = edited.DurationSeconds;
        double originalSpeed = edited.Speed;
        int originalRemapCount = edited.TimeRemapPoints.Count;
        speedBox.TextChanged += (_, _) =>
        {
            if (preserveSource.IsChecked != true ||
                !double.TryParse(speedBox.Text, out double parsed) ||
                !double.IsFinite(parsed) || parsed <= 0) return;
            durationBox.Text = (originalSourceSpan / parsed).ToString("0.###");
        };
        var confirm = new Button { Content = "确定", Padding = new Thickness(16, 5, 16, 5), HorizontalAlignment = HorizontalAlignment.Right };
        confirm.Click += (_, _) => dialog.DialogResult = true;
        Grid.SetRow(confirm, 3); Grid.SetColumn(confirm, 1); grid.Children.Add(confirm);
        dialog.Content = grid;
        if (dialog.ShowDialog() != true) return;
        if (!_project.Tracks.Any(track => track.Clips.Contains(edited) && !track.IsLocked) ||
            edited.IsReversed != originalReverse || edited.SourceInSeconds != originalSourceIn ||
            edited.DurationSeconds != originalDuration || edited.Speed != originalSpeed ||
            edited.TimeRemapPoints.Count != originalRemapCount)
        {
            StatusText.Text = "片段或轨道已变化 · EDIT-SPEED-STATE";
            return;
        }
        if (!double.TryParse(speedBox.Text, out double speed) || !double.IsFinite(speed) || speed < .05 || speed > 32 ||
            !double.TryParse(durationBox.Text, out double duration) || !double.IsFinite(duration) ||
            duration < 1 / Math.Max(1, _project.Sequence.FramesPerSecond) || duration > 24 * 3600)
        {
            MessageBox.Show(this, "速度必须在 0.05 到 32 倍之间，时长须在一帧到 24 小时之间。\n阶段代码：EDIT-SPEED-VALUE",
                "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        MediaAsset? source = _project.Media.FirstOrDefault(asset => asset.Id == edited.MediaAssetId);
        if (source?.DurationSeconds is double sourceDuration &&
            edited.SourceInSeconds + duration * speed > sourceDuration + .001)
        {
            StatusText.Text = "变速后的片段超出源素材范围 · EDIT-SPEED-SOURCE";
            return;
        }
        if (edited.MulticamGroupId is not null && Math.Abs(speed - 1) > .000001)
        {
            StatusText.Text = "多机位切段需保持原速 · MULTICAM-EDIT";
            return;
        }
        if (edited.MulticamGroupId is not null &&
            _project.MulticamGroups.FirstOrDefault(group => group.Id == edited.MulticamGroupId)
                is { } group &&
            edited.MulticamLocalStartSeconds + duration > group.DurationSeconds + .001)
        {
            StatusText.Text = "片段时长超出多机位共享范围 · MULTICAM-EDIT";
            return;
        }
        SaveUndoState();
        // A shorter duration must not leave persisted keyframes beyond the
        // clip boundary, otherwise the project file would fail validation.
        ClipAnimation.KeepRange(edited, 0, duration);
        edited.Speed = speed;
        edited.SpeedAdjustmentApplied = true;
        edited.DurationSeconds = duration;
        edited.TimeRemapPoints.Clear();
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(_project.Sequence.PlayheadSeconds);
    }

    private void ToggleSelectedReverse()
    {
        if (_selectedClip is null) return;
        if (_selectedTrack?.IsLocked != false) { StatusText.Text = "轨道已锁定"; return; }
        if (_selectedClip.Kind is not (ClipKind.Video or ClipKind.Audio))
        { StatusText.Text = "反向播放适用于视频或音频片段 · EDIT-REVERSE-TYPE"; return; }
        if (_selectedClip.MulticamGroupId is not null)
        {
            StatusText.Text = "多机位切段不支持倒放；请先复制为普通片段 · MULTICAM-EDIT";
            return;
        }
        SaveUndoState();
        _selectedClip.IsReversed = !_selectedClip.IsReversed;
        MarkProjectDirty();
        SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = _selectedClip.IsReversed ? "已设置反向播放" : "已恢复正向播放";
    }

    private void CopySelectedClip()
    {
        if (_selectedClip is null) return;
        _clipboardClip = CloneClip(_selectedClip);
        PasteMenuItem.IsEnabled = true;
        TimelinePasteMenuItem.IsEnabled = true;
        StatusText.Text = "已复制片段";
    }

    private static List<ClipTimeRemapPoint> CloneTimeRemap(IEnumerable<ClipTimeRemapPoint>? points) =>
        points?.Select(point => new ClipTimeRemapPoint
        { TimeSeconds = point.TimeSeconds, Speed = point.Speed }).ToList() ?? [];

    /// <summary>
    /// Cuts the selected clip into the internal edit clipboard. The source
    /// media remains untouched and the removal participates in undo history.
    /// </summary>
    private void CutSelectedClip()
    {
        if (_selectedClip is null || _selectedTrack is null) return;
        if (_selectedTrack.IsLocked) { StatusText.Text = "轨道已锁定"; return; }
        CopySelectedClip();
        DeleteSelectedClip();
        StatusText.Text = "已剪切片段";
    }

    private void EditSelectedText()
    {
        if (_selectedClip?.Kind == ClipKind.Text) ShowTextProperties(_selectedClip);
    }

    private void EditSelectedClipProperties()
    {
        if (_selectedClip is null)
        {
            StatusText.Text = "请先选择时间线片段";
            return;
        }
        if (_selectedClip.Kind == ClipKind.Text && IsSubtitleClip(_selectedClip)) ShowSubtitleProperties(_selectedClip);
        else if (_selectedClip.Kind == ClipKind.Text) EditSelectedText();
        else if (_selectedClip.Kind == ClipKind.Audio) ShowAudioClipProperties(_selectedClip);
        else if (_selectedClip.Kind is ClipKind.Image or ClipKind.Video) EditMediaTransform(_selectedClip);
        else EditSelectedSpeed();
    }

    private void TrimSelectedStart()
    {
        if (_selectedClip is null) return;
        if (_selectedTrack?.IsLocked != false) { StatusText.Text = "轨道已锁定"; return; }
        double playhead = _project.Sequence.PlayheadSeconds;
        if (playhead <= _selectedClip.StartSeconds || playhead >= _selectedClip.StartSeconds + _selectedClip.DurationSeconds) return;
        SaveUndoState();
        double removed = playhead - _selectedClip.StartSeconds;
        ClipAnimation.KeepRange(_selectedClip, removed, _selectedClip.DurationSeconds - removed);
        AdvanceMulticamLocalStart(_selectedClip, removed);
        if (_selectedClip.TimeRemapPoints.Count > 0)
            ClipTimeMap.KeepRange(_selectedClip, removed, _selectedClip.DurationSeconds - removed);
        else if (!_selectedClip.IsReversed)
            _selectedClip.SourceInSeconds += removed * _selectedClip.Speed;
        _selectedClip.StartSeconds = playhead;
        _selectedClip.DurationSeconds -= removed;
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(_project.Sequence.PlayheadSeconds);
    }

    private void TrimSelectedEnd()
    {
        if (_selectedClip is null) return;
        if (_selectedTrack?.IsLocked != false) { StatusText.Text = "轨道已锁定"; return; }
        double playhead = _project.Sequence.PlayheadSeconds;
        if (playhead <= _selectedClip.StartSeconds || playhead >= _selectedClip.StartSeconds + _selectedClip.DurationSeconds) return;
        SaveUndoState();
        ClipAnimation.KeepRange(_selectedClip, 0, playhead - _selectedClip.StartSeconds);
        if (_selectedClip.TimeRemapPoints.Count > 0)
            ClipTimeMap.KeepRange(_selectedClip, 0, playhead - _selectedClip.StartSeconds);
        else if (_selectedClip.IsReversed)
            _selectedClip.SourceInSeconds +=
                (_selectedClip.DurationSeconds - (playhead - _selectedClip.StartSeconds)) * _selectedClip.Speed;
        _selectedClip.DurationSeconds = playhead - _selectedClip.StartSeconds;
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(_project.Sequence.PlayheadSeconds);
    }

    private void AddTrack(TrackKind kind)
    {
        SaveUndoState();
        int count = _project.Tracks.Count(track => track.Kind == kind) + 1;
        var track = new TimelineTrack { Name = $"{(kind == TrackKind.Audio ? "音频" : "视频")} {count}", Kind = kind, Order = _project.Tracks.Count };
        _project.Tracks.Add(track);
        _selectedTrack = track;
        MarkProjectDirty();
        RefreshTimeline();
    }

    private void ChangeContextTrack(Action<TimelineTrack> change)
    {
        TimelineTrack? track = _selectedTrack is not null && _project.Tracks.Contains(_selectedTrack)
            ? _selectedTrack : _contextTrack;
        if (track is null || !_project.Tracks.Contains(track)) return;
        SaveUndoState();
        change(track);
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(_project.Sequence.PlayheadSeconds);
    }

    /// <summary>
    /// Predicts the drop slot from the pointer and redraws only when that slot
    /// actually changes, so the gap animates without rebuilding every frame.
    /// </summary>
    private void UpdateTrackDragPrediction(Point canvasPoint, bool wasDragging)
    {
        if (_draggingTrack is null) return;
        // Bias by half a row so the gap opens once the pointer passes a row's
        // midpoint rather than only after it fully enters the next row.
        int predicted = ClampTrackTarget(_draggingTrack, Math.Clamp(
            (int)Math.Round((canvasPoint.Y - RulerSpace - TrackSpace / 2) / TrackSpace),
            0, Math.Max(0, _project.Tracks.Count - 1)));
        bool changed = predicted != _trackDragTargetIndex;
        _trackDragTargetIndex = predicted;
        ShowTrackDragBlock(_draggingTrack);
        if (changed || !wasDragging) DrawTimeline();
        int from = _project.Tracks.IndexOf(_draggingTrack);
        bool audio = _draggingTrack.Kind == TrackKind.Audio;
        StatusText.Text = from == predicted
            ? $"拖动中：{_draggingTrack.Name} · 松开放回原位" +
                (_project.Tracks.Any(item => (item.Kind == TrackKind.Audio) != audio)
                    ? $"；{(audio ? "音频" : "视频")}轨道只能在同类轨道之间排序" : string.Empty)
            : $"拖动中：{_draggingTrack.Name} → 第 {predicted + 1} 条轨道，上层轨道会盖住下层";
    }

    /// <summary>
    /// Shows the dragged track's name as a block that follows the pointer. It
    /// lives on the adorner layer so canvas scrolling never displaces it.
    /// </summary>
    private void ShowTrackDragBlock(TimelineTrack track)
    {
        _trackDragBlock ??= CreateTrackDragBlock(track);
        if (_trackDragBlock is null) return;
        if (!TrackDragOverlay.Children.Contains(_trackDragBlock))
            TrackDragOverlay.Children.Add(_trackDragBlock);
        Canvas.SetLeft(_trackDragBlock, _trackDragPointer.X + 12);
        Canvas.SetTop(_trackDragBlock, _trackDragPointer.Y - TrackSpace * .16);
    }

    private Border CreateTrackDragBlock(TimelineTrack track) => new()
    {
        Width = HeaderSpace * .82,
        Height = TrackSpace * .32,
        Background = Brush("#FF9957"),
        CornerRadius = new CornerRadius(4),
        Opacity = .92,
        IsHitTestVisible = false,
        Child = new TextBlock
        {
            Text = track.Name,
            Foreground = Brushes.Black,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        }
    };

    private void HideTrackDragBlock()
    {
        if (_trackDragBlock is not null) TrackDragOverlay.Children.Remove(_trackDragBlock);
        _trackDragBlock = null;
    }

    /// <summary>
    /// Scrolls the timeline while the pointer rests near the top or bottom of
    /// the viewport, so a track can be moved beyond the visible rows.
    /// </summary>
    private void UpdateTrackDragEdgeScroll()
    {
        double viewport = TimelineScrollViewer.ViewportHeight;
        if (viewport <= 0) { StopTrackDragEdgeScroll(); return; }
        double margin = Math.Min(TrackSpace * .75, viewport * .25);
        double step = 0;
        if (_trackDragPointer.Y < margin)
            step = -Math.Max(6, (margin - _trackDragPointer.Y) / margin * TrackSpace * .5);
        else if (_trackDragPointer.Y > viewport - margin)
            step = Math.Max(6, (_trackDragPointer.Y - (viewport - margin)) / margin * TrackSpace * .5);
        _trackDragScrollStep = step;
        if (Math.Abs(step) < .001) { StopTrackDragEdgeScroll(); return; }
        if (_trackDragScrollTimer is not null) return;
        _trackDragScrollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _trackDragScrollTimer.Tick += TrackDragEdgeScrollTick;
        _trackDragScrollTimer.Start();
    }

    private void TrackDragEdgeScrollTick(object? sender, EventArgs args)
    {
        if (_draggingTrack is null || Math.Abs(_trackDragScrollStep) < .001)
        {
            StopTrackDragEdgeScroll();
            return;
        }
        double target = Math.Clamp(TimelineScrollViewer.VerticalOffset + _trackDragScrollStep,
            0, Math.Max(0, TimelineScrollViewer.ScrollableHeight));
        if (Math.Abs(target - TimelineScrollViewer.VerticalOffset) < .001) return;
        TimelineScrollViewer.ScrollToVerticalOffset(target);
        // Scrolling moves rows under a stationary pointer, so the predicted
        // slot has to be recomputed from the new canvas position.
        Point canvasPoint = new(_trackDragPointer.X + TimelineScrollViewer.HorizontalOffset,
            _trackDragPointer.Y + target);
        UpdateTrackDragPrediction(canvasPoint, true);
    }

    private void StopTrackDragEdgeScroll()
    {
        if (_trackDragScrollTimer is null) return;
        _trackDragScrollTimer.Stop();
        _trackDragScrollTimer.Tick -= TrackDragEdgeScrollTick;
        _trackDragScrollTimer = null;
    }

    private void FinishTrackDrag(Point dropPoint)
    {
        TimelineTrack? dragged = _draggingTrack;
        bool moved = _trackDragStarted;
        int predicted = _trackDragTargetIndex;
        _draggingTrack = null;
        _trackDragStarted = false;
        _trackDragTargetIndex = -1;
        StopTrackDragEdgeScroll();
        HideTrackDragBlock();
        if (TimelineCanvas.IsMouseCaptured) TimelineCanvas.ReleaseMouseCapture();
        if (dragged is null) return;
        if (!moved) { StatusText.Text = $"已选择轨道：{dragged.Name}"; return; }
        // Commit to the slot the gap was showing. Falling back to the pointer
        // keeps the drop sane if prediction never ran.
        int targetIndex = predicted >= 0
            ? Math.Clamp(predicted, 0, _project.Tracks.Count - 1)
            : Math.Clamp((int)Math.Floor((dropPoint.Y - RulerSpace) / TrackSpace), 0, _project.Tracks.Count - 1);
        if (targetIndex == _project.Tracks.IndexOf(dragged))
        {
            DrawTimeline();
            StatusText.Text = $"轨道未移动：{dragged.Name}";
            return;
        }
        ReorderTrack(dragged, targetIndex);
    }

    private void MoveContextTrack(int step)
    {
        TimelineTrack? track = _selectedTrack is not null && _project.Tracks.Contains(_selectedTrack)
            ? _selectedTrack : _contextTrack;
        if (track is null) { StatusText.Text = "请先点击轨道名称选择轨道"; return; }
        ReorderTrack(track, _project.Tracks.IndexOf(track) + step);
    }

    /// <summary>
    /// Limits a move to the rows already occupied by the track's own kind, so
    /// audio never lands between video layers. Video order decides which
    /// picture covers which, and that stack has to stay contiguous.
    /// </summary>
    private int ClampTrackTarget(TimelineTrack track, int targetIndex)
    {
        bool audio = track.Kind == TrackKind.Audio;
        var sameKind = _project.Tracks
            .Select((item, index) => (item, index))
            .Where(entry => (entry.item.Kind == TrackKind.Audio) == audio)
            .Select(entry => entry.index)
            .ToList();
        if (sameKind.Count == 0) return targetIndex;
        return Math.Clamp(targetIndex, sameKind[0], sameKind[^1]);
    }

    private void ReorderTrack(TimelineTrack track, int targetIndex)
    {
        int oldIndex = _project.Tracks.IndexOf(track);
        if (oldIndex < 0) return;
        targetIndex = ClampTrackTarget(track, targetIndex);
        if (targetIndex < 0 || targetIndex >= _project.Tracks.Count || oldIndex == targetIndex) return;
        SaveUndoState();
        _project.Tracks.RemoveAt(oldIndex);
        _project.Tracks.Insert(targetIndex, track);
        for (int index = 0; index < _project.Tracks.Count; index++) _project.Tracks[index].Order = index;
        _selectedTrack = track;
        _contextTrack = track;
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = $"轨道已移动：{track.Name}";
    }

    private void RemoveEmptyTracks()
    {
        int removable = Math.Min(_project.Tracks.Count - 1, _project.Tracks.Count(track => track.Clips.Count == 0));
        if (removable <= 0) { StatusText.Text = "没有可删除的空轨道"; return; }
        SaveUndoState();
        // Keep one track so the timeline always has a drop destination.
        for (int index = _project.Tracks.Count - 1; index >= 0 && _project.Tracks.Count > 1; index--)
            if (_project.Tracks[index].Clips.Count == 0) _project.Tracks.RemoveAt(index);
        for (int index = 0; index < _project.Tracks.Count; index++) _project.Tracks[index].Order = index;
        if (_selectedTrack is not null && !_project.Tracks.Contains(_selectedTrack)) _selectedTrack = null;
        if (_contextTrack is not null && !_project.Tracks.Contains(_contextTrack)) _contextTrack = null;
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = $"已删除 {removable} 条空轨道";
    }

    private void RemoveContextTrack()
    {
        TimelineTrack? track = _selectedTrack is not null && _project.Tracks.Contains(_selectedTrack)
            ? _selectedTrack : _contextTrack;
        if (track is null) { StatusText.Text = "请先点击轨道名称选择轨道"; return; }
        if (!_project.Tracks.Contains(track) || _project.Tracks.Count <= 1) return;
        if (track.Clips.Count > 0 && MessageBox.Show(this,
            $"删除轨道“{track.Name}”及其中的 {track.Clips.Count} 个片段？",
            "删除轨道", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SaveUndoState();
        _project.Tracks.Remove(track);
        for (int index = 0; index < _project.Tracks.Count; index++) _project.Tracks[index].Order = index;
        _contextTrack = null;
        _selectedTrack = null;
        _selectedClip = null;
        _selectedClips.Clear();
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(_project.Sequence.PlayheadSeconds);
    }

    private void FitTimeline()
    {
        double end = Math.Max(20, _project.Tracks.SelectMany(track => track.Clips)
            .Select(clip => clip.StartSeconds + clip.DurationSeconds).DefaultIfEmpty(20).Max());
        double viewport = Math.Max(200, TimelineScrollViewer.ViewportWidth - HeaderSpace - 20);
        TimelineZoomSlider.Value = Math.Clamp(viewport / (end * BasePixelsPerSecond), TimelineZoomSlider.Minimum, TimelineZoomSlider.Maximum);
        TimelineScrollViewer.ScrollToHorizontalOffset(0);
    }

    private void TimelineScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            TimelineZoomSlider.Value = Math.Clamp(TimelineZoomSlider.Value * (args.Delta > 0 ? 1.1 : 0.9), TimelineZoomSlider.Minimum, TimelineZoomSlider.Maximum);
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            TimelineScrollViewer.ScrollToVerticalOffset(TimelineScrollViewer.VerticalOffset - args.Delta);
        else
            TimelineScrollViewer.ScrollToHorizontalOffset(TimelineScrollViewer.HorizontalOffset - args.Delta);
        args.Handled = true;
    }

    private TimelineTrack? TrackAt(double y)
    {
        int index = (int)Math.Floor((y - RulerSpace) / TrackSpace);
        return index >= 0 && index < _project.Tracks.Count ? _project.Tracks[index] : null;
    }

    private static bool IsCompatible(TimelineTrack track, TimelineClip clip) => IsCompatible(track, clip.Kind);
    private static bool IsCompatible(TimelineTrack track, ClipKind kind) => kind switch
    {
        ClipKind.Audio => track.Kind == TrackKind.Audio,
        _ => track.Kind == TrackKind.Video
    };

    private double SnapTime(double time) => Math.Round(time * Math.Max(1, _project.Sequence.FramesPerSecond)) /
        Math.Max(1, _project.Sequence.FramesPerSecond);

    /// <summary>
    /// Snap an edit point to nearby sequence markers, the playhead, or clip
    /// boundaries. The threshold is measured on screen so zooming does not
    /// make snapping unexpectedly harder to use.
    /// </summary>
    private double SnapEditTime(double time, TimelineClip? excludedClip = null)
    {
        double frameTime = SnapTime(Math.Max(0, time));
        return SnapTime(frameTime + (FindSnapOffset(frameTime, excludedClip) ?? 0));
    }

    private double SnapMovingClipStart(double time)
    {
        if (_selectedClip is null) return SnapEditTime(time);
        if (_selectedTrack?.IsSnapping == false) return SnapTime(Math.Max(0, time));
        double frameTime = SnapTime(Math.Max(0, time));
        double? leading = FindSnapOffset(frameTime, _selectedClip);
        double? trailing = FindSnapOffset(frameTime + _selectedClip.DurationSeconds, _selectedClip);
        // Both edges of a dragged clip are useful alignment targets. The
        // smaller correction wins if they are near different edit points.
        double? correction = leading is null ? trailing : trailing is null ? leading :
            Math.Abs(leading.Value) <= Math.Abs(trailing.Value) ? leading : trailing;
        // Preserve the frame grid when an imported clip has a nonintegral
        // duration or a marker was authored between frames.
        return Math.Max(0, SnapTime(frameTime + (correction ?? 0)));
    }

    private double? FindSnapOffset(double time, TimelineClip? excludedClip)
    {
        if (!_snapEnabled || !double.IsFinite(time) || PixelsPerSecond <= 0) return null;
        double threshold = 9 / PixelsPerSecond;
        double bestDistance = threshold + 1e-9;
        double? bestOffset = null;
        void Consider(double anchor)
        {
            if (!double.IsFinite(anchor) || anchor < 0) return;
            double distance = Math.Abs(anchor - time);
            if (distance >= bestDistance) return;
            bestDistance = distance;
            bestOffset = anchor - time;
        }

        Consider(0);
        Consider(_project.Sequence.PlayheadSeconds);
        foreach (TimelineMarker marker in _project.Markers) Consider(marker.TimeSeconds);
        TimelineTrack? activeTrack = _selectedTrack;
        foreach (TimelineTrack track in _project.Tracks.Where(track => activeTrack is null || ReferenceEquals(track, activeTrack)))
        foreach (TimelineClip clip in track.Clips)
        {
            if (ReferenceEquals(clip, excludedClip) ||
                _moveCompanionOriginalStarts.ContainsKey(clip)) continue;
            Consider(clip.StartSeconds);
            Consider(clip.StartSeconds + clip.DurationSeconds);
        }
        return bestOffset;
    }

    private void SaveUndoState()
    {
        _undoStates.Push(JsonSerializer.Serialize(_project));
        TrimHistoryCapacity();
        _redoStates.Clear();
    }

    private void TrimHistoryCapacity()
    {
        while (_undoStates.Count > 30)
        {
            // Preserve a bounded history for long editing sessions. Rebuild
            // only when the cap is exceeded, keeping the newest snapshots.
            string[] newestFirst = _undoStates.Take(30).ToArray();
            _undoStates.Clear();
            for (int index = newestFirst.Length - 1; index >= 0; index--) _undoStates.Push(newestFirst[index]);
        }
    }

    private async void Undo()
    {
        await RestoreHistoryAsync(_undoStates, _redoStates, "已撤销");
    }

    private async void Redo()
    {
        await RestoreHistoryAsync(_redoStates, _undoStates, "已重做");
    }

    private async Task RestoreHistoryAsync(Stack<string> source, Stack<string> destination, string completedText)
    {
        if (_historyOperationInProgress || _projectIoInProgress || _exportInProgress ||
            _mediaImportGate.CurrentCount == 0 || source.Count == 0 ||
            _movingClip || _trimmingStart || _trimmingEnd || _scrubbingPlayhead ||
            _rubberBandDrag is not null || _bezierHandleDrag is not null ||
            _draggingTitleClip is not null) return;

        if (_isPlaying) TogglePlayback();
        _historyOperationInProgress = true;
        // Disable editing while a worker reads the mutable project graph. Any
        // asynchronous import already in flight is invalidated by the new
        // generation; metadata probes are cancelled before snapshotting.
        IsEnabled = false;
        try
        {
            _projectGeneration++;
            _mediaMetadataCancellation?.Cancel();
            _mediaMetadataCancellation = null;
            StatusText.Text = "正在恢复编辑历史…";
            ProjectDocument current = _project;
            string nextJson = source.Peek();
            var restored = await Task.Run(() =>
            {
                ProjectDocument next = JsonSerializer.Deserialize<ProjectDocument>(nextJson)
                    ?? throw new JsonException("编辑历史为空。");
                return next;
            });
            // The dispatcher is free while deserializing the large history
            // snapshot. The editor is disabled here so the current graph is
            // stable while the reciprocal snapshot is built on the worker.
            string currentJson = await Task.Run(() => JsonSerializer.Serialize(current));
            source.Pop();
            destination.Push(currentJson);
            // The history snapshot stores asset metadata but the extracted
            // project media lives in a cache leased by the document instance.
            // Hand that lease to the restored document before releasing the
            // current graph, so Undo/Redo can still resolve embedded media.
            ProjectFileService.TransferCachedMedia(current, restored);
            _project = restored;
            ClearTimelineSelection();
            _pendingDropBatches.Clear();
            MarkProjectDirty();
            RefreshEditorFromProject();
            StatusText.Text = completedText;
        }
        catch (Exception exception)
        {
            StatusText.Text = "编辑历史恢复失败 · HIST-RESTORE";
            MessageBox.Show(this, $"无法恢复编辑历史。\n阶段代码：HIST-RESTORE\n{exception.Message}",
                "编辑历史恢复失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
            _historyOperationInProgress = false;
        }
    }

    private static TimelineClip CloneClip(TimelineClip clip) => JsonSerializer.Deserialize<TimelineClip>(JsonSerializer.Serialize(clip))!;

    private void AddRectangle(double x, double y, double width, double height, string color)
    {
        var rectangle = new Rectangle { Width = Math.Max(0, width), Height = Math.Max(0, height), Fill = Brush(color), IsHitTestVisible = false };
        Canvas.SetLeft(rectangle, x);
        Canvas.SetTop(rectangle, y);
        TimelineCanvas.Children.Add(rectangle);
    }

    private void AddLine(double x1, double y1, double x2, double y2, string color, double thickness)
    {
        var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = Brush(color), StrokeThickness = thickness, IsHitTestVisible = false };
        TimelineCanvas.Children.Add(line);
    }

    private void AddText(string value, double x, double y, string color, double size)
    {
        var text = new TextBlock { Text = value, Foreground = Brush(color), FontSize = size, IsHitTestVisible = false };
        Canvas.SetLeft(text, x);
        Canvas.SetTop(text, y);
        TimelineCanvas.Children.Add(text);
    }

    private static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
    private static Brush SafeMarkerBrush(string? color)
    {
        try { return Brush(string.IsNullOrWhiteSpace(color) ? "#FFB347" : color); }
        catch (FormatException) { return Brush("#FFB347"); }
    }

    private static string FormatRuler(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");
}
