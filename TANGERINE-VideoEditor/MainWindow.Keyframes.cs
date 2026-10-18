using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Provides a precise keyframe editor. Keyframe times are clip-relative, so
/// moving a clip preserves its animation and future project versions can add
/// properties without invalidating existing .tvep animation records.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Shows a settings page in the left area. Every page scrolls vertically:
    /// text, effect and audio pages hold far more fields than the area is tall,
    /// and without a scroll viewer the lower fields were simply cut off. A page
    /// that manages its own scrolling (a virtualized list) passes
    /// <paramref name="selfScrolling"/>, because a scroll viewer gives its child
    /// unlimited height, which would defeat the list's virtualization.
    /// </summary>
    private void OpenSettingsCategory(string name, UIElement content, bool selfScrolling = false)
    {
        _mcpInspectorRefresh = null;
        CategoryList.ItemsSource = _categories.Concat(new[] { name }).ToArray();
        CategoryList.SelectedIndex = _categories.Length;
        LibraryContentGrid.Visibility = Visibility.Collapsed;
        AssetList.Visibility = Visibility.Collapsed;
        EmptyLibraryMessage.Visibility = Visibility.Collapsed;
        SettingsHost.Content = selfScrolling ? content : new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            // Width is the area's own; a horizontal bar would only appear when a
            // field overflowed, and wrapping text is the better answer to that.
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false
        };
        SettingsHost.Visibility = Visibility.Visible;
        LibraryTitle.Text = name;
        LibrarySubtitle.Text = "设置会立即作用于当前选中的对象";
        LibraryAddButton.Content = "关闭";
        LibraryAddButton.Click -= CloseSettingsTabsHandler;
        LibraryAddButton.Click += CloseSettingsTabsHandler;
    }

    private void CloseSettingsTabsHandler(object sender, RoutedEventArgs args) => CloseSettingsTabs();

    private void OpenKeyframeSettingsTab(TimelineClip clip, TimelineTrack track, ClipKeyframe key)
    {
        var panel = new StackPanel { Margin = new Thickness(4) };
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock { Text = $"关键帧设置 · {KeyframePropertyName(key.Property)}",
            FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var closeTop = new Button { Content = "关闭", Padding = new Thickness(10, 3, 10, 3),
            Foreground = Brushes.White, Background = Brush("#292B31"), BorderBrush = Brush("#626874") };
        closeTop.Click += (_, _) => CloseSettingsTabs();
        DockPanel.SetDock(closeTop, Dock.Right); header.Children.Add(closeTop);
        panel.Children.Add(header);
        var property = new ComboBox
        {
            ItemsSource = ClipAnimation.Properties.Where(item => clip.Kind == ClipKind.Audio ? item.Id == "Volume" : item.Id != "Volume")
                .Select(item => item.Name).ToArray(),
            SelectedItem = ClipAnimation.Properties.FirstOrDefault(item => item.Id == key.Property).Name,
            Margin = new Thickness(0, 0, 0, 8)
        };
        panel.Children.Add(new TextBlock { Text = "属性", Foreground = Brush("#C5CAD4") }); panel.Children.Add(property);
        var value = new TextBox { Text = key.Value.ToString("0.###", CultureInfo.CurrentCulture), Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(new TextBlock { Text = "数值", Foreground = Brush("#C5CAD4") }); panel.Children.Add(value);
        var type = new ComboBox { ItemsSource = new[] { "突然切换", "线性", "保持", "缓入缓出", "贝塞尔" }, Margin = new Thickness(0, 0, 0, 8) };
        type.SelectedIndex = key.Interpolation switch { "Linear" => 1, "Hold" => 2, "EaseInOut" => 3, "Bezier" => 4, _ => 0 };
        ClipKeyframe? previous = clip.Keyframes.Where(item => item.Property == key.Property && item.TimeSeconds < key.TimeSeconds)
            .OrderByDescending(item => item.TimeSeconds).FirstOrDefault();
        type.IsEnabled = previous is not null;
        panel.Children.Add(new TextBlock { Text = previous is null ? "动画类型：前面没有同类关键帧，只能突然切换" : "动画类型", Foreground = Brush("#C5CAD4") });
        panel.Children.Add(type);
        var apply = new Button { Content = "确认并立即应用", Padding = new Thickness(12, 7, 12, 7), Background = Brush("#FF9957"), Foreground = Brushes.Black };
        var close = new Button { Content = "关闭设置", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 8, 0, 0) };
        apply.Click += (_, _) =>
        {
            if (!double.TryParse(value.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsed) || !double.IsFinite(parsed))
            { StatusText.Text = "关键帧数值无效"; return; }
            string selectedName = property.SelectedItem as string ?? KeyframePropertyName(key.Property);
            string selectedProperty = ClipAnimation.Properties.FirstOrDefault(item => item.Name == selectedName).Id;
            SaveUndoState(); key.Property = selectedProperty; key.Value = parsed;
            key.Interpolation = type.SelectedIndex switch { 1 => "Linear", 2 => "Hold", 3 => "EaseInOut", 4 => "Bezier", _ => "Linear" };
            MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
            StatusText.Text = "关键帧设置已立即生效";
        };
        close.Click += (_, _) => CloseSettingsTabs();
        panel.Children.Add(apply); panel.Children.Add(close);
        OpenSettingsCategory("关键帧设置", panel);
        string clipId = clip.Id, keyId = key.Id;
        _mcpInspectorRefresh = () =>
        {
            TimelineClip current = McpClip(clipId);
            ClipKeyframe? updated = current.Keyframes.FirstOrDefault(item => item.Id == keyId);
            if (updated is null) ShowSelectedObjectSettings();
            else { _selectedKeyframe = updated; OpenKeyframeSettingsTab(current, McpOwner(current), updated); }
        };
    }

    private void CloseSettingsTabs()
    {
        _mcpInspectorRefresh = null;
        SettingsHost.Content = null;
        SettingsHost.Visibility = Visibility.Collapsed;
        LibraryContentGrid.Visibility = Visibility.Visible;
        AssetList.Visibility = Visibility.Visible;
        CategoryList.ItemsSource = _categories;
        CategoryList.SelectedIndex = 0;
    }
    private void InitializeKeyframeCommands()
    {
        TimelineKeyframeItem.Click += (_, _) => AddKeyframeAtPlayhead();
        TimelineKeyframeItem.IsEnabled = true;
    }

    private void AddKeyframeAtPlayhead()
    {
        AddKeyframeAtPoint(new Point(HeaderSpace + _project.Sequence.PlayheadSeconds * PixelsPerSecond, RulerSpace));
    }

    private void AddKeyframeAtPoint(Point point)
    {
        TimelineTrack? pointedTrack = TrackAt(point.Y);
        TimelineClip? clip = pointedTrack?.Clips.FirstOrDefault(item =>
            point.X >= HeaderSpace + item.StartSeconds * PixelsPerSecond &&
            point.X <= HeaderSpace + (item.StartSeconds + item.DurationSeconds) * PixelsPerSecond);
        clip ??= _selectedClip;
        TimelineTrack? track = clip is null ? null : _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
        if (clip is null || track is null)
        {
            StatusText.Text = "请先选择片段，再在播放头添加关键帧";
            return;
        }
        if (track.IsLocked) { StatusText.Text = "轨道已锁定，无法添加关键帧"; return; }
        string property = (_selectedKeyframe is not null && ReferenceEquals(_selectedClip, clip) ? _selectedKeyframe.Property : null) ?? (clip.Kind == ClipKind.Audio ? "Volume" :
            clip.Kind == ClipKind.Text ? "PositionX" : "Opacity");
        double local = Math.Clamp((point.X - HeaderSpace) / PixelsPerSecond - clip.StartSeconds, 0, clip.DurationSeconds);
        double fps = Math.Max(1, _project.Sequence.FramesPerSecond);
        local = Math.Clamp(Math.Round(local * fps) / fps, 0, clip.DurationSeconds);
        ClipKeyframe? key = clip.Keyframes.FirstOrDefault(item => item.Property == property &&
            Math.Abs(item.TimeSeconds - local) < .0001);
        SaveUndoState();
        if (key is null)
        {
            key = new ClipKeyframe { Property = property, TimeSeconds = local,
                Value = ClipAnimation.Evaluate(clip, property, _project.Sequence.PlayheadSeconds) };
            clip.Keyframes.Add(key);
        }
        _selectedKeyframe = key;
        MarkProjectDirty(); RefreshTimeline();
        OpenKeyframeSettingsTab(clip, track, key);
    }

    private void EditSelectedKeyframes()
    {
        TimelineClip? clip = _selectedClip;
        if (clip is null)
        {
            StatusText.Text = "请先在时间线上选择片段";
            return;
        }
        TimelineTrack? track = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
        if (track is null || track.IsLocked)
        {
            StatusText.Text = track is null ? "片段已不在当前项目中" : "轨道已锁定，无法编辑关键帧";
            return;
        }
        if (_selectedKeyframe is null)
        {
            AddKeyframeAtPlayhead();
            return;
        }
        OpenKeyframeSettingsTab(clip, track, _selectedKeyframe);
    }
    private static string InterpolationName(string value) => value switch
    { "Hold" => "保持", "EaseInOut" => "缓入缓出", "Bezier" => "贝塞尔", _ => "线性" };

    private static bool TryBezierHandleNumber(string text, double minimum, double maximum,
        out double value) => double.TryParse(text, NumberStyles.Float,
        CultureInfo.CurrentCulture, out value) && double.IsFinite(value) &&
        value >= minimum && value <= maximum;

    private sealed record KeyframeRow(ClipKeyframe Keyframe, string Label)
    {
        public override string ToString() => Label;
    }
}

