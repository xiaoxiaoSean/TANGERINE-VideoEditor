using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;
#pragma warning disable CS8602, CS8604

namespace TANGERINE_VideoEditor;

/// <summary>
/// Presents local visual effects, keying, masking, color controls, LUTs and
/// scopes. Every edit is persisted on the timeline clip and supports undo.
/// </summary>
public partial class MainWindow
{
    private void InitializeVisualEffectsCommands()
    {
        EffectControlsMenuItem.Click += (_, _) => EditSelectedEffects();
        ColorScopesMenuItem.Click += (_, _) => ShowColorScopes();
    }

    private void EditColorManagement()
    {
        string[] ids = ["Auto", "Rec709", "Rec601", "Rec2020"];
        string[] names = ["自动读取素材", "Rec.709", "Rec.601", "Rec.2020"];
        var dialog = new Window
        {
            Title = "色彩管理", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Max(1, SystemParameters.PrimaryScreenWidth * .32),
            Height = Math.Max(1, SystemParameters.PrimaryScreenHeight * .38),
            Background = Brush("#191B20"), Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI")
        };
        var root = new StackPanel { Margin = new Thickness(15) };
        MediaAsset? asset = _selectedClip is null ? null :
            _project.Media.FirstOrDefault(item => item.Id == _selectedClip.MediaAssetId);
        var input = new ComboBox { ItemsSource = names,
            SelectedIndex = Math.Max(0, Array.IndexOf(ids, asset?.InputColorSpace ?? "Auto")),
            IsEnabled = asset is not null, Margin = new Thickness(0, 2, 0, 10) };
        var working = new ComboBox { ItemsSource = names[1..],
            SelectedIndex = Math.Max(0, Array.IndexOf(ids[1..], _project.Sequence.WorkingColorSpace)),
            Margin = new Thickness(0, 2, 0, 10) };
        var output = new ComboBox { ItemsSource = names[1..],
            SelectedIndex = Math.Max(0, Array.IndexOf(ids[1..], _project.Sequence.OutputColorSpace)),
            Margin = new Thickness(0, 2, 0, 10) };
        AddField("所选素材输入空间", input);
        AddField("序列工作空间", working);
        AddField("导出输出空间", output);
        root.Children.Add(new TextBlock { Text = "自动模式沿用素材元数据；手动指定时请与源素材实际颜色空间一致。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush("#C5CAD4"), Margin = new Thickness(0, 0, 0, 12) });
        var save = new Button { Content = "应用", HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 5, 16, 5) };
        save.Click += (_, _) =>
        {
            string selectedInput = ids[Math.Clamp(input.SelectedIndex, 0, ids.Length - 1)];
            string selectedWorking = ids[Math.Clamp(working.SelectedIndex + 1, 1, ids.Length - 1)];
            string selectedOutput = ids[Math.Clamp(output.SelectedIndex + 1, 1, ids.Length - 1)];
            if ((asset is null || asset.InputColorSpace == selectedInput) &&
                _project.Sequence.WorkingColorSpace == selectedWorking &&
                _project.Sequence.OutputColorSpace == selectedOutput) { dialog.Close(); return; }
            SaveUndoState();
            if (asset is not null) asset.InputColorSpace = selectedInput;
            _project.Sequence.WorkingColorSpace = selectedWorking;
            _project.Sequence.OutputColorSpace = selectedOutput;
            MarkProjectDirty(); SeekPreview(_project.Sequence.PlayheadSeconds);
            StatusText.Text = "已更新色彩空间";
            dialog.Close();
        };
        root.Children.Add(save);
        dialog.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.ShowDialog();

        void AddField(string label, ComboBox control)
        {
            root.Children.Add(new TextBlock { Text = label });
            root.Children.Add(control);
        }
    }

    private void EditSelectedEffects()
    {
        TimelineClip? clip = _selectedClip;
        if (clip is null || clip.Kind == ClipKind.Audio)
        {
            StatusText.Text = "请先选择画面片段";
            return;
        }
        TimelineTrack? track = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
        if (track is null || track.IsLocked)
        {
            StatusText.Text = track is null ? "片段不在当前项目中 · VIS-EFFECT-TARGET" : "轨道已锁定";
            return;
        }
        OpenObjectToolPage(clip, "效果列表", BuildEffectControls);
    }

    /// <summary>
    /// The full effect page: blend mode, mask, every effect's parameters with
    /// keyframe buttons, effect order, per-effect strength, and the clip's own
    /// transitions. This body used to sit behind an early return after the
    /// simpler list page replaced it, so blend mode and masks had no UI at all.
    /// </summary>
    private UIElement BuildEffectControls(TimelineClip clip, Action back)
    {
        // draw() rebuilds the body, so the header lives outside it.
        var root = new StackPanel { Margin = new Thickness(4) };
        root.Children.Add(SettingsHeader("效果控件", back));
        var panel = new StackPanel();
        root.Children.Add(panel);

        void changed(string message)
        {
            MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
            StatusText.Text = message;
        }

        void draw()
        {
            panel.Children.Clear();
            panel.Children.Add(new TextBlock { Text = "效果按列表顺序处理。片段参数可在下方编辑并设关键帧。",
                Foreground = Brush("#C5CAD4"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            var colorSpace = new Button { Content = "色彩管理 · 输入 / 工作 / 输出空间",
                Padding = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 10) };
            colorSpace.Click += (_, _) => EditColorManagement();
            panel.Children.Add(colorSpace);
            if (clip.Kind is ClipKind.Video or ClipKind.Image)
            {
                var colorMatch = new Button { Content = "匹配参考片段色彩",
                    Padding = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 10) };
                colorMatch.Click += async (_, _) =>
                {
                    colorMatch.IsEnabled = false;
                    try { await MatchClipColorAsync(clip, this); draw(); }
                    finally { colorMatch.IsEnabled = true; }
                };
                panel.Children.Add(colorMatch);
            }

            var blendRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 9) };
            blendRow.Children.Add(new TextBlock { Text = "混合模式", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            (string Id, string Name)[] modes =
            [
                ("Normal", "正常"), ("Multiply", "正片叠底"), ("Screen", "滤色"),
                ("Overlay", "叠加"), ("Add", "线性减淡"), ("Difference", "差值"),
                ("Darken", "变暗"), ("Lighten", "变亮"), ("SoftLight", "柔光")
            ];
            var blend = new ComboBox { ItemsSource = modes.Select(item => item.Name).ToArray(),
                SelectedIndex = Math.Max(0, Array.FindIndex(modes, item => item.Id == clip.BlendMode)) };
            blend.SelectionChanged += (_, _) =>
            {
                if (blend.SelectedIndex < 0) return;
                string mode = modes[blend.SelectedIndex].Id;
                if (mode == clip.BlendMode) return;
                SaveUndoState(); clip.BlendMode = mode; changed("已更新混合模式");
            };
            blendRow.Children.Add(blend);
            panel.Children.Add(blendRow);

            if (clip.Kind is ClipKind.Video or ClipKind.Image)
            {
                var reframe = new Button { Content = "自动重构图 · 生成位置关键帧",
                    Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 0, 10),
                    HorizontalAlignment = HorizontalAlignment.Left };
                reframe.Click += async (_, _) =>
                {
                    reframe.IsEnabled = false;
                    try { await AutoReframeClipAsync(clip); }
                    finally { reframe.IsEnabled = true; }
                };
                panel.Children.Add(reframe);
            }

            var mask = clip.Mask;
            var maskToggle = new CheckBox { Content = "启用片段蒙版", IsChecked = mask is not null, Margin = new Thickness(0, 0, 0, 8) };
            maskToggle.Checked += (_, _) => { if (clip.Mask is not null) return; SaveUndoState(); clip.Mask = new ClipMask(); changed("已启用片段蒙版"); draw(); };
            maskToggle.Unchecked += (_, _) => { if (clip.Mask is null) return; SaveUndoState(); clip.Mask = null; changed("已关闭片段蒙版"); draw(); };
            panel.Children.Add(maskToggle);
            if (mask is not null)
            {
                panel.Children.Add(new TextBlock { Text = "蒙版限制整个片段的可见区域；需要局部效果时可复制片段到上方轨道，再给上层添加蒙版和效果。",
                    Foreground = Brush("#C5CAD4"), TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(12, 0, 0, 7) });
                (string Id, string Name)[] shapes = [("Rectangle", "矩形"), ("Ellipse", "椭圆"), ("Path", "钢笔自由路径")];
                var shape = new ComboBox { ItemsSource = shapes.Select(item => item.Name).ToArray(),
                    SelectedIndex = Math.Max(0, Array.FindIndex(shapes, item => item.Id == mask.Shape)),
                    Margin = new Thickness(12, 0, 0, 7), HorizontalAlignment = HorizontalAlignment.Stretch };
                shape.SelectionChanged += (_, _) =>
                {
                    if (shape.SelectedIndex < 0) return;
                    string selected = shapes[shape.SelectedIndex].Id;
                    if (selected == mask.Shape) return;
                    SaveUndoState();
                    mask.Shape = selected;
                    if (selected == "Path" && (mask.Points?.Count ?? 0) < 3)
                        mask.Points =
                        [
                            new MaskPoint { X = .25, Y = .25 }, new MaskPoint { X = .75, Y = .25 },
                            new MaskPoint { X = .75, Y = .75 }, new MaskPoint { X = .25, Y = .75 }
                        ];
                    changed("已更新蒙版形状"); draw();
                };
                panel.Children.Add(shape);
                if (mask.Shape == "Path")
                {
                    var pen = new Button { Content = "编辑自由路径顶点", Padding = new Thickness(10, 5, 10, 5),
                        Margin = new Thickness(12, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
                    pen.Click += (_, _) => EditMaskPath(clip, mask, this);
                    panel.Children.Add(pen);
                }
                AddMaskNumber("中心 X（%）", mask.CenterX, value => mask.CenterX = value);
                AddMaskNumber("中心 Y（%）", mask.CenterY, value => mask.CenterY = value);
                AddMaskNumber("宽度（%）", mask.Width, value => mask.Width = Math.Max(.001, value));
                AddMaskNumber("高度（%）", mask.Height, value => mask.Height = Math.Max(.001, value));
                AddMaskNumber("羽化（%）", mask.Feather, value => mask.Feather = Math.Min(.5, value));
                var tracking = new StackPanel { Orientation = Orientation.Horizontal,
                    Margin = new Thickness(12, 1, 0, 8) };
                var trackX = new Button { Content = "◇ 记录中心 X", Padding = new Thickness(7, 3, 7, 3),
                    Margin = new Thickness(0, 0, 6, 0) };
                var trackY = new Button { Content = "◇ 记录中心 Y", Padding = new Thickness(7, 3, 7, 3) };
                trackX.Click += (_, _) => RecordMaskPositionKeyframe("Mask.CenterX", mask.CenterX);
                trackY.Click += (_, _) => RecordMaskPositionKeyframe("Mask.CenterY", mask.CenterY);
                tracking.Children.Add(trackX); tracking.Children.Add(trackY);
                panel.Children.Add(tracking);
                panel.Children.Add(new TextBlock { Text = "在播放头手工定位中心并记录关键帧，再按方向跟踪素材画面的局部平移；每次最多 300 帧。",
                    Foreground = Brush("#C5CAD4"), TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(12, 0, 0, 6) });
                if (clip.Kind == ClipKind.Video)
                {
                    var motionRow = new StackPanel { Orientation = Orientation.Horizontal,
                        Margin = new Thickness(12, 1, 0, 8) };
                    var backward = new Button { Content = "向后跟踪蒙版", Padding = new Thickness(7, 3, 7, 3),
                        Margin = new Thickness(0, 0, 6, 0) };
                    var forward = new Button { Content = "向前跟踪蒙版", Padding = new Thickness(7, 3, 7, 3) };
                    backward.Click += async (_, _) =>
                    {
                        backward.IsEnabled = forward.IsEnabled = false;
                        try { await TrackSelectedMaskAsync(clip, forward: false); }
                        finally { backward.IsEnabled = forward.IsEnabled = true; }
                    };
                    forward.Click += async (_, _) =>
                    {
                        backward.IsEnabled = forward.IsEnabled = false;
                        try { await TrackSelectedMaskAsync(clip, forward: true); }
                        finally { backward.IsEnabled = forward.IsEnabled = true; }
                    };
                    motionRow.Children.Add(backward);
                    motionRow.Children.Add(forward);
                    panel.Children.Add(motionRow);
                }
                var invert = new CheckBox { Content = "反转蒙版", IsChecked = mask.Invert,
                    Margin = new Thickness(12, 0, 0, 8) };
                invert.Click += (_, _) => { SaveUndoState(); mask.Invert = invert.IsChecked == true; changed("已更新蒙版方向"); };
                panel.Children.Add(invert);
            }

            var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 12) };
            var catalog = BuiltinCatalog.Effects;
            var addChoice = new ComboBox { ItemsSource = catalog.Select(item => item.Name).ToArray(),
                SelectedIndex = 0, Margin = new Thickness(0, 0, 8, 0) };
            var add = new Button { Content = "＋ 添加效果", Padding = new Thickness(10, 5, 10, 5) };
            add.Click += (_, _) =>
            {
                BuiltinPreset preset = catalog[Math.Clamp(addChoice.SelectedIndex, 0, catalog.Count - 1)];
                SaveUndoState(); clip.Effects.Add(new ClipEffect { PresetId = preset.Id, Name = preset.Name });
                changed($"已添加效果：{preset.Name}"); draw();
            };
            addRow.Children.Add(addChoice); addRow.Children.Add(add); panel.Children.Add(addRow);

            foreach (ClipEffect effect in clip.Effects.ToArray())
            {
                var card = new StackPanel { Background = Brush("#252830"), Margin = new Thickness(0, 0, 0, 10) };
                var header = new DockPanel { Margin = new Thickness(10, 9, 10, 6) };
                var remove = new Button { Content = "删除", Padding = new Thickness(8, 3, 8, 3) };
                remove.Click += (_, _) =>
                {
                    SaveUndoState(); clip.Effects.Remove(effect); changed($"已移除效果：{effect.Name}");
                    // With the last effect gone the list has nothing left to
                    // adjust, so return to the object page instead of leaving an
                    // empty list open. Reopen "效果控件" to add another.
                    if (clip.Effects.Count == 0) back(); else draw();
                };
                DockPanel.SetDock(remove, Dock.Right); header.Children.Add(remove);
                var down = new Button { Content = "↓", Padding = new Thickness(8, 3, 8, 3) };
                down.Click += (_, _) => MoveEffect(effect, 1);
                DockPanel.SetDock(down, Dock.Right); header.Children.Add(down);
                var up = new Button { Content = "↑", Padding = new Thickness(8, 3, 8, 3) };
                up.Click += (_, _) => MoveEffect(effect, -1);
                DockPanel.SetDock(up, Dock.Right); header.Children.Add(up);
                var enabled = new CheckBox { Content = effect.Name, IsChecked = effect.IsEnabled, FontWeight = FontWeights.SemiBold };
                enabled.Click += (_, _) => { SaveUndoState(); effect.IsEnabled = enabled.IsChecked == true; changed("已更新效果开关"); };
                header.Children.Add(enabled); card.Children.Add(header);
                AddEffectStrengthRow(card, effect);
                foreach (VisualEffectPipeline.Parameter parameter in VisualEffectPipeline.ParametersFor(effect.PresetId))
                {
                    var row = new DockPanel { Margin = new Thickness(10, 2, 10, 5) };
                    if (parameter.Animatable)
                    {
                        var key = new Button { Content = "◇ 关键帧", Padding = new Thickness(7, 3, 7, 3) };
                        key.Click += (_, _) =>
                        {
                            string property = VisualEffectPipeline.ParameterProperty(effect, parameter);
                            double time = Math.Clamp(_project.Sequence.PlayheadSeconds - clip.StartSeconds, 0, clip.DurationSeconds);
                            SaveUndoState();
                            ClipKeyframe? existing = clip.Keyframes.FirstOrDefault(item => item.Property == property && Math.Abs(item.TimeSeconds - time) < .0001);
                            if (existing is null) { existing = new ClipKeyframe { Property = property, TimeSeconds = time }; clip.Keyframes.Add(existing); }
                            existing.Value = VisualEffectPipeline.GetNumber(effect, parameter);
                            changed($"已在 {time:0.###} 秒设置关键帧");
                        };
                        DockPanel.SetDock(key, Dock.Right); row.Children.Add(key);
                    }
                    var box = new TextBox { Text = VisualEffectPipeline.GetNumber(effect, parameter).ToString("0.###", CultureInfo.CurrentCulture),
                        Margin = new Thickness(6, 0, 0, 0) };
                    box.LostFocus += (_, _) =>
                    {
                        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ||
                            !double.IsFinite(value) || value < parameter.Minimum || value > parameter.Maximum)
                        {
                            MessageBox.Show(this, $"{parameter.Name}范围：{parameter.Minimum} 到 {parameter.Maximum}。\n阶段代码：VIS-EFFECT-PARAM", "参数无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                            box.Text = VisualEffectPipeline.GetNumber(effect, parameter).ToString("0.###", CultureInfo.CurrentCulture);
                            return;
                        }
                        if (Math.Abs(value - VisualEffectPipeline.GetNumber(effect, parameter)) < .0000001) return;
                        SaveUndoState(); VisualEffectPipeline.SetNumber(effect, parameter, value);
                        changed($"已更新 {effect.Name} · {parameter.Name}");
                    };
                    DockPanel.SetDock(box, Dock.Right); row.Children.Add(box);
                    row.Children.Add(new TextBlock { Text = parameter.Name, VerticalAlignment = VerticalAlignment.Center });
                    card.Children.Add(row);
                }
                if (effect.PresetId == "effect.key") AddColorPathRow(effect, card, "键控颜色", "color", false);
                if (effect.PresetId == "effect.lut") AddColorPathRow(effect, card, "LUT 文件 (.cube)", "path", true);
                panel.Children.Add(card);
            }

            panel.Children.Add(new TextBlock { Text = "片段转场", FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 7) });
            var transitions = BuiltinCatalog.Transitions;
            var transitionRow = new StackPanel { Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8) };
            var transitionChoice = new ComboBox { ItemsSource = transitions.Select(item => item.Name).ToArray(),
                SelectedIndex = 0, Margin = new Thickness(0, 0, 7, 0) };
            var addTransition = new Button { Content = "＋ 添加转场", Padding = new Thickness(10, 5, 10, 5) };
            addTransition.Click += (_, _) =>
            {
                BuiltinPreset preset = transitions[Math.Clamp(transitionChoice.SelectedIndex, 0, transitions.Count - 1)];
                TimelineTrack? owner = _project.Tracks.FirstOrDefault(track => track.Clips.Contains(clip));
                if (owner is null) { StatusText.Text = "片段已从轨道移除 · VIS-TRANSITION-STATE"; return; }
                TimelineClip? incoming = TryFindTransitionBoundary(owner, clip, null,
                    out TimelineClip? pairedIncoming) && pairedIncoming is not null
                    ? pairedIncoming : BoundaryCrossfade.IsVisible(clip) ? clip : null;
                if (incoming is null)
                {
                    StatusText.Text = "只有可见对象可以添加转场 · VIS-TRANSITION-VISIBILITY";
                    return;
                }
                SaveUndoState();
                incoming.Transitions.RemoveAll(item => item.Edge == "In");
                incoming.Transitions.Add(new ClipTransition { PresetId = preset.Id, Name = preset.Name, Edge = "In" });
                changed($"已添加转场：{preset.Name}"); draw();
            };
            transitionRow.Children.Add(transitionChoice); transitionRow.Children.Add(addTransition);
            panel.Children.Add(transitionRow);
            foreach (ClipTransition transition in clip.Transitions.ToArray())
            {
                var row = new StackPanel { Background = Brush("#252830"), Margin = new Thickness(0, 0, 0, 8) };
                var title = new DockPanel { Margin = new Thickness(10, 8, 10, 5) };
                var removeTransition = new Button { Content = "删除", Padding = new Thickness(8, 3, 8, 3) };
                removeTransition.Click += (_, _) =>
                { SaveUndoState(); clip.Transitions.Remove(transition); changed("已删除转场"); draw(); };
                DockPanel.SetDock(removeTransition, Dock.Right); title.Children.Add(removeTransition);
                title.Children.Add(new TextBlock { Text = transition.Name, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(title);
                var options = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 10, 9) };
                var edge = new ComboBox { ItemsSource = new[] { "片段开始", "片段结束" },
                    SelectedIndex = transition.Edge.Equals("Out", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                    Margin = new Thickness(0, 0, 7, 0) };
                edge.SelectionChanged += (_, _) =>
                {
                    string value = edge.SelectedIndex == 1 ? "Out" : "In";
                    if (value == transition.Edge) return;
                    SaveUndoState(); transition.Edge = value; changed("已更新转场位置");
                };
                var duration = new TextBox { Text = transition.DurationSeconds.ToString("0.###", CultureInfo.CurrentCulture),
                    Margin = new Thickness(0, 0, 7, 0) };
                duration.LostFocus += (_, _) =>
                {
                    if (!double.TryParse(duration.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ||
                        !double.IsFinite(value) || value <= 0 || value > Math.Min(30, clip.DurationSeconds))
                    {
                        MessageBox.Show(this, "转场时长无效。\n阶段代码：VIS-TRANSITION-DURATION", "输入无效",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        duration.Text = transition.DurationSeconds.ToString("0.###", CultureInfo.CurrentCulture);
                        return;
                    }
                    if (Math.Abs(value - transition.DurationSeconds) < .000001) return;
                    SaveUndoState(); transition.DurationSeconds = value; changed("已更新转场时长");
                };
                options.Children.Add(edge);
                options.Children.Add(new TextBlock { Text = "时长（秒）", VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0) });
                options.Children.Add(duration);
                row.Children.Add(options);
                AddTransitionParameterRows(row, transition, new Thickness(10, 0, 10, 9), changed);
                panel.Children.Add(row);
            }

            void AddMaskNumber(string label, double current, Action<double> setter)
            {
                var row = new DockPanel { Margin = new Thickness(12, 0, 0, 5) };
                var box = new TextBox { Text = (current * 100).ToString("0.##", CultureInfo.CurrentCulture),
                    Margin = new Thickness(7, 0, 0, 0) };
                box.LostFocus += (_, _) =>
                {
                    if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ||
                        !double.IsFinite(value) || value < 0 || value > 100)
                    {
                        MessageBox.Show(this, "蒙版数值须在 0% 到 100% 之间。\n阶段代码：VIS-MASK-PARAM", "蒙版参数无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                        box.Text = (current * 100).ToString("0.##", CultureInfo.CurrentCulture); return;
                    }
                    if (Math.Abs(value / 100 - current) < .000001) return;
                    SaveUndoState(); setter(value / 100); changed("已更新片段蒙版");
                };
                DockPanel.SetDock(box, Dock.Right); row.Children.Add(box);
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
                panel.Children.Add(row);
            }
        }

        void MoveEffect(ClipEffect effect, int offset)
        {
            int index = clip.Effects.IndexOf(effect);
            int destination = index + offset;
            if (index < 0 || destination < 0 || destination >= clip.Effects.Count) return;
            SaveUndoState(); clip.Effects.RemoveAt(index); clip.Effects.Insert(destination, effect);
            changed("已调整效果顺序"); draw();
        }

        void RecordMaskPositionKeyframe(string property, double value)
        {
            double time = Math.Clamp(_project.Sequence.PlayheadSeconds - clip.StartSeconds, 0, clip.DurationSeconds);
            SaveUndoState();
            ClipKeyframe? keyframe = clip.Keyframes.FirstOrDefault(item => item.Property == property &&
                Math.Abs(item.TimeSeconds - time) < .0001);
            if (keyframe is null)
            {
                keyframe = new ClipKeyframe { Property = property, TimeSeconds = time };
                clip.Keyframes.Add(keyframe);
            }
            keyframe.Value = value;
            changed($"已在 {time:0.###} 秒记录蒙版中心");
        }

        void AddColorPathRow(ClipEffect effect, StackPanel card, string label, string key, bool isPath)
        {
            var row = new DockPanel { Margin = new Thickness(10, 2, 10, 10) };
            var box = new TextBox { Text = VisualEffectPipeline.GetString(effect, key) ?? (isPath ? string.Empty : "#00FF00"),
                Margin = new Thickness(6, 0, 0, 0) };
            box.LostFocus += async (_, _) =>
            {
                string value = box.Text.Trim();
                ProjectDocument pathProject = _project;
                long pathGeneration = _projectGeneration;
                if (isPath && value.Length > 0)
                {
                    bool validPath;
                    try
                    {
                        validPath = await Task.Run(() =>
                            File.Exists(value) && System.IO.Path.GetExtension(value)
                                .Equals(".cube", StringComparison.OrdinalIgnoreCase));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                        ArgumentException or NotSupportedException)
                    {
                        StatusText.Text = $"LUT 路径检查失败 · VIS-LUT-FILE：{exception.Message}";
                        return;
                    }
                    if (!box.IsVisible || !ReferenceEquals(pathProject, _project) ||
                        pathGeneration != _projectGeneration || box.Text.Trim() != value) return;
                    if (!validPath)
                    {
                        MessageBox.Show(this, "请选择可读取的 .cube 文件。\n阶段代码：VIS-LUT-FILE",
                            "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                else if (!isPath && !TryColorQuiet(value))
                {
                    MessageBox.Show(this, "请输入有效颜色。\n阶段代码：VIS-KEY-COLOR",
                        "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (value == VisualEffectPipeline.GetString(effect, key)) return;
                SaveUndoState(); VisualEffectPipeline.SetString(effect, key, value); changed($"已更新 {label}");
            };
            if (isPath)
            {
                var browse = new Button { Content = "浏览…", Padding = new Thickness(7, 3, 7, 3) };
                browse.Click += (_, _) =>
                {
                    var picker = new OpenFileDialog { Title = "选择 3D LUT", Filter = "3D LUT|*.cube", CheckFileExists = true };
                    if (picker.ShowDialog(this) != true) return;
                    SaveUndoState(); VisualEffectPipeline.SetString(effect, key, picker.FileName);
                    box.Text = picker.FileName; changed("已载入 3D LUT");
                };
                DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse);
            }
            DockPanel.SetDock(box, Dock.Right); row.Children.Add(box);
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            card.Children.Add(row);
        }

        draw();
        return root;
    }

    /// <summary>
    /// Adds the strength control for one effect: 0 to 200 percent, where 100 is
    /// the effect as authored. A slider and a box edit the same value. A drag
    /// produces many small changes, so the undo snapshot is taken once per
    /// gesture rather than once per step; a single drag used to push dozens of
    /// snapshots and make undo step through each one.
    /// </summary>
    private void AddEffectStrengthRow(StackPanel card, ClipEffect effect)
    {
        double current = effect.Parameters.TryGetValue("intensity", out System.Text.Json.JsonElement stored) &&
            stored.ValueKind == System.Text.Json.JsonValueKind.Number ? stored.GetDouble() : 100;
        current = Math.Clamp(current, 0, 200);

        // Proportional columns instead of fixed widths, per the layout rules.
        var row = new Grid { Margin = new Thickness(10, 0, 10, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = "强度（%）", VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("#C5CAD4") });
        var slider = new Slider { Minimum = 0, Maximum = 200, Value = current, SmallChange = 1, LargeChange = 10,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
        var box = new TextBox { Text = current.ToString("0", CultureInfo.CurrentCulture),
            TextAlignment = TextAlignment.Right };
        Grid.SetColumn(slider, 1); Grid.SetColumn(box, 2);
        row.Children.Add(slider); row.Children.Add(box);

        bool undoTaken = false, syncing = false;
        void EndGesture() => undoTaken = false;
        void Commit(double value)
        {
            if (!undoTaken) { SaveUndoState(); undoTaken = true; }
            effect.Parameters["intensity"] = System.Text.Json.JsonSerializer.SerializeToElement(Math.Round(value, 1));
            MarkProjectDirty();
            SeekPreview(_project.Sequence.PlayheadSeconds);
        }
        slider.ValueChanged += (_, args) =>
        {
            if (syncing) return;
            syncing = true;
            try { box.Text = args.NewValue.ToString("0", CultureInfo.CurrentCulture); }
            finally { syncing = false; }
            Commit(args.NewValue);
        };
        slider.LostMouseCapture += (_, _) => EndGesture();
        slider.LostKeyboardFocus += (_, _) => EndGesture();
        box.LostKeyboardFocus += (_, _) =>
        {
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double typed) ||
                !double.IsFinite(typed))
            {
                box.Text = slider.Value.ToString("0", CultureInfo.CurrentCulture);
                return;
            }
            // Setting the slider raises ValueChanged, which commits it.
            slider.Value = Math.Clamp(typed, 0, 200);
            box.Text = slider.Value.ToString("0", CultureInfo.CurrentCulture);
            EndGesture();
        };
        card.Children.Add(row);
    }

    /// <summary>
    /// Edits a closed pen path over the untransformed source frame. Pointer
    /// coordinates are normalized to the source image, matching the FFmpeg
    /// mask expression and the local motion tracker's source-space centers.
    /// The working list is copied so Cancel leaves the persisted clip intact.
    /// </summary>
    private void EditMaskPath(TimelineClip clip, ClipMask mask, Window owner)
    {
        var editor = new Window
        {
            Title = "编辑钢笔蒙版", Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Max(1, SystemParameters.PrimaryScreenWidth * .58),
            Height = Math.Max(1, SystemParameters.PrimaryScreenHeight * .75),
            Background = Brush("#191B20"), Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI"), ResizeMode = ResizeMode.CanResize
        };
        var layout = new DockPanel { Margin = new Thickness(14) };
        var instructions = new TextBlock
        {
            Text = "单击画面添加顶点；拖动圆点调整位置；右击圆点删除。至少保留 3 个顶点，最多 32 个。路径跟踪以素材画面的局部平移为准。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush("#C5CAD4"),
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(instructions, Dock.Top);
        layout.Children.Add(instructions);
        var actions = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = "取消", Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => editor.DialogResult = false;
        var apply = new Button { Content = "应用路径", Padding = new Thickness(14, 5, 14, 5) };
        actions.Children.Add(cancel);
        actions.Children.Add(apply);
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);
        var frame = new Grid { Background = Brush("#111317"), ClipToBounds = true };
        var image = new Image { Stretch = Stretch.Uniform, IsHitTestVisible = false };
        var vertices = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
        frame.Children.Add(image);
        frame.Children.Add(vertices);
        layout.Children.Add(frame);
        editor.Content = layout;
        var points = (mask.Points ?? []).Select(point => new MaskPoint { X = point.X, Y = point.Y }).ToList();
        if (points.Count < 3)
            points =
            [
                new MaskPoint { X = .25, Y = .25 }, new MaskPoint { X = .75, Y = .25 },
                new MaskPoint { X = .75, Y = .75 }, new MaskPoint { X = .25, Y = .75 }
            ];
        bool dragging = false;
        int draggedIndex = -1;
        double sourceAspect = _project.Sequence.Width / (double)Math.Max(1, _project.Sequence.Height);
        Rect contentBounds()
        {
            // Image uses Uniform stretch. Match its letterboxed bounds when
            // converting pointer positions to normalized source coordinates.
            double availableWidth = Math.Max(1, vertices.ActualWidth);
            double availableHeight = Math.Max(1, vertices.ActualHeight);
            double aspect = double.IsFinite(sourceAspect) && sourceAspect > 0 ? sourceAspect : 1;
            double contentWidth = Math.Min(availableWidth, availableHeight * aspect);
            double contentHeight = contentWidth / aspect;
            return new Rect((availableWidth - contentWidth) / 2,
                (availableHeight - contentHeight) / 2, contentWidth, contentHeight);
        }
        MaskPoint? normalizedPoint(Point point, bool clamp)
        {
            Rect bounds = contentBounds();
            if (!clamp && !bounds.Contains(point)) return null;
            return new MaskPoint
            {
                X = Math.Clamp((point.X - bounds.X) / bounds.Width, 0, 1),
                Y = Math.Clamp((point.Y - bounds.Y) / bounds.Height, 0, 1)
            };
        }
        void redraw()
        {
            vertices.Children.Clear();
            if (vertices.ActualWidth <= 0 || vertices.ActualHeight <= 0) return;
            Rect bounds = contentBounds();
            var outline = new Polyline
            {
                Stroke = Brush("#55DFCB"), StrokeThickness = 2, IsHitTestVisible = false,
                Points = new PointCollection(points.Select(point =>
                    new Point(bounds.X + point.X * bounds.Width,
                        bounds.Y + point.Y * bounds.Height)))
            };
            outline.Points.Add(new Point(bounds.X + points[0].X * bounds.Width,
                bounds.Y + points[0].Y * bounds.Height));
            vertices.Children.Add(outline);
            for (int index = 0; index < points.Count; index++)
            {
                int vertexIndex = index;
                var marker = new Ellipse
                {
                    Width = Math.Clamp(Math.Min(bounds.Width, bounds.Height) * .026, 6, 20),
                    Height = Math.Clamp(Math.Min(bounds.Width, bounds.Height) * .026, 6, 20),
                    Fill = Brush("#55DFCB"),
                    Stroke = Brushes.White, StrokeThickness = 1.5, Cursor = System.Windows.Input.Cursors.SizeAll
                };
                Canvas.SetLeft(marker, bounds.X + points[index].X * bounds.Width - marker.Width / 2);
                Canvas.SetTop(marker, bounds.Y + points[index].Y * bounds.Height - marker.Height / 2);
                marker.MouseLeftButtonDown += (_, args) =>
                {
                    draggedIndex = vertexIndex;
                    dragging = true;
                    vertices.CaptureMouse();
                    vertices.MouseLeftButtonUp += stopDrag;
                    args.Handled = true;
                };
                marker.MouseRightButtonDown += (_, args) =>
                {
                    args.Handled = true;
                    if (points.Count <= 3) return;
                    points.RemoveAt(vertexIndex);
                    redraw();
                };
                vertices.Children.Add(marker);
            }
        }
        vertices.SizeChanged += (_, _) => redraw();
        vertices.MouseMove += (_, args) =>
        {
            if (!dragging || draggedIndex < 0 || draggedIndex >= points.Count) return;
            if (System.Windows.Input.Mouse.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            {
                dragging = false;
                draggedIndex = -1;
                vertices.ReleaseMouseCapture();
                vertices.MouseLeftButtonUp -= stopDrag;
                return;
            }
            MaskPoint point = normalizedPoint(args.GetPosition(vertices), clamp: true)!;
            points[draggedIndex].X = point.X;
            points[draggedIndex].Y = point.Y;
            redraw();
        };
        void stopDrag(object? sender, System.Windows.Input.MouseButtonEventArgs args)
        {
            if (!dragging) return;
            dragging = false;
            draggedIndex = -1;
            vertices.ReleaseMouseCapture();
            vertices.MouseLeftButtonUp -= stopDrag;
            args.Handled = true;
        }
        vertices.MouseLeftButtonDown += (_, args) =>
        {
            if (points.Count >= 32) return;
            MaskPoint? point = normalizedPoint(args.GetPosition(vertices), clamp: false);
            if (point is null) return;
            points.Add(point);
            redraw();
        };
        apply.Click += (_, _) =>
        {
            TimelineTrack? track = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
            if (track is null || track.IsLocked || !ReferenceEquals(mask, clip.Mask) || points.Count < 3)
            {
                MessageBox.Show(editor, "蒙版路径已失效或轨道被锁定。\n阶段代码：VIS-MASK-PATH",
                    "无法应用路径", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SaveUndoState();
            mask.Points = points.Select(point => new MaskPoint { X = point.X, Y = point.Y }).ToList();
            mask.Shape = "Path";
            MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
            StatusText.Text = $"已保存 {points.Count} 个自由路径顶点";
            editor.DialogResult = true;
        };
        editor.Loaded += async (_, _) =>
        {
            MediaAsset? asset = _project.Media.FirstOrDefault(item => item.Id == clip.MediaAssetId);
            if (asset is null || clip.Kind is not (ClipKind.Video or ClipKind.Image)) return;
            try
            {
                if (asset.Width is > 0 && asset.Height is > 0)
                    sourceAspect = asset.Width.Value / (double)asset.Height.Value;
                redraw();
                int width = Math.Clamp((int)Math.Round(frame.ActualWidth), 16, 2048);
                int height = Math.Clamp((int)Math.Round(frame.ActualHeight), 16, 2048);
                width = Math.Max(16, width - width % 2);
                height = Math.Max(16, height - height % 2);
                BitmapSource picture = await ClipFrameRenderer.RenderClipFrameAsync(clip, asset, _project.Sequence,
                    _project.Sequence.PlayheadSeconds, width, height, includeVisualEffects: false);
                image.Source = picture;
                redraw();
            }
            catch (Exception exception) when (exception is ClipFrameException or IOException or InvalidOperationException)
            {
                StatusText.Text = $"蒙版背景帧不可用 · VIS-MASK-PREVIEW：{exception.Message}";
            }
        };
        editor.ShowDialog();
    }

    /// <summary>
    /// Tracks a user-positioned mask on nearby source frames. Each estimated
    /// center is recorded as two ordinary mask keyframes, so the preview and
    /// export paths use the same stored trajectory.
    /// </summary>
    private async Task TrackSelectedMaskAsync(TimelineClip clip, bool forward)
    {
        ClipMask? mask = clip.Mask;
        TimelineTrack? track = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
        MediaAsset? asset = _project.Media.FirstOrDefault(item => item.Id == clip.MediaAssetId);
        if (mask is null || track is null || track.IsLocked || asset is null ||
            !double.IsFinite(clip.DurationSeconds) || clip.DurationSeconds <= 0 ||
            !double.IsFinite(_project.Sequence.FramesPerSecond) || _project.Sequence.FramesPerSecond <= 0 ||
            !double.IsFinite(_project.Sequence.PlayheadSeconds) ||
            !double.IsFinite(mask.Width) || !double.IsFinite(mask.Height) ||
            !double.IsFinite(mask.CenterX) || !double.IsFinite(mask.CenterY))
        {
            StatusText.Text = "请先选择可编辑的蒙版画面片段 · VIS-TRACK-VALIDATE";
            return;
        }
        double frameSeconds = 1 / _project.Sequence.FramesPerSecond;
        double start = Math.Clamp(_project.Sequence.PlayheadSeconds - clip.StartSeconds, 0,
            Math.Max(0, clip.DurationSeconds - frameSeconds));
        if (clip.Kind != ClipKind.Video)
        {
            StatusText.Text = "运动跟踪需要视频片段 · VIS-TRACK-VALIDATE";
            return;
        }
        double direction = forward ? 1 : -1;
        double end = forward ? clip.DurationSeconds : 0;
        int frameLimit = (int)Math.Min(300, Math.Ceiling(Math.Abs(end - start) / frameSeconds));
        if (frameLimit < 1)
        {
            StatusText.Text = "播放头已位于该方向的片段边界 · VIS-TRACK-RANGE";
            return;
        }
        StatusText.Text = forward ? "正在本地向前跟踪蒙版…" : "正在本地向后跟踪蒙版…";
        try
        {
            // Track the source picture before clip effects and transforms;
            // this matches the normalized coordinates stored by ClipMask.
            double aspect = asset.Width is > 0 && asset.Height is > 0
                ? asset.Width.Value / (double)asset.Height.Value
                : _project.Sequence.Width / (double)Math.Max(1, _project.Sequence.Height);
            if (!double.IsFinite(aspect) || aspect <= 0)
                throw new InvalidOperationException("素材宽高比无效。");
            int sampleWidth = aspect >= 1 ? 320 : Math.Clamp((int)Math.Round(320 * aspect), 64, 320);
            int sampleHeight = aspect >= 1 ? Math.Clamp((int)Math.Round(320 / aspect), 64, 320) : 320;
            double centerX = ClipAnimation.Evaluate(clip, "Mask.CenterX", clip.StartSeconds + start);
            double centerY = ClipAnimation.Evaluate(clip, "Mask.CenterY", clip.StartSeconds + start);
            BitmapSource previous = await ClipFrameRenderer.RenderClipFrameAsync(clip, asset, _project.Sequence,
                clip.StartSeconds + start, sampleWidth, sampleHeight, includeVisualEffects: false);
            var path = new List<(double Time, double X, double Y)> { (start, centerX, centerY) };
            for (int step = 1; step <= frameLimit; step++)
            {
                if ((forward && start + step * frameSeconds >= clip.DurationSeconds) ||
                    (!forward && start - step * frameSeconds < 0)) break;
                double local = Math.Clamp(start + direction * step * frameSeconds, 0,
                    Math.Max(0, clip.DurationSeconds - .001));
                if (Math.Abs(local - path[^1].Time) < .0000001) break;
                BitmapSource current = await ClipFrameRenderer.RenderClipFrameAsync(clip, asset, _project.Sequence,
                    clip.StartSeconds + local, sampleWidth, sampleHeight, includeVisualEffects: false);
                (centerX, centerY) = await MaskMotionTracker.TrackStepAsync(previous, current,
                    centerX, centerY, mask.Width, mask.Height);
                path.Add((local, centerX, centerY));
                previous = current;
            }
            track = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
            if (track is null || track.IsLocked || !ReferenceEquals(mask, clip.Mask)) return;
            SaveUndoState();
            foreach ((double local, double x, double y) in path)
            {
                AddKey("Mask.CenterX", x);
                AddKey("Mask.CenterY", y);

                void AddKey(string property, double value)
                {
                    ClipKeyframe? key = clip.Keyframes.FirstOrDefault(item => item.Property == property &&
                        Math.Abs(item.TimeSeconds - local) < frameSeconds * .25);
                    if (key is null)
                    {
                        key = new ClipKeyframe { Property = property, TimeSeconds = local };
                        clip.Keyframes.Add(key);
                    }
                    key.Value = value;
                    key.Interpolation = "Linear";
                }
            }
            MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
            StatusText.Text = $"已记录 {path.Count} 帧蒙版轨迹，可继续手工修正";
        }
        catch (Exception exception) when (exception is ClipFrameException or IOException or InvalidOperationException or ArgumentException)
        {
            StatusText.Text = $"蒙版跟踪失败 · VIS-TRACK-ANALYZE：{exception.Message}";
        }
    }

    /// <summary>
    /// Samples the source locally, estimates an edge-weighted subject center,
    /// then records standard transform keyframes. The user can refine every
    /// generated point in the normal keyframe editor after this operation.
    /// </summary>
    private async Task AutoReframeClipAsync(TimelineClip clip)
    {
        TimelineTrack? track = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
        MediaAsset? asset = _project.Media.FirstOrDefault(item => item.Id == clip.MediaAssetId);
        if (track is null || track.IsLocked || asset is null || asset.Width is not > 0 || asset.Height is not > 0)
        {
            StatusText.Text = "自动重构图需要有尺寸信息的未锁定画面片段 · VIS-REFRAME-VALIDATE";
            return;
        }
        int sequenceWidth = _project.Sequence.Width;
        int sequenceHeight = _project.Sequence.Height;
        if (sequenceWidth <= 0 || sequenceHeight <= 0 || !double.IsFinite(clip.DurationSeconds) || clip.DurationSeconds <= 0)
        {
            StatusText.Text = "序列画幅或片段时长无效 · VIS-REFRAME-VALIDATE";
            return;
        }
        if (!double.IsFinite(_project.Sequence.PlayheadSeconds))
        {
            StatusText.Text = "播放头时间无效 · VIS-REFRAME-VALIDATE";
            return;
        }
        StatusText.Text = "正在本地分析画面并生成重构图关键帧…";
        try
        {
            int count = (int)Math.Min(61, Math.Max(2, Math.Ceiling(clip.DurationSeconds) + 1));
            double aspect = asset.Width.Value / (double)asset.Height.Value;
            if (!double.IsFinite(aspect) || aspect <= 0)
                throw new InvalidOperationException("素材宽高比无效。");
            int sampleWidth = aspect >= 1 ? 320 : Math.Clamp((int)Math.Round(320 * aspect), 64, 320);
            int sampleHeight = aspect >= 1 ? Math.Clamp((int)Math.Round(320 / aspect), 64, 320) : 320;
            double[] times = Enumerable.Range(0, count)
                .Select(index => clip.DurationSeconds * index / (count - 1))
                .ToArray();
            var centers = new List<(double Time, double X, double Y)>();
            foreach (double local in times)
            {
                BitmapSource frame = await ClipFrameRenderer.RenderClipFrameAsync(clip, asset, _project.Sequence,
                    clip.StartSeconds + Math.Min(local, Math.Max(0, clip.DurationSeconds -
                        1 / Math.Max(1, asset.FramesPerSecond ?? _project.Sequence.FramesPerSecond))),
                    sampleWidth, sampleHeight, includeVisualEffects: false);
                (double x, double y) = await AutoReframeAnalyzer.AnalyzeAsync(frame);
                centers.Add((local, x, y));
            }
            // Verify that the same clip is still editable after asynchronous
            // decoding. One undo snapshot covers the entire generated path.
            track = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
            if (track is null || track.IsLocked) return;
            SaveUndoState();
            clip.Keyframes.RemoveAll(keyframe => keyframe.Property is "PositionX" or "PositionY" or "ScaleX" or "ScaleY");
            double fittedWidth = Math.Min(sequenceWidth / (double)asset.Width.Value,
                sequenceHeight / (double)asset.Height.Value) * asset.Width.Value;
            double fittedHeight = Math.Min(sequenceWidth / (double)asset.Width.Value,
                sequenceHeight / (double)asset.Height.Value) * asset.Height.Value;
            double fillScale = Math.Clamp(Math.Max(sequenceWidth / fittedWidth, sequenceHeight / fittedHeight), .05, 8);
            clip.Transform.ScaleX = fillScale;
            clip.Transform.ScaleY = fillScale;
            double horizontalTravel = Math.Max(0, fittedWidth * fillScale - sequenceWidth);
            double verticalTravel = Math.Max(0, fittedHeight * fillScale - sequenceHeight);
            foreach ((double local, double x, double y) in centers)
            {
                AddKey("PositionX", (0.5 - x) * horizontalTravel);
                AddKey("PositionY", (0.5 - y) * verticalTravel);

                void AddKey(string property, double value) => clip.Keyframes.Add(new ClipKeyframe
                {
                    Property = property,
                    TimeSeconds = local,
                    Value = value,
                    Interpolation = "EaseInOut"
                });
            }
            MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
            StatusText.Text = $"已生成 {centers.Count} 个重构图位置采样点，可在关键帧编辑器中微调";
        }
        catch (Exception exception) when (exception is ClipFrameException or IOException or InvalidOperationException or ArgumentException)
        {
            StatusText.Text = $"自动重构图失败 · VIS-REFRAME-ANALYZE：{exception.Message}";
        }
    }

    /// <summary>
    /// Compares a frame from the selected clip with a user-chosen reference
    /// clip. It writes ordinary editable exposure and color-balance effects,
    /// so the match is visible in the same preview and export filter chain.
    /// </summary>
    private async Task MatchClipColorAsync(TimelineClip clip, Window owner)
    {
        var candidates = _project.Tracks.SelectMany(track => track.Clips)
            .Where(item => item.Id != clip.Id && item.Kind is (ClipKind.Video or ClipKind.Image) &&
                _project.Media.Any(asset => asset.Id == item.MediaAssetId))
            .ToArray();
        if (candidates.Length == 0)
        {
            StatusText.Text = "项目中没有可用的参考片段 · VIS-MATCH-REFERENCE";
            return;
        }
        var picker = new Window { Title = "选择色彩参考片段", Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Max(1, SystemParameters.PrimaryScreenWidth * .30),
            Height = Math.Max(1, SystemParameters.PrimaryScreenHeight * .32),
            Background = Brush("#191B20"), Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI") };
        var root = new StackPanel { Margin = new Thickness(14) };
        var choice = new ComboBox { ItemsSource = candidates.Select(item =>
            $"{item.Name} · {item.StartSeconds:0.##} 秒").ToArray(), SelectedIndex = 0,
            Margin = new Thickness(0, 0, 0, 12) };
        root.Children.Add(new TextBlock { Text = "参考片段" });
        root.Children.Add(choice);
        var apply = new Button { Content = "分析并匹配", HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(12, 5, 12, 5) };
        apply.Click += (_, _) => picker.DialogResult = true;
        root.Children.Add(apply);
        picker.Content = root;
        if (picker.ShowDialog() != true) return;
        TimelineClip reference = candidates[Math.Clamp(choice.SelectedIndex, 0, candidates.Length - 1)];
        MediaAsset? sourceAsset = _project.Media.FirstOrDefault(asset => asset.Id == clip.MediaAssetId);
        MediaAsset? referenceAsset = _project.Media.FirstOrDefault(asset => asset.Id == reference.MediaAssetId);
        if (sourceAsset is null || referenceAsset is null) return;
        try
        {
            BitmapSource sourceFrame = await ClipFrameRenderer.RenderClipFrameAsync(clip, sourceAsset,
                _project.Sequence, clip.StartSeconds + Math.Min(clip.DurationSeconds / 2, 1), 320, 180);
            BitmapSource referenceFrame = await ClipFrameRenderer.RenderClipFrameAsync(reference, referenceAsset,
                _project.Sequence, reference.StartSeconds + Math.Min(reference.DurationSeconds / 2, 1), 320, 180);
            (double sourceLuma, double sourceRed, double sourceBlue) = await ColorScopeRenderer.AverageColorAsync(sourceFrame);
            (double referenceLuma, double referenceRed, double referenceBlue) = await ColorScopeRenderer.AverageColorAsync(referenceFrame);
            TimelineTrack? track = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
            if (track is null || track.IsLocked) return;
            SaveUndoState();
            double exposure = Math.Clamp((referenceLuma - sourceLuma) * 4, -3, 3);
            double red = Math.Clamp((referenceRed - sourceRed) * .7, -.5, .5);
            double blue = Math.Clamp((referenceBlue - sourceBlue) * .7, -.5, .5);
            AddEffect("effect.exposure", "色彩匹配 · 曝光", ("amount", exposure));
            AddEffect("effect.wheels", "色彩匹配 · 色轮",
                ("shadowRed", red), ("shadowBlue", blue),
                ("highlightRed", red), ("highlightBlue", blue));
            MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
            StatusText.Text = "已按参考片段的平均亮度与颜色生成可编辑调色效果";

            void AddEffect(string preset, string name, params (string Key, double Value)[] values)
            {
                var effect = new ClipEffect { PresetId = preset, Name = name };
                foreach ((string key, double value) in values)
                {
                    VisualEffectPipeline.Parameter? parameter = VisualEffectPipeline.ParametersFor(preset)
                        .FirstOrDefault(item => item.Id == key);
                    if (parameter is not null) VisualEffectPipeline.SetNumber(effect, parameter, value);
                }
                clip.Effects.Add(effect);
            }
        }
        catch (Exception exception) when (exception is ClipFrameException or IOException or InvalidOperationException or ArgumentException)
        {
            StatusText.Text = $"色彩匹配失败 · VIS-MATCH-ANALYZE：{exception.Message}";
        }
    }

    private static bool TryColorQuiet(string value)
    {
        try { _ = ColorConverter.ConvertFromString(value); return true; }
        catch (FormatException) { return false; }
    }

    private void ShowColorScopes()
    {
        var dialog = new Window { Title = "视频示波器", Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Max(1, SystemParameters.PrimaryScreenWidth * .42),
            Height = Math.Max(1, SystemParameters.PrimaryScreenHeight * .57),
            Background = Brush("#191B20"), Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI") };
        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var mode = new ComboBox { ItemsSource = new[] { "亮度波形", "RGB 分量", "矢量示波器", "亮度直方图" },
            SelectedIndex = 0, Margin = new Thickness(0, 0, 10, 0) };
        var refresh = new Button { Content = "刷新当前帧", Padding = new Thickness(10, 5, 10, 5) };
        controls.Children.Add(mode); controls.Children.Add(refresh);
        root.Children.Add(controls);
        var image = new Image { Stretch = Stretch.Uniform };
        Grid.SetRow(image, 1); root.Children.Add(image);
        async Task update()
        {
            try
            {
                if (PreviewRenderedFrame.Source is System.Windows.Media.Imaging.BitmapSource rendered)
                    image.Source = await ColorScopeRenderer.RenderBitmapAsync(rendered, mode.SelectedIndex);
                else if (PreviewImage.Source is System.Windows.Media.Imaging.BitmapSource still)
                    image.Source = await ColorScopeRenderer.RenderBitmapAsync(still, mode.SelectedIndex);
                else if (_activePreviewVisualClip is { Kind: ClipKind.Video } active &&
                    _project.Media.FirstOrDefault(asset => asset.Id == active.MediaAssetId) is MediaAsset asset)
                {
                    var frame = await ClipFrameRenderer.RenderClipFrameAsync(active, asset, _project.Sequence,
                        _project.Sequence.PlayheadSeconds,
                        Math.Clamp((int)PreviewVisualLayer.ActualWidth, 1, 1024),
                        Math.Clamp((int)PreviewVisualLayer.ActualHeight, 1, 1024));
                    image.Source = await ColorScopeRenderer.RenderBitmapAsync(frame, mode.SelectedIndex);
                }
                else image.Source = await ColorScopeRenderer.RenderCurrentFrameAsync(
                    PreviewVisualLayer, PreviewOverlay, mode.SelectedIndex);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or ClipFrameException)
            {
                StatusText.Text = $"示波器更新失败 · VIS-SCOPE-RENDER：{exception.Message}";
            }
        }
        mode.SelectionChanged += async (_, _) => await update();
        refresh.Click += async (_, _) => await update();
        dialog.Content = root;
        dialog.Loaded += async (_, _) => await update();
        dialog.ShowDialog();
    }
}
