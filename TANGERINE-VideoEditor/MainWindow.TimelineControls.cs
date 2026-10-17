using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Adds targeted tracks and editable clip rubber bands to the timeline. The
/// active band selects one project property without changing source media.
/// </summary>
public partial class MainWindow
{
    private static List<ClipKeyframe> CloneTimelineKeyframes(IEnumerable<ClipKeyframe>? source) =>
        source?.Select(key => new ClipKeyframe
        {
            Id = key.Id, Property = key.Property, TimeSeconds = key.TimeSeconds,
            Value = key.Value, Interpolation = key.Interpolation,
            BezierOutTime = key.BezierOutTime, BezierOutValue = key.BezierOutValue,
            BezierInTime = key.BezierInTime, BezierInValue = key.BezierInValue,
            AdditionalData = key.AdditionalData is null ? null :
                new Dictionary<string, System.Text.Json.JsonElement>(key.AdditionalData)
        }).ToList() ?? [];

    private readonly Dictionary<TrackKind, string> _targetTrackIds = [];
    private string? _targetSequenceId;
    private string _rubberBandProperty = "Opacity";
    private RubberBandDrag? _rubberBandDrag;
    private BezierHandleDrag? _bezierHandleDrag;

    private sealed class BezierHandleDrag
    {
        public required ProjectDocument Project { get; init; }
        public required TimelineTrack Track { get; init; }
        public required TimelineClip Clip { get; init; }
        public required string Property { get; init; }
        public required ClipKeyframe Left { get; init; }
        public required ClipKeyframe Right { get; init; }
        public bool Outgoing { get; init; }
        public required Point Origin { get; init; }
        public required string UndoJson { get; init; }
        public double OriginalTime { get; init; }
        public double OriginalValue { get; init; }
        public double InnerWidth { get; init; }
        public double Lower { get; init; }
        public double Upper { get; init; }
        public bool BeganMotion { get; set; }
        public DateTime LastRedrawUtc { get; set; }
    }

    /// <summary>
    /// Holds one pointer gesture independently of the visual handle. Timeline
    /// redraws replace the handle on every drag frame, while the canvas keeps
    /// mouse capture and this state retains the original undo snapshot.
    /// </summary>
    private sealed class RubberBandDrag
    {
        public required ProjectDocument Project { get; init; }
        public required TimelineTrack Track { get; init; }
        public required TimelineClip Clip { get; init; }
        public required string Property { get; init; }
        public ClipKeyframe? Keyframe { get; init; }
        public ClipTimeRemapPoint? SpeedPoint { get; init; }
        public required Point Origin { get; init; }
        public required string UndoJson { get; init; }
        public double OriginalTime { get; init; }
        public double OriginalValue { get; init; }
        public double InnerWidth { get; init; }
        public double Lower { get; init; }
        public double Upper { get; init; }
        public bool BeganMotion { get; set; }
        public DateTime LastRedrawUtc { get; set; }
    }

    private void InitializeTimelineControls()
    {
        // Track targets are session state. Existing projects remain readable
        // and every sequence can have a different active destination.
        _targetSequenceId = _project.Sequence.Id;
    }

    private void EnsureTargetSequence()
    {
        if (_targetSequenceId == _project.Sequence.Id) return;
        _targetTrackIds.Clear();
        _targetSequenceId = _project.Sequence.Id;
    }

    private TimelineTrack? ResolveDropTarget(ClipKind kind, int trackIndex)
    {
        EnsureTargetSequence();
        TimelineTrack? dropped = trackIndex >= 0 && trackIndex < _project.Tracks.Count
            ? _project.Tracks[trackIndex] : null;
        if (dropped is not null && IsCompatible(dropped, kind) && !dropped.IsLocked) return dropped;
        TrackKind targetKind = kind == ClipKind.Audio ? TrackKind.Audio : TrackKind.Video;
        if (_targetTrackIds.TryGetValue(targetKind, out string? id))
        {
            TimelineTrack? targeted = _project.Tracks.FirstOrDefault(track => track.Id == id &&
                IsCompatible(track, kind) && !track.IsLocked);
            if (targeted is not null) return targeted;
        }
        return _project.Tracks.FirstOrDefault(track => IsCompatible(track, kind) && !track.IsLocked);
    }

    private TimelineTrack? ResolveCommandTarget(TimelineClip clip)
    {
        EnsureTargetSequence();
        TrackKind kind = clip.Kind == ClipKind.Audio ? TrackKind.Audio : TrackKind.Video;
        if (_targetTrackIds.TryGetValue(kind, out string? id))
        {
            TimelineTrack? targeted = _project.Tracks.FirstOrDefault(track => track.Id == id &&
                IsCompatible(track, clip) && !track.IsLocked);
            if (targeted is not null) return targeted;
        }
        if (_selectedTrack is not null && IsCompatible(_selectedTrack, clip) && !_selectedTrack.IsLocked)
            return _selectedTrack;
        return _project.Tracks.FirstOrDefault(track => IsCompatible(track, clip) && !track.IsLocked);
    }

    private void DrawTrackTarget(TimelineTrack track, double trackY)
    {
        EnsureTargetSequence();
        TrackKind targetKind = track.Kind == TrackKind.Audio ? TrackKind.Audio : TrackKind.Video;
        bool targeted = _targetTrackIds.TryGetValue(targetKind, out string? id) && id == track.Id;
        double width = HeaderSpace * .17;
        double height = TrackSpace * .32;
        var target = new Button
        {
            Content = track.Kind == TrackKind.Audio ? "A" : "V",
            Width = width, Height = height,
            Padding = new Thickness(0),
            Foreground = targeted ? Brushes.Black : Brushes.White,
            Background = targeted ? Brush("#FF9957") : Brush("#3C414C"),
            BorderThickness = new Thickness(0),
            ToolTip = targeted ? "当前轨道目标；点击取消" : "设为此类型片段的目标轨道"
        };
        target.Click += (_, _) =>
        {
            if (targeted) _targetTrackIds.Remove(targetKind);
            else _targetTrackIds[targetKind] = track.Id;
            DrawTimeline();
            StatusText.Text = targeted ? "已取消轨道目标" : $"目标轨道：{track.Name}";
        };
        Canvas.SetLeft(target, HeaderSpace - width - HeaderSpace * .035);
        Canvas.SetTop(target, trackY + TrackSpace * .055);
        TimelineCanvas.Children.Add(target);
        var snap = new Button
        {
            Content = track.IsSnapping ? "⌁" : "∅",
            Width = HeaderSpace * .17, Height = TrackSpace * .32,
            Padding = new Thickness(0), Foreground = track.IsSnapping ? Brushes.Black : Brushes.White,
            Background = track.IsSnapping ? Brush("#FF9957") : Brush("#3C414C"),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            ToolTip = track.IsSnapping ? "轨道吸附：已激活，点击关闭" : "轨道吸附：未激活，点击开启"
        };
        snap.Click += (_, _) =>
        {
            SaveUndoState(); track.IsSnapping = !track.IsSnapping;
            if (track.IsSnapping) NormalizeTrackGaps(track);
            MarkProjectDirty(); RefreshTimeline();
            StatusText.Text = track.IsSnapping ? $"已开启 {track.Name} 的吸附" : $"已关闭 {track.Name} 的吸附";
        };
        Canvas.SetLeft(snap, HeaderSpace * .54);
        Canvas.SetTop(snap, trackY + TrackSpace * .055);
        TimelineCanvas.Children.Add(snap);
    }

