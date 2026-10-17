using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Creates local generator clips directly at the playhead. These contain no
/// external asset path; the existing preview and export pipelines render them
/// from their saved project color or effect settings.
/// </summary>
public partial class MainWindow
{
    private void InitializeGeneratedClipCommands()
    {
        if (SpeedRampMenuItem.Parent is MenuItem clipItems)
        {
            var adjustment = new MenuItem { Header = "新增调整图层" };
            adjustment.Click += (_, _) => AddGeneratedClip(ClipKind.Adjustment);
            clipItems.Items.Add(adjustment);
            var color = new MenuItem { Header = "新增颜色片段…" };
            color.Click += (_, _) => AddGeneratedClip(ClipKind.Color);
            clipItems.Items.Add(color);
        }
        if (TimelineCanvas.ContextMenu is ContextMenu context)
        {
            AddContextCommand(context, "新增调整图层", () => AddGeneratedClip(ClipKind.Adjustment));
            AddContextCommand(context, "新增颜色片段…", () => AddGeneratedClip(ClipKind.Color));
        }
    }

    private void AddGeneratedClip(ClipKind kind)
    {
        if (kind is not (ClipKind.Color or ClipKind.Adjustment)) return;
        double start = _project.Sequence.PlayheadSeconds;
        if (!double.IsFinite(start) || start < 0 ||
            !double.IsFinite(_project.Sequence.FramesPerSecond) ||
            _project.Sequence.FramesPerSecond <= 0)
        {
            StatusText.Text = "播放头或序列帧率无效 · GEN-RANGE";
            return;
        }
        TimelineTrack? track = _selectedTrack is { IsLocked: false, Kind: not TrackKind.Audio }
            ? _selectedTrack : _project.Tracks.FirstOrDefault(candidate =>
                candidate.Kind != TrackKind.Audio && !candidate.IsLocked &&
                candidate.Clips.All(clip => clip.StartSeconds > start ||
                    clip.StartSeconds + clip.DurationSeconds <= start));
        track ??= ResolveDropTarget(kind, -1);
        if (track is null || track.IsLocked || track.Kind == TrackKind.Audio)
        {
            StatusText.Text = "没有未锁定的画面轨道 · GEN-TARGET";
            return;
        }
        double duration = 5;
        TimelineClip? next = track.Clips.Where(clip => clip.StartSeconds > start)
            .OrderBy(clip => clip.StartSeconds).FirstOrDefault();
        if (next is not null)
            duration = Math.Min(duration, next.StartSeconds - start);
        double frame = 1 / _project.Sequence.FramesPerSecond;
        if (duration < frame || track.Clips.Any(clip =>
            clip.StartSeconds <= start && clip.StartSeconds + clip.DurationSeconds > start))
        {
            StatusText.Text = "播放头所在轨道没有可用区间 · GEN-RANGE";
            return;
        }
        string color = "#30343D";
        if (kind == ClipKind.Color && !TryChooseGeneratedColor(out color)) return;
        var generated = new TimelineClip
        {
            Kind = kind,
            Name = kind == ClipKind.Color ? "颜色片段" : "调整图层",
            StartSeconds = start,
            DurationSeconds = duration,
            Color = kind == ClipKind.Color ? color : null
        };
        if (track.Clips.Any(clip => clip.StartSeconds < start + duration &&
            clip.StartSeconds + clip.DurationSeconds > start))
        {
            StatusText.Text = "轨道在此区间发生变化，请重新创建 · GEN-RANGE";
            return;
        }
        SaveUndoState();
        track.Clips.Add(generated);
        SelectClip(generated);
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(start);
        StatusText.Text = kind == ClipKind.Color ? "已新增颜色片段" : "已新增调整图层";
        if (kind == ClipKind.Adjustment) EditSelectedEffects();
    }

    private bool TryChooseGeneratedColor(out string color)
    {
        color = "#30343D";
        var dialog = new Window
        {
            Title = "新增颜色片段", Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = SystemParameters.PrimaryScreenWidth * .3,
            Height = SystemParameters.PrimaryScreenHeight * .24,
            Background = Brush("#191B20"), Foreground = Brushes.White,
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel { Margin = new Thickness(dialog.Width * .06) };
        panel.Children.Add(new TextBlock
        {
            Text = "输入 RGB 十六进制颜色（例如 #FF9957）",
            TextWrapping = TextWrapping.Wrap
        });
        var input = new TextBox
        {
            Text = color,
            Margin = new Thickness(0, dialog.Height * .055, 0, 0)
        };
        panel.Children.Add(input);
        var swatch = new Border
        {
            Height = dialog.Height * .13,
            Background = Brush(color),
            Margin = new Thickness(0, dialog.Height * .055, 0, 0)
        };
        panel.Children.Add(swatch);
        input.TextChanged += (_, _) =>
        {
            if (IsRgbHex(input.Text)) swatch.Background = Brush(input.Text);
        };
        var add = new Button
        {
            Content = "创建",
            Margin = new Thickness(0, dialog.Height * .055, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        add.Click += (_, _) =>
        {
            if (!IsRgbHex(input.Text))
            {
                StatusText.Text = "颜色需为 #RRGGBB 格式 · GEN-COLOR";
                return;
            }
            dialog.DialogResult = true;
        };
        panel.Children.Add(add);
        dialog.Content = panel;
        if (dialog.ShowDialog() != true) return false;
        color = input.Text.ToUpperInvariant();
        return true;
    }

    private static bool IsRgbHex(string text) => text.Length == 7 && text[0] == '#' &&
        text.Skip(1).All(Uri.IsHexDigit);
}
