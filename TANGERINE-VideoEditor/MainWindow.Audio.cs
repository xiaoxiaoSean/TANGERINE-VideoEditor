using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Provides local audio clip processing and a track mixer. Settings are kept
/// in the project file and rendered by the same FFmpeg filter builder used by
/// export, so the controls remain non-destructive and undoable.
/// </summary>
public partial class MainWindow
{
    private void InitializeAudioCommands()
    {
        AudioClipPropertiesMenuItem.Click += (_, _) =>
        {
            if (_selectedClip is not null) ShowAudioClipProperties(_selectedClip);
            else StatusText.Text = "请先选择音频或含音轨的视频片段";
        };
        AudioMixerMenuItem.Click += (_, _) => ShowAudioMixer();
        VoiceOverMenuItem.Click += async (_, _) => await RecordVoiceOverAsync();
    }

    private void ShowAudioClipProperties(TimelineClip clip)
    {
        SelectClip(clip);
        OpenObjectToolPage(clip, "均衡器 / 音量", BuildAudioTool);
        return;
#pragma warning disable CS0162
        TimelineTrack? track = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
        if (track is null || track.IsLocked)
        {
            StatusText.Text = track is null ? "片段已不在当前项目中" : "轨道已锁定";
            return;
        }
#pragma warning restore CS0162
        if (clip.Kind is not (ClipKind.Audio or ClipKind.Video))
        {
            StatusText.Text = "所选片段不包含可处理的音频";
            return;
        }
        if (clip.Kind == ClipKind.Video &&
            _project.Media.FirstOrDefault(item => item.Id == clip.MediaAssetId)?.HasAudio != true)
        {
            StatusText.Text = "所选视频没有可处理的音轨";
            return;
        }
        try
        {
            AudioSettings settings = AudioSettings.FromClip(clip);
            ShowAudioSettingsDialog($"音频属性 · {clip.Name}", settings, newVolume =>
            {
                TimelineTrack? current = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
                if (current is null || current.IsLocked) { StatusText.Text = "片段不可编辑"; return false; }
                settings.Validate();
                SaveUndoState();
                if (newVolume.HasValue) clip.Volume = newVolume.Value;
                settings.Save(clip);
                MarkProjectDirty();
                StatusText.Text = $"已更新“{clip.Name}”的音频设置";
                return true;
            }, clip);
        }
        catch (AudioOperationException exception) { ReportAudioError(exception); }
    }

