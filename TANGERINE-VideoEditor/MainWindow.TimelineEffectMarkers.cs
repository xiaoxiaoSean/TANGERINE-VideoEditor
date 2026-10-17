using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Draws visual markers for effects applied to timeline clips. Each marker
/// shows the effect count and provides quick access to edit or remove effects.
/// </summary>
public partial class MainWindow
{
    private void DrawClipEffectMarkers(TimelineTrack track, TimelineClip clip,
        double left, double top, double clipWidth)
    {
        if (clip.Effects.Count == 0) return;
        if (clipWidth < 30) return; // Too narrow to show marker

        double iconSize = Math.Min(TrackSpace * 0.20, 16);
        double margin = TrackSpace * 0.10;

        // Draw effect marker in top-right corner
        var effectBadge = new Border
        {
            Width = iconSize,
            Height = iconSize,
            Background = Brush("#A78BFA"), // Purple for effects
            CornerRadius = new CornerRadius(iconSize / 2),
            BorderBrush = Brush("#8B5CF6"),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = clip.Effects.Count.ToString(),
                Foreground = Brushes.White,
                FontSize = Math.Max(8, iconSize * 0.55),
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Cursor = Cursors.Hand,
            ToolTip = BuildEffectTooltip(clip),
            Tag = (track, clip)
        };

        Canvas.SetLeft(effectBadge, left + clipWidth - iconSize - margin);
        Canvas.SetTop(effectBadge, top + margin);
        Canvas.SetZIndex(effectBadge, 15);

        effectBadge.MouseLeftButtonDown += EffectBadge_Click;
        effectBadge.ContextMenu = BuildEffectContextMenu(track, clip);

        TimelineCanvas.Children.Add(effectBadge);
    }

    private string BuildEffectTooltip(TimelineClip clip)
    {
        var lines = new List<string> { $"{clip.Effects.Count} 个效果" };
        foreach (var effect in clip.Effects.Take(5))
        {
            lines.Add($"• {effect.Name}");
        }
        if (clip.Effects.Count > 5)
        {
            lines.Add($"...还有 {clip.Effects.Count - 5} 个");
        }
        lines.Add("\n左键：编辑效果");
        lines.Add("右键：管理效果");
        return string.Join("\n", lines);
    }

    private void EffectBadge_Click(object sender, MouseButtonEventArgs args)
    {
        if (sender is not Border badge || badge.Tag is not (TimelineTrack track, TimelineClip clip))
            return;

        args.Handled = true;
        _selectedClip = clip;
        _selectedTrack = track;
        EditSelectedEffects();
        DrawTimeline();
    }

    private ContextMenu BuildEffectContextMenu(TimelineTrack track, TimelineClip clip)
    {
        var menu = new ContextMenu
        {
            Background = Brush("#24262C"),
            Foreground = Brushes.White
        };

        if (clip.Effects.Count > 0)
        {
            AddContextCommand(menu, "编辑所有效果…", () =>
            {
                _selectedClip = clip;
                _selectedTrack = track;
                EditSelectedEffects();
            });

            menu.Items.Add(new Separator { Background = Brush("#34363D") });

            foreach (var effect in clip.Effects)
            {
                var effectItem = new MenuItem
                {
                    Header = effect.Name,
                    Background = Brush("#24262C"),
                    Foreground = Brushes.White
                };

                var editItem = new MenuItem
                {
                    Header = "编辑参数…",
                    Background = Brush("#24262C"),
                    Foreground = Brushes.White
                };
                editItem.Click += (_, _) =>
                {
                    _selectedClip = clip;
                    _selectedTrack = track;
                    EditSelectedEffects();
                };
                effectItem.Items.Add(editItem);

                var removeItem = new MenuItem
                {
                    Header = "删除此效果",
                    Background = Brush("#24262C"),
                    Foreground = Brushes.White
                };
                removeItem.Click += (_, _) =>
                {
                    SaveUndoState();
                    clip.Effects.Remove(effect);
                    MarkProjectDirty();
                    DrawTimeline();
                    SeekPreview(_project.Sequence.PlayheadSeconds);
                    StatusText.Text = $"已删除效果：{effect.Name}";
                };
                effectItem.Items.Add(removeItem);

                menu.Items.Add(effectItem);
            }

            menu.Items.Add(new Separator { Background = Brush("#34363D") });

            AddContextCommand(menu, "清除所有效果", () =>
            {
                SaveUndoState();
                int count = clip.Effects.Count;
                clip.Effects.Clear();
                MarkProjectDirty();
                DrawTimeline();
                SeekPreview(_project.Sequence.PlayheadSeconds);
                StatusText.Text = $"已清除 {count} 个效果";
            });
        }

        return menu;
    }
}
