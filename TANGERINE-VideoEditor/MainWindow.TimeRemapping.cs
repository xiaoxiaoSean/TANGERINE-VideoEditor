using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Edits local speed knots and frame interpolation. Knots map sequence time
/// to source time and are kept as project instructions, never source edits.
/// </summary>
public partial class MainWindow
{
    private void EditSelectedTimeRemapping()
    {
        TimelineClip? clip = _selectedClip;
        if (clip is null || clip.Kind is not (ClipKind.Video or ClipKind.Audio))
        { StatusText.Text = "请先选择视频或音频片段"; return; }
        if (_selectedTrack?.IsLocked != false)
        { StatusText.Text = "轨道已锁定，无法修改速度"; return; }
        if (clip.MulticamGroupId is not null)
        { StatusText.Text = "多机位切段需保持原速 · MULTICAM-EDIT"; return; }
        var dialog = new Window
        {
            Title = $"时间重映射 · {clip.Name}", Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Max(1, SystemParameters.PrimaryScreenWidth * .42),
            Height = Math.Max(1, SystemParameters.PrimaryScreenHeight * .52),
            Background = Brush("#191B20"), Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI")
        };
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var hint = new TextBlock { Text = "时间从片段起点计算。速度节点控制其后区间的播放速率。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        root.Children.Add(hint);
        var controls = new WrapPanel { Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 10) };
        var time = new TextBox { Text = Math.Clamp(_project.Sequence.PlayheadSeconds - clip.StartSeconds, 0,
            clip.DurationSeconds).ToString("0.###", CultureInfo.CurrentCulture),
            MinWidth = dialog.Width * .13, Margin = new Thickness(0, 0, 8, 0),
            Foreground = Brushes.White, Background = Brush("#292B31"), CaretBrush = Brushes.White };
        var speed = new TextBox { Text = ClipTimeMap.SpeedAt(clip,
            Math.Clamp(_project.Sequence.PlayheadSeconds - clip.StartSeconds, 0, clip.DurationSeconds))
            .ToString("0.###", CultureInfo.CurrentCulture), MinWidth = dialog.Width * .13,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = Brushes.White, Background = Brush("#292B31"), CaretBrush = Brushes.White };
        controls.Children.Add(new TextBlock { Text = "时间（秒）", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0) });
        controls.Children.Add(time);
        controls.Children.Add(new TextBlock { Text = "速度（倍）", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0) });
        controls.Children.Add(speed);
        var add = new Button { Content = "添加 / 更新", Padding = new Thickness(10, 5, 10, 5) };
        controls.Children.Add(add);
        Grid.SetRow(controls, 1); root.Children.Add(controls);
        var list = new ListBox { Background = Brush("#24262C"), Foreground = Brushes.White };
        Grid.SetRow(list, 2); root.Children.Add(list);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0) };
        var interpolation = new ComboBox { ItemsSource = new[] { "帧采样", "帧混合", "光流" },
            SelectedIndex = clip.FrameInterpolation switch { "Blend" => 1, "OpticalFlow" => 2, _ => 0 },
            Margin = new Thickness(0, 0, 9, 0), MinWidth = dialog.Width * .2,
            Foreground = Brushes.White, Background = Brush("#292B31") };
        var remove = new Button { Content = "删除所选节点", Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "关闭", Padding = new Thickness(16, 5, 16, 5) };
        bottom.Children.Add(interpolation); bottom.Children.Add(remove); bottom.Children.Add(close);
        Grid.SetRow(bottom, 3); root.Children.Add(bottom);
        dialog.Content = root;

        void Refresh()
        {
            list.ItemsSource = clip.TimeRemapPoints.OrderBy(point => point.TimeSeconds)
                .Select(point => new TimeRemapRow(point,
                    $"{point.TimeSeconds:0.###} 秒  →  {point.Speed:0.###} 倍"))
                .ToArray();
            RefreshTimeline();
            SeekPreview(_project.Sequence.PlayheadSeconds);
        }
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not TimeRemapRow row) return;
            time.Text = row.Point.TimeSeconds.ToString("0.###", CultureInfo.CurrentCulture);
            speed.Text = row.Point.Speed.ToString("0.###", CultureInfo.CurrentCulture);
        };
        add.Click += (_, _) =>
        {
            if (!_project.Tracks.Any(track => !track.IsLocked && track.Clips.Contains(clip)))
            {
                StatusText.Text = "片段或轨道已变化 · EDIT-REMAP-STATE";
                return;
            }
            if (!double.TryParse(time.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double at) ||
                !double.IsFinite(at) || at < 0 || at >= clip.DurationSeconds ||
                !double.TryParse(speed.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double rate) ||
                !double.IsFinite(rate) || rate < .05 || rate > 32)
            {
                MessageBox.Show(dialog, "时间需在片段内部，速度需在 0.05 至 32 倍之间。\n阶段代码：EDIT-REMAP-VALUE",
                    "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // The project format requires unique knot times. Editing an
            // existing row is permitted within 0.1 ms; a different knot
            // inside the validation tolerance must be rejected.
            if (clip.TimeRemapPoints.Count(point => Math.Abs(point.TimeSeconds - at) < .0001) > 1)
            {
                StatusText.Text = "速度节点重叠 · EDIT-REMAP-DUPLICATE";
                return;
            }
            ClipTimeRemapPoint? selectedPoint = (list.SelectedItem as TimeRemapRow)?.Point;
            if (selectedPoint is not null && clip.TimeRemapPoints.Any(point =>
                !ReferenceEquals(point, selectedPoint) && Math.Abs(point.TimeSeconds - at) < .0001))
            {
                StatusText.Text = "目标时间已有速度节点 · EDIT-REMAP-DUPLICATE";
                return;
            }
            var proposed = new TimelineClip
            {
                DurationSeconds = clip.DurationSeconds,
                Speed = clip.Speed,
                SourceInSeconds = clip.SourceInSeconds,
                TimeRemapPoints = CloneTimeRemap(clip.TimeRemapPoints)
            };
            int selectedIndex = selectedPoint is null ? -1 : clip.TimeRemapPoints.IndexOf(selectedPoint);
            ClipTimeRemapPoint? proposedPoint = selectedIndex >= 0
                ? proposed.TimeRemapPoints[selectedIndex]
                : proposed.TimeRemapPoints.FirstOrDefault(item => Math.Abs(item.TimeSeconds - at) < .0001);
            if (proposedPoint is null)
                proposed.TimeRemapPoints.Add(new ClipTimeRemapPoint { TimeSeconds = at, Speed = rate });
            else { proposedPoint.TimeSeconds = at; proposedPoint.Speed = rate; }
            if (selectedPoint is not null &&
                Math.Abs(selectedPoint.TimeSeconds - at) < .000001 &&
                Math.Abs(selectedPoint.Speed - rate) < .000001)
                return;
            // Mirror the project validator before changing live state. A
            // knot closer than its persisted tolerance would make .tvep
            // saving fail even if its exact timestamps differ.
            ClipTimeRemapPoint[] ordered = proposed.TimeRemapPoints
                .OrderBy(point => point.TimeSeconds).ToArray();
            if (ordered.Zip(ordered.Skip(1), (left, right) =>
                    right.TimeSeconds - left.TimeSeconds).Any(gap => gap < .000001))
            {
                StatusText.Text = "速度节点过近 · EDIT-REMAP-DUPLICATE";
                return;
            }
            MediaAsset? source = _project.Media.FirstOrDefault(item => item.Id == clip.MediaAssetId);
            double sourceSpan = ClipTimeMap.SourceOffset(proposed, proposed.DurationSeconds);
            if (source?.DurationSeconds is double available &&
                proposed.SourceInSeconds + sourceSpan > available + .001)
            {
                StatusText.Text = "速度节点超出源素材范围 · EDIT-REMAP-SOURCE";
                return;
            }
            if (!double.IsFinite(sourceSpan))
            {
                StatusText.Text = "速度节点时间映射无效 · EDIT-REMAP-SOURCE";
                return;
            }
            SaveUndoState();
            ClipTimeRemapPoint? point = selectedIndex >= 0 ? selectedPoint :
                clip.TimeRemapPoints.FirstOrDefault(item => Math.Abs(item.TimeSeconds - at) < .0001);
            if (point is null) clip.TimeRemapPoints.Add(new ClipTimeRemapPoint { TimeSeconds = at, Speed = rate });
            else { point.TimeSeconds = at; point.Speed = rate; }
            MarkProjectDirty(); Refresh();
        };
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is not TimeRemapRow row) return;
            if (!_project.Tracks.Any(track => !track.IsLocked && track.Clips.Contains(clip)) ||
                !clip.TimeRemapPoints.Contains(row.Point)) return;
            SaveUndoState(); clip.TimeRemapPoints.Remove(row.Point);
            MarkProjectDirty(); Refresh();
        };
        interpolation.SelectionChanged += (_, _) =>
        {
            if (!_project.Tracks.Any(track => !track.IsLocked && track.Clips.Contains(clip))) return;
            string selected = interpolation.SelectedIndex switch { 1 => "Blend", 2 => "OpticalFlow", _ => "Sampling" };
            if (clip.FrameInterpolation == selected) return;
            SaveUndoState(); clip.FrameInterpolation = selected;
            MarkProjectDirty(); SeekPreview(_project.Sequence.PlayheadSeconds);
        };
        close.Click += (_, _) => dialog.Close();
        Refresh();
        dialog.ShowDialog();
    }

    private sealed record TimeRemapRow(ClipTimeRemapPoint Point, string Label)
    {
        public override string ToString() => Label;
    }
}