    private static void NormalizeTrackGaps(TimelineTrack track)
    {
        double cursor = 0;
        foreach (TimelineClip clip in track.Clips.OrderBy(item => item.StartSeconds).ToArray())
        {
            if (clip.StartSeconds > cursor + .0001) clip.StartSeconds = cursor;
            cursor = Math.Max(cursor, clip.StartSeconds + Math.Max(0, clip.DurationSeconds));
        }
    }

    private static double SnapNewClipStart(TimelineTrack track, double requested)
    {
        if (!track.IsSnapping) return Math.Max(0, requested);
        double time = Math.Max(0, requested);
        TimelineClip[] clips = track.Clips.OrderBy(item => item.StartSeconds).ToArray();
        TimelineClip? prior = clips.LastOrDefault(item => item.StartSeconds + item.DurationSeconds <= time + .0001);
        TimelineClip? next = clips.FirstOrDefault(item => item.StartSeconds >= time - .0001);
        if (prior is not null && next is not null && prior.StartSeconds + prior.DurationSeconds <= next.StartSeconds + .0001)
            return prior.StartSeconds + prior.DurationSeconds;
        return prior is not null && next is null ? prior.StartSeconds + prior.DurationSeconds : time;
    }

    private void AddRubberBandContextCommands(ContextMenu menu, TimelineClip clip)
    {
        var selector = new MenuItem { Header = "片段橡皮筋" };
        if (clip.Kind == ClipKind.Audio || clip.Kind == ClipKind.Video)
            AddBandChoice(selector, "音量", "Volume");
        else
            AddBandChoice(selector, "不透明度", "Opacity");
        if ((clip.Kind is ClipKind.Audio or ClipKind.Video) && clip.MulticamGroupId is null)
            AddBandChoice(selector, "播放速度", "Speed");
        menu.Items.Add(selector);
        AddContextCommand(menu, "在播放头添加橡皮筋关键点", () => AddRubberBandPointAtPlayhead(clip));
    }

    private void AddBandChoice(MenuItem selector, string label, string property)
    {
        var choice = new MenuItem { Header = label, IsCheckable = true,
            IsChecked = _rubberBandProperty == property };
        choice.Click += (_, _) => { _rubberBandProperty = property; DrawTimeline(); };
        selector.Items.Add(choice);
    }

    private void DrawClipRubberBand(TimelineTrack track, TimelineClip clip,
        double left, double top, double clipWidth)
    {
        string property = BandPropertyFor(clip);
        if (property.Length == 0 || clipWidth <= TrackSpace * .4) return;
        if (!double.IsFinite(clip.DurationSeconds) || clip.DurationSeconds <= 0) return;
        double bodyHeight = TrackSpace - 13;
        double lower = top + bodyHeight * .84;
        double upper = top + bodyHeight * .47;
        double innerLeft = left + TrackSpace * .12;
        double innerWidth = Math.Max(1, clipWidth - TrackSpace * .24);
        if (property == "Speed")
        {
            DrawSpeedRubberBand(track, clip, innerLeft, innerWidth, lower, upper);
            return;
        }
        double ValueY(double value) => property switch
        {
            "Speed" => lower - Math.Clamp(Math.Log2(Math.Max(.05, value) / .05) / Math.Log2(32 / .05),
                0, 1) * (lower - upper),
            // Keep the editor's 1.0 default on the visible 100% guide. Values
            // above 1 remain available for intentional gain adjustments.
            "Volume" => lower - Math.Clamp(value / 2, 0, 1) * (lower - upper),
            _ => lower - Math.Clamp(value, 0, 1) * (lower - upper)
        };
        double DurationX(double local) => innerLeft +
            Math.Clamp(local / Math.Max(clip.DurationSeconds, .000001), 0, 1) * innerWidth;
        var points = clip.Keyframes.Where(key => key.Property == property)
            .Select(key => (Time: key.TimeSeconds, Value: key.Value)).ToList();
        points.Add((Time: 0, Value: BandValueAt(clip, property, 0)));
        points.Add((Time: clip.DurationSeconds, Value: BandValueAt(clip, property, clip.DurationSeconds)));
        points = points.OrderBy(point => point.Item1).ToList();
        for (int index = 0; index < points.Count - 1; index++)
        {
            double startTime = points[index].Time;
            double endTime = points[index + 1].Time;
            if (endTime - startTime <= .000001) continue;
            ClipKeyframe? leftKey = clip.Keyframes.FirstOrDefault(key =>
                key.Property == property && Math.Abs(key.TimeSeconds - startTime) < .000001);
            int samples = leftKey?.Interpolation is "Bezier" or "EaseInOut" ? 16 : 1;
            double priorTime = startTime;
            double priorValue = points[index].Value;
            for (int sample = 1; sample <= samples; sample++)
            {
                double nextTime = startTime + (endTime - startTime) * sample / samples;
                double nextValue = leftKey?.Interpolation == "Hold" && sample == samples
                    ? priorValue : BandValueAt(clip, property, nextTime);
                DrawSegment(priorTime, priorValue, nextTime, nextValue);
                priorTime = nextTime;
                priorValue = nextValue;
            }
            if (leftKey?.Interpolation == "Hold")
                DrawSegment(endTime, priorValue, endTime, points[index + 1].Value);
        }
        void DrawSegment(double fromTime, double fromValue, double toTime, double toValue)
        {
            var line = new Line
            {
                X1 = DurationX(fromTime), Y1 = ValueY(fromValue),
                X2 = DurationX(toTime), Y2 = ValueY(toValue),
                Stroke = property == "Volume" ? Brushes.White : Brush("#FFE0B5"),
                StrokeThickness = TrackSpace * .027,
                IsHitTestVisible = false
            };
            TimelineCanvas.Children.Add(line);
            // One interactive segment per sampled chord would create many
            // transparent hit targets and rebuild them on every drag frame.
            // The broad clip body still offers playhead insertion; expose a
            // curve hit target only on its un-sampled straight spans.
            if (toTime - fromTime >= clip.DurationSeconds * .03)
                AddBandHitLine(track, clip, property, line, innerLeft, innerWidth);
        }
        foreach (ClipKeyframe key in clip.Keyframes.Where(key => key.Property == property &&
            key.TimeSeconds >= 0 && key.TimeSeconds < clip.DurationSeconds))
        {
            double time = key.TimeSeconds;
            double value = key.Value;
            double pointSize = TrackSpace * .11;
            var handle = new Ellipse
            {
                Width = pointSize, Height = pointSize,
                Fill = Brush("#FF9957"), Stroke = Brushes.White,
                StrokeThickness = TrackSpace * .018,
                Cursor = Cursors.SizeAll,
                ToolTip = $"拖动：{property} · {time:0.###} 秒 · {value:0.###}；单击编辑数值"
            };
            Canvas.SetLeft(handle, DurationX(time) - pointSize / 2);
            Canvas.SetTop(handle, ValueY(value) - pointSize / 2);
            TimelineCanvas.Children.Add(handle);
            handle.PreviewMouseLeftButtonDown += (_, args) =>
                BeginRubberBandDrag(args, track, clip, property, key, null, innerWidth, lower, upper);
        }
        DrawBezierHandles(track, clip, property, innerWidth, lower, upper,
            DurationX, ValueY);
    }

