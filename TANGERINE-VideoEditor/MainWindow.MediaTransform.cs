using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Edits the persistent frame-space transform of image and video clips.
/// Crop values are percentages of the source, while position is measured in
/// sequence pixels. Both preview and export read the same ClipTransform data.
/// </summary>
public partial class MainWindow
{
    private void EditMediaTransform(TimelineClip clip)
    {
        OpenObjectToolPage(clip, "变换 / 裁切", BuildTransformTool);
        return;
#pragma warning disable CS0162
        TimelineTrack? track = _project.Tracks.FirstOrDefault(candidate => candidate.Clips.Contains(clip));
        if (track is null || track.IsLocked)
        {
            StatusText.Text = track is null ? "片段已不在当前项目中" : "轨道已锁定，无法编辑画面";
            return;
        }
        if (clip.Kind is not (ClipKind.Image or ClipKind.Video))
        {
            StatusText.Text = "请先选择图片或视频片段 · VIS-TRANSFORM-KIND";
            return;
        }
        ProjectDocument dialogProject = _project;
        long dialogGeneration = _projectGeneration;
        var current = clip.Transform;
        double editTime = _project.Sequence.PlayheadSeconds;
        var dialog = new Window
        {
            Title = clip.Kind == ClipKind.Image ? "图片变换与裁切" : "视频变换与裁切",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("#191B20"),
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Width = Math.Max(1, SystemParameters.PrimaryScreenWidth * 0.36),
            Height = Math.Max(1, SystemParameters.PrimaryScreenHeight * 0.70),
            ResizeMode = ResizeMode.CanResize
        };
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55, GridUnitType.Star) });
        int row = 0;
        var tip = new TextBlock
        {
            Text = "缩放 X/Y 可分别拉伸画面。裁切从源画面的四边向内裁去；位置以序列画面中心为原点。锚点位于裁切后画面的相对位置。",
            Foreground = Brush("#BFC3CD"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        };
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(tip, row++);
        Grid.SetColumnSpan(tip, 2);
        form.Children.Add(tip);

        var positionX = AddTextBox(form, ref row, "位置 X（像素）",
            Editor.ClipAnimation.Evaluate(clip, "PositionX", editTime).ToString(CultureInfo.CurrentCulture));
        var positionY = AddTextBox(form, ref row, "位置 Y（像素）",
            Editor.ClipAnimation.Evaluate(clip, "PositionY", editTime).ToString(CultureInfo.CurrentCulture));
        var scaleX = AddTextBox(form, ref row, "水平缩放 / 拉伸（%）",
            (Editor.ClipAnimation.Evaluate(clip, "ScaleX", editTime) * 100).ToString("0.##", CultureInfo.CurrentCulture));
        var scaleY = AddTextBox(form, ref row, "垂直缩放 / 拉伸（%）",
            (Editor.ClipAnimation.Evaluate(clip, "ScaleY", editTime) * 100).ToString("0.##", CultureInfo.CurrentCulture));
        var rotation = AddTextBox(form, ref row, "旋转角度（°）",
            Editor.ClipAnimation.Evaluate(clip, "RotationDegrees", editTime).ToString("0.##", CultureInfo.CurrentCulture));
        var anchorX = AddTextBox(form, ref row, "锚点 X（%）", (current.AnchorX * 100).ToString("0.##", CultureInfo.CurrentCulture));
        var anchorY = AddTextBox(form, ref row, "锚点 Y（%）", (current.AnchorY * 100).ToString("0.##", CultureInfo.CurrentCulture));
        var cropLeft = AddTextBox(form, ref row, "左侧裁切（%）", (current.CropLeft * 100).ToString("0.##", CultureInfo.CurrentCulture));
        var cropTop = AddTextBox(form, ref row, "上方裁切（%）", (current.CropTop * 100).ToString("0.##", CultureInfo.CurrentCulture));
        var cropRight = AddTextBox(form, ref row, "右侧裁切（%）", (current.CropRight * 100).ToString("0.##", CultureInfo.CurrentCulture));
        var cropBottom = AddTextBox(form, ref row, "下方裁切（%）", (current.CropBottom * 100).ToString("0.##", CultureInfo.CurrentCulture));
        var opacity = AddTextBox(form, ref row, "画面透明度（%）",
            (Editor.ClipAnimation.Evaluate(clip, "Opacity", editTime) * 100).ToString("0.##", CultureInfo.CurrentCulture));

        var resetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 6) };
        var resetTransform = new Button { Content = "重置位置 / 缩放 / 旋转 / 锚点", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
        resetTransform.Click += (_, _) =>
        {
            positionX.Text = "0"; positionY.Text = "0";
            scaleX.Text = "100"; scaleY.Text = "100"; rotation.Text = "0";
            anchorX.Text = "50"; anchorY.Text = "50";
        };
        var resetCrop = new Button { Content = "重置裁切", Padding = new Thickness(10, 6, 10, 6) };
        resetCrop.Click += (_, _) =>
        {
            cropLeft.Text = "0"; cropTop.Text = "0"; cropRight.Text = "0"; cropBottom.Text = "0";
        };
        resetRow.Children.Add(resetTransform);
        resetRow.Children.Add(resetCrop);
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(resetRow, row);
        Grid.SetColumnSpan(resetRow, 2);
        form.Children.Add(resetRow);

        var scroller = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroller);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "取消", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var apply = new Button { Content = "应用", Padding = new Thickness(16, 6, 16, 6), Background = Brush("#FF9957"), Foreground = Brushes.Black };
        apply.Click += (_, _) =>
        {
            if (!ReferenceEquals(dialogProject, _project) || dialogGeneration != _projectGeneration)
            {
                StatusText.Text = "画面编辑已中止：项目已经切换 · VIS-TRANSFORM-STALE";
                dialog.DialogResult = false;
                return;
            }
            TimelineTrack? currentTrack = _project.Tracks.FirstOrDefault(candidate => candidate.Clips.Contains(clip));
            if (currentTrack is null || currentTrack.IsLocked)
            {
                StatusText.Text = currentTrack is null ? "片段已不在当前项目中" : "轨道已锁定，无法编辑画面";
                return;
            }
            if (!TryNumber(positionX.Text, -_project.Sequence.Width * 4, _project.Sequence.Width * 4, "位置 X", out double x) ||
                !TryNumber(positionY.Text, -_project.Sequence.Height * 4, _project.Sequence.Height * 4, "位置 Y", out double y) ||
                !TryNumber(scaleX.Text, 5, 800, "水平缩放", out double sx) ||
                !TryNumber(scaleY.Text, 5, 800, "垂直缩放", out double sy) ||
                !TryNumber(rotation.Text, -36000, 36000, "旋转角度", out double angle) ||
                !TryNumber(anchorX.Text, 0, 100, "锚点 X", out double ax) ||
                !TryNumber(anchorY.Text, 0, 100, "锚点 Y", out double ay) ||
                !TryNumber(cropLeft.Text, 0, 95, "左侧裁切", out double left) ||
                !TryNumber(cropTop.Text, 0, 95, "上方裁切", out double top) ||
                !TryNumber(cropRight.Text, 0, 95, "右侧裁切", out double right) ||
                !TryNumber(cropBottom.Text, 0, 95, "下方裁切", out double bottom) ||
                !TryNumber(opacity.Text, 0, 100, "画面透明度", out double alpha)) return;
            if (left + right >= 100 || top + bottom >= 100)
            {
                MessageBox.Show(this, "左右裁切之和、上下裁切之和都必须小于 100%。\n阶段代码：VIS-TRANSFORM-CROP", "裁切范围无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SaveUndoState();
            ShiftAnimatedVisualProperty(clip, "PositionX", x);
            ShiftAnimatedVisualProperty(clip, "PositionY", y);
            ShiftAnimatedVisualProperty(clip, "ScaleX", sx / 100);
            ShiftAnimatedVisualProperty(clip, "ScaleY", sy / 100);
            ShiftAnimatedVisualProperty(clip, "RotationDegrees", angle);
            ShiftAnimatedVisualProperty(clip, "Opacity", alpha / 100);
            current.AnchorX = ax / 100;
            current.AnchorY = ay / 100;
            current.CropLeft = left / 100;
            current.CropTop = top / 100;
            current.CropRight = right / 100;
            current.CropBottom = bottom / 100;
            MarkProjectDirty();
            RefreshTimeline();
            SeekPreview(_project.Sequence.PlayheadSeconds);
            StatusText.Text = $"已更新画面：{clip.Name}";
            dialog.DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
        dialog.Content = root;
        dialog.ShowDialog();
#pragma warning restore CS0162
    }
}
