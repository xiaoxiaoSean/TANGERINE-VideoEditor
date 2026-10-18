using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

public partial class MainWindow
{
    private void ShowSelectedObjectSettings()
    {
        TimelineClip? clip = _selectedClip;
        if (clip is null)
        {
            CloseSettingsTabs();
            return;
        }
        var panel = new StackPanel { Margin = new Thickness(4) };
        // The root object page is also a real settings page. Give it the
        // same top-left return affordance as every child tool page.
        panel.Children.Add(SettingsHeader($"{clip.Name}设置", () => CloseSettingsTabs()));
        panel.Children.Add(new TextBlock { Text = $"类型：{ClipKindName(clip.Kind)} · 设置会即时作用于当前对象",
            Foreground = Brush("#C5CAD4"), Margin = new Thickness(0, 0, 0, 12) });
        var tools = new WrapPanel { Orientation = Orientation.Horizontal };
        if (clip.Kind is ClipKind.Video or ClipKind.Image)
        {
            AddToolButton(tools, "变换 / 裁切", () => OpenObjectToolPage(clip, "变换 / 裁切", BuildTransformTool));
            AddToolButton(tools, clip.Kind == ClipKind.Image ? "调色" : "滤镜 / 调色",
                () => OpenObjectToolPage(clip, "滤镜 / 调色", BuildFilterTool));
        }
        else if (clip.Kind == ClipKind.Text)
        {
            AddToolButton(tools, IsGraphicClip(clip) ? "形状设置" : "文字设置", () => OpenTextToolPage(clip));
            AddToolButton(tools, "滤镜 / 调色", () => OpenObjectToolPage(clip, "滤镜 / 调色", BuildFilterTool));
        }
        if (clip.Kind == ClipKind.Video)
            AddToolButton(tools, "视频调速", () => OpenObjectToolPage(clip, "视频调速", BuildSpeedTool));
        if (clip.Kind is ClipKind.Audio or ClipKind.Video)
            AddToolButton(tools, "均衡器 / 音量", () => OpenObjectToolPage(clip, "均衡器 / 音量", BuildAudioTool));
        AddToolButton(tools, "关键帧", () =>
        {
            if (_selectedKeyframe is null) AddKeyframeAtPlayhead();
            else OpenKeyframeSettingsTab(clip, _selectedTrack!, _selectedKeyframe);
        });
        // Effects, blend mode and masks act on the picture; an audio clip has none.
        if (clip.Kind != ClipKind.Audio)
            AddToolButton(tools, "效果控件", () => OpenObjectToolPage(clip, "效果列表", BuildEffectControls));
        panel.Children.Add(tools);
        panel.Children.Add(new TextBlock { Text = "可见对象也可以直接在预览区拖动、旋转；时间线中的关键帧、音量线和片段边界可直接操作。",
            Foreground = Brush("#AEB5C2"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 0) });
        OpenSettingsCategory($"{clip.Name}设置", panel);
    }

