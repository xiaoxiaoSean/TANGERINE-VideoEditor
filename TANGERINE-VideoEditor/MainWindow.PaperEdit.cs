using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Uses the caption track as an editable paper transcript. The user marks
/// passages in text, previews their timecodes, and either removes those
/// passages from a chosen media track or creates a sequential rough cut.
/// </summary>
public partial class MainWindow
{
    private sealed record PaperEditRow(TimelineTrack Track, TimelineClip Clip)
    {
        public override string ToString() =>
            $"{TimeSpan.FromSeconds(Clip.StartSeconds):hh\\:mm\\:ss} – " +
            $"{TimeSpan.FromSeconds(Clip.StartSeconds + Clip.DurationSeconds):hh\\:mm\\:ss}  " +
            (Clip.Text?.Content.Replace('\n', ' ') ?? Clip.Name);
    }

    private void ShowPaperEditDialog()
    {
        PaperEditRow[] captions = _project.Tracks
            .Where(track => track.Kind != TrackKind.Audio && !track.IsHidden)
            .SelectMany(track => track.Clips.Where(IsSubtitleClip)
                .Select(clip => new PaperEditRow(track, clip)))
            .OrderBy(row => row.Clip.StartSeconds).ToArray();
        TimelineTrack[] mediaTracks = _project.Tracks
            .Where(track => (track.Kind is TrackKind.Audio or TrackKind.Video) &&
                !track.IsLocked && track.Clips.Any(clip => clip.Kind is ClipKind.Audio or ClipKind.Video))
            .OrderBy(track => track.Order).ToArray();
        if (captions.Length == 0 || mediaTracks.Length == 0)
        {
            StatusText.Text = "文本剪辑需要字幕和可编辑的视频或音频轨";
            return;
        }

        ProjectDocument sourceProject = _project;
        var dialog = new Window
        {
            Title = "基于文本的剪辑", Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Max(1, SystemParameters.PrimaryScreenWidth * .65),
            Height = Math.Max(1, SystemParameters.PrimaryScreenHeight * .72),
            Background = Brush("#191B20"), Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI")
        };
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var hint = new TextBlock
        {
            Text = "字幕时间码作为文本选择范围。Ctrl/Shift 可多选；双击一行预览对应位置。" +
                "“删除选中段”会切分目标轨道并保留其余内容；“生成粗剪”会新建轨道，按选中顺序排列内容。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush("#C5CAD4"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(hint);
        var trackRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        trackRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        trackRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock { Text = "目标轨道", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0) };
        var trackBox = new ComboBox { ItemsSource = mediaTracks,
            DisplayMemberPath = nameof(TimelineTrack.Name), SelectedIndex = 0 };
        Grid.SetColumn(trackBox, 1); trackRow.Children.Add(label); trackRow.Children.Add(trackBox);
        Grid.SetRow(trackRow, 1); root.Children.Add(trackRow);
        var list = new ListBox { ItemsSource = captions,
            SelectionMode = SelectionMode.Extended };
        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedItem is PaperEditRow row)
                SeekPreview(row.Clip.StartSeconds);
        };
        Grid.SetRow(list, 2); root.Children.Add(list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var remove = new Button { Content = "删除选中段", Padding = new Thickness(13, 6, 13, 6),
            Margin = new Thickness(0, 0, 8, 0) };
        var roughCut = new Button { Content = "按顺序生成粗剪", Padding = new Thickness(13, 6, 13, 6),
            Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "关闭", Padding = new Thickness(13, 6, 13, 6) };
        remove.Click += (_, _) => ApplyPaperEdit(sourceProject, trackBox.SelectedItem as TimelineTrack,
            list.SelectedItems.Cast<PaperEditRow>().ToArray(), keepSelected: false);
        roughCut.Click += (_, _) => ApplyPaperEdit(sourceProject, trackBox.SelectedItem as TimelineTrack,
            list.SelectedItems.Cast<PaperEditRow>().ToArray(), keepSelected: true);
        close.Click += (_, _) => dialog.Close();
        buttons.Children.Add(remove); buttons.Children.Add(roughCut); buttons.Children.Add(close);
        Grid.SetRow(buttons, 3); root.Children.Add(buttons);
        dialog.Content = root;
        dialog.ShowDialog();
    }

    private void ApplyPaperEdit(ProjectDocument sourceProject, TimelineTrack? sourceTrack,
        IReadOnlyList<PaperEditRow> selected, bool keepSelected)
    {
        if (!ReferenceEquals(sourceProject, _project) || sourceTrack is null ||
            !_project.Tracks.Contains(sourceTrack) || sourceTrack.IsLocked ||
            selected.Any(row => row.Track.Kind == TrackKind.Audio || !IsSubtitleClip(row.Clip) ||
                !double.IsFinite(row.Clip.StartSeconds) || row.Clip.StartSeconds < 0 ||
                !double.IsFinite(row.Clip.DurationSeconds) || row.Clip.DurationSeconds <= 0) ||
            selected.Any(row => !_project.Tracks.Contains(row.Track) || !row.Track.Clips.Contains(row.Clip)))
        {
            StatusText.Text = "文本剪辑已中止：项目或轨道发生变化";
            return;
        }
        if (selected.Count == 0)
        {
            StatusText.Text = "请先选择要处理的字幕段";
            return;
        }
        IReadOnlyList<PaperEditPiece> plan;
        double originalPlayhead = _project.Sequence.PlayheadSeconds;
        if (!double.IsFinite(originalPlayhead) || originalPlayhead < 0 ||
            originalPlayhead > 24 * 3600)
        {
            ReportPaperEditError(new PaperEditException(PaperEditStageCode.Validate,
                "播放头时间无效，无法创建粗剪。"));
            return;
        }
        try
        {
            plan = PaperEditPlanner.Plan(sourceTrack, selected.Select(row =>
                (row.Clip.StartSeconds, row.Clip.StartSeconds + row.Clip.DurationSeconds)),
                keepSelected);
        }
        catch (PaperEditException exception)
        {
            ReportPaperEditError(exception);
            return;
        }
        bool overlapsTarget = selected.Any(row =>
            double.IsFinite(row.Clip.StartSeconds) &&
            double.IsFinite(row.Clip.DurationSeconds) &&
            sourceTrack.Clips.Any(clip =>
                (sourceTrack.Kind == TrackKind.Video && clip.Kind == ClipKind.Video ||
                    sourceTrack.Kind == TrackKind.Audio && clip.Kind == ClipKind.Audio) &&
                clip.StartSeconds < row.Clip.StartSeconds + row.Clip.DurationSeconds &&
                row.Clip.StartSeconds < clip.StartSeconds + clip.DurationSeconds));
        if (plan.Count == 0 && (keepSelected || !overlapsTarget))
        {
            StatusText.Text = "所选字幕与目标轨道的媒体没有重叠";
            return;
        }
        var replacements = new List<TimelineClip>(plan.Count);
        double roughCutStart = originalPlayhead;
        try
        {
            double cursor = roughCutStart;
            foreach (PaperEditPiece piece in plan)
            {
                TimelineClip clone = CloneClip(piece.SourceClip);
                clone.Id = Guid.NewGuid().ToString("N");
                ClipAnimation.KeepRange(clone, piece.LocalStartSeconds, piece.DurationSeconds);
                // Text selections trim a camera cut just like an edge edit.
                // Keep its shared multicamera clock aligned with the source.
                AdvanceMulticamLocalStart(clone, piece.LocalStartSeconds);
                if (clone.TimeRemapPoints.Count > 0)
                    ClipTimeMap.KeepRange(clone, piece.LocalStartSeconds, piece.DurationSeconds);
                else if (clone.IsReversed)
                    clone.SourceInSeconds +=
                        (piece.SourceClip.DurationSeconds - piece.LocalStartSeconds - piece.DurationSeconds) * clone.Speed;
                else clone.SourceInSeconds += piece.LocalStartSeconds * clone.Speed;
                if (!double.IsFinite(clone.SourceInSeconds) || clone.SourceInSeconds < 0)
                    throw new PaperEditException(PaperEditStageCode.Validate,
                        "文本剪辑片段的源入点无效。");
                clone.DurationSeconds = piece.DurationSeconds;
                clone.StartSeconds = keepSelected ? cursor : piece.StartSeconds;
                clone.GroupId = null;
                clone.LinkGroupId = null;
                clone.AdditionalData?.Remove(AudioProcessing.RemixFadeInKey);
                clone.AdditionalData?.Remove(AudioProcessing.RemixFadeOutKey);
                // ClipAnimation.KeepRange inserts boundary values before
                // shifting keyframes, preserving the original envelope.
                clone.Transitions.Clear();
                replacements.Add(clone);
                if (keepSelected) cursor += piece.DurationSeconds;
            }
            if (keepSelected && cursor > 24 * 3600)
                throw new PaperEditException(PaperEditStageCode.Validate,
                    "粗剪结果超出项目允许的 24 小时时长。");
        }
        catch (PaperEditException exception)
        {
            ReportPaperEditError(exception);
            return;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            ReportPaperEditError(new PaperEditException(PaperEditStageCode.Read,
                "无法读取目标片段。", exception));
            return;
        }

        SaveUndoState();
        TimelineTrack destination;
        if (keepSelected)
        {
            destination = new TimelineTrack
            {
                Name = $"文本粗剪 {sourceTrack.Name}", Kind = sourceTrack.Kind,
                Clips = replacements
            };
            // Place the generated cut directly above its source. Appending a
            // video track would put it below the original footage, hiding the
            // new rough cut in both preview and export.
            _project.Tracks.Insert(_project.Tracks.IndexOf(sourceTrack), destination);
            for (int index = 0; index < _project.Tracks.Count; index++)
                _project.Tracks[index].Order = index;
        }
        else
        {
            destination = sourceTrack;
            // An empty plan is valid when selected text covers the complete
            // target. Preserve any other clip kind an imported track carries.
            sourceTrack.Clips.RemoveAll(clip => sourceTrack.Kind == TrackKind.Video
                ? clip.Kind == ClipKind.Video : clip.Kind == ClipKind.Audio);
            sourceTrack.Clips.AddRange(replacements);
        }
        _selectedTrack = destination;
        _contextTrack = destination;
        _selectedClip = replacements.FirstOrDefault();
        _selectedClips.Clear();
        if (_selectedClip is not null) _selectedClips.Add(_selectedClip);
        MarkProjectDirty(); RefreshTimeline();
        SeekPreview(keepSelected ? roughCutStart : replacements.FirstOrDefault()?.StartSeconds ??
            selected.Min(row => row.Clip.StartSeconds));
        StatusText.Text = keepSelected
            ? $"已按文本顺序生成 {replacements.Count} 段粗剪"
            : $"已按文本删除选中段，保留 {replacements.Count} 段媒体";
    }

    private void ReportPaperEditError(PaperEditException exception)
    {
        StatusText.Text = $"文本剪辑失败 · {exception.StageCode}";
        MessageBox.Show(this, $"{exception.Message}\n阶段代码：{exception.StageCode}",
            "文本剪辑失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