    /// <summary>
    /// Control handles appear only on Bezier spans. Dragging each handle
    /// changes its normalized time and value coordinates directly in the
    /// timeline while the numeric keyframe panel remains available for exact
    /// entry. A gesture writes one undo state on release.
    /// </summary>
    private void DrawBezierHandles(TimelineTrack track, TimelineClip clip, string property,
        double innerWidth, double lower, double upper,
        Func<double, double> x, Func<double, double> y)
    {
        ClipKeyframe[] keys = clip.Keyframes.Where(key => key.Property == property &&
            key.TimeSeconds >= 0 && key.TimeSeconds <= clip.DurationSeconds)
            .OrderBy(key => key.TimeSeconds).ToArray();
        for (int index = 0; index < keys.Length - 1; index++)
        {
            ClipKeyframe left = keys[index];
            ClipKeyframe right = keys[index + 1];
            if (left.Interpolation != "Bezier") continue;
            double span = right.TimeSeconds - left.TimeSeconds;
            double delta = right.Value - left.Value;
            double scale = Math.Abs(delta) < 1e-9 ? 1 : delta;
            if (span <= 0) continue;
            AddBezierHandle(left, right, outgoing: true,
                x(left.TimeSeconds + span * left.BezierOutTime),
                y(left.Value + scale * left.BezierOutValue), x(left.TimeSeconds), y(left.Value));
            AddBezierHandle(left, right, outgoing: false,
                x(left.TimeSeconds + span * right.BezierInTime),
                y(right.Value + scale * (right.BezierInValue - 1)),
                x(right.TimeSeconds), y(right.Value));
        }

        void AddBezierHandle(ClipKeyframe left, ClipKeyframe right, bool outgoing,
            double handleX, double handleY, double anchorX, double anchorY)
        {
            var link = new Line
            {
                X1 = anchorX, Y1 = anchorY, X2 = handleX, Y2 = handleY,
                Stroke = Brush("#85E5D8"), StrokeThickness = TrackSpace * .012,
                IsHitTestVisible = false
            };
            TimelineCanvas.Children.Add(link);
            double size = TrackSpace * .075;
            var handle = new Ellipse
            {
                Width = size, Height = size,
                Fill = Brush("#85E5D8"), Stroke = Brushes.Black,
                StrokeThickness = TrackSpace * .01,
                Cursor = Cursors.SizeAll,
                ToolTip = outgoing ? "拖动贝塞尔出控制柄" : "拖动贝塞尔入控制柄"
            };
            Canvas.SetLeft(handle, handleX - size / 2);
            Canvas.SetTop(handle, handleY - size / 2);
            TimelineCanvas.Children.Add(handle);
            handle.PreviewMouseLeftButtonDown += (_, args) =>
                BeginBezierHandleDrag(args, track, clip, property, left, right,
                    outgoing, innerWidth, lower, upper);
        }
    }

    private string BandPropertyFor(TimelineClip clip)
    {
        if (_rubberBandProperty == "Speed" && clip.MulticamGroupId is null &&
            clip.Kind is (ClipKind.Video or ClipKind.Audio))
            return "Speed";
        if (_rubberBandProperty == "Volume" && clip.Kind is ClipKind.Audio or ClipKind.Video)
            return "Volume";
        return clip.Kind == ClipKind.Audio ? "Volume" : "Opacity";
    }

    private void DrawClipKeyframeMarkers(TimelineTrack track, TimelineClip clip,
        double left, double top, double clipWidth)
    {
        if (clip.Keyframes.Count == 0 || clip.DurationSeconds <= 0 || clipWidth <= 0) return;
        string[] baseProperties = clip.Kind == ClipKind.Audio
            ? ["Volume"]
            : ["PositionX", "PositionY", "ScaleX", "ScaleY", "RotationDegrees", "Opacity"];
        string[] properties = baseProperties.Concat(clip.Keyframes.Select(key => key.Property)
            .Where(property => !baseProperties.Contains(property))
            .Distinct(StringComparer.Ordinal)).ToArray();
        ClipKeyframe[] keys = clip.Keyframes.Where(key => properties.Contains(key.Property) &&
            key.TimeSeconds >= 0 && key.TimeSeconds <= clip.DurationSeconds &&
            double.IsFinite(key.TimeSeconds)).ToArray();
        if (keys.Length == 0) return;
        double innerLeft = left + TrackSpace * .12;
        double innerWidth = Math.Max(1, clipWidth - TrackSpace * .24);
        double laneHeight = Math.Max(3, (TrackSpace - 17) / Math.Max(1, properties.Length));
        for (int index = 0; index < keys.Length; index++)
        {
            ClipKeyframe key = keys[index];
            int lane = Math.Max(0, Array.IndexOf(properties, key.Property));
            double x = innerLeft + Math.Clamp(key.TimeSeconds / clip.DurationSeconds, 0, 1) * innerWidth;
            double y = top + 5 + lane * laneHeight + laneHeight / 2;
            double size = Math.Clamp(TrackSpace * .10, 5, 9);
            var marker = new Polygon
            {
                Points = new PointCollection { new(-size, 0), new(0, -size), new(size, 0), new(0, size) },
                Fill = ReferenceEquals(key, _selectedKeyframe) ? Brushes.White : Brush("#FFD166"),
                Stroke = Brush("#16181D"), StrokeThickness = 1,
                Cursor = Cursors.Hand,
                ToolTip = $"关键帧 · {KeyframePropertyName(key.Property)} · {key.TimeSeconds:0.###} 秒 · {key.Value:0.###}\n左键选中，双击编辑，Delete 删除"
            };
            Canvas.SetLeft(marker, x); Canvas.SetTop(marker, y);
            marker.MouseLeftButtonDown += (_, args) =>
            {
                _selectedTrack = track; _selectedClip = clip; _selectedKeyframe = key;
                _selectedClips.Clear(); _selectedClips.Add(clip);
                if (args.ClickCount >= 2) EditSelectedKeyframes();
                else { DrawTimeline(); StatusText.Text = $"已选中关键帧：{KeyframePropertyName(key.Property)}"; }
                args.Handled = true;
            };
            var menu = new ContextMenu { Background = Brush("#24262C"), Foreground = Brushes.White };
            AddContextCommand(menu, "编辑关键帧…", () =>
            {
                _selectedTrack = track; _selectedClip = clip; _selectedKeyframe = key;
                EditSelectedKeyframes();
            });
            AddContextCommand(menu, "删除关键帧", () => RemoveSelectedKeyframe(track, clip, key));
            marker.ContextMenu = menu;
            TimelineCanvas.Children.Add(marker);
        }
    }

