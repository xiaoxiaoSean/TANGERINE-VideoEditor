using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;
using TANGERINE_VideoEditor.Tools;

namespace TANGERINE_VideoEditor;

public partial class MainWindow
{
    private void DetachClipAudio(TimelineClip video)
    {
        var track = _project.Tracks.FirstOrDefault(t => t.Clips.Contains(video));
        var asset = _project.Media.FirstOrDefault(m => m.Id == video.MediaAssetId);
        if (track is null || track.IsLocked || video.Kind != ClipKind.Video || asset?.HasAudio != true || video.AudioDetached)
            throw new StageException("AUDDET0001", "请选择尚未分离音频、且轨道未锁定的有声视频。");
        SaveUndoState();
        var audio = CloneClip(video); audio.Id = Guid.NewGuid().ToString("N"); audio.Kind = ClipKind.Audio;
        audio.Name += " · 分离音频"; audio.Effects.Clear(); audio.Transitions.Clear(); audio.Mask = null; audio.Transform = new();
        audio.DetachedAudioClipId = null; audio.DetachedVideoClipId = video.Id; audio.AudioDetached = false;
        audio.LinkGroupId = null; audio.GroupId = null;
        audio.Keyframes.RemoveAll(k => k.Property != "Volume");
        var target = _project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Audio && !t.IsLocked &&
            t.Clips.All(c => c.StartSeconds + c.DurationSeconds <= audio.StartSeconds || c.StartSeconds >= audio.StartSeconds + audio.DurationSeconds));
        if (target is null) { target = new TimelineTrack { Name = "分离音频", Kind = TrackKind.Audio, Order = _project.Tracks.Count }; _project.Tracks.Add(target); }
        target.Clips.Add(audio); video.AudioDetached = true; video.DetachedAudioClipId = audio.Id;
        MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = "已分离音频，可独立修剪；源文件未改写";
    }

    private void AttachClipAudio(TimelineClip selected)
    {
        var video = selected.Kind == ClipKind.Video ? selected : _project.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == selected.DetachedVideoClipId);
        var audio = _project.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == video?.DetachedAudioClipId);
        if (video is null || audio is null || !video.AudioDetached ||
            _project.Tracks.Any(t => t.IsLocked && (t.Clips.Contains(video) || t.Clips.Contains(audio))))
            throw new StageException("AUDDET0002", "未找到可重新附着的分离音频，或关联轨道已锁定。");
        if (video.MediaAssetId != audio.MediaAssetId || Math.Abs(video.StartSeconds - audio.StartSeconds) > .00001 ||
            Math.Abs(video.DurationSeconds - audio.DurationSeconds) > .00001 || video.SourceInSeconds != audio.SourceInSeconds ||
            video.Speed != audio.Speed || video.IsReversed != audio.IsReversed || JsonSerializer.Serialize(video.TimeRemapPoints) != JsonSerializer.Serialize(audio.TimeRemapPoints))
            throw new StageException("AUDDET0003", "音画已分别修剪或移动；请先对齐起点、时长、源入点和速度，再重新附着。");
        SaveUndoState(); video.Volume = audio.Volume; video.IsMuted = audio.IsMuted; video.SourceAudioStreamIndex = audio.SourceAudioStreamIndex;
        AudioSettings.FromClip(audio).Save(video);
        video.Keyframes.RemoveAll(k => k.Property == "Volume"); video.Keyframes.AddRange(audio.Keyframes.Where(k => k.Property == "Volume"));
        _project.Tracks.First(t => t.Clips.Contains(audio)).Clips.Remove(audio);
        video.AudioDetached = false; video.DetachedAudioClipId = null;
        SelectClip(video); MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = "已重新附着音频，保留音量与音频处理参数";
    }

    private async Task NormalizeClipAudioAsync(TimelineClip clip, double target, double ceiling, CancellationToken token)
    {
        var owner = _project; long revision = _projectRevision;
        var track = owner.Tracks.FirstOrDefault(t => t.Clips.Contains(clip));
        var asset = owner.Media.FirstOrDefault(m => m.Id == clip.MediaAssetId);
        if (track is null || track.IsLocked || clip.AudioDetached || asset is null || clip.Kind is not (ClipKind.Audio or ClipKind.Video))
            throw new StageException("AUDNORM0001", "请选择未锁定的可听片段。");
        StatusText.Text = "正在测量所选源音轨的 R128 响度…";
        var snapshot = CloneClip(clip);
        var measured = await ClipLoudnessNormalizer.MeasureAsync(snapshot, asset, target, ceiling, token);
        if (!ReferenceEquals(owner, _project) || revision != _projectRevision) throw new StageException("AUDNORM0002", "分析期间项目已修改，请重新测量。");
        SaveUndoState(); var settings = AudioSettings.FromClip(clip);
        settings.NormalizeEnabled = true; settings.TargetLufs = target; settings.TargetTruePeakDb = ceiling;
        settings.MeasuredLufs = measured.IntegratedLufs; settings.MeasuredTruePeakDb = measured.TruePeakDb; settings.NormalizationGainDb = measured.GainDb; settings.Save(clip);
        MarkProjectDirty(); SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = $"响度 {measured.IntegratedLufs:0.0} LUFS，补偿 {measured.GainDb:+0.0;-0.0;0} dB；峰值约束优先";
    }

    private void AddAudioWorkflowControls(StackPanel panel, TimelineClip clip)
    {
        var asset = _project.Media.FirstOrDefault(m => m.Id == clip.MediaAssetId);
        var streams = asset?.AudioStreams ?? [];
        var picker = new ComboBox { ItemsSource = streams.Count > 0 ? streams : new[] { new SourceAudioStream { Index = 0, Title = "默认源音轨" } }.ToList(),
            SelectedIndex = clip.SourceAudioStreamIndex, Margin = new Thickness(0, 5, 0, 8), ToolTip = "选择源文件内音频流；预览、波形、归一化与导出同步使用" };
        panel.Children.Add(new TextBlock { Text = "源文件音轨" }); panel.Children.Add(picker);
        var refresh = new Button { Style = (System.Windows.Style)FindResource("ToolButton"), Content = "重新探测源音轨", Margin = new Thickness(0, 0, 0, 8) }; panel.Children.Add(refresh);
        refresh.Click += async (_, _) =>
        {
            if (asset is null) return;
            refresh.IsEnabled = false;
            try
            {
                await PopulateMediaMetadataAsync(_project, [asset], _experienceLifetime.Token);
                OpenObjectToolPage(clip, "均衡器 / 音量", BuildAudioTool);
            }
            catch (Exception ex) { StatusText.Text = $"源音轨探测失败 · AUDIO0007：{ex.Message}"; }
            finally { refresh.IsEnabled = true; }
        };
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is not SourceAudioStream stream || stream.Index == clip.SourceAudioStreamIndex) return;
            if (_project.Tracks.Any(t => t.IsLocked && t.Clips.Contains(clip))) return;
            SaveUndoState(); clip.SourceAudioStreamIndex = stream.Index; MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
        };
        var detach = new Button { Style = (System.Windows.Style)FindResource("ToolButton"), Content = clip.AudioDetached || clip.DetachedVideoClipId is not null ? "重新附着音频" : "分离音频", Margin = new Thickness(0, 4, 0, 8) };
        detach.Click += (_, _) => { try { if (clip.AudioDetached || clip.DetachedVideoClipId is not null) AttachClipAudio(clip); else DetachClipAudio(clip); } catch (StageException ex) { StatusText.Text = $"{ex.Message} · {ex.StageCode}"; } };
        panel.Children.Add(detach);
        var settings = AudioSettings.FromClip(clip);
        var target = AddToolTextBox(panel, "归一化目标（-36 至 -5 LUFS）", settings.TargetLufs);
        var ceiling = AddToolTextBox(panel, "真峰值上限（-9 至 0 dBTP）", settings.TargetTruePeakDb);
        var normalize = new Button { Style = (System.Windows.Style)FindResource("ToolButton"), Content = "测量并一键归一化", Margin = new Thickness(0, 4, 0, 8) };
        normalize.Click += async (_, _) =>
        {
            if (!TryNumberQuiet(target.Text, -36, -5, out double lu) || !TryNumberQuiet(ceiling.Text, -9, 0, out double peak)) { StatusText.Text = "响度参数无效 · AUDNORM0003"; return; }
            normalize.IsEnabled = false;
            try { await NormalizeClipAudioAsync(clip, lu, peak, _experienceLifetime.Token); }
            catch (Exception ex) { StatusText.Text = $"响度处理失败 · AUDNORM0004：{ex.Message}"; }
            finally { normalize.IsEnabled = true; }
        };
        panel.Children.Add(normalize);
        var disable = new Button { Style = (System.Windows.Style)FindResource("ToolButton"), Content = "关闭归一化", Margin = new Thickness(0, 0, 0, 8) };
        disable.Click += (_, _) => { if (_project.Tracks.Any(t => t.IsLocked && t.Clips.Contains(clip))) return; SaveUndoState(); var current = AudioSettings.FromClip(clip); current.NormalizeEnabled = false; current.Save(clip); MarkProjectDirty(); SeekPreview(_project.Sequence.PlayheadSeconds); };
        panel.Children.Add(disable);
    }
}