    private void ShowAudioMixer()
    {
        var tracks = _project.Tracks.Where(track => track.Kind is TrackKind.Audio or TrackKind.Video)
            .OrderBy(track => track.Order).ToArray();
        if (tracks.Length == 0) { StatusText.Text = "当前项目没有可混音的轨道"; return; }
        var dialog = new Window
        {
            Title = "音轨混音器", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Max(1, SystemParameters.PrimaryScreenWidth * .55),
            Height = Math.Max(1, SystemParameters.PrimaryScreenHeight * .6),
            Background = Brush("#191B20"), Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI")
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        var heading = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = "轨道", Foreground = Brush("#A8AFBA") });
        var gainHeading = new TextBlock { Text = "增益（0–4）", Foreground = Brush("#A8AFBA") };
        var panHeading = new TextBlock { Text = "声像（-1–1）", Foreground = Brush("#A8AFBA") };
        Grid.SetColumn(gainHeading, 1); Grid.SetColumn(panHeading, 2);
        heading.Children.Add(gainHeading); heading.Children.Add(panHeading);
        panel.Children.Add(heading);
        foreach (TimelineTrack track in tracks)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = track.Name, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis });
            AudioSettings settings;
            try { settings = AudioSettings.FromTrack(track); }
            catch (AudioOperationException exception) { ReportAudioError(exception); return; }
            var gain = new TextBox { Text = settings.Gain.ToString(CultureInfo.CurrentCulture),
                ToolTip = "轨道增益，0 至 4", Margin = new Thickness(6, 0, 6, 0) };
            var pan = new TextBox { Text = settings.Pan.ToString(CultureInfo.CurrentCulture),
                ToolTip = "左右声像，-1 至 1", Margin = new Thickness(6, 0, 6, 0) };
            foreach (TextBox box in new[] { gain, pan })
            {
                box.Background = Brush("#282B32");
                box.Foreground = Brushes.White;
                box.CaretBrush = Brushes.White;
                box.BorderBrush = Brush("#565B65");
                box.Padding = new Thickness(8, 6, 8, 6);
            }
            var edit = new Button { Content = "效果…", Padding = new Thickness(12, 5, 12, 5), IsEnabled = !track.IsLocked };
            Grid.SetColumn(gain, 1); Grid.SetColumn(pan, 2); Grid.SetColumn(edit, 3);
            row.Children.Add(gain); row.Children.Add(pan); row.Children.Add(edit);
            gain.IsEnabled = pan.IsEnabled = !track.IsLocked;
            gain.LostKeyboardFocus += (_, _) => ApplyMixerValue(track, gain, true);
            pan.LostKeyboardFocus += (_, _) => ApplyMixerValue(track, pan, false);
            edit.Click += (_, _) =>
            {
                // Commit the quick controls before opening the complete
                // processor editor so their latest values are shown there.
                ApplyMixerValue(track, gain, true);
                ApplyMixerValue(track, pan, false);
                try
                {
                    AudioSettings current = AudioSettings.FromTrack(track);
                    ShowAudioSettingsDialog($"轨道效果 · {track.Name}", current, _ =>
                    {
                        if (!_project.Tracks.Contains(track) || track.IsLocked) return false;
                        current.Validate();
                        SaveUndoState(); current.Save(track); MarkProjectDirty();
                        gain.Text = current.Gain.ToString(CultureInfo.CurrentCulture);
                        pan.Text = current.Pan.ToString(CultureInfo.CurrentCulture);
                        return true;
                    });
                }
                catch (AudioOperationException exception) { ReportAudioError(exception); }
            };
            panel.Children.Add(row);
        }
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.ShowDialog();
    }

    private void ApplyMixerValue(TimelineTrack track, TextBox box, bool isGain)
    {
        if (track.IsLocked || !_project.Tracks.Contains(track)) return;
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double number) ||
            !double.IsFinite(number) || number < (isGain ? 0 : -1) || number > (isGain ? 4 : 1))
        {
            StatusText.Text = isGain ? "轨道增益必须在 0 至 4 之间" : "声像必须在 -1 至 1 之间";
            return;
        }
        try
        {
            AudioSettings settings = AudioSettings.FromTrack(track);
            if (isGain && Math.Abs(settings.Gain - number) < .00001 ||
                !isGain && Math.Abs(settings.Pan - number) < .00001) return;
            if (isGain) settings.Gain = number; else settings.Pan = number;
            settings.Validate();
            SaveUndoState();
            settings.Save(track);
            MarkProjectDirty();
            StatusText.Text = $"已更新轨道“{track.Name}”的{(isGain ? "增益" : "声像")}";
        }
        catch (AudioOperationException exception) { ReportAudioError(exception); }
    }

    private void ShowAudioSettingsDialog(string title, AudioSettings settings, Func<double?, bool> commit, TimelineClip? clip = null)
    {
        var dialog = new Window
        {
            Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Max(1, SystemParameters.PrimaryScreenWidth * .42),
            Height = Math.Max(1, SystemParameters.PrimaryScreenHeight * .72),
            Background = Brush("#191B20"), Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI")
        };
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58, GridUnitType.Star) });
        int row = 0;
        var category = AddCombo(form, ref row, "基本声音类型", ["未分类", "对白", "音乐", "音效", "环境声"], settings.Category);
        category.IsEditable = false;
        TextBox? clipVolume = clip is null ? null : AddTextBox(form, ref row, "片段音量（0–4）", clip.Volume.ToString(CultureInfo.CurrentCulture));
        var gain = AddTextBox(form, ref row, "额外增益（0–4）", settings.Gain.ToString(CultureInfo.CurrentCulture));
        var pan = AddTextBox(form, ref row, "声像（-1 左 / 1 右）", settings.Pan.ToString(CultureInfo.CurrentCulture));
        var channelMode = AddCombo(form, ref row, "声道映射",
            ["立体声", "单声道", "仅左声道", "仅右声道"], settings.ChannelMode);
        channelMode.IsEditable = false;
        var highPass = AddTextBox(form, ref row, "高通 Hz（0 关闭）", settings.HighPassHz.ToString(CultureInfo.CurrentCulture));
        var lowPass = AddTextBox(form, ref row, "低通 Hz（0 关闭）", settings.LowPassHz.ToString(CultureInfo.CurrentCulture));
        var equalizerHz = AddTextBox(form, ref row, "均衡中心频率 Hz", settings.EqualizerHz.ToString(CultureInfo.CurrentCulture));
        var equalizerGain = AddTextBox(form, ref row, "均衡增益 dB（-24–24）", settings.EqualizerGainDb.ToString(CultureInfo.CurrentCulture));
        var noiseReduction = AddCheck(form, ref row, "启用本地频谱降噪", settings.NoiseReductionEnabled);
        var noiseAmount = AddTextBox(form, ref row, "降噪强度 dB（0–40）", settings.NoiseReductionDb.ToString(CultureInfo.CurrentCulture));
        var compressor = AddCheck(form, ref row, "启用压缩器", settings.CompressorEnabled);
        var threshold = AddTextBox(form, ref row, "压缩阈值 dB（-60–0）", settings.CompressorThresholdDb.ToString(CultureInfo.CurrentCulture));
        var ratio = AddTextBox(form, ref row, "压缩比（1–20）", settings.CompressorRatio.ToString(CultureInfo.CurrentCulture));
        var echo = AddCheck(form, ref row, "启用回声", settings.EchoEnabled);
        var echoDelay = AddTextBox(form, ref row, "回声延迟 ms（1–2000）", settings.EchoDelayMs.ToString(CultureInfo.CurrentCulture));
        var echoDecay = AddTextBox(form, ref row, "回声衰减（0–0.9）", settings.EchoDecay.ToString(CultureInfo.CurrentCulture));
        var limiter = AddCheck(form, ref row, "启用限幅器", settings.LimiterEnabled);
        var ceiling = AddTextBox(form, ref row, "限幅峰值（0.05–1）", settings.LimiterCeiling.ToString(CultureInfo.CurrentCulture));
        var ducking = AddCheck(form, ref row, "启用对白期间音乐闪避（片段）", settings.AutoDuckingEnabled);
        ducking.IsEnabled = clip is not null;
        var duckingGain = AddTextBox(form, ref row, "闪避后音量（0.05–1）",
            settings.DuckingGain.ToString(CultureInfo.CurrentCulture));
        var duckingFade = AddTextBox(form, ref row, "闪避过渡秒数（0–2）",
            settings.DuckingFadeSeconds.ToString(CultureInfo.CurrentCulture));
        Grid.SetRow(form, 0);
        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "取消", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "应用", Padding = new Thickness(16, 6, 16, 6) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        apply.Click += (_, _) =>
        {
            if ((clipVolume is not null && !TryNumber(clipVolume.Text, 0, 4, "片段音量", out _)) ||
                !TryNumber(gain.Text, 0, 4, "额外增益", out double gainValue) ||
                !TryNumber(pan.Text, -1, 1, "声像", out double panValue) ||
                !TryNumber(highPass.Text, 0, 20000, "高通频率", out double hpValue) ||
                !TryNumber(lowPass.Text, 0, 20000, "低通频率", out double lpValue) ||
                !TryNumber(equalizerHz.Text, 0, 20000, "均衡频率", out double eqHzValue) ||
                !TryNumber(equalizerGain.Text, -24, 24, "均衡增益", out double eqGainValue) ||
                !TryNumber(noiseAmount.Text, 0, 40, "降噪强度", out double nrValue) ||
                !TryNumber(threshold.Text, -60, 0, "压缩阈值", out double thresholdValue) ||
                !TryNumber(ratio.Text, 1, 20, "压缩比", out double ratioValue) ||
                !TryNumber(echoDelay.Text, 1, 2000, "回声延迟", out double delayValue) ||
                !TryNumber(echoDecay.Text, 0, .9, "回声衰减", out double decayValue) ||
                !TryNumber(ceiling.Text, .05, 1, "限幅峰值", out double ceilingValue) ||
                !TryNumber(duckingGain.Text, .05, 1, "闪避后音量", out double duckGainValue) ||
                !TryNumber(duckingFade.Text, 0, 2, "闪避过渡时间", out double duckFadeValue)) return;
            settings.Category = category.Text.Trim(); settings.Gain = gainValue; settings.Pan = panValue;
            settings.ChannelMode = channelMode.Text;
            settings.HighPassHz = hpValue; settings.LowPassHz = lpValue;
            settings.EqualizerHz = eqHzValue; settings.EqualizerGainDb = eqGainValue;
            settings.NoiseReductionEnabled = noiseReduction.IsChecked == true; settings.NoiseReductionDb = nrValue;
            settings.CompressorEnabled = compressor.IsChecked == true;
            settings.CompressorThresholdDb = thresholdValue; settings.CompressorRatio = ratioValue;
            settings.EchoEnabled = echo.IsChecked == true;
            settings.EchoDelayMs = delayValue; settings.EchoDecay = decayValue;
            settings.LimiterEnabled = limiter.IsChecked == true; settings.LimiterCeiling = ceilingValue;
            settings.AutoDuckingEnabled = clip is not null && ducking.IsChecked == true;
            settings.DuckingGain = duckGainValue; settings.DuckingFadeSeconds = duckFadeValue;
            try
            {
                double? volume = clipVolume is null ? null :
                    double.Parse(clipVolume.Text, NumberStyles.Float, CultureInfo.CurrentCulture);
                if (!commit(volume)) return;
                dialog.DialogResult = true;
            }
            catch (AudioOperationException exception) { ReportAudioError(exception); }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(apply);
        Grid.SetRow(buttons, 1); root.Children.Add(buttons);
        dialog.Content = root;
        dialog.ShowDialog();
    }

    private void ReportAudioError(AudioOperationException exception)
    {
        StatusText.Text = $"音频操作失败 · {exception.StageCode}";
        MessageBox.Show(this, $"{exception.Message}\n阶段代码：{exception.StageCode}", "音频操作失败",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async Task SyncSelectedAudioAsync()
    {
        var selection = ExistingSelection().Where(clip => clip.Kind is ClipKind.Audio or ClipKind.Video)
            .Distinct().ToArray();
        if (selection.Length != 2)
        {
            StatusText.Text = "请按住 Ctrl 选择两个含音轨的片段再执行波形同步";
            return;
        }
        TimelineClip reference = selection.OrderBy(clip => clip.StartSeconds).First();
        TimelineClip moving = selection.First(clip => !ReferenceEquals(clip, reference));
        TimelineTrack? movingTrack = _project.Tracks.FirstOrDefault(track => track.Clips.Contains(moving));
        MediaAsset? referenceAsset = _project.Media.FirstOrDefault(item => item.Id == reference.MediaAssetId);
        MediaAsset? movingAsset = _project.Media.FirstOrDefault(item => item.Id == moving.MediaAssetId);
        if (movingTrack is null || movingTrack.IsLocked || referenceAsset is null || movingAsset is null ||
            !double.IsFinite(reference.StartSeconds) || reference.StartSeconds < 0 ||
            (reference.Kind == ClipKind.Video && !referenceAsset.HasAudio) ||
            (moving.Kind == ClipKind.Video && !movingAsset.HasAudio))
        {
            StatusText.Text = "两个片段都须包含音频，且待移动轨道不能锁定";
            return;
        }
        ProjectDocument targetProject = _project;
        long revision = _projectRevision;
        TimelineClip referenceSnapshot;
        TimelineClip movingSnapshot;
        try
        {
            referenceSnapshot = CloneClip(reference);
            movingSnapshot = CloneClip(moving);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            ReportAudioError(new AudioOperationException(AudioStageCode.Read,
                "无法读取待同步片段的设置。", exception));
            return;
        }
        double referenceStart = reference.StartSeconds;
        string referencePath = referenceAsset.EffectiveExportPath;
        string movingPath = movingAsset.EffectiveExportPath;
        StatusText.Text = "正在分析两个片段的音频波形…";
        try
        {
            double offset = await AudioWaveformAnalysis.FindOffsetAsync(referenceSnapshot,
                referencePath, movingSnapshot, movingPath);
            if (!ReferenceEquals(targetProject, _project) || revision != _projectRevision ||
                !_project.Tracks.Any(track => track.Clips.Contains(moving) && !track.IsLocked))
            {
                StatusText.Text = "音频同步已中止：分析期间项目发生变化";
                return;
            }
            double newStart = referenceStart + offset;
            if (!double.IsFinite(newStart) || newStart < 0 || newStart > 24 * 3600)
            {
                StatusText.Text = "同步结果超出时间线范围，请先调整参考片段位置";
                return;
            }
            SaveUndoState(); moving.StartSeconds = newStart;
            MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
            StatusText.Text = $"已按波形同步“{moving.Name}”，偏移 {offset:+0.###;-0.###;0} 秒";
        }
        catch (AudioOperationException exception) { ReportAudioError(exception); }
        catch (OperationCanceledException) { StatusText.Text = "音频同步已取消"; }
    }

    /// <summary>
    /// Aligns two clips using their embedded source timecodes. FFprobe tags
    /// are retained in the project metadata at import, so this operation does
    /// not need a decoder pass or any network service.
    /// </summary>
    private void SyncSelectedAudioByTimecode()
    {
        TimelineClip[] selection = ExistingSelection()
            .Where(clip => clip.Kind is ClipKind.Audio or ClipKind.Video)
            .Distinct().ToArray();
        if (selection.Length != 2)
        {
            StatusText.Text = "请按住 Ctrl 选择两个含源时间码的片段";
            return;
        }
        TimelineClip reference = selection.OrderBy(clip => clip.StartSeconds).First();
        TimelineClip moving = selection.First(clip => !ReferenceEquals(clip, reference));
        TimelineTrack? movingTrack = _project.Tracks.FirstOrDefault(track => track.Clips.Contains(moving));
        MediaAsset? referenceAsset = _project.Media.FirstOrDefault(item => item.Id == reference.MediaAssetId);
        MediaAsset? movingAsset = _project.Media.FirstOrDefault(item => item.Id == moving.MediaAssetId);
        if (movingTrack is null || movingTrack.IsLocked || referenceAsset is null || movingAsset is null ||
            !double.IsFinite(reference.StartSeconds) || reference.StartSeconds < 0)
        {
            StatusText.Text = "待同步片段或轨道不可编辑";
            return;
        }
        if (!TrySourceTimecode(referenceAsset, out double referenceTime,
                out int referenceTimecodeRate, out bool referenceDropFrame) ||
            !TrySourceTimecode(movingAsset, out double movingTime,
                out int movingTimecodeRate, out bool movingDropFrame))
        {
            StatusText.Text = "两个源文件都需要有效的 timecode 元数据";
            return;
        }
        if (referenceTimecodeRate != movingTimecodeRate || referenceDropFrame != movingDropFrame ||
            Math.Abs(referenceAsset.FramesPerSecond.GetValueOrDefault() -
                movingAsset.FramesPerSecond.GetValueOrDefault()) > .01)
        {
            StatusText.Text = "源文件的时间码帧率或丢帧方式不同，无法安全同步";
            return;
        }
        if (reference.IsReversed || moving.IsReversed ||
            Math.Abs(reference.Speed - 1) > .000001 || Math.Abs(moving.Speed - 1) > .000001 ||
            reference.TimeRemapPoints is null || moving.TimeRemapPoints is null ||
            reference.TimeRemapPoints.Count > 0 || moving.TimeRemapPoints.Count > 0)
        {
            StatusText.Text = "时间码同步需要正向、原速片段";
            return;
        }
        // A source timecode marks its file's zero point. The later source
        // frame must start later on the sequence clock to align both clocks.
        double delta = (movingTime + moving.SourceInSeconds) -
            (referenceTime + reference.SourceInSeconds);
        double newStart = reference.StartSeconds + delta;
        if (!double.IsFinite(newStart) || newStart < 0 || newStart > 24 * 3600)
        {
            StatusText.Text = "时间码同步结果超出时间线范围";
            return;
        }
        SaveUndoState();
        moving.StartSeconds = newStart;
        MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = $"已按源时间码同步“{moving.Name}”，偏移 {delta:+0.###;-0.###;0} 秒";
    }

    private static bool TrySourceTimecode(MediaAsset asset, out double seconds) =>
        TrySourceTimecode(asset, out seconds, out _, out _);

    private static bool TrySourceTimecode(MediaAsset asset, out double seconds,
        out int nominalRate, out bool dropFrame)
    {
        seconds = 0;
        nominalRate = 0;
        dropFrame = false;
        if (asset.Metadata is null) return false;
        string? timecode = asset.Metadata.FirstOrDefault(tag =>
            string.Equals(tag.Key, "timecode", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(timecode)) return false;
        dropFrame = timecode.Contains(';');
        string[] parts = timecode.Replace(';', ':').Split(':');
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int wholeSeconds) ||
            !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int frames)) return false;
        double rate = asset.FramesPerSecond ?? 0;
        if (!double.IsFinite(rate) || rate <= 0 || rate > 120) return false;
        nominalRate = (int)Math.Round(rate);
        if (hours is < 0 or > 23 || minutes is < 0 or > 59 || wholeSeconds is < 0 or > 59 ||
            nominalRate is < 1 or > 120 ||
            frames < 0 || frames >= nominalRate) return false;
        // SMPTE drop-frame timecode skips frame labels at every minute that
        // is not a multiple of ten; no video frame is actually discarded.
        int droppedPerMinute = nominalRate switch
        {
            30 when Math.Abs(rate - 30000d / 1001) < .01 => 2,
            60 when Math.Abs(rate - 60000d / 1001) < .01 => 4,
            _ => 0
        };
        if (dropFrame && (droppedPerMinute == 0 ||
            (minutes % 10 != 0 && wholeSeconds == 0 && frames < droppedPerMinute))) return false;
        long totalMinutes = hours * 60L + minutes;
        long frameNumber = ((hours * 3600L + minutes * 60L + wholeSeconds) * nominalRate + frames) -
            (dropFrame ? droppedPerMinute * (totalMinutes - totalMinutes / 10) : 0);
        seconds = frameNumber / rate;
        return true;
    }

    private void ShowMusicRemixDialog(TimelineClip clip)
    {
        TimelineTrack? track = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
        MediaAsset? asset = _project.Media.FirstOrDefault(item => item.Id == clip.MediaAssetId);
        if (track is null || track.IsLocked || asset is null || clip.Kind != ClipKind.Audio)
        { StatusText.Text = "请选择未锁定的音乐片段"; return; }
        if (clip.IsReversed || Math.Abs(clip.Speed - 1) > .001 ||
            clip.TimeRemapPoints is null || clip.TimeRemapPoints.Count > 0 ||
            asset.DurationSeconds is not > 0)
        { StatusText.Text = "音乐重定时需要正向、原速且已读取时长的音频片段"; return; }
        var dialog = new Window
        {
            Title = "音乐重定时", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Max(1, SystemParameters.PrimaryScreenWidth * .36),
            Height = Math.Max(1, SystemParameters.PrimaryScreenHeight * .36),
            Background = Brush("#191B20"), Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI")
        };
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = "把音乐按原速分段重排至目标时长，接缝会短暂交叠并淡化。分析出的 BPM 可手动修正。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        });
        var bpm = new TextBox { Text = "120", Margin = new Thickness(0, 0, 0, 8), ToolTip = "节拍 BPM，40 至 240" };
        var estimate = new Button { Content = "分析节拍", Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 0, 8) };
        var duration = new TextBox { Text = clip.DurationSeconds.ToString("0.###", CultureInfo.CurrentCulture),
            Margin = new Thickness(0, 0, 0, 10), ToolTip = "目标时长（秒）" };
        foreach (TextBox box in new[] { bpm, duration })
        {
            box.Background = Brush("#282B32");
            box.Foreground = Brushes.White;
            box.CaretBrush = Brushes.White;
            box.BorderBrush = Brush("#565B65");
            box.Padding = new Thickness(8, 6, 8, 6);
        }
        root.Children.Add(new TextBlock { Text = "节拍 BPM" }); root.Children.Add(bpm);
        root.Children.Add(estimate);
        root.Children.Add(new TextBlock { Text = "目标时长（秒）" }); root.Children.Add(duration);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "生成", Padding = new Thickness(14, 6, 14, 6) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        estimate.Click += async (_, _) =>
        {
            estimate.IsEnabled = false;
            StatusText.Text = "正在分析本地音乐节拍…";
            ProjectDocument sourceProject = _project;
            long sourceRevision = _projectRevision;
            try
            {
                double result = await AudioWaveformAnalysis.EstimateTempoAsync(asset.EffectiveExportPath,
                    clip.SourceInSeconds, asset.DurationSeconds.Value - clip.SourceInSeconds, sourceStreamIndex: clip.SourceAudioStreamIndex);
                if (!ReferenceEquals(sourceProject, _project) ||
                    sourceRevision != _projectRevision || !track.Clips.Contains(clip)) return;
                bpm.Text = result.ToString("0", CultureInfo.CurrentCulture);
                StatusText.Text = $"估计节拍：{result:0} BPM，请试听并按需修正";
            }
            catch (AudioOperationException exception) { ReportAudioError(exception); }
            catch (OperationCanceledException) { StatusText.Text = "节拍分析已取消"; }
            finally { estimate.IsEnabled = true; }
        };
        apply.Click += (_, _) =>
        {
            if (!TryNumber(bpm.Text, 40, 240, "节拍 BPM", out double beatsPerMinute) ||
                !TryNumber(duration.Text, .1, 24 * 3600, "目标时长", out double targetDuration)) return;
            if (!_project.Tracks.Any(item => ReferenceEquals(item, track) &&
                item.Clips.Contains(clip) && !item.IsLocked) ||
                !_project.Media.Contains(asset))
            { StatusText.Text = "音乐片段已不可编辑"; return; }
            IReadOnlyList<MusicRemixSegment> segments;
            try
            {
                segments = MusicRemixPlanner.Plan(clip.StartSeconds, clip.SourceInSeconds,
                    asset.DurationSeconds.Value, targetDuration, beatsPerMinute);
            }
            catch (AudioOperationException exception) { ReportAudioError(exception); return; }
            var pieces = new List<TimelineClip>(segments.Count);
            try
            {
                foreach (MusicRemixSegment segment in segments)
                {
                    var piece = CloneClip(clip);
                    // The new arrangement changes the clip-local clock and
                    // may reuse source phrases. Old keyframes and transitions
                    // no longer identify a reliable musical position.
                    piece.Keyframes.Clear();
                    piece.Id = Guid.NewGuid().ToString("N");
                    piece.StartSeconds = segment.StartSeconds;
                    piece.SourceInSeconds = segment.SourceInSeconds;
                    piece.DurationSeconds = segment.DurationSeconds;
                    piece.Name = $"{clip.Name} · {pieces.Count + 1}";
                    piece.GroupId = null;
                    piece.LinkGroupId = null;
                    piece.Transitions.Clear();
                    piece.AdditionalData ??= [];
                    piece.AdditionalData[AudioProcessing.RemixFadeInKey] = JsonSerializer.SerializeToElement(segment.FadeInSeconds);
                    piece.AdditionalData[AudioProcessing.RemixFadeOutKey] = JsonSerializer.SerializeToElement(segment.FadeOutSeconds);
                    pieces.Add(piece);
                }
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                ReportAudioError(new AudioOperationException(AudioStageCode.Read,
                    "无法读取音乐片段的设置。", exception));
                return;
            }
            SaveUndoState();
            track.Clips.Remove(clip);
            track.Clips.AddRange(pieces);
            _selectedClip = pieces[0]; _selectedClips.Clear(); _selectedClips.Add(pieces[0]);
            MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
            StatusText.Text = $"已把音乐重排为 {pieces.Count} 段，时长 {targetDuration:0.##} 秒";
            dialog.DialogResult = true;
        };
        buttons.Children.Add(cancel); buttons.Children.Add(apply); root.Children.Add(buttons);
        dialog.Content = new ScrollViewer
        {
            Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        dialog.ShowDialog();
    }

    private async Task RecordVoiceOverAsync()
    {
        IReadOnlyList<CaptureDevice> devices;
        StatusText.Text = "正在查询本机麦克风…";
        try { devices = await Task.Run(WaveInRecorder.GetDevices); }
        catch (AudioOperationException exception) { ReportAudioError(exception); return; }
        if (devices.Count == 0) { StatusText.Text = "没有找到可用的麦克风"; return; }
        var picker = new SaveFileDialog
        {
            Title = "保存画外音录音", Filter = "WAV 音频 (*.wav)|*.wav",
            DefaultExt = ".wav", AddExtension = true,
            FileName = $"画外音_{DateTime.Now:yyyyMMdd_HHmmss}.wav"
        };
        if (picker.ShowDialog(this) != true) return;
        ProjectDocument targetProject = _project;
        long targetRevision = _projectRevision;
        double startSeconds = Math.Max(0, _project.Sequence.PlayheadSeconds);
        if (!double.IsFinite(startSeconds) || startSeconds >= 24 * 3600)
        { StatusText.Text = "播放头超出可录音的时间线范围"; return; }
        var dialog = new Window
        {
            Title = "录制画外音", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Max(1, SystemParameters.PrimaryScreenWidth * .3),
            Height = Math.Max(1, SystemParameters.PrimaryScreenHeight * .24),
            Background = Brush("#191B20"), Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI")
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        var label = new TextBlock { Text = "麦克风", Margin = new Thickness(0, 0, 0, 8) };
        var deviceBox = new ComboBox { ItemsSource = devices, DisplayMemberPath = nameof(CaptureDevice.Name),
            SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 12),
            Background = Brush("#282B32"), Foreground = Brushes.White,
            BorderBrush = Brush("#565B65") };
        var status = new TextBlock { Text = "点击开始录音。文件会在停止后加入播放头位置。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var start = new Button { Content = "开始录音", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        var stop = new Button { Content = "停止并加入时间线", Padding = new Thickness(14, 6, 14, 6), IsEnabled = false };
        var recorder = new WaveInRecorder();
        byte[]? samples = null;
        bool captureBusy = false;
        bool closingAfterStop = false;
        dialog.Closing += (_, args) =>
        {
            if (!captureBusy || closingAfterStop) return;
            args.Cancel = true;
            status.Text = "正在完成录音设备操作，请稍候…";
        };
        start.Click += async (_, _) =>
        {
            if (deviceBox.SelectedItem is not CaptureDevice device) return;
            captureBusy = true;
            start.IsEnabled = false;
            deviceBox.IsEnabled = false;
            try
            {
                await Task.Run(() => recorder.Start(device.Id));
                stop.IsEnabled = true; status.Text = "正在录音…";
            }
            catch (AudioOperationException exception)
            {
                ReportAudioError(exception);
                start.IsEnabled = true;
                deviceBox.IsEnabled = true;
            }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or
                InvalidOperationException or IOException)
            {
                ReportAudioError(new AudioOperationException(AudioStageCode.Capture,
                    "无法开始录音。", exception));
                start.IsEnabled = true;
                deviceBox.IsEnabled = true;
            }
            finally { captureBusy = false; }
        };
        stop.Click += async (_, _) =>
        {
            captureBusy = true;
            stop.IsEnabled = false;
            try
            {
                samples = await Task.Run(recorder.Stop);
                if (samples.Length == 0)
                {
                    status.Text = "没有录到声音，请重新录制。";
                    start.IsEnabled = true; stop.IsEnabled = false; deviceBox.IsEnabled = true;
                    return;
                }
                closingAfterStop = true;
                dialog.DialogResult = true;
            }
            catch (AudioOperationException exception)
            {
                ReportAudioError(exception);
                status.Text = "录音未能完成，可关闭窗口后重试。";
            }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or
                InvalidOperationException or IOException)
            {
                ReportAudioError(new AudioOperationException(AudioStageCode.Capture,
                    "无法停止录音。", exception));
                status.Text = "录音未能完成，可关闭窗口后重试。";
            }
            finally { captureBusy = false; }
        };
        dialog.Closed += async (_, _) =>
        {
            try { await Task.Run(recorder.Dispose); }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or
                InvalidOperationException)
            {
                ReportAudioError(new AudioOperationException(AudioStageCode.Capture,
                    "无法释放录音设备。", exception));
            }
        };
        buttons.Children.Add(start); buttons.Children.Add(stop);
        panel.Children.Add(label); panel.Children.Add(deviceBox); panel.Children.Add(status); panel.Children.Add(buttons);
        dialog.Content = panel;
        if (dialog.ShowDialog() != true || samples is null) return;
        try
        {
            StatusText.Text = "正在检查录音文件空间并写入 WAV…";
            await EnsureVoiceOverSpaceAsync(picker.FileName, samples.LongLength);
            await WaveInRecorder.WriteWaveAsync(picker.FileName, samples);
            if (!ReferenceEquals(_project, targetProject) || targetRevision != _projectRevision)
            { StatusText.Text = "录音已保存，但项目在录音期间发生变化，请手动导入 WAV 文件"; return; }
            double durationSeconds = samples.Length / 96000d;
            if (durationSeconds < .01 || startSeconds + durationSeconds > 24 * 3600)
            {
                StatusText.Text = "录音未能加入时间线：时长过短或超出 24 小时范围，WAV 已保存";
                return;
            }
            // The WAV is a project asset, so reserve room for the complete
            // portable .tvep before recording can be committed to the bin.
            // Unsaved projects check the temporary staging volume instead.
            string[] projectedPaths = _project.Media.SelectMany(asset =>
                    new[] { asset.SourcePath, asset.ProxyPath })
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .Append(picker.FileName)
                .ToArray();
            if (_projectPath is string projectPath)
                await ProjectFileService.EnsureImportCapacityAsync(projectPath, projectedPaths);
            else
                await ProjectFileService.EnsureImportCapacityAsync(projectedPaths);
            if (!ReferenceEquals(_project, targetProject) || targetRevision != _projectRevision)
            { StatusText.Text = "录音已保存，但项目在空间检查期间发生变化，请手动导入 WAV 文件"; return; }
            TimelineTrack? track = _selectedTrack is { Kind: TrackKind.Audio, IsLocked: false } selected
                ? selected : _project.Tracks.FirstOrDefault(item => item.Kind == TrackKind.Audio && !item.IsLocked);
            SaveUndoState();
            if (track is null)
            {
                track = new TimelineTrack { Kind = TrackKind.Audio,
                    Name = $"音频 {_project.Tracks.Count(item => item.Kind == TrackKind.Audio) + 1}",
                    Order = _project.Tracks.Select(item => item.Order).DefaultIfEmpty(-1).Max() + 1 };
                _project.Tracks.Add(track);
            }
            var asset = new MediaAsset { Name = Path.GetFileName(picker.FileName), Kind = MediaKind.Audio,
                SourcePath = picker.FileName, DurationSeconds = durationSeconds, HasAudio = true,
                AudioChannels = 1, AudioSampleRate = 48000, AudioCodec = "pcm_s16le",
                FileSizeBytes = samples.LongLength + 44,
                BinId = _selectedBinId };
            _project.Media.Add(asset);
            var clip = new TimelineClip { Name = asset.Name, Kind = ClipKind.Audio,
                MediaAssetId = asset.Id, StartSeconds = startSeconds, DurationSeconds = asset.DurationSeconds.Value };
            track.Clips.Add(clip); _selectedTrack = track; _selectedClip = clip;
            _selectedClips.Clear(); _selectedClips.Add(clip);
            MarkProjectDirty(); RefreshLibrary(); RefreshTimeline(); SeekPreview(startSeconds);
            StatusText.Text = $"已加入画外音：{asset.Name}";
        }
        catch (AudioOperationException exception) { ReportAudioError(exception); }
        catch (ProjectFileException exception)
        {
            ReportAudioError(new AudioOperationException(AudioStageCode.Write,
                $"录音已保存，但无法加入项目：{exception.Message}", exception));
        }
    }

    /// <summary>
    /// A voice-over writes a new source file before it enters the project.
    /// Check the destination volume on a worker so a slow mounted drive does
    /// not freeze the editor while the user waits for the WAV to be saved.
    /// </summary>
    private static async Task EnsureVoiceOverSpaceAsync(string path, long sampleBytes)
    {
        await Task.Run(() =>
        {
            try
            {
                long required = checked(sampleBytes + 44L);
                long existing = File.Exists(path) ? new FileInfo(path).Length : 0;
                string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
                if (root.Length == 0)
                    throw new AudioOperationException(AudioStageCode.Validate, "录音保存路径无效。");
                long free = new DriveInfo(root).AvailableFreeSpace;
                // Keep a small reserve for the filesystem, WAV metadata,
                // and the in-project copy made at the next project save.
                long reserve = Math.Max(16L * 1024 * 1024, required / 100);
                if (free + existing < required + reserve)
                    throw new AudioOperationException(AudioStageCode.Write,
                        "录音保存位置剩余空间不足，请选择其他磁盘或缩短录音。");
            }
            catch (AudioOperationException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or OverflowException)
            {
                throw new AudioOperationException(AudioStageCode.Read,
                    "无法检查录音保存位置的剩余空间。", exception);
            }
        });
    }
}