    private static string KeyframePropertyName(string property) => property switch
    {
        "PositionX" => "位置 X", "PositionY" => "位置 Y", "ScaleX" => "水平缩放",
        "ScaleY" => "垂直缩放", "RotationDegrees" => "旋转角度", "Opacity" => "不透明度",
        "Volume" => "音量", _ => property
    };

    private void RemoveSelectedKeyframe(TimelineTrack track, TimelineClip clip, ClipKeyframe key)
    {
        if (track.IsLocked || !_project.Tracks.Contains(track) || !track.Clips.Contains(clip) ||
            !clip.Keyframes.Contains(key)) return;
        SaveUndoState();
        clip.Keyframes.Remove(key);
        _selectedKeyframe = null;
        MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = "已删除关键帧";
    }

    private void DrawSpeedRubberBand(TimelineTrack track, TimelineClip clip,
        double innerLeft, double innerWidth, double lower, double upper)
    {
        double X(double local) => innerLeft + local / clip.DurationSeconds * innerWidth;
        double Y(double speed) => lower - Math.Clamp(Math.Log2(Math.Max(.05, speed) / .05) /
            Math.Log2(32 / .05), 0, 1) * (lower - upper);
        ClipTimeRemapPoint[] knots = clip.TimeRemapPoints.Where(point =>
            point.TimeSeconds > 0 && point.TimeSeconds < clip.DurationSeconds)
            .OrderBy(point => point.TimeSeconds).ToArray();
        var boundaries = new List<double> { 0 };
        boundaries.AddRange(knots.Select(point => point.TimeSeconds));
        boundaries.Add(clip.DurationSeconds);
        for (int index = 0; index < boundaries.Count - 1; index++)
        {
            double start = boundaries[index];
            double end = boundaries[index + 1];
            double segmentSpeed = ClipTimeMap.SpeedAt(clip, start);
            var line = new Line
            {
                X1 = X(start), Y1 = Y(segmentSpeed),
                X2 = X(end), Y2 = Y(segmentSpeed),
                Stroke = Brush("#FFE0B5"), StrokeThickness = TrackSpace * .027,
                IsHitTestVisible = false
            };
            TimelineCanvas.Children.Add(line);
            AddBandHitLine(track, clip, "Speed", line, innerLeft, innerWidth);
            // Speed changes immediately at a knot. Draw the vertical jump so
            // the visible band matches the segment mapping used by preview
            // and export, instead of appearing to continue at the old rate.
            if (index < knots.Length &&
                Math.Abs(segmentSpeed - knots[index].Speed) > .000001)
            {
                var jump = new Line
                {
                    X1 = X(end), X2 = X(end),
                    Y1 = Y(segmentSpeed), Y2 = Y(knots[index].Speed),
                    Stroke = Brush("#FFE0B5"), StrokeThickness = TrackSpace * .027,
                    IsHitTestVisible = false
                };
                TimelineCanvas.Children.Add(jump);
            }
        }
        foreach (ClipTimeRemapPoint point in clip.TimeRemapPoints.Where(point =>
            point.TimeSeconds >= 0 && point.TimeSeconds < clip.DurationSeconds))
        {
            double size = TrackSpace * .11;
            var handle = new Ellipse
            {
                Width = size, Height = size, Fill = Brush("#FF9957"),
                Stroke = Brushes.White, StrokeThickness = TrackSpace * .018,
                Cursor = Cursors.SizeAll,
                ToolTip = $"拖动：速度 · {point.TimeSeconds:0.###} 秒 · {point.Speed:0.###} 倍；单击编辑数值"
            };
            Canvas.SetLeft(handle, X(point.TimeSeconds) - size / 2);
            Canvas.SetTop(handle, Y(point.Speed) - size / 2);
            TimelineCanvas.Children.Add(handle);
            handle.PreviewMouseLeftButtonDown += (_, args) =>
                BeginRubberBandDrag(args, track, clip, "Speed", null, point, innerWidth, lower, upper);
        }
    }

    private static double BandValueAt(TimelineClip clip, string property, double local) =>
        property == "Speed" ? ClipTimeMap.SpeedAt(clip, local) :
        ClipAnimation.Evaluate(clip, property, clip.StartSeconds + local);

    /// <summary>
    /// A wide transparent segment supports direct double-click insertion at
    /// the current band position. It is added before handles so an existing
    /// keyframe always wins pointer hit testing.
    /// </summary>
    private void AddBandHitLine(TimelineTrack track, TimelineClip clip, string property,
        Line segment, double innerLeft, double innerWidth)
    {
        var hit = new Line
        {
            X1 = segment.X1, X2 = segment.X2, Y1 = segment.Y1, Y2 = segment.Y2,
            Stroke = Brushes.Transparent, StrokeThickness = TrackSpace * .17,
            Cursor = Cursors.Cross, ToolTip = "双击添加橡皮筋关键点"
        };
        hit.MouseLeftButtonDown += (_, args) =>
        {
            args.Handled = true;
            _selectedTrack = track;
            _selectedClip = clip;
            _selectedClips.Clear();
            _selectedClips.Add(clip);
            if (args.ClickCount < 2) return;
            double local = Math.Clamp((args.GetPosition(TimelineCanvas).X - innerLeft) /
                innerWidth * clip.DurationSeconds, 0, clip.DurationSeconds);
            AddRubberBandPoint(track, clip, property, local);
        };
        TimelineCanvas.Children.Add(hit);
    }

