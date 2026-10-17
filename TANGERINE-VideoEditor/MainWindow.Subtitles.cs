using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Maintains editable captions as ordinary text clips on video tracks.
/// This lets the preview, undo history, project file, and video renderer use
/// the same objects while SubRip provides a portable sidecar workflow.
/// </summary>
public partial class MainWindow
{
    private void InitializeSubtitleCommands()
    {
        ImportSrtMenuItem.Click += async (_, _) => await ImportSubtitlesAsync();
        ExportSrtMenuItem.Click += async (_, _) => await ExportSubtitlesAsync();
        AddSubtitleMenuItem.Click += (_, _) => AddSubtitleAtPlayhead();
        SubtitleEditorMenuItem.Click += (_, _) => ShowSubtitleEditor();
        PaperEditMenuItem.Click += (_, _) => ShowPaperEditDialog();
        SaveGraphicTemplateMenuItem.Click += async (_, _) => await SaveSelectedGraphicTemplateAsync();
        InsertGraphicTemplateMenuItem.Click += async (_, _) => await InsertGraphicTemplateAsync();
    }

    /// <summary>
    /// A subtitle is a plain text clip. Titles from the library and shape
    /// stickers carry a template id, so they are never listed or exported as
    /// subtitles.
    /// </summary>
    private static bool IsSubtitleClip(TimelineClip clip) =>
        clip.Kind == ClipKind.Text && string.IsNullOrEmpty(clip.TemplateId) &&
        clip.Text?.AdditionalData?.ContainsKey(GraphicShape.ExtensionKey) != true;

    /// <summary>
    /// Text always lives on a video track. The selected track is used when it
    /// has room for the clip's time span; otherwise the topmost video track,
    /// and when that is busy or locked a new video track is added on top so the
    /// text is not hidden under the picture. The caller owns the undo snapshot.
    /// </summary>
    private TimelineTrack GetOrCreateTextTrack(double startSeconds, double durationSeconds)
    {
        bool HasRoom(TimelineTrack candidate) => candidate.Kind != TrackKind.Audio && !candidate.IsLocked &&
            !candidate.Clips.Any(clip => clip.StartSeconds < startSeconds + durationSeconds - .000001 &&
                clip.StartSeconds + clip.DurationSeconds > startSeconds + .000001);
        if (_selectedTrack is not null && _project.Tracks.Contains(_selectedTrack) && HasRoom(_selectedTrack))
            return _selectedTrack;
        TimelineTrack? top = _project.Tracks.FirstOrDefault(candidate => candidate.Kind != TrackKind.Audio);
        if (top is not null && HasRoom(top)) return top;
        var track = new TimelineTrack
        {
            Name = $"视频 {_project.Tracks.Count(candidate => candidate.Kind == TrackKind.Video) + 1}",
            Kind = TrackKind.Video
        };
        _project.Tracks.Insert(0, track);
        for (int index = 0; index < _project.Tracks.Count; index++) _project.Tracks[index].Order = index;
        return track;
    }

    private void AddSubtitleAtPlayhead()
    {
        double start = Math.Max(0, _project.Sequence.PlayheadSeconds);
        if (!double.IsFinite(start) || start + 3 > 24 * 3600)
        {
            StatusText.Text = "播放头超出可添加字幕的范围 · SUBTITLE-VALIDATE";
            return;
        }
        var clip = new TimelineClip
        {
            Kind = ClipKind.Text,
            Name = "新字幕",
            StartSeconds = start,
            DurationSeconds = 3,
            Text = CreateSubtitleStyle("输入字幕文字")
        };
        SaveUndoState();
        TimelineTrack track = GetOrCreateTextTrack(clip.StartSeconds, clip.DurationSeconds);
        track.Clips.Add(clip);
        _selectedTrack = track;
        _selectedClip = clip;
        _selectedClips.Clear();
        _selectedClips.Add(clip);
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(clip.StartSeconds);
        OpenTextToolPage(clip);
        StatusText.Text = "已添加字幕";
    }

    private static ClipText CreateSubtitleStyle(string content) => new()
    {
        Content = content,
        FontSize = 56,
        ForegroundColor = "#FFFFFF",
        OutlineEnabled = true,
        OutlineColor = "#000000",
        OutlineThickness = 2,
        ShadowEnabled = true,
        ShadowColor = "#000000",
        ShadowBlur = 6,
        ShadowOpacity = .8,
        HorizontalAlignment = "Center",
        VerticalAlignment = "Bottom"
    };