    private UIElement SettingsHeader(string title, Action? back)
    {
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 14) };
        if (back is not null)
        {
            var button = new Button { Content = "返回", Padding = new Thickness(10, 3, 10, 3),
                Background = Brush("#292B31"), Foreground = Brushes.White, BorderBrush = Brush("#626874") };
            button.Click += (_, _) => back();
            DockPanel.SetDock(button, Dock.Left); header.Children.Add(button);
        }
        header.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(back is null ? 0 : 10, 0, 0, 0) });
        return header;
    }

    private static string ClipKindName(ClipKind kind) => kind switch
    {
        ClipKind.Video => "视频",
        ClipKind.Image => "图片",
        ClipKind.Audio => "音频",
        ClipKind.Text => "文字 / 贴纸",
        _ => kind.ToString()
    };

    private static void AddToolButton(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(14, 8, 14, 8),
            Background = Brush("#292B31"), Foreground = Brushes.White, BorderBrush = Brush("#626874") };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }

    private void OpenObjectToolPage(TimelineClip clip, string title, Func<TimelineClip, Action, UIElement> builder)
    {
        OpenSettingsCategory(title, builder(clip, () => ShowSelectedObjectSettings()));
        // Rebuild the same inspector with the current model after an external
        // edit; its old event handlers otherwise retain stale model objects.
        string id = clip.Id;
        _mcpInspectorRefresh = () => OpenObjectToolPage(McpClip(id), title, builder);
    }

    private UIElement BuildSpeedTool(TimelineClip clip, Action back)
    {
        var panel = new StackPanel { Margin = new Thickness(4) };
        panel.Children.Add(SettingsHeader(clip.Kind == ClipKind.Video ? "视频调速" : "音频调速", back));
        panel.Children.Add(new TextBlock
        {
            Text = "设置播放速度和片段时长。应用后，时间线片段会显示可点击的速度提示。",
            Foreground = Brush("#C5CAD4"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        var speedBox = new TextBox
        {
            Text = clip.Speed.ToString("0.###", CultureInfo.CurrentCulture),
            Margin = new Thickness(0, 4, 0, 8)
        };
        var durationBox = new TextBox
        {
            Text = clip.DurationSeconds.ToString("0.###", CultureInfo.CurrentCulture),
            Margin = new Thickness(0, 4, 0, 8)
        };
        panel.Children.Add(new TextBlock { Text = "播放速度（倍）", Foreground = Brush("#D9DCE3") });
        panel.Children.Add(speedBox);
        panel.Children.Add(new TextBlock { Text = "片段时长（秒）", Foreground = Brush("#D9DCE3") });
        panel.Children.Add(durationBox);

        MediaAsset? source = clip.MediaAssetId is null
            ? null : _project.Media.FirstOrDefault(asset => asset.Id == clip.MediaAssetId);
        bool hasFiniteSource = clip.Kind is ClipKind.Video or ClipKind.Audio &&
            source?.DurationSeconds is > 0;
        var preserveSource = new CheckBox
        {
            Content = "变速时保持源画面范围",
            IsChecked = false,
            IsEnabled = hasFiniteSource,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 10)
        };
        panel.Children.Add(preserveSource);

        double sourceSpan = ClipTimeMap.SourceOffset(clip, clip.DurationSeconds);
        speedBox.TextChanged += (_, _) =>
        {
            if (preserveSource.IsChecked != true ||
                !double.TryParse(speedBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture,
                    out double speed) || !double.IsFinite(speed) || speed <= 0 || sourceSpan <= 0)
                return;
            durationBox.Text = (sourceSpan / speed).ToString("0.###", CultureInfo.CurrentCulture);
        };

        var apply = new Button
        {
            Content = "立即应用",
            Padding = new Thickness(12, 7, 12, 7),
            Background = Brush("#FF9957"), Foreground = Brushes.Black,
            Margin = new Thickness(0, 4, 0, 0)
        };
        apply.Click += (_, _) =>
        {
            if (_project.Tracks.FirstOrDefault(track => track.Clips.Contains(clip)) is not { IsLocked: false })
            {
                StatusText.Text = "轨道已锁定或片段已移除 · EDIT-SPEED-STATE";
                return;
            }
            if (!TryNumber(speedBox.Text, .05, 32, "播放速度", out double speed) ||
                !TryNumber(durationBox.Text, 1 / Math.Max(1, _project.Sequence.FramesPerSecond),
                    24 * 3600, "片段时长", out double duration)) return;
            if (source?.DurationSeconds is double sourceDuration &&
                clip.SourceInSeconds + duration * speed > sourceDuration + .001)
            {
                StatusText.Text = "变速后的片段超出源素材范围 · EDIT-SPEED-SOURCE";
                return;
            }
            if (clip.MulticamGroupId is not null && Math.Abs(speed - 1) > .000001)
            {
                StatusText.Text = "多机位切段需保持原速 · MULTICAM-EDIT";
                return;
            }
            SaveUndoState();
            ClipAnimation.KeepRange(clip, 0, duration);
            clip.Speed = speed;
            clip.SpeedAdjustmentApplied = true;
            clip.DurationSeconds = duration;
            clip.TimeRemapPoints.Clear();
            MarkProjectDirty();
            RefreshTimeline();
            SeekPreview(_project.Sequence.PlayheadSeconds);
            StatusText.Text = $"已应用速度 {speed:0.###} 倍";
            // Rebuild the active sub-tab so its values and the back button
            // remain in sync with the object after the timeline redraw.
            OpenObjectToolPage(clip, "视频调速", BuildSpeedTool);
        };
        panel.Children.Add(apply);
        return panel;
    }

    private UIElement BuildTransformTool(TimelineClip clip, Action back)
    {
        var panel = new StackPanel { Margin = new Thickness(4) };
        panel.Children.Add(SettingsHeader(clip.Kind == ClipKind.Image ? "图片变换 / 裁切" : "视频变换 / 裁切", back));
        if (clip.Kind is not (ClipKind.Image or ClipKind.Video))
        {
            panel.Children.Add(new TextBlock { Text = "只有图片和视频支持画面变换及裁切。", Foreground = Brush("#C5CAD4"), TextWrapping = TextWrapping.Wrap });
            return panel;
        }
        AddStabilizationControls(panel, clip);
        double editTime = _project.Sequence.PlayheadSeconds;
        ClipTransform transform = clip.Transform;
        TextBox positionX = AddToolTextBox(panel, "位置 X（像素）", ClipAnimation.Evaluate(clip, "PositionX", editTime));
        TextBox positionY = AddToolTextBox(panel, "位置 Y（像素）", ClipAnimation.Evaluate(clip, "PositionY", editTime));
        TextBox scaleX = AddToolTextBox(panel, "水平缩放 / 拉伸（%）", ClipAnimation.Evaluate(clip, "ScaleX", editTime) * 100);
        TextBox scaleY = AddToolTextBox(panel, "垂直缩放 / 拉伸（%）", ClipAnimation.Evaluate(clip, "ScaleY", editTime) * 100);
        TextBox rotation = AddToolTextBox(panel, "旋转角度（°）", ClipAnimation.Evaluate(clip, "RotationDegrees", editTime));
        TextBox opacity = AddToolTextBox(panel, "透明度（%）", ClipAnimation.Evaluate(clip, "Opacity", editTime) * 100);
        panel.Children.Add(new TextBlock { Text = "裁切（从源画面四边向内，百分比）", Foreground = Brush("#D9DCE3"), Margin = new Thickness(0, 12, 0, 4), FontWeight = FontWeights.SemiBold });
        TextBox cropLeft = AddToolTextBox(panel, "左侧裁切（%）", transform.CropLeft * 100);
        TextBox cropTop = AddToolTextBox(panel, "上方裁切（%）", transform.CropTop * 100);
        TextBox cropRight = AddToolTextBox(panel, "右侧裁切（%）", transform.CropRight * 100);
        TextBox cropBottom = AddToolTextBox(panel, "下方裁切（%）", transform.CropBottom * 100);
        bool applying = false;
        bool ApplyValues(bool showErrors)
        {
            if (applying) return false;
            if (!TryNumberQuiet(positionX.Text, -_project.Sequence.Width * 4, _project.Sequence.Width * 4, out double x) ||
                !TryNumberQuiet(positionY.Text, -_project.Sequence.Height * 4, _project.Sequence.Height * 4, out double y) ||
                !TryNumberQuiet(scaleX.Text, 5, 800, out double sx) || !TryNumberQuiet(scaleY.Text, 5, 800, out double sy) ||
                !TryNumberQuiet(rotation.Text, -36000, 36000, out double angle) || !TryNumberQuiet(opacity.Text, 0, 100, out double alpha) ||
                !TryNumberQuiet(cropLeft.Text, 0, 95, out double left) || !TryNumberQuiet(cropTop.Text, 0, 95, out double top) ||
                !TryNumberQuiet(cropRight.Text, 0, 95, out double right) || !TryNumberQuiet(cropBottom.Text, 0, 95, out double bottom))
            { if (showErrors) StatusText.Text = "变换或裁切参数无效"; return false; }
            if (left + right >= 100 || top + bottom >= 100)
            { if (showErrors) StatusText.Text = "左右裁切之和、上下裁切之和必须小于 100%"; return false; }
            applying = true;
            try
            {
                SaveUndoState();
                ShiftAnimatedVisualProperty(clip, "PositionX", x); ShiftAnimatedVisualProperty(clip, "PositionY", y);
                ShiftAnimatedVisualProperty(clip, "ScaleX", sx / 100); ShiftAnimatedVisualProperty(clip, "ScaleY", sy / 100);
                ShiftAnimatedVisualProperty(clip, "RotationDegrees", angle); ShiftAnimatedVisualProperty(clip, "Opacity", alpha / 100);
                transform.CropLeft = left / 100; transform.CropTop = top / 100; transform.CropRight = right / 100; transform.CropBottom = bottom / 100;
                MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds); StatusText.Text = "变换 / 裁切已即时应用"; return true;
            }
            finally { applying = false; }
        }
        foreach (TextBox field in new[] { positionX, positionY, scaleX, scaleY, rotation, opacity, cropLeft, cropTop, cropRight, cropBottom }) field.LostKeyboardFocus += (_, _) => ApplyValues(false);
        var apply = new Button { Content = "应用当前值", Padding = new Thickness(12, 7, 12, 7), Background = Brush("#FF9957"), Foreground = Brushes.Black, Margin = new Thickness(0, 8, 0, 0) };
        apply.Click += (_, _) => ApplyValues(true);
        var reset = new Button { Content = "重置裁切", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(8, 8, 0, 0) };
        reset.Click += (_, _) => { cropLeft.Text = cropTop.Text = cropRight.Text = cropBottom.Text = "0"; ApplyValues(true); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; buttons.Children.Add(apply); buttons.Children.Add(reset); panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock { Text = "输入框失去焦点或点击应用后立即更新预览和导出设置。也可以在预览区直接拖动对象。", Foreground = Brush("#AEB5C2"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        return panel;
    }

    private UIElement BuildFilterTool(TimelineClip clip, Action back)
    {
        var panel = new StackPanel { Margin = new Thickness(4) }; panel.Children.Add(SettingsHeader("滤镜 / 调色", back));
        panel.Children.Add(new TextBlock { Text = "亮度、对比度、饱和度和其他效果可在此对象上调整；参数修改后立即刷新预览和导出。", Foreground = Brush("#C5CAD4"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        foreach (var effect in clip.Effects)
        {
            panel.Children.Add(new TextBlock { Text = $"● {effect.Name}", Margin = new Thickness(0, 3, 0, 3) });
            foreach (var parameter in VisualEffectPipeline.ParametersFor(effect.PresetId))
            {
                double current = VisualEffectPipeline.GetNumber(effect, parameter);
                var value = AddToolTextBox(panel, parameter.Name, current);
                value.LostKeyboardFocus += (_, _) =>
                {
                    if (!TryNumberQuiet(value.Text, parameter.Minimum, parameter.Maximum, out double next)) return;
                    SaveUndoState(); VisualEffectPipeline.SetNumber(effect, parameter, next);
                    MarkProjectDirty(); SeekPreview(_project.Sequence.PlayheadSeconds);
                };
            }
        }
        AddToolButton(panel, "打开效果 Tab", () => { CategoryList.SelectedIndex = Array.IndexOf(_categories, "效果"); });
        return panel;
    }

    private UIElement BuildAudioTool(TimelineClip clip, Action back)
    {
        var panel = new StackPanel { Margin = new Thickness(4) }; panel.Children.Add(SettingsHeader("均衡器 / 音量", back));
        panel.Children.Add(new TextBlock { Text = "修改后立即作用于当前片段，时间线音量线仍可直接上下拖动。", Foreground = Brush("#C5CAD4"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        AudioSettings settings;
        try { settings = AudioSettings.FromClip(clip); }
        catch (AudioOperationException exception)
        {
            panel.Children.Add(new TextBlock { Text = $"音频设置无法读取：{exception.Message}", Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap });
            return panel;
        }
        AddAudioWorkflowControls(panel, clip);
        TextBox volume = AddToolTextBox(panel, "片段音量（0–400%）", clip.Volume * 100);
        TextBox gain = AddToolTextBox(panel, "额外增益（0–400%）", settings.Gain * 100);
        TextBox pan = AddToolTextBox(panel, "声像（-100 左 / 100 右）", settings.Pan * 100);
        TextBox eqHz = AddToolTextBox(panel, "均衡中心频率（Hz）", settings.EqualizerHz);
        TextBox eqGain = AddToolTextBox(panel, "均衡增益（dB）", settings.EqualizerGainDb);
        TextBox highPass = AddToolTextBox(panel, "高通（Hz，0 关闭）", settings.HighPassHz);
        TextBox lowPass = AddToolTextBox(panel, "低通（Hz，0 关闭）", settings.LowPassHz);
        var compressor = new CheckBox { Content = "启用压缩器", IsChecked = settings.CompressorEnabled, Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 8) };
        panel.Children.Add(compressor);
        TextBox threshold = AddToolTextBox(panel, "压缩阈值（dB）", settings.CompressorThresholdDb);
        TextBox ratio = AddToolTextBox(panel, "压缩比", settings.CompressorRatio);
        bool applying = false;
        bool ApplyAudio(bool showErrors)
        {
            if (applying) return false;
            if (!TryNumberQuiet(volume.Text, 0, 400, out double volumeValue) || !TryNumberQuiet(gain.Text, 0, 400, out double gainValue) ||
                !TryNumberQuiet(pan.Text, -100, 100, out double panValue) || !TryNumberQuiet(eqHz.Text, 0, 20000, out double eqFrequency) ||
                !TryNumberQuiet(eqGain.Text, -24, 24, out double eqDb) || !TryNumberQuiet(highPass.Text, 0, 20000, out double high) ||
                !TryNumberQuiet(lowPass.Text, 0, 20000, out double low) || !TryNumberQuiet(threshold.Text, -60, 0, out double thresholdValue) ||
                !TryNumberQuiet(ratio.Text, 1, 20, out double ratioValue))
            { if (showErrors) StatusText.Text = "音频参数无效"; return false; }
            applying = true;
            try
            {
                SaveUndoState(); clip.Volume = volumeValue / 100; settings.Gain = gainValue / 100; settings.Pan = panValue / 100;
                settings.EqualizerHz = eqFrequency; settings.EqualizerGainDb = eqDb; settings.HighPassHz = high; settings.LowPassHz = low;
                settings.CompressorEnabled = compressor.IsChecked == true; settings.CompressorThresholdDb = thresholdValue; settings.CompressorRatio = ratioValue;
                var latest = AudioSettings.FromClip(clip);
                settings.NormalizeEnabled = latest.NormalizeEnabled; settings.TargetLufs = latest.TargetLufs; settings.TargetTruePeakDb = latest.TargetTruePeakDb;
                settings.NormalizationGainDb = latest.NormalizationGainDb; settings.MeasuredLufs = latest.MeasuredLufs; settings.MeasuredTruePeakDb = latest.MeasuredTruePeakDb;
                settings.Validate(); settings.Save(clip); MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
                StatusText.Text = "均衡器 / 音量已即时应用"; return true;
            }
            catch (AudioOperationException exception) { if (showErrors) ReportAudioError(exception); return false; }
            finally { applying = false; }
        }
        foreach (TextBox field in new[] { volume, gain, pan, eqHz, eqGain, highPass, lowPass, threshold, ratio }) field.LostKeyboardFocus += (_, _) => ApplyAudio(false);
        compressor.Checked += (_, _) => ApplyAudio(false); compressor.Unchecked += (_, _) => ApplyAudio(false);
        var apply = new Button { Content = "应用当前值", Padding = new Thickness(12, 7, 12, 7), Background = Brush("#FF9957"), Foreground = Brushes.Black, Margin = new Thickness(0, 8, 0, 0) };
        apply.Click += (_, _) => ApplyAudio(true); panel.Children.Add(apply);
        return panel;
    }

    private static TextBox AddToolTextBox(Panel panel, string label, double value)
    {
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brush("#D9DCE3"), Margin = new Thickness(0, 4, 0, 2) });
        var box = new TextBox { Text = value.ToString("0.###", CultureInfo.CurrentCulture), Background = Brush("#282B32"), Foreground = Brushes.White, BorderBrush = Brush("#565B65"), Padding = new Thickness(7, 5, 7, 5), Margin = new Thickness(0, 0, 0, 6) };
        panel.Children.Add(box); return box;
    }

    private static bool TryNumberQuiet(string text, double minimum, double maximum, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value) && value >= minimum && value <= maximum;


}