    private void BeginRubberBandDrag(MouseButtonEventArgs args, TimelineTrack track,
        TimelineClip clip, string property, ClipKeyframe? keyframe,
        ClipTimeRemapPoint? speedPoint, double innerWidth, double lower, double upper)
    {
        args.Handled = true;
        if (track.IsLocked)
        {
            StatusText.Text = "轨道已锁定 · EDIT-RUBBER-LOCKED";
            return;
        }
        if ((keyframe is null && speedPoint is null) || !double.IsFinite(innerWidth) ||
            innerWidth <= 0)
        {
            StatusText.Text = "橡皮筋节点不可拖动 · EDIT-RUBBER-STATE";
            return;
        }
        if (property == "Speed" && clip.MulticamGroupId is not null)
        {
            StatusText.Text = "多机位切段需保持原速 · MULTICAM-EDIT";
            return;
        }
        if (!_project.Tracks.Contains(track) || !track.Clips.Contains(clip) ||
            (keyframe is not null && !clip.Keyframes.Contains(keyframe)) ||
            (speedPoint is not null && !clip.TimeRemapPoints.Contains(speedPoint)))
        {
            StatusText.Text = "橡皮筋节点已变化 · EDIT-RUBBER-STATE";
            return;
        }
        if (_movingClip || _trimmingStart || _trimmingEnd || _scrubbingPlayhead) return;
        _selectedTrack = track;
        _selectedClip = clip;
        _selectedClips.Clear();
        _selectedClips.Add(clip);
        _rubberBandDrag = new RubberBandDrag
        {
            Project = _project, Track = track, Clip = clip, Property = property,
            Keyframe = keyframe, SpeedPoint = speedPoint,
            Origin = args.GetPosition(TimelineCanvas),
            UndoJson = JsonSerializer.Serialize(_project),
            OriginalTime = speedPoint?.TimeSeconds ?? keyframe?.TimeSeconds ?? 0,
            OriginalValue = speedPoint?.Speed ?? keyframe?.Value ?? 0,
            InnerWidth = innerWidth, Lower = lower, Upper = upper
        };
        TimelineCanvas.CaptureMouse();
    }

    private void BeginBezierHandleDrag(MouseButtonEventArgs args, TimelineTrack track,
        TimelineClip clip, string property, ClipKeyframe left, ClipKeyframe right,
        bool outgoing, double innerWidth, double lower, double upper)
    {
        args.Handled = true;
        if (track.IsLocked)
        {
            StatusText.Text = "轨道已锁定 · VIS-KEYFRAME-BEZIER";
            return;
        }
        if (!double.IsFinite(innerWidth) || innerWidth <= 0 ||
            !_project.Tracks.Contains(track) ||
            !track.Clips.Contains(clip) || !clip.Keyframes.Contains(left) ||
            !clip.Keyframes.Contains(right))
        {
            StatusText.Text = "贝塞尔节点已变化 · VIS-KEYFRAME-BEZIER";
            return;
        }
        if (_movingClip || _trimmingStart || _trimmingEnd || _scrubbingPlayhead) return;
        _selectedTrack = track;
        _selectedClip = clip;
        _selectedClips.Clear();
        _selectedClips.Add(clip);
        _bezierHandleDrag = new BezierHandleDrag
        {
            Project = _project, Track = track, Clip = clip, Property = property,
            Left = left, Right = right, Outgoing = outgoing,
            Origin = args.GetPosition(TimelineCanvas),
            UndoJson = JsonSerializer.Serialize(_project),
            OriginalTime = outgoing ? left.BezierOutTime : right.BezierInTime,
            OriginalValue = outgoing ? left.BezierOutValue : right.BezierInValue,
            InnerWidth = innerWidth, Lower = lower, Upper = upper
        };
        TimelineCanvas.CaptureMouse();
    }