    private async Task ImportSubtitlesAsync()
    {
        var picker = new OpenFileDialog { Title = "导入 SRT 字幕", Filter = "SRT 字幕 (*.srt)|*.srt",
            CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        ProjectDocument targetProject = _project;
        long targetRevision = _projectRevision;
        try
        {
            StatusText.Text = "正在解析 SRT 字幕…";
            IReadOnlyList<SubtitleCue> cues = await SubtitleService.ReadSrtAsync(picker.FileName);
            if (!ReferenceEquals(targetProject, _project) || targetRevision != _projectRevision)
            { StatusText.Text = "字幕导入已中止：读取期间项目发生变化"; return; }
            if (cues.Count == 0) { StatusText.Text = "字幕文件没有可导入的内容"; return; }
            // A new video track on top keeps an import from overlapping earlier
            // captions or from landing underneath the picture.
            var track = new TimelineTrack
            {
                Name = $"视频 {_project.Tracks.Count(item => item.Kind == TrackKind.Video) + 1}",
                Kind = TrackKind.Video
            };
            SaveUndoState();
            _project.Tracks.Insert(0, track);
            for (int index = 0; index < _project.Tracks.Count; index++) _project.Tracks[index].Order = index;
            foreach (SubtitleCue cue in cues)
                track.Clips.Add(new TimelineClip
                {
                    Kind = ClipKind.Text,
                    Name = cue.Text.Split('\n')[0],
                    StartSeconds = cue.StartSeconds,
                    DurationSeconds = cue.EndSeconds - cue.StartSeconds,
                    Text = CreateSubtitleStyle(cue.Text)
                });
            _selectedTrack = track;
            MarkProjectDirty();
            RefreshTimeline();
            SeekPreview(_project.Sequence.PlayheadSeconds);
            StatusText.Text = $"已导入 {cues.Count} 条字幕";
        }
        catch (SubtitleException exception) { ReportSubtitleError(exception); }
    }

    private async Task ExportSubtitlesAsync()
    {
        var cues = _project.Tracks.Where(track => track.Kind != TrackKind.Audio && !track.IsHidden)
            .SelectMany(track => track.Clips)
            .Where(IsSubtitleClip)
            .OrderBy(clip => clip.StartSeconds)
            .Select(clip => new SubtitleCue(clip.StartSeconds, clip.StartSeconds + clip.DurationSeconds,
                clip.Text?.Content ?? clip.Name)).ToArray();
        if (cues.Length == 0) { StatusText.Text = "当前项目没有可导出的字幕"; return; }
        var picker = new SaveFileDialog { Title = "导出 SRT 字幕", Filter = "SRT 字幕 (*.srt)|*.srt",
            DefaultExt = ".srt", AddExtension = true, FileName = _project.Name + ".srt" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            StatusText.Text = "正在导出 SRT 字幕…";
            await SubtitleService.WriteSrtAsync(picker.FileName, cues);
            StatusText.Text = $"已导出 {cues.Length} 条字幕：{System.IO.Path.GetFileName(picker.FileName)}";
        }
        catch (SubtitleException exception) { ReportSubtitleError(exception); }
    }

    private void ShowSubtitleProperties(TimelineClip clip)
    {
        TimelineTrack? track = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
        if (track is null || !IsSubtitleClip(clip)) return;
        if (track.IsLocked) { StatusText.Text = "字幕所在的轨道已锁定"; return; }
        SelectClip(clip);
        OpenTextToolPage(clip);
    }

    private bool EditSubtitleDialog(TimelineClip clip, bool isNew)
    {
        ProjectDocument dialogProject = _project;
        long dialogGeneration = _projectGeneration;
        var dialog = new Window
        {
            Title = isNew ? "添加字幕" : "编辑字幕", Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Max(1, SystemParameters.PrimaryScreenWidth * .38),
            Height = Math.Max(1, SystemParameters.PrimaryScreenHeight * .4),
            Background = Brush("#191B20"), Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI")
        };
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64, GridUnitType.Star) });
        int row = 0;
        var content = AddTextBox(form, ref row, "字幕文字", clip.Text?.Content ?? clip.Name, multiline: true);
        var start = AddTextBox(form, ref row, "开始秒数", clip.StartSeconds.ToString("0.###", CultureInfo.CurrentCulture));
        var end = AddTextBox(form, ref row, "结束秒数", (clip.StartSeconds + clip.DurationSeconds).ToString("0.###", CultureInfo.CurrentCulture));
        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var style = new Button { Content = "样式…", Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "应用", Padding = new Thickness(14, 6, 14, 6) };
        style.Click += (_, _) =>
        {
            if (isNew)
            {
                // Keep the new caption as a local draft. The user can set
                // fill, outline, shadow, font and shape before its first save.
                clip.Text ??= CreateSubtitleStyle(content.Text);
                clip.Text.Content = content.Text;
                ShowTextProperties(clip, unattachedDraft: true, dialogOwner: dialog);
                content.Text = clip.Text?.Content ?? content.Text;
                return;
            }
            dialog.Close();
            ShowTextProperties(clip);
        };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        apply.Click += (_, _) =>
        {
            if (!ReferenceEquals(dialogProject, _project) || dialogGeneration != _projectGeneration)
            {
                StatusText.Text = "字幕编辑已中止：项目已经切换";
                dialog.DialogResult = false;
                return;
            }
            if (string.IsNullOrWhiteSpace(content.Text))
            {
                MessageBox.Show(dialog, "请输入字幕文字。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (content.Text.Length > 100000)
            {
                MessageBox.Show(dialog, "字幕文字超过 100000 个字符。", "输入无效",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!TryNumber(start.Text, 0, 24 * 3600, "开始秒数", out double startSeconds) ||
                !TryNumber(end.Text, 0, 24 * 3600, "结束秒数", out double endSeconds)) return;
            if (endSeconds <= startSeconds)
            {
                MessageBox.Show(dialog, "结束时间必须晚于开始时间。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!isNew)
            {
                TimelineTrack? track = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
                if (track is null || track.IsLocked) { StatusText.Text = "字幕不可编辑"; return; }
                SaveUndoState();
            }
            clip.StartSeconds = startSeconds;
            clip.DurationSeconds = endSeconds - startSeconds;
            clip.Text ??= CreateSubtitleStyle(content.Text);
            clip.Text.Content = content.Text;
            clip.Name = content.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "字幕";
            dialog.DialogResult = true;
        };
        buttons.Children.Add(style); buttons.Children.Add(cancel); buttons.Children.Add(apply);
        Grid.SetRow(buttons, 1); root.Children.Add(buttons);
        dialog.Content = root;
        return dialog.ShowDialog() == true;
    }

    private void ShowSubtitleEditor()
    {
        var dialog = new Window
        {
            Title = "字幕编辑器", Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Max(1, SystemParameters.PrimaryScreenWidth * .58),
            Height = Math.Max(1, SystemParameters.PrimaryScreenHeight * .62),
            Background = Brush("#191B20"), Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI")
        };
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var list = new ListBox();
        ProjectDocument editorProject = _project;
        void Refresh()
        {
            if (!ReferenceEquals(editorProject, _project))
            {
                dialog.Close();
                return;
            }
            list.ItemsSource = _project.Tracks.Where(track => track.Kind != TrackKind.Audio)
                .SelectMany(track => track.Clips.Select(clip => (Track: track, Clip: clip)))
                .Where(item => IsSubtitleClip(item.Clip))
                .OrderBy(item => item.Clip.StartSeconds)
                .Select(item => new SubtitleListEntry(item.Track, item.Clip)).ToArray();
        }
        Refresh();
        list.Background = Brush("#24262D");
        list.Foreground = Brushes.White;
        list.BorderBrush = Brush("#565B65");
        var subtitleItemStyle = new Style(typeof(ListBoxItem));
        subtitleItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#24262D")));
        subtitleItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        subtitleItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
        var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#69503F")));
        selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        subtitleItemStyle.Triggers.Add(selectedTrigger);
        list.ItemContainerStyle = subtitleItemStyle;
        root.Children.Add(list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var add = new Button { Content = "添加", Padding = new Thickness(13, 6, 13, 6), Margin = new Thickness(0, 0, 8, 0) };
        var edit = new Button { Content = "编辑", Padding = new Thickness(13, 6, 13, 6), Margin = new Thickness(0, 0, 8, 0) };
        var remove = new Button { Content = "删除", Padding = new Thickness(13, 6, 13, 6), Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "关闭", Padding = new Thickness(13, 6, 13, 6) };
        add.Click += (_, _) =>
        {
            if (!ReferenceEquals(editorProject, _project)) { dialog.Close(); return; }
            AddSubtitleAtPlayhead(); Refresh();
        };
        edit.Click += (_, _) =>
        {
            if (!ReferenceEquals(editorProject, _project)) { dialog.Close(); return; }
            if (list.SelectedItem is not SubtitleListEntry entry) return;
            ShowSubtitleProperties(entry.Clip); Refresh();
        };
        remove.Click += (_, _) =>
        {
            if (!ReferenceEquals(editorProject, _project)) { dialog.Close(); return; }
            if (list.SelectedItem is not SubtitleListEntry entry || entry.Track.IsLocked) return;
            SaveUndoState(); entry.Track.Clips.Remove(entry.Clip);
            MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds); Refresh();
        };
        list.MouseDoubleClick += (_, _) => edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        close.Click += (_, _) => dialog.Close();
        buttons.Children.Add(add); buttons.Children.Add(edit); buttons.Children.Add(remove); buttons.Children.Add(close);
        Grid.SetRow(buttons, 1); root.Children.Add(buttons);
        dialog.Content = root;
        dialog.ShowDialog();
    }

    /// <summary>
    /// Builds a preview vector from the same proportional geometry used by
    /// TitleBitmapRenderer. It is called for every visible text clip, so
    /// changing a graphic's properties updates the local preview immediately.
    /// </summary>
    private void DrawGraphicShapeOverlay(TimelineClip clip, double scale, double frameLeft, double frameTop,
        double frameWidth, double frameHeight)
    {
        GraphicShape? graphic;
        try { graphic = GraphicShape.FromClip(clip); }
        catch (GraphicShapeException exception)
        {
            StatusText.Text = $"图形预览失败 · {exception.StageCode}";
            return;
        }
        if (graphic is null) return;
        System.Windows.Shapes.Shape shape;
        try { shape = graphic.CreateVisual(_project.Sequence.Width, _project.Sequence.Height, scale); }
        catch (GraphicShapeException exception)
        {
            StatusText.Text = $"图形预览失败 · {exception.StageCode}";
            return;
        }
        double now = _project.Sequence.PlayheadSeconds;
        double local = now - clip.StartSeconds;
        double graphicOpacity = Math.Clamp(ClipAnimation.Evaluate(clip, "Opacity", now), 0, 1) *
            DynamicTitlePreset.OpacityMultiplier(clip, local);
        shape.IsHitTestVisible = false;
        // The shape is centered in the full sequence frame, so its own center
        // is the same scale/rotation pivot as the exported title bitmap.
        double shapeLeft = frameLeft + (frameWidth - shape.Width) / 2 +
            (ClipAnimation.Evaluate(clip, "PositionX", now) +
                DynamicTitlePreset.HorizontalOffset(clip, local, _project.Sequence.Width)) * scale;
        // Slide-in can come from the top or bottom, so the vertical position
        // carries the same motion offset the horizontal one does.
        double verticalPosition = ClipAnimation.Evaluate(clip, "PositionY", now) +
            DynamicTitlePreset.VerticalOffset(clip, local, _project.Sequence.Height);
        double shapeTop = frameTop + (frameHeight - shape.Height) / 2 + verticalPosition * scale;
        double pivotX = (frameLeft + frameWidth / 2 +
            (ClipAnimation.Evaluate(clip, "PositionX", now) +
                DynamicTitlePreset.HorizontalOffset(clip, local, _project.Sequence.Width)) * scale - shapeLeft) /
            Math.Max(1, shape.Width);
        double pivotY = (frameTop + frameHeight / 2 + verticalPosition * scale - shapeTop) /
            Math.Max(1, shape.Height);
        var graphicVisual = new Canvas
        {
            Width = shape.Width,
            Height = shape.Height,
            Background = Brushes.Transparent,
            Opacity = graphicOpacity,
            Cursor = System.Windows.Input.Cursors.SizeAll,
            ToolTip = "拖动图形调整位置，拖动顶点改变形状；双击打开形状设置",
            RenderTransformOrigin = new Point(pivotX, pivotY),
            RenderTransform = new TransformGroup
            {
                Children = new TransformCollection
                {
                    new ScaleTransform(
                        Math.Clamp(ClipAnimation.Evaluate(clip, "ScaleX", now), .05, 8) *
                            DynamicTitlePreset.ScaleMultiplier(clip, local),
                        Math.Clamp(ClipAnimation.Evaluate(clip, "ScaleY", now), .05, 8) *
                            DynamicTitlePreset.ScaleMultiplier(clip, local)),
                    new RotateTransform(ClipAnimation.Evaluate(clip, "RotationDegrees", now))
                }
            }
        };
        graphicVisual.Children.Add(shape);

        // Editable vertex handles for the selected shape. Geometry comes from
        // the rendered shape itself, so a polygon's real corners are used
        // instead of an approximated bounding box.
        if (ReferenceEquals(clip, _selectedClip))
        {
            _vertexOverlayScale = scale;
            _vertexOverlayShape = shape;
            _vertexOverlayVisual = graphicVisual;
            _vertexOverlayHandles.Clear();
            List<Point> vertices = GetShapeVerticesForOverlay(graphic, shape);
            for (int i = 0; i < vertices.Count; i++)
            {
                var vertexHandle = new Ellipse
                {
                    Width = VertexHandleSize,
                    Height = VertexHandleSize,
                    Fill = Brush("#FF9957"),
                    Stroke = Brushes.White,
                    StrokeThickness = 2,
                    Cursor = System.Windows.Input.Cursors.SizeNWSE,
                    ToolTip = VertexHandleTip(graphic.Kind, i),
                    Tag = VertexHandleTag
                };
                Canvas.SetLeft(vertexHandle, vertices[i].X - VertexHandleSize / 2);
                Canvas.SetTop(vertexHandle, vertices[i].Y - VertexHandleSize / 2);
                Canvas.SetZIndex(vertexHandle, 100);
                int index = i;
                vertexHandle.MouseLeftButtonDown += (_, args) =>
                    BeginShapeVertexDrag(clip, graphic, index, args);
                _vertexOverlayHandles.Add(vertexHandle);
                graphicVisual.Children.Add(vertexHandle);
            }
        }

        // Graphic stickers use vertex handles for editing. Do not add the
        // dashed bounding rectangle used by text selection; it appears as an
        // unwanted frame through the middle of a selected shape.
        // A vertex handle must win over the whole-shape drag. The parent uses a
        // tunneling event, so it would otherwise consume the click before the
        // handle's own bubbling handler ever runs.
        graphicVisual.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (IsVertexHandle(args.OriginalSource)) return;
            StartTitleDrag(clip, graphicVisual, args);
        };
        graphicVisual.MouseLeftButtonDown += (_, args) =>
        {
            if (args.Handled || _vertexDragActive || IsVertexHandle(args.OriginalSource)) return;
            StartTitleDrag(clip, graphicVisual, args);
        };
        graphicVisual.MouseMove += (_, args) => { if (!_vertexDragActive) MoveTitleDrag(args); };
        graphicVisual.MouseLeftButtonUp += (_, args) => { if (!_vertexDragActive) EndTitleDrag(args); };
        graphicVisual.LostMouseCapture += (_, _) => { if (!_vertexDragActive) FinishTitleDrag(); };
        Canvas.SetLeft(graphicVisual, shapeLeft);
        Canvas.SetTop(graphicVisual, shapeTop);
        PreviewOverlay.Children.Add(graphicVisual);
        _graphicPreviewVisuals[clip.Id] = graphicVisual;
    }

    private const double VertexHandleSize = 11;
    private const string VertexHandleTag = "TangerineShapeVertex";

    private static bool IsVertexHandle(object? source) =>
        source is FrameworkElement element && Equals(element.Tag, VertexHandleTag);

    /// <summary>
    /// Rotates a screen-space delta back into the shape's unrotated axes so a
    /// pointer move maps onto the shape's own width and height directions.
    /// </summary>
    private static Point UnrotatedShapeOffset(double x, double y, double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180;
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        return new Point(x * cosine + y * sine, -x * sine + y * cosine);
    }

    /// <summary>Rotates a shape-space offset back into screen space.</summary>
    private static Point RotatedShapeOffset(double x, double y, double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180;
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        return new Point(x * cosine - y * sine, x * sine + y * cosine);
    }

    private static string VertexHandleTip(string kind, int index) => kind switch
    {
        "Rectangle" => index == 0
            ? "左上角是固定锚点，拖动其他三个点拉伸矩形"
            : "拖动改变矩形大小，左上角保持不动",
        "Triangle" => index == 0
            ? "顶点是固定锚点，拖动下方两点拉伸三角形"
            : "拖动改变三角形大小，顶点保持不动",
        _ => "拖动改变图形大小，左上角保持不动"
    };

    private bool TryScaleSelectedGraphicWithWheel(Point pointer, int delta)
    {
        TimelineClip? clip = _selectedClip;
        if (clip is null || clip.Kind != ClipKind.Text) return false;
        TimelineTrack? track = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
        if (track is null || track.IsLocked) return false;
        GraphicShape? graphic;
        try { graphic = GraphicShape.FromClip(clip); }
        catch (GraphicShapeException) { return false; }
        if (graphic is null || !_graphicPreviewVisuals.TryGetValue(clip.Id, out Canvas? visual)) return false;
        Point local;
        try { local = visual.TransformToAncestor(PreviewOverlay).Inverse.Transform(pointer); }
        catch (InvalidOperationException) { return false; }
        if (local.X < 0 || local.Y < 0 || local.X > visual.Width || local.Y > visual.Height) return false;
        double factor = Math.Pow(1.0018, delta);
        double nextWidth = Math.Clamp(graphic.WidthFraction * factor, .01, 4);
        double nextHeight = Math.Clamp(graphic.HeightFraction * factor, .01, 4);
        if (Math.Abs(nextWidth - graphic.WidthFraction) < .000001 &&
            Math.Abs(nextHeight - graphic.HeightFraction) < .000001) return true;
        SaveUndoState();
        graphic.WidthFraction = nextWidth;
        graphic.HeightFraction = nextHeight;
        graphic.Save(clip);
        MarkProjectDirty();
        _lastTitleLayoutKey = null;
        DrawTextOverlay();
        StatusText.Text = $"形状大小：{nextWidth:0.###} × {nextHeight:0.###}";
        return true;
    }

    /// <summary>
    /// Handle positions come from the rendered geometry so a polygon exposes
    /// its real corners. The returned points are in the shape's own
    /// coordinates, which is also the parent canvas's coordinate space.
    /// </summary>
    private static List<Point> GetShapeVerticesForOverlay(GraphicShape graphic,
        System.Windows.Shapes.Shape shape)
    {
        double width = shape.Width;
        double height = shape.Height;
        if (shape is Polygon polygon && polygon.Points.Count >= 3)
            return polygon.Points.ToList();
        if (shape is Ellipse)
            return
            [
                new Point(width / 2, 0), new Point(width, height / 2),
                new Point(width / 2, height), new Point(0, height / 2)
            ];
        return
        [
            new Point(0, 0), new Point(width, 0),
            new Point(width, height), new Point(0, height)
        ];
    }

    private bool _vertexDragActive;
    private TimelineClip? _vertexDragClip;
    private GraphicShape? _vertexDragGraphic;
    private int _vertexDragIndex = -1;
    private Point _vertexDragOrigin;
    private double _vertexDragStartWidthFraction;
    private double _vertexDragStartHeightFraction;
    private double _vertexDragStartLeft;
    private double _vertexDragStartTop;
    private double _vertexDragLiveLeft;
    private double _vertexDragLiveTop;
    private double _vertexDragStartRotation;
    private double _vertexDragStartScaleX = 1;
    private double _vertexDragStartScaleY = 1;
    private double _vertexOverlayScale = 1;
    private System.Windows.Shapes.Shape? _vertexOverlayShape;
    private Canvas? _vertexOverlayVisual;
    private readonly List<Ellipse> _vertexOverlayHandles = [];
    private List<Point> _vertexDragStartPoints = []; // Store original custom points

    /// <summary>
    /// Starts a vertex resize. The gesture captures on the overlay rather than
    /// the handle, and updates the existing visual in place: redrawing the
    /// overlay mid-drag would destroy the handle and drop mouse capture.
    /// </summary>
    private void BeginShapeVertexDrag(TimelineClip clip, GraphicShape graphic, int index,
        System.Windows.Input.MouseButtonEventArgs args)
    {
        TimelineTrack? track = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
        if (track is null || track.IsLocked)
        {
            StatusText.Text = track is null ? "图形已不在当前项目中" : "轨道已锁定，无法调整图形";
            return;
        }
        if (_vertexOverlayVisual is null || _vertexOverlayShape is null) return;
        _vertexDragActive = true;
        _vertexDragClip = clip;
        _vertexDragGraphic = graphic;
        _vertexDragIndex = index;
        _vertexDragStartWidthFraction = graphic.WidthFraction;
        _vertexDragStartHeightFraction = graphic.HeightFraction;
        _vertexDragStartLeft = Canvas.GetLeft(_vertexOverlayVisual);
        _vertexDragStartTop = Canvas.GetTop(_vertexOverlayVisual);
        _vertexDragLiveLeft = _vertexDragStartLeft;
        _vertexDragLiveTop = _vertexDragStartTop;
        // The overlay canvas carries the clip's rotation and scale, so the
        // pointer travels in rotated screen space while Width/HeightFraction
        // and Points live in the shape's unrotated local space.
        double dragTime = _project.Sequence.PlayheadSeconds;
        double dragLocal = dragTime - clip.StartSeconds;
        _vertexDragStartRotation = ClipAnimation.Evaluate(clip, "RotationDegrees", dragTime);
        _vertexDragStartScaleX = Math.Clamp(ClipAnimation.Evaluate(clip, "ScaleX", dragTime), .05, 8) *
            DynamicTitlePreset.ScaleMultiplier(clip, dragLocal);
        _vertexDragStartScaleY = Math.Clamp(ClipAnimation.Evaluate(clip, "ScaleY", dragTime), .05, 8) *
            DynamicTitlePreset.ScaleMultiplier(clip, dragLocal);

        // Keep the persisted default triangle identical to the rendered
        // regular-polygon geometry before calculating the pointer origin.
        if (graphic.Kind == "Triangle" && graphic.Points.Count != 3)
        {
            graphic.Points =
            [
                new Point(.5, .04),
                new Point(.898, .73),
                new Point(.102, .73)
            ];
        }
        // Use the actual vertex center as the gesture origin.  Using the
        // pointer position inside the ellipse handle introduces a small,
        // visible offset between the cursor and the triangle point.
        List<Point> overlayVertices = GetShapeVerticesForOverlay(graphic, _vertexOverlayShape);
        Point vertex = index >= 0 && index < overlayVertices.Count
            ? overlayVertices[index] : args.GetPosition(_vertexOverlayVisual);
        // The handle is inside a canvas that may already be scaled and
        // rotated. Convert its local vertex through the actual visual tree so
        // the drag origin is exactly where the pointer sees the handle.
        try
        {
            _vertexDragOrigin = _vertexOverlayVisual.TransformToAncestor(PreviewOverlay)
                .Transform(vertex);
        }
        catch (InvalidOperationException)
        {
            _vertexDragOrigin = new Point(_vertexDragStartLeft + vertex.X,
                _vertexDragStartTop + vertex.Y);
        }

        // Save initial custom points for triangles
        if (graphic.Kind == "Triangle")
        {
            _vertexDragStartPoints = new List<Point>(graphic.Points);
        }
        else if (_vertexOverlayShape is Polygon polygon && polygon.Points.Count >= 3)
        {
            // Persist the exact rendered polygon geometry so regular polygons
            // use the same direct vertex editing path as custom shapes.
            double width = Math.Max(1, _vertexOverlayShape.Width);
            double height = Math.Max(1, _vertexOverlayShape.Height);
            graphic.Points = polygon.Points.Select(point =>
                new Point(Math.Clamp(point.X / width, 0, 1), Math.Clamp(point.Y / height, 0, 1))).ToList();
            _vertexDragStartPoints = new List<Point>(graphic.Points);
        }

        PreviewOverlay.CaptureMouse();
        args.Handled = true;
    }

    /// <summary>
    /// Resizes the live visual while keeping the shape's anchor fixed: the
    /// top-left corner for rectangles and ellipses, the apex for triangles.
    /// </summary>
    private void MoveShapeVertexDrag(System.Windows.Input.MouseEventArgs args)
    {
        if (!_vertexDragActive || _vertexDragGraphic is null ||
            _vertexOverlayShape is null || _vertexOverlayVisual is null) return;
        if (args.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            FinishShapeVertexDrag();
            return;
        }
        Point current = args.GetPosition(PreviewOverlay);
        double shapeWidth = _project.Sequence.Width * _vertexDragStartWidthFraction * _vertexOverlayScale;
        double shapeHeight = _project.Sequence.Height * _vertexDragStartHeightFraction * _vertexOverlayScale;
        double frameWidth = _project.Sequence.Width * _vertexOverlayScale;
        double frameHeight = _project.Sequence.Height * _vertexOverlayScale;
        if (shapeWidth <= 0 || shapeHeight <= 0 || frameWidth <= 0 || frameHeight <= 0) return;
        // Map the screen-space travel back onto the shape's own axes. Without
        // this, dragging a rotated shape resizes it along the wrong direction.
        Point localDelta = UnrotatedShapeOffset(
            current.X - _vertexDragOrigin.X, current.Y - _vertexDragOrigin.Y,
            _vertexDragStartRotation);
        double dx = localDelta.X / Math.Max(.001, _vertexDragStartScaleX);
        double dy = localDelta.Y / Math.Max(.001, _vertexDragStartScaleY);
        // Polygon vertices are stored in the shape's own normalized bounds.
        // Convert the pointer through the live visual transform and write the
        // selected point directly; this keeps every polygon vertex under the
        // pointer, including ten-sided and rotated shapes.
        if (_vertexDragGraphic.Kind != "Rectangle" &&
            _vertexOverlayShape is Polygon polygon && _vertexDragStartPoints.Count == polygon.Points.Count &&
            _vertexDragIndex >= 0 && _vertexDragIndex < _vertexDragStartPoints.Count)
        {
            Point local = _vertexOverlayVisual.TransformToAncestor(PreviewOverlay).Inverse.Transform(current);
            double polyWidth = Math.Max(1, _vertexOverlayVisual.Width);
            double polyHeight = Math.Max(1, _vertexOverlayVisual.Height);
            _vertexDragGraphic.Points[_vertexDragIndex] = new Point(
                Math.Clamp(local.X / polyWidth, 0, 1), Math.Clamp(local.Y / polyHeight, 0, 1));
            ApplyLiveVertexGeometry();
            args.Handled = true;
            return;
        }
        double deltaX = dx / frameWidth;
        double deltaY = dy / frameHeight;
        (double width, double height) = ResolveVertexDrag(_vertexDragGraphic.Kind, _vertexDragIndex,
            deltaX, deltaY);
        if (_vertexDragGraphic.Kind is not "Triangle")
        {
            // Growing a centered shape moves both edges by half the change.
            // Offset the canvas so the dragged vertex tracks the pointer and
            // the opposite edge stays put.
            double widthPixels = (width - _vertexDragStartWidthFraction) * frameWidth;
            double heightPixels = (height - _vertexDragStartHeightFraction) * frameHeight;
            bool leftVertex = _vertexDragGraphic.Kind is "Ellipse" or "Circle"
                ? _vertexDragIndex == 3
                : _vertexDragIndex is 0 or 3;
            bool topVertex = _vertexDragGraphic.Kind is "Ellipse" or "Circle"
                ? _vertexDragIndex == 0
                : _vertexDragIndex is 0 or 1;
            // The offset is computed on the shape's axes but Canvas.Left and
            // Canvas.Top are screen space, so rotate it back out and include
            // the render scale the canvas applies.
            Point shift = RotatedShapeOffset(
                (leftVertex ? -widthPixels : 0) * _vertexDragStartScaleX,
                (topVertex ? -heightPixels : 0) * _vertexDragStartScaleY,
                _vertexDragStartRotation);
            _vertexDragLiveLeft = _vertexDragStartLeft + shift.X;
            _vertexDragLiveTop = _vertexDragStartTop + shift.Y;
        }
        _vertexDragGraphic.WidthFraction = width;
        _vertexDragGraphic.HeightFraction = height;
        ApplyLiveVertexGeometry();
        args.Handled = true;
    }

    /// <summary>
    /// Maps a pointer delta onto new width and height fractions for the dragged
    /// vertex. For triangles with custom points, updates the specific vertex.
    /// </summary>
    private (double Width, double Height) ResolveVertexDrag(string kind, int index,
        double deltaX, double deltaY)
    {
        double width = _vertexDragStartWidthFraction;
        double height = _vertexDragStartHeightFraction;

        // For triangles, update the specific vertex being dragged
        if (kind == "Triangle" && _vertexDragGraphic != null && _vertexDragStartPoints.Count == 3)
        {
            // Update the dragged vertex directly from the start position
            Point startPoint = _vertexDragStartPoints[index];
            _vertexDragGraphic.Points[index] = new Point(
                Math.Clamp(startPoint.X + deltaX, 0, 1),
                Math.Clamp(startPoint.Y + deltaY, 0, 1)
            );

            // Return original dimensions - we're using custom points now
            return (width, height);
        }

        switch (kind)
        {
            case "Ellipse":
            case "Circle":
                if (index == 1) width += deltaX;        // right
                else if (index == 3) width -= deltaX;   // left
                else if (index == 2) height += deltaY;  // bottom
                else height -= deltaY;                  // top
                break;
            default:
                if (index is 0 or 3) width -= deltaX;                   // left edge
                else width += deltaX;                                  // right edge
                if (index is 0 or 1) height -= deltaY;                 // top edge
                else height += deltaY;                                 // bottom edge
                break;
        }
        return (Math.Clamp(width, .01, 4), Math.Clamp(height, .01, 4));
    }

    /// <summary>
    /// Applies the in-progress size to the existing visual and its handles so
    /// the anchor corner stays under the pointer's original position.
    /// </summary>
    private void ApplyLiveVertexGeometry()
    {
        if (_vertexDragGraphic is null || _vertexOverlayShape is null ||
            _vertexOverlayVisual is null) return;

        // All polygon shapes use their normalized points directly.
        if (_vertexDragGraphic.Kind != "Rectangle" &&
            _vertexOverlayShape is Polygon polygon && _vertexDragGraphic.Points.Count == polygon.Points.Count)
        {
            double width = Math.Max(1, _vertexOverlayVisual.Width);
            double height = Math.Max(1, _vertexOverlayVisual.Height);
            polygon.Points = new PointCollection(_vertexDragGraphic.Points.Select(point =>
                new Point(point.X * width, point.Y * height)));
            for (int i = 0; i < _vertexOverlayHandles.Count && i < polygon.Points.Count; i++)
            {
                Canvas.SetLeft(_vertexOverlayHandles[i], polygon.Points[i].X - VertexHandleSize / 2);
                Canvas.SetTop(_vertexOverlayHandles[i], polygon.Points[i].Y - VertexHandleSize / 2);
            }
            return;
        }

        // Standard resize logic for rectangles, ellipses, etc.
        double newWidth = _project.Sequence.Width * _vertexDragGraphic.WidthFraction * _vertexOverlayScale;
        double newHeight = _project.Sequence.Height * _vertexDragGraphic.HeightFraction * _vertexOverlayScale;
        if (!double.IsFinite(newWidth) || !double.IsFinite(newHeight) ||
            newWidth <= 0 || newHeight <= 0) return;
        double startWidth = _project.Sequence.Width * _vertexDragStartWidthFraction * _vertexOverlayScale;
        if (_vertexOverlayShape is Polygon polygon2)
        {
            List<Point> points = BuildPolygonPoints(_vertexDragGraphic.Kind, newWidth, newHeight);
            polygon2.Points = new PointCollection(points);
        }
        _vertexOverlayShape.Width = newWidth;
        _vertexOverlayShape.Height = newHeight;
        _vertexOverlayVisual.Width = newWidth;
        _vertexOverlayVisual.Height = newHeight;
        // A triangle's apex is centered, so compensate half of the width change
        // to leave it visually pinned while the base grows.
        double left = _vertexDragGraphic.Kind == "Triangle"
            ? _vertexDragStartLeft - (newWidth - startWidth) / 2
            : _vertexDragStartLeft;
        Canvas.SetLeft(_vertexOverlayVisual, _vertexDragGraphic.Kind == "Triangle" ? left : _vertexDragLiveLeft);
        Canvas.SetTop(_vertexOverlayVisual, _vertexDragGraphic.Kind == "Triangle" ? _vertexDragStartTop : _vertexDragLiveTop);
        List<Point> vertices = GetShapeVerticesForOverlay(_vertexDragGraphic, _vertexOverlayShape);
        for (int i = 0; i < _vertexOverlayHandles.Count && i < vertices.Count; i++)
        {
            Canvas.SetLeft(_vertexOverlayHandles[i], vertices[i].X - VertexHandleSize / 2);
            Canvas.SetTop(_vertexOverlayHandles[i], vertices[i].Y - VertexHandleSize / 2);
        }
        foreach (UIElement child in _vertexOverlayVisual.Children)
            if (child is System.Windows.Shapes.Rectangle outline && !ReferenceEquals(child, _vertexOverlayShape))
            {
                outline.Width = newWidth + 6;
                outline.Height = newHeight + 6;
            }
    }

    /// <summary>Rebuilds polygon geometry for the in-progress size.</summary>
    private static List<Point> BuildPolygonPoints(string kind, double width, double height)
    {
        if (kind == "Rectangle")
            return
            [
                new Point(0, 0), new Point(width, 0),
                new Point(width, height), new Point(0, height)
            ];
        if (kind == "Parallelogram")
            return
            [
                new Point(width * .16, 0), new Point(width, 0),
                new Point(width * .84, height), new Point(0, height)
            ];
        int sides = kind switch
        {
            "Triangle" => 3, "Pentagon" => 5, "Hexagon" => 6, "Decagon" => 10, _ => 4
        };
        var points = new List<Point>(sides);
        for (int index = 0; index < sides; index++)
        {
            double angle = -Math.PI / 2 + index * Math.PI * 2 / sides;
            points.Add(new Point((.5 + Math.Cos(angle) * .46) * width,
                (.5 + Math.Sin(angle) * .46) * height));
        }
        return points;
    }

    /// <summary>
    /// Commits the resize. Because the shape is centered in the sequence frame,
    /// growing it moves both edges; the position offset compensates so the
    /// anchor the user saw stays where it was in preview and export.
    /// </summary>
    private void FinishShapeVertexDrag()
    {
        if (!_vertexDragActive) return;
        TimelineClip? clip = _vertexDragClip;
        GraphicShape? graphic = _vertexDragGraphic;
        int index = _vertexDragIndex;
        double startWidth = _vertexDragStartWidthFraction;
        double startHeight = _vertexDragStartHeightFraction;
        _vertexDragActive = false;
        _vertexDragClip = null;
        _vertexDragGraphic = null;
        _vertexDragIndex = -1;
        if (PreviewOverlay.IsMouseCaptured) PreviewOverlay.ReleaseMouseCapture();
        if (clip is null || graphic is null) return;
        TimelineTrack? track = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
        if (track is null || track.IsLocked)
        {
            StatusText.Text = "图形调整已取消：轨道状态已变化 · VIS-SHAPE-LOCKED";
            SeekPreview(_project.Sequence.PlayheadSeconds);
            return;
        }
        double widthChange = graphic.WidthFraction - startWidth;
        double heightChange = graphic.HeightFraction - startHeight;
        bool pointsChanged = _vertexDragStartPoints.Count == graphic.Points.Count &&
            graphic.Points.Zip(_vertexDragStartPoints).Any(pair =>
                Math.Abs(pair.First.X - pair.Second.X) > .000001 ||
                Math.Abs(pair.First.Y - pair.Second.Y) > .000001);
        if (Math.Abs(widthChange) < .000001 && Math.Abs(heightChange) < .000001 && !pointsChanged)
        {
            SeekPreview(_project.Sequence.PlayheadSeconds);
            return;
        }
        try { graphic.Validate(); }
        catch (GraphicShapeException exception)
        {
            // The drag mutated this instance in place, so an invalid result has
            // to be rolled back. Leaving it would make the next redraw throw
            // and the sticker would vanish from the monitor.
            graphic.WidthFraction = startWidth;
            graphic.HeightFraction = startHeight;
            if (_vertexDragStartPoints.Count > 0)
                graphic.Points = new List<Point>(_vertexDragStartPoints);
            StatusText.Text = $"图形顶点无效，已还原 · {exception.StageCode}：{exception.Message}";
            SeekPreview(_project.Sequence.PlayheadSeconds);
            return;
        }
        SaveUndoState();
        double now = _project.Sequence.PlayheadSeconds;
        // The shape renders centered, so growing it moves both edges by half
        // the change. Which edge must stay put depends on the dragged vertex:
        // pulling a right edge pins the left one and vice versa. A triangle
        // edits its own points, so its box needs no compensation.
        bool leftEdge = graphic.Kind is "Ellipse" or "Circle" ? index == 3 : index is 0 or 3;
        bool topEdge = graphic.Kind is "Ellipse" or "Circle" ? index == 0 : index is 0 or 1;
        bool directPointShape = graphic.Kind != "Rectangle" && graphic.Points.Count > 0;
        double localShiftX = directPointShape
            ? 0 : _project.Sequence.Width * widthChange / 2 * (leftEdge ? -1 : 1);
        double localShiftY = directPointShape
            ? 0 : _project.Sequence.Height * heightChange / 2 * (topEdge ? -1 : 1);
        // Position is applied before the clip's rotation, so convert the
        // shape-space compensation into the same space Position uses.
        Point committedShift = RotatedShapeOffset(localShiftX, localShiftY, _vertexDragStartRotation);
        double shiftX = committedShift.X;
        double shiftY = committedShift.Y;
        if (Math.Abs(shiftX) > .000001)
            ShiftAnimatedVisualProperty(clip, "PositionX",
                ClipAnimation.Evaluate(clip, "PositionX", now) + shiftX);
        if (Math.Abs(shiftY) > .000001)
            ShiftAnimatedVisualProperty(clip, "PositionY",
                ClipAnimation.Evaluate(clip, "PositionY", now) + shiftY);
        graphic.Save(clip);
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(now);
        StatusText.Text = $"已调整图形：宽 {graphic.WidthFraction:0.###} · 高 {graphic.HeightFraction:0.###}";
    }

    private sealed record SubtitleListEntry(TimelineTrack Track, TimelineClip Clip)
    {
        public override string ToString() => $"{TimeSpan.FromSeconds(Clip.StartSeconds):hh\\:mm\\:ss} – " +
            $"{TimeSpan.FromSeconds(Clip.StartSeconds + Clip.DurationSeconds):hh\\:mm\\:ss}   {Clip.Text?.Content.Replace('\n', ' ') ?? Clip.Name}";
    }

    private async Task SaveSelectedGraphicTemplateAsync()
    {
        if (_selectedClip is not { Kind: ClipKind.Text } clip)
        { StatusText.Text = "请先选择文字或图形片段"; return; }
        var picker = new SaveFileDialog { Title = "保存图形模板", Filter = "TANGERINE 图形模板 (*.tgt)|*.tgt",
            DefaultExt = GraphicTemplateDocument.Extension, AddExtension = true,
            FileName = string.Concat(clip.Name.Take(120).Select(character =>
                System.IO.Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)) + ".tgt" };
        if (picker.ShowDialog(this) != true) return;
        ProjectDocument sourceProject = _project;
        long sourceRevision = _projectRevision;
        try
        {
            StatusText.Text = "正在准备图形模板…";
            GraphicTemplateDocument template = await Task.Run(() => GraphicTemplateDocument.FromClip(clip));
            if (!ReferenceEquals(sourceProject, _project) || sourceRevision != _projectRevision ||
                !_project.Tracks.Any(track => track.Clips.Contains(clip)))
            { StatusText.Text = "图形模板保存已中止：项目在准备期间变化"; return; }
            await template.WriteAsync(picker.FileName);
            StatusText.Text = $"已保存图形模板：{System.IO.Path.GetFileName(picker.FileName)}";
        }
        catch (GraphicTemplateException exception) { ReportGraphicTemplateError(exception); }
        catch (GraphicShapeException exception)
        {
            StatusText.Text = $"图形模板操作失败 · {exception.StageCode}";
            MessageBox.Show(this, $"{exception.Message}\n阶段代码：{exception.StageCode}", "图形模板操作失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException or
            InvalidOperationException)
        {
            ReportGraphicTemplateError(new GraphicTemplateException(GraphicTemplateStageCode.Read,
                "无法读取所选图形片段的模板数据。", exception));
        }
    }

    private async Task InsertGraphicTemplateAsync()
    {
        var picker = new OpenFileDialog { Title = "插入图形模板", Filter = "TANGERINE 图形模板 (*.tgt)|*.tgt",
            CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        ProjectDocument targetProject = _project;
        long targetRevision = _projectRevision;
        try
        {
            GraphicTemplateDocument template = await GraphicTemplateDocument.ReadAsync(picker.FileName);
            if (!ReferenceEquals(targetProject, _project) || targetRevision != _projectRevision)
            { StatusText.Text = "模板插入已中止：项目在读取期间切换"; return; }
            TimelineTrack? track = _selectedTrack is { IsLocked: false } selected &&
                selected.Kind == TrackKind.Video
                ? selected : _project.Tracks.FirstOrDefault(item => item.Kind == TrackKind.Video && !item.IsLocked);
            if (track is null) { StatusText.Text = "没有可插入图形的未锁定视频轨道"; return; }
            double startSeconds = Math.Max(0, _project.Sequence.PlayheadSeconds);
            TimelineClip clip = await Task.Run(() => template.Instantiate(startSeconds));
            if (!ReferenceEquals(targetProject, _project) || targetRevision != _projectRevision ||
                !_project.Tracks.Any(item => ReferenceEquals(item, track) && !item.IsLocked))
            { StatusText.Text = "模板插入已中止：项目在准备期间变化"; return; }
            SaveUndoState(); track.Clips.Add(clip);
            _selectedTrack = track; _selectedClip = clip; _selectedClips.Clear(); _selectedClips.Add(clip);
            MarkProjectDirty(); RefreshTimeline(); SeekPreview(clip.StartSeconds);
            StatusText.Text = $"已插入图形模板：{template.Name}";
        }
        catch (GraphicTemplateException exception) { ReportGraphicTemplateError(exception); }
        catch (GraphicShapeException exception)
        {
            StatusText.Text = $"图形模板操作失败 · {exception.StageCode}";
            MessageBox.Show(this, $"{exception.Message}\n阶段代码：{exception.StageCode}", "图形模板操作失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException or
            InvalidOperationException)
        {
            ReportGraphicTemplateError(new GraphicTemplateException(GraphicTemplateStageCode.Read,
                "无法实例化图形模板。", exception));
        }
    }

    private void ReportSubtitleError(SubtitleException exception)
    {
        StatusText.Text = $"字幕操作失败 · {exception.StageCode}";
        MessageBox.Show(this, $"{exception.Message}\n阶段代码：{exception.StageCode}", "字幕操作失败",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ReportGraphicTemplateError(GraphicTemplateException exception)
    {
        StatusText.Text = $"图形模板操作失败 · {exception.StageCode}";
        MessageBox.Show(this, $"{exception.Message}\n阶段代码：{exception.StageCode}", "图形模板操作失败",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