    private void MoveBezierHandleDrag(MouseEventArgs args)
    {
        BezierHandleDrag? drag = _bezierHandleDrag;
        if (drag is null) return;
        args.Handled = true;
        if (args.LeftButton != MouseButtonState.Pressed)
        {
            FinishBezierHandleDrag();
            return;
        }
        Point pointer = args.GetPosition(TimelineCanvas);
        double dx = pointer.X - drag.Origin.X;
        double dy = pointer.Y - drag.Origin.Y;
        if (!drag.BeganMotion)
        {
            if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance) return;
            drag.BeganMotion = true;
        }
        if (!ReferenceEquals(drag.Project, _project) || drag.Track.IsLocked ||
            !_project.Tracks.Contains(drag.Track) ||
            !drag.Track.Clips.Contains(drag.Clip) ||
            !drag.Clip.Keyframes.Contains(drag.Left) ||
            !drag.Clip.Keyframes.Contains(drag.Right))
        {
            if (drag.Outgoing)
            {
                drag.Left.BezierOutTime = drag.OriginalTime;
                drag.Left.BezierOutValue = drag.OriginalValue;
            }
            else
            {
                drag.Right.BezierInTime = drag.OriginalTime;
                drag.Right.BezierInValue = drag.OriginalValue;
            }
            _bezierHandleDrag = null;
            if (TimelineCanvas.IsMouseCaptured) TimelineCanvas.ReleaseMouseCapture();
            return;
        }
        double span = drag.Right.TimeSeconds - drag.Left.TimeSeconds;
        double delta = drag.Right.Value - drag.Left.Value;
        if (span <= 0) return;
        double scale = Math.Abs(delta) < 1e-9 ? 1 : delta;
        double time = Math.Clamp(drag.OriginalTime + dx / drag.InnerWidth *
            drag.Clip.DurationSeconds / span, 0, 1);
        if (drag.Outgoing) time = Math.Min(time, drag.Right.BezierInTime);
        else time = Math.Max(time, drag.Left.BezierOutTime);
        double maxValue = drag.Property == "Volume" ? 4 : 1;
        double currentValue = drag.Outgoing
            ? drag.Left.Value + scale * drag.OriginalValue
            : drag.Right.Value + scale * (drag.OriginalValue - 1);
        double physical = Math.Clamp(currentValue - dy /
            Math.Max(1, drag.Lower - drag.Upper) * maxValue, 0, maxValue);
        double value = Math.Clamp(drag.Outgoing
            ? (physical - drag.Left.Value) / scale
            : 1 + (physical - drag.Right.Value) / scale, -4, 4);
        if (drag.Outgoing)
        {
            drag.Left.BezierOutTime = time;
            drag.Left.BezierOutValue = value;
        }
        else
        {
            drag.Right.BezierInTime = time;
            drag.Right.BezierInValue = value;
        }
        DateTime now = DateTime.UtcNow;
        if (now - drag.LastRedrawUtc >= TimeSpan.FromMilliseconds(33))
        {
            drag.LastRedrawUtc = now;
            DrawTimeline();
        }
        StatusText.Text = $"贝塞尔控制柄 · 时间 {time:0.###} · 值 {value:0.###}";
    }

    private void FinishBezierHandleDrag()
    {
        BezierHandleDrag? drag = _bezierHandleDrag;
        if (drag is null) return;
        _bezierHandleDrag = null;
        if (TimelineCanvas.IsMouseCaptured) TimelineCanvas.ReleaseMouseCapture();
        if (!ReferenceEquals(drag.Project, _project) ||
            !_project.Tracks.Contains(drag.Track) || drag.Track.IsLocked ||
            !drag.Track.Clips.Contains(drag.Clip) ||
            !drag.Clip.Keyframes.Contains(drag.Left) ||
            !drag.Clip.Keyframes.Contains(drag.Right))
        {
            if (drag.Outgoing)
            {
                drag.Left.BezierOutTime = drag.OriginalTime;
                drag.Left.BezierOutValue = drag.OriginalValue;
            }
            else
            {
                drag.Right.BezierInTime = drag.OriginalTime;
                drag.Right.BezierInValue = drag.OriginalValue;
            }
            return;
        }
        double time = drag.Outgoing ? drag.Left.BezierOutTime : drag.Right.BezierInTime;
        double value = drag.Outgoing ? drag.Left.BezierOutValue : drag.Right.BezierInValue;
        if (Math.Abs(time - drag.OriginalTime) > 1e-6 ||
            Math.Abs(value - drag.OriginalValue) > 1e-6)
        {
            _undoStates.Push(drag.UndoJson);
            TrimHistoryCapacity();
            _redoStates.Clear();
            MarkProjectDirty();
            DrawTimeline();
            SeekPreview(_project.Sequence.PlayheadSeconds);
        }
        else
        {
            if (drag.Outgoing)
            {
                drag.Left.BezierOutTime = drag.OriginalTime;
                drag.Left.BezierOutValue = drag.OriginalValue;
            }
            else
            {
                drag.Right.BezierInTime = drag.OriginalTime;
                drag.Right.BezierInValue = drag.OriginalValue;
            }
            DrawTimeline();
        }
    }

    private void MoveRubberBandDrag(MouseEventArgs args)
    {
        RubberBandDrag? drag = _rubberBandDrag;
        if (drag is null) return;
        args.Handled = true;
        if (args.LeftButton != MouseButtonState.Pressed)
        {
            FinishRubberBandDrag();
            return;
        }
        Point pointer = args.GetPosition(TimelineCanvas);
        double dx = pointer.X - drag.Origin.X;
        double dy = pointer.Y - drag.Origin.Y;
        if (!drag.BeganMotion)
        {
            if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance) return;
            drag.BeganMotion = true;
        }
        TimelineClip clip = drag.Clip;
        if (!ReferenceEquals(drag.Project, _project) || drag.Track.IsLocked ||
            !_project.Tracks.Contains(drag.Track) || !drag.Track.Clips.Contains(clip))
        {
            if (drag.SpeedPoint is not null)
            {
                drag.SpeedPoint.TimeSeconds = drag.OriginalTime;
                drag.SpeedPoint.Speed = drag.OriginalValue;
            }
            if (drag.Keyframe is not null)
            {
                drag.Keyframe.TimeSeconds = drag.OriginalTime;
                drag.Keyframe.Value = drag.OriginalValue;
            }
            _rubberBandDrag = null;
            if (TimelineCanvas.IsMouseCaptured) TimelineCanvas.ReleaseMouseCapture();
            return;
        }
        if ((drag.Keyframe is not null && !clip.Keyframes.Contains(drag.Keyframe)) ||
            (drag.SpeedPoint is not null && !clip.TimeRemapPoints.Contains(drag.SpeedPoint)))
        {
            if (drag.SpeedPoint is not null)
            {
                drag.SpeedPoint.TimeSeconds = drag.OriginalTime;
                drag.SpeedPoint.Speed = drag.OriginalValue;
            }
            if (drag.Keyframe is not null)
            {
                drag.Keyframe.TimeSeconds = drag.OriginalTime;
                drag.Keyframe.Value = drag.OriginalValue;
            }
            _rubberBandDrag = null;
            if (TimelineCanvas.IsMouseCaptured) TimelineCanvas.ReleaseMouseCapture();
            StatusText.Text = "橡皮筋节点已变化 · EDIT-RUBBER-STATE";
            return;
        }
        double framesPerSecond = double.IsFinite(_project.Sequence.FramesPerSecond) &&
            _project.Sequence.FramesPerSecond > 0 ? _project.Sequence.FramesPerSecond : 30;
        double frame = 1 / framesPerSecond;
        double proposedTime = Math.Round((drag.OriginalTime + dx / drag.InnerWidth *
            clip.DurationSeconds) * framesPerSecond) / framesPerSecond;
        double latest = Math.Max(0, clip.DurationSeconds - frame);
        // A speed knot at zero defines the first segment's rate. Keeping its
        // time pinned avoids silently replacing that rate with clip.Speed.
        if (drag.SpeedPoint is not null && drag.OriginalTime <= .0001)
            proposedTime = 0;
        else
        {
            double prior = drag.Property == "Speed"
                ? clip.TimeRemapPoints.Where(point => !ReferenceEquals(point, drag.SpeedPoint) &&
                    point.TimeSeconds < drag.OriginalTime).Select(point => point.TimeSeconds)
                    .DefaultIfEmpty(-frame).Max()
                : clip.Keyframes.Where(key => !ReferenceEquals(key, drag.Keyframe) &&
                    key.Property == drag.Property && key.TimeSeconds < drag.OriginalTime)
                    .Select(key => key.TimeSeconds).DefaultIfEmpty(-frame).Max();
            double next = drag.Property == "Speed"
                ? clip.TimeRemapPoints.Where(point => !ReferenceEquals(point, drag.SpeedPoint) &&
                    point.TimeSeconds > drag.OriginalTime).Select(point => point.TimeSeconds)
                    .DefaultIfEmpty(clip.DurationSeconds).Min()
                : clip.Keyframes.Where(key => !ReferenceEquals(key, drag.Keyframe) &&
                    key.Property == drag.Property && key.TimeSeconds > drag.OriginalTime)
                    .Select(key => key.TimeSeconds).DefaultIfEmpty(clip.DurationSeconds).Min();
            double minimum = Math.Max(0, prior + frame);
            double maximum = Math.Min(latest, next - frame);
            proposedTime = minimum > maximum ? drag.OriginalTime :
                Math.Clamp(proposedTime, minimum, maximum);
        }
        if (drag.Property == "Speed" && proposedTime > 0 &&
            proposedTime < clip.DurationSeconds)
            proposedTime = Math.Round(proposedTime, 6);
        double yRange = Math.Max(1, drag.Lower - drag.Upper);
        double originalFraction = drag.Property == "Speed"
            ? Math.Log2(Math.Max(.05, drag.OriginalValue) / .05) / Math.Log2(32 / .05)
            : drag.OriginalValue / (drag.Property == "Volume" ? 4 : 1);
        double fraction = Math.Clamp(originalFraction - dy / yRange, 0, 1);
        double proposedValue = drag.Property == "Speed"
            ? .05 * Math.Pow(32 / .05, fraction)
            : fraction * (drag.Property == "Volume" ? 4 : 1);
        if (drag.SpeedPoint is not null)
        {
            if (proposedTime >= clip.DurationSeconds) proposedTime = drag.SpeedPoint.TimeSeconds;
            if (clip.TimeRemapPoints.Any(point => !ReferenceEquals(point, drag.SpeedPoint) &&
                Math.Abs(point.TimeSeconds - proposedTime) < .000001))
                proposedTime = drag.SpeedPoint.TimeSeconds;
            double oldTime = drag.SpeedPoint.TimeSeconds;
            double oldSpeed = drag.SpeedPoint.Speed;
            drag.SpeedPoint.TimeSeconds = proposedTime;
            drag.SpeedPoint.Speed = proposedValue;
            MediaAsset? media = _project.Media.FirstOrDefault(asset => asset.Id == clip.MediaAssetId);
            double sourceSpan = ClipTimeMap.SourceOffset(clip, clip.DurationSeconds);
            if (media?.DurationSeconds is double duration &&
                clip.SourceInSeconds + sourceSpan > duration + .001)
            {
                drag.SpeedPoint.TimeSeconds = oldTime;
                drag.SpeedPoint.Speed = oldSpeed;
                StatusText.Text = "速度节点超出源素材 · EDIT-RUBBER-SOURCE";
                return;
            }
            if (!double.IsFinite(sourceSpan))
            {
                drag.SpeedPoint.TimeSeconds = oldTime;
                drag.SpeedPoint.Speed = oldSpeed;
                StatusText.Text = "速度节点时间映射无效 · EDIT-RUBBER-SOURCE";
                return;
            }
        }
        else if (drag.Keyframe is not null)
        {
            drag.Keyframe.TimeSeconds = proposedTime;
            drag.Keyframe.Value = proposedValue;
        }
        // Redraw at an interactive cadence; the final mouse-up always paints
        // the exact frame and value. This prevents canvas rebuilds for every
        // high-frequency pointer event on a large sequence.
        DateTime now = DateTime.UtcNow;
        if (now - drag.LastRedrawUtc >= TimeSpan.FromMilliseconds(33))
        {
            drag.LastRedrawUtc = now;
            DrawTimeline();
        }
        StatusText.Text = $"{drag.Property} · {proposedTime:0.###} 秒 · {proposedValue:0.###}";
    }

    private void FinishRubberBandDrag()
    {
        RubberBandDrag? drag = _rubberBandDrag;
        if (drag is null) return;
        _rubberBandDrag = null;
        // Releasing capture raises LostMouseCapture synchronously. Clear the
        // gesture first so that handler cannot commit the same edit twice.
        if (TimelineCanvas.IsMouseCaptured) TimelineCanvas.ReleaseMouseCapture();
        if (!ReferenceEquals(drag.Project, _project) ||
            !_project.Tracks.Contains(drag.Track) || drag.Track.IsLocked ||
            !drag.Track.Clips.Contains(drag.Clip) ||
            (drag.SpeedPoint is not null &&
                !drag.Clip.TimeRemapPoints.Contains(drag.SpeedPoint)) ||
            (drag.Keyframe is not null &&
                !drag.Clip.Keyframes.Contains(drag.Keyframe)))
        {
            if (drag.SpeedPoint is not null)
            {
                drag.SpeedPoint.TimeSeconds = drag.OriginalTime;
                drag.SpeedPoint.Speed = drag.OriginalValue;
            }
            if (drag.Keyframe is not null)
            {
                drag.Keyframe.TimeSeconds = drag.OriginalTime;
                drag.Keyframe.Value = drag.OriginalValue;
            }
            return;
        }
        double finalTime = drag.SpeedPoint?.TimeSeconds ?? drag.Keyframe?.TimeSeconds ?? drag.OriginalTime;
        double finalValue = drag.SpeedPoint?.Speed ?? drag.Keyframe?.Value ?? drag.OriginalValue;
        bool changed = drag.BeganMotion &&
            (Math.Abs(finalTime - drag.OriginalTime) > .0001 ||
             Math.Abs(finalValue - drag.OriginalValue) > .0001);
        if (changed)
        {
            _undoStates.Push(drag.UndoJson);
            TrimHistoryCapacity();
            _redoStates.Clear();
            MarkProjectDirty();
            DrawTimeline();
            SeekPreview(_project.Sequence.PlayheadSeconds);
            StatusText.Text = "已更新橡皮筋关键点";
        }
        else if (!drag.BeganMotion)
            EditRubberBandPoint(drag.Track, drag.Clip, drag.Property, drag.OriginalTime);
        else
        {
            if (drag.SpeedPoint is not null)
            {
                drag.SpeedPoint.TimeSeconds = drag.OriginalTime;
                drag.SpeedPoint.Speed = drag.OriginalValue;
            }
            if (drag.Keyframe is not null)
            {
                drag.Keyframe.TimeSeconds = drag.OriginalTime;
                drag.Keyframe.Value = drag.OriginalValue;
            }
            DrawTimeline();
        }
    }

    private void AddRubberBandPointAtPlayhead(TimelineClip clip)
    {
        TimelineTrack? track = _project.Tracks.FirstOrDefault(candidate => candidate.Clips.Contains(clip));
        if (track is null) return;
        double local = Math.Clamp(_project.Sequence.PlayheadSeconds - clip.StartSeconds, 0,
            clip.DurationSeconds);
        AddRubberBandPoint(track, clip, BandPropertyFor(clip), local);
    }

    private void AddRubberBandPoint(TimelineTrack track, TimelineClip clip, string property, double local)
    {
        if (property == "Speed" && clip.Kind is not (ClipKind.Video or ClipKind.Audio))
            property = clip.Kind == ClipKind.Audio ? "Volume" : "Opacity";
        if (!_project.Tracks.Contains(track) || !track.Clips.Contains(clip) ||
            track.IsLocked || !double.IsFinite(local) ||
            local >= clip.DurationSeconds || local < 0 ||
            !double.IsFinite(_project.Sequence.FramesPerSecond) || _project.Sequence.FramesPerSecond <= 0)
        { StatusText.Text = "无法添加关键点 · EDIT-RUBBER-RANGE"; return; }
        double snapped = Math.Clamp(Math.Round(local * _project.Sequence.FramesPerSecond) /
            Math.Max(1, _project.Sequence.FramesPerSecond), 0, clip.DurationSeconds);
        if (snapped >= clip.DurationSeconds)
        { StatusText.Text = "关键点必须位于片段内部 · EDIT-RUBBER-RANGE"; return; }
        if (property == "Speed")
        {
            if (clip.MulticamGroupId is not null)
            { StatusText.Text = "多机位切段需保持原速 · MULTICAM-EDIT"; return; }
            if (clip.TimeRemapPoints.Any(point => Math.Abs(point.TimeSeconds - snapped) < .0001))
            { StatusText.Text = "此时间已有速度节点 · EDIT-RUBBER-DUPLICATE"; return; }
            var point = new ClipTimeRemapPoint
            { TimeSeconds = snapped, Speed = ClipTimeMap.SpeedAt(clip, snapped) };
            clip.TimeRemapPoints.Add(point);
            MediaAsset? media = _project.Media.FirstOrDefault(asset => asset.Id == clip.MediaAssetId);
            double sourceSpan = ClipTimeMap.SourceOffset(clip, clip.DurationSeconds);
            if (!double.IsFinite(sourceSpan) ||
                (media?.DurationSeconds is double sourceDuration &&
                    sourceSpan > sourceDuration - clip.SourceInSeconds + .001))
            {
                clip.TimeRemapPoints.Remove(point);
                StatusText.Text = "速度节点超出源素材 · EDIT-RUBBER-SOURCE";
                return;
            }
            clip.TimeRemapPoints.Remove(point);
            SaveUndoState();
            clip.TimeRemapPoints.Add(point);
        }
        else
        {
            if (clip.Keyframes.Any(key => key.Property == property && Math.Abs(key.TimeSeconds - snapped) < .0001))
            { StatusText.Text = "此时间已有关键帧 · EDIT-RUBBER-DUPLICATE"; return; }
            SaveUndoState();
            clip.Keyframes.Add(new ClipKeyframe
            { Property = property, TimeSeconds = snapped,
                Value = ClipAnimation.Evaluate(clip, property, clip.StartSeconds + snapped) });
        }
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = "已添加橡皮筋关键点";
    }

    private void EditRubberBandPoint(TimelineTrack track, TimelineClip clip, string property, double time)
    {
        if (track.IsLocked) { StatusText.Text = "轨道已锁定 · EDIT-RUBBER-LOCKED"; return; }
        if (!_project.Tracks.Contains(track) || !track.Clips.Contains(clip))
        { StatusText.Text = "片段已不在当前轨道 · EDIT-RUBBER-STATE"; return; }
        if (property == "Speed" && clip.MulticamGroupId is not null)
        { StatusText.Text = "多机位切段需保持原速 · MULTICAM-EDIT"; return; }
        var dialog = new Window
        {
            Title = "编辑橡皮筋关键点", Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Max(1, SystemParameters.PrimaryScreenWidth * .28),
            Height = Math.Max(1, SystemParameters.PrimaryScreenHeight * .27),
            Background = Brush("#191B20"), Foreground = Brushes.White
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = $"{clip.Name} · {property} · {time:0.###} 秒" });
        var value = new TextBox
        {
            Text = BandValueAt(clip, property, time).ToString("0.###"),
            Margin = new Thickness(0, 12, 0, 0),
            Background = Brush("#292B31"), Foreground = Brushes.White,
            CaretBrush = Brushes.White
        };
        panel.Children.Add(value);
        var save = new Button { Content = "保存数值", Margin = new Thickness(0, 12, 0, 0) };
        var remove = new Button { Content = "删除关键点", Margin = new Thickness(0, 8, 0, 0) };
        save.Click += (_, _) =>
        {
            if (!_project.Tracks.Contains(track) || track.IsLocked || !track.Clips.Contains(clip))
            {
                StatusText.Text = "片段或轨道已变化 · EDIT-RUBBER-STATE";
                dialog.Close();
                return;
            }
            if (!double.TryParse(value.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.CurrentCulture, out double parsed) || !double.IsFinite(parsed) ||
                parsed < 0 || parsed > (property is "Speed" ? 32 : property is "Volume" ? 4 : 1) ||
                property == "Speed" && parsed < .05)
            {
                StatusText.Text = "关键点数值无效 · EDIT-RUBBER-VALUE";
                return;
            }
            if (property == "Speed")
            {
                ClipTimeRemapPoint? point = clip.TimeRemapPoints.FirstOrDefault(candidate =>
                    Math.Abs(candidate.TimeSeconds - time) < .0001);
                if (point is null) { dialog.Close(); return; }
                if (Math.Abs(point.Speed - parsed) < .000001) { dialog.Close(); return; }
                double originalSpeed = point.Speed;
                point.Speed = parsed;
                MediaAsset? media = _project.Media.FirstOrDefault(asset => asset.Id == clip.MediaAssetId);
                double sourceSpan = ClipTimeMap.SourceOffset(clip, clip.DurationSeconds);
                if (!double.IsFinite(sourceSpan) ||
                    (media?.DurationSeconds is double sourceDuration &&
                        sourceSpan > sourceDuration - clip.SourceInSeconds + .001))
                {
                    point.Speed = originalSpeed;
                    StatusText.Text = "速度节点超出源素材 · EDIT-RUBBER-SOURCE";
                    return;
                }
                point.Speed = originalSpeed;
                SaveUndoState();
                point.Speed = parsed;
            }
            else
            {
                ClipKeyframe? point = clip.Keyframes.FirstOrDefault(candidate =>
                    candidate.Property == property && Math.Abs(candidate.TimeSeconds - time) < .0001);
                if (point is null) { dialog.Close(); return; }
                if (Math.Abs(point.Value - parsed) < .000001) { dialog.Close(); return; }
                SaveUndoState();
                point.Value = parsed;
            }
            MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
            dialog.Close();
        };
        remove.Click += (_, _) =>
        {
            if (!_project.Tracks.Contains(track) || track.IsLocked || !track.Clips.Contains(clip))
            {
                StatusText.Text = "片段或轨道已变化 · EDIT-RUBBER-STATE";
                dialog.Close();
                return;
            }
            bool exists = property == "Speed"
                ? clip.TimeRemapPoints.Any(candidate => Math.Abs(candidate.TimeSeconds - time) < .0001)
                : clip.Keyframes.Any(candidate => candidate.Property == property &&
                    Math.Abs(candidate.TimeSeconds - time) < .0001);
            if (!exists) { dialog.Close(); return; }
            SaveUndoState();
            if (property == "Speed") clip.TimeRemapPoints.RemoveAll(candidate =>
                Math.Abs(candidate.TimeSeconds - time) < .0001);
            else clip.Keyframes.RemoveAll(candidate => candidate.Property == property &&
                Math.Abs(candidate.TimeSeconds - time) < .0001);
            MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
            dialog.Close();
        };
        panel.Children.Add(save); panel.Children.Add(remove);
        dialog.Content = panel;
        dialog.ShowDialog();
    }
}
