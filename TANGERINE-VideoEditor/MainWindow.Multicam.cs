using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

public enum MulticamStageCode
{
    Validate,
    Synchronize,
    Source,
    Edit
}

public sealed class MulticamException(MulticamStageCode stageCode, string message,
    Exception? inner = null) : Exception(message, inner)
{
    public MulticamStageCode StageCode { get; } = stageCode;
}

/// <summary>
/// Builds local multicamera groups from selected source clips and writes angle
/// changes as ordinary video cuts. All cuts retain a position on the group's
/// shared clock, so the chosen source stays frame-continuous at every switch.
/// </summary>
public partial class MainWindow
{
    private Button? _multicamPreviewButton;
    private bool _multicamSynchronizing;

    private void InitializeMulticamCommands()
    {
        var create = new MenuItem { Header = "从所选视频创建多机位…" };
        create.Click += async (_, _) => await ShowCreateMulticamDialogAsync();
        if (SpeedRampMenuItem.Parent is MenuItem clipItems)
            clipItems.Items.Add(create);
        else if (EffectControlsMenuItem.Parent is MenuItem alternateMenu)
            alternateMenu.Items.Add(create);
        var button = new Button
        {
            Content = "多机位 ▾",
            ToolTip = "创建或切换本地多机位角度",
            Background = Brush("#333741"), Foreground = Brushes.White,
            BorderBrush = Brush("#555A65"),
            Padding = new Thickness(SystemParameters.PrimaryScreenWidth * .004,
                SystemParameters.PrimaryScreenHeight * .004, SystemParameters.PrimaryScreenWidth * .004,
                SystemParameters.PrimaryScreenHeight * .004),
            Margin = new Thickness(0, 0, SystemParameters.PrimaryScreenWidth * .004, 0)
        };
        DockPanel.SetDock(button, Dock.Right);
        if (PreviewQualityBox.Parent is DockPanel previewHeader)
            previewHeader.Children.Add(button);
        button.Click += (_, _) => OpenMulticamPreviewMenu();
        _multicamPreviewButton = button;
    }

    private void AddMulticamContextCommands(ContextMenu menu, TimelineClip clip)
    {
        if (clip.Kind == ClipKind.Video)
            AddContextCommand(menu, "从所选视频创建多机位…", () =>
                _ = ShowCreateMulticamDialogAsync());
        if (clip.MulticamGroupId is null) return;
        var angles = new MenuItem { Header = "在播放头切换多机位" };
        MulticamGroup? group = _project.MulticamGroups.FirstOrDefault(item =>
            item.Id == clip.MulticamGroupId);
        if (group is not null)
        {
            foreach (MulticamAngle angle in group.Angles)
            {
                MulticamGroup selectedGroup = group;
                MulticamAngle selectedAngle = angle;
                var item = new MenuItem
                {
                    Header = selectedAngle.Name,
                    IsCheckable = true,
                    IsChecked = selectedAngle.Id == clip.MulticamAngleId,
                    Background = Brush("#24262C"), Foreground = Brushes.White
                };
                item.Click += async (_, _) => await SwitchMulticamAngleAsync(selectedGroup, selectedAngle);
                angles.Items.Add(item);
            }
            menu.Items.Add(angles);
        }
    }

    /// <summary>Called from SeekPreview after the sequence playhead changes.</summary>
    private void UpdateMulticamPreviewControls(double seconds)
    {
        if (_multicamPreviewButton is null) return;
        TimelineClip? cut = ActiveMulticamCut(seconds);
        MulticamGroup? group = _project.MulticamGroups.FirstOrDefault(item =>
            item.Id == cut?.MulticamGroupId);
        MulticamAngle? angle = group?.Angles.FirstOrDefault(item =>
            item.Id == cut?.MulticamAngleId);
        _multicamPreviewButton.Content = group is null ? "多机位 ▾" :
            $"{group.Name} · {angle?.Name ?? "角度"} ▾";
        _multicamPreviewButton.ToolTip = group is null
            ? "按住 Ctrl 选择至少两个视频片段后创建多机位"
            : "点击选择角度；播放中点击即在播放头写入切段";
    }

    private TimelineClip? ActiveMulticamCut(double seconds)
    {
        // Select the foremost opaque visual first. A hidden multicamera cut
        // must not appear editable through another visible shot above it.
        TimelineClip? foremost = _project.Tracks
            .Where(track => track.Kind != TrackKind.Audio && !track.IsHidden)
            .OrderBy(track => track.Order)
            .SelectMany(track => track.Clips.OrderByDescending(clip => clip.StartSeconds))
            .FirstOrDefault(clip => (clip.Kind is ClipKind.Video or ClipKind.Image or
                    ClipKind.Color or ClipKind.Sequence) &&
                seconds >= clip.StartSeconds && seconds < clip.StartSeconds + clip.DurationSeconds);
        return foremost?.MulticamGroupId is not null ? foremost : null;
    }

    private void OpenMulticamPreviewMenu()
    {
        if (_multicamPreviewButton is null) return;
        var menu = new ContextMenu
        {
            Background = Brush("#24262C"), Foreground = Brushes.White,
            PlacementTarget = _multicamPreviewButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
        var create = new MenuItem { Header = "从所选视频创建多机位…" };
        create.Click += async (_, _) => await ShowCreateMulticamDialogAsync();
        menu.Items.Add(create);
        TimelineClip? cut = ActiveMulticamCut(_project.Sequence.PlayheadSeconds);
        MulticamGroup? group = _project.MulticamGroups.FirstOrDefault(item =>
            item.Id == cut?.MulticamGroupId);
        if (group is not null)
        {
            menu.Items.Add(new Separator());
            TimelineClip activeCut = cut!;
            foreach (MulticamAngle angle in group.Angles)
            {
                MulticamGroup selectedGroup = group;
                MulticamAngle selectedAngle = angle;
                var item = new MenuItem
                {
                    Header = selectedAngle.Name,
                    IsCheckable = true,
                    IsChecked = selectedAngle.Id == activeCut.MulticamAngleId,
                    Background = Brush("#24262C"), Foreground = Brushes.White
                };
                item.Click += async (_, _) => await SwitchMulticamAngleAsync(selectedGroup, selectedAngle);
                menu.Items.Add(item);
            }
        }
        menu.IsOpen = true;
    }

    private async Task ShowCreateMulticamDialogAsync()
    {
        if (_multicamSynchronizing) return;
        var selected = ExistingSelection().Where(clip => clip.Kind == ClipKind.Video)
            .Distinct().OrderBy(clip => clip.StartSeconds).ToArray();
        if (selected.Length > 16)
        {
            StatusText.Text = "一次最多选择 16 个视频机位 · MULTICAM-VALIDATE";
            return;
        }
        if (selected.Length < 2)
        {
            StatusText.Text = "请按住 Ctrl 选择至少两个不同机位的视频片段 · MULTICAM-VALIDATE";
            return;
        }
        var dialog = new Window
        {
            Title = "创建本地多机位", Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = SystemParameters.PrimaryScreenWidth * .34,
            Height = SystemParameters.PrimaryScreenHeight * .31,
            Background = Brush("#1C1E24"), Foreground = Brushes.White,
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel
        {
            Margin = new Thickness(SystemParameters.PrimaryScreenWidth * .012)
        };
        panel.Children.Add(new TextBlock
        {
            Text = $"已选择 {selected.Length} 个视频片段。同步后会用多机位切段替换它们。",
            TextWrapping = TextWrapping.Wrap
        });
        var name = new TextBox
        {
            Text = $"多机位 {_project.MulticamGroups.Count + 1}",
            Margin = new Thickness(0, SystemParameters.PrimaryScreenHeight * .012, 0, 0)
        };
        panel.Children.Add(name);
        var mode = new ComboBox
        {
            ItemsSource = new[] { "按当前时间轴位置", "按源时间码", "按音频波形" },
            SelectedIndex = 0,
            Background = Brush("#30333C"), Foreground = Brushes.White,
            Margin = new Thickness(0, SystemParameters.PrimaryScreenHeight * .012, 0, 0)
        };
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(ComboBoxItem.BackgroundProperty, Brush("#30333C")));
        itemStyle.Setters.Add(new Setter(ComboBoxItem.ForegroundProperty, Brushes.White));
        mode.ItemContainerStyle = itemStyle;
        panel.Children.Add(mode);
        panel.Children.Add(new TextBlock
        {
            Text = "音频波形同步需要各机位含音轨；源时间码同步需要有效的 timecode 元数据。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#BFC3CB"),
            Margin = new Thickness(0, SystemParameters.PrimaryScreenHeight * .012, 0, 0)
        });
        var submit = new Button
        {
            Content = "创建多机位",
            Background = Brush("#FF9957"), Foreground = Brushes.Black,
            Margin = new Thickness(0, SystemParameters.PrimaryScreenHeight * .015, 0, 0)
        };
        submit.Click += (_, _) => dialog.DialogResult = true;
        panel.Children.Add(submit);
        dialog.Content = panel;
        if (dialog.ShowDialog() != true) return;
        string groupName = name.Text.Trim();
        if (groupName.Length == 0 || groupName.Length > 100)
        {
            ReportMulticamError(new MulticamException(MulticamStageCode.Validate,
                "多机位名称需在 1 至 100 个字符之间。"));
            return;
        }
        string method = mode.SelectedIndex switch
        {
            1 => "Timecode", 2 => "Waveform", _ => "Timeline"
        };
        await CreateMulticamGroupAsync(selected, groupName, method);
    }

    private async Task CreateMulticamGroupAsync(TimelineClip[] clips, string name, string method)
    {
        ProjectDocument sourceProject = _project;
        long revision = _projectRevision;
        string sourceSequenceId = _project.Sequence.Id;
        _multicamSynchronizing = true;
        try
        {
            if (clips.Length < 2 || clips.Length > 16 ||
                clips.Select(clip => clip.MediaAssetId).Distinct().Count() < 2)
                throw new MulticamException(MulticamStageCode.Validate,
                    "至少需要两个不同视频源。请选择不同机位的片段。");
            if (method is not ("Timeline" or "Timecode" or "Waveform"))
                throw new MulticamException(MulticamStageCode.Validate, "多机位同步方式无效。");
            var sources = new List<(TimelineClip Clip, TimelineTrack Track, MediaAsset Asset)>();
            foreach (TimelineClip clip in clips)
            {
                TimelineTrack? track = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
                MediaAsset? asset = _project.Media.FirstOrDefault(item => item.Id == clip.MediaAssetId);
                if (track is null || track.IsLocked ||
                    track.Kind != TrackKind.Video ||
                    asset is null || asset.Kind != MediaKind.Video ||
                    clip.MulticamGroupId is not null || clip.IsReversed ||
                    Math.Abs(clip.Speed - 1) > .000001 || clip.TimeRemapPoints.Count > 0 ||
                    !double.IsFinite(clip.SourceInSeconds) || clip.SourceInSeconds < 0 ||
                    !double.IsFinite(clip.DurationSeconds) || clip.DurationSeconds <= 0 ||
                    (asset.DurationSeconds is double available &&
                     clip.SourceInSeconds + clip.DurationSeconds > available + .001))
                    throw new MulticamException(MulticamStageCode.Validate,
                        "机位需要未锁定轨道上的正向原速视频，且源入出点须有效。");
                sources.Add((clip, track, asset));
            }
            var originalEdits = sources.Select(source => (
                StartSeconds: source.Clip.StartSeconds,
                DurationSeconds: source.Clip.DurationSeconds,
                SourceInSeconds: source.Clip.SourceInSeconds,
                Speed: source.Clip.Speed,
                IsReversed: source.Clip.IsReversed,
                RemapCount: source.Clip.TimeRemapPoints.Count)).ToArray();
            // Embedded project media can live on a slow drive. Probe all
            // source paths off the dispatcher before any timeline mutation.
            string[] sourcePaths = sources.Select(source => source.Asset.EffectiveExportPath).ToArray();
            bool[] sourceExists = await Task.Run(() => sourcePaths.Select(File.Exists).ToArray());
            int missingSource = Array.FindIndex(sourceExists, exists => !exists);
            if (missingSource >= 0)
                throw new MulticamException(MulticamStageCode.Source,
                    $"找不到机位“{sources[missingSource].Clip.Name}”的本地源文件。");
            var alignedStarts = new double[sources.Count];
            alignedStarts[0] = sources[0].Clip.StartSeconds;
            int referenceRate = 0;
            bool referenceDropFrame = false;
            double referenceTime = 0;
            if (method == "Timecode" && !TrySourceTimecode(sources[0].Asset,
                    out referenceTime, out referenceRate, out referenceDropFrame))
                throw new MulticamException(MulticamStageCode.Synchronize,
                    "参考机位缺少有效的源 timecode 元数据。");
            for (int index = 1; index < sources.Count; index++)
            {
                if (method == "Timeline") alignedStarts[index] = sources[index].Clip.StartSeconds;
                else if (method == "Timecode")
                {
                    if (!TrySourceTimecode(sources[index].Asset, out double movingTime,
                            out int movingRate, out bool movingDropFrame))
                        throw new MulticamException(MulticamStageCode.Synchronize,
                            "所有机位都需要有效的源 timecode 元数据。");
                    if (referenceRate != movingRate || referenceDropFrame != movingDropFrame ||
                        Math.Abs(sources[0].Asset.FramesPerSecond.GetValueOrDefault() -
                            sources[index].Asset.FramesPerSecond.GetValueOrDefault()) > .01)
                        throw new MulticamException(MulticamStageCode.Synchronize,
                            "机位时间码帧率或丢帧方式不一致，无法安全同步。");
                    alignedStarts[index] = alignedStarts[0] +
                        movingTime + sources[index].Clip.SourceInSeconds -
                        referenceTime - sources[0].Clip.SourceInSeconds;
                }
                else
                {
                    if (!sources[0].Asset.HasAudio || !sources[index].Asset.HasAudio)
                        throw new MulticamException(MulticamStageCode.Synchronize,
                            "波形同步要求所有视频都包含音频轨。");
                    try
                    {
                        double offset = await AudioWaveformAnalysis.FindOffsetAsync(
                            CloneClip(sources[0].Clip), sources[0].Asset.EffectiveExportPath,
                            CloneClip(sources[index].Clip), sources[index].Asset.EffectiveExportPath);
                        alignedStarts[index] = alignedStarts[0] + offset;
                    }
                    catch (AudioOperationException exception)
                    {
                        throw new MulticamException(MulticamStageCode.Synchronize,
                            "无法可靠地对齐机位音频波形。", exception);
                    }
                }
            }
            if (!ReferenceEquals(sourceProject, _project) || revision != _projectRevision ||
                _project.Sequence.Id != sourceSequenceId ||
                sources.Select(source => source.Asset)
                    .Any(asset => !_project.Media.Contains(asset)) ||
                sources.Where((source, index) =>
                    !_project.Tracks.Contains(source.Track) ||
                    !source.Track.Clips.Contains(source.Clip) || source.Track.IsLocked ||
                    source.Clip.StartSeconds != originalEdits[index].StartSeconds ||
                    source.Clip.DurationSeconds != originalEdits[index].DurationSeconds ||
                    source.Clip.SourceInSeconds != originalEdits[index].SourceInSeconds ||
                    source.Clip.Speed != originalEdits[index].Speed ||
                    source.Clip.IsReversed != originalEdits[index].IsReversed ||
                    source.Clip.TimeRemapPoints.Count != originalEdits[index].RemapCount).Any())
                throw new MulticamException(MulticamStageCode.Edit,
                    "同步期间项目发生变化，请重新选择机位。");
            if (!double.IsFinite(_project.Sequence.FramesPerSecond) ||
                _project.Sequence.FramesPerSecond <= 0)
                throw new MulticamException(MulticamStageCode.Validate, "序列帧率无效。");
            // Normalize the group to a nonnegative timeline clock. A negative
            // synchronized start still has valid source overlap; shifting all
            // angles equally keeps their relative timecodes unchanged.
            double shift = Math.Max(0, -alignedStarts.Min());
            for (int index = 0; index < alignedStarts.Length; index++)
                alignedStarts[index] += shift;
            double commonStart = alignedStarts.Max();
            double commonEnd = sources.Select((source, index) =>
                alignedStarts[index] + source.Clip.DurationSeconds).Min();
            double frame = 1 / _project.Sequence.FramesPerSecond;
            if (!double.IsFinite(commonStart) || commonStart < 0 ||
                !double.IsFinite(commonEnd))
                throw new MulticamException(MulticamStageCode.Synchronize,
                    "同步时间无效或超出时间线起点。");
            double duration = Math.Floor((commonEnd - commonStart) / frame + 1e-9) * frame;
            duration = Math.Min(duration, commonEnd - commonStart);
            if (!double.IsFinite(duration) || duration < frame * .999)
                throw new MulticamException(MulticamStageCode.Synchronize,
                    "机位没有共同的可用画面区间；请调整源入出点或同步方式。");
            // Other edits must stay intact when replacing selected camera
            // takes. An overlap on the destination track would otherwise
            // silently composite another shot above or below the new group.
            TimelineTrack destination = sources[0].Track;
            if (destination.Clips.Any(clip => !clips.Contains(clip) &&
                clip.StartSeconds < commonStart + duration &&
                clip.StartSeconds + clip.DurationSeconds > commonStart))
                throw new MulticamException(MulticamStageCode.Edit,
                    "目标轨道的共同区间已有其他片段；请先移开该片段。");
            var group = new MulticamGroup
            {
                Name = name, SyncMethod = method, DurationSeconds = duration
            };
            for (int index = 0; index < sources.Count; index++)
            {
                TimelineClip source = sources[index].Clip;
                double skip = commonStart - alignedStarts[index];
                TimelineClip normalized = CloneClip(source);
                ClipAnimation.KeepRange(normalized, skip, duration);
                normalized.SourceInSeconds += skip;
                normalized.StartSeconds = 0;
                normalized.DurationSeconds = duration;
                normalized.GroupId = null;
                normalized.LinkGroupId = null;
                normalized.MulticamGroupId = null;
                normalized.MulticamAngleId = null;
                normalized.MulticamLocalStartSeconds = 0;
                group.Angles.Add(new MulticamAngle
                {
                    Name = source.Name, SourceClip = normalized
                });
            }
            // Build the replacement before mutating the project, so a source
            // or serialization failure leaves the original camera clips intact.
            TimelineClip firstCut = MakeMulticamCut(group, group.Angles[0],
                commonStart, 0, duration);
            SaveUndoState();
            foreach (var source in sources)
                source.Track.Clips.Remove(source.Clip);
            _project.MulticamGroups.Add(group);
            destination.Clips.Add(firstCut);
            SelectClip(firstCut);
            MarkProjectDirty();
            RefreshTimeline();
            SeekPreview(Math.Clamp(_project.Sequence.PlayheadSeconds, commonStart,
                commonStart + duration - frame));
            StatusText.Text = $"已创建“{name}”，共 {group.Angles.Count} 个同步机位";
        }
        catch (MulticamException exception) { ReportMulticamError(exception); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or InvalidOperationException or NotSupportedException)
        {
            ReportMulticamError(new MulticamException(MulticamStageCode.Source,
                "无法读取多机位素材或项目数据。", exception));
        }
        finally { _multicamSynchronizing = false; }
    }

    private static TimelineClip MakeMulticamCut(MulticamGroup group, MulticamAngle angle,
        double start, double localStart, double duration)
    {
        TimelineClip cut = CloneClip(angle.SourceClip);
        cut.Id = Guid.NewGuid().ToString("N");
        ClipAnimation.KeepRange(cut, localStart, duration);
        if (cut.TimeRemapPoints.Count > 0)
            ClipTimeMap.KeepRange(cut, localStart, duration);
        else cut.SourceInSeconds += localStart * cut.Speed;
        cut.Name = $"{group.Name} · {angle.Name}";
        cut.StartSeconds = start;
        cut.DurationSeconds = duration;
        cut.MulticamGroupId = group.Id;
        cut.MulticamAngleId = angle.Id;
        cut.MulticamLocalStartSeconds = localStart;
        return cut;
    }

    /// <summary>
    /// Rebase the shared multicamera clock whenever a normal trim or split
    /// removes material from a cut's left edge. The clip remains a regular
    /// video clip and may still be moved between compatible tracks.
    /// </summary>
    private static void AdvanceMulticamLocalStart(TimelineClip clip, double removedSeconds)
    {
        if (clip.MulticamGroupId is not null)
            clip.MulticamLocalStartSeconds += removedSeconds;
    }

    private async Task SwitchMulticamAngleAsync(MulticamGroup group, MulticamAngle angle)
    {
        double playhead = _project.Sequence.PlayheadSeconds;
        TimelineClip? current = _selectedClip is { MulticamGroupId: not null } selected &&
            _project.Tracks.Any(item => item.Clips.Contains(selected)) &&
            selected.MulticamGroupId == group.Id &&
            playhead >= selected.StartSeconds &&
            playhead < selected.StartSeconds + selected.DurationSeconds
            ? selected : ActiveMulticamCut(playhead);
        TimelineTrack? track = _project.Tracks.FirstOrDefault(item => current is not null &&
            item.Clips.Contains(current));
        if (!double.IsFinite(playhead) || current is null || track is null || track.IsLocked ||
            current.MulticamGroupId != group.Id ||
            !group.Angles.Contains(angle) ||
            current.IsReversed || current.TimeRemapPoints.Count > 0 ||
            Math.Abs(current.Speed - 1) > .000001 ||
            current.DurationSeconds <= 0 || !double.IsFinite(current.DurationSeconds))
        {
            ReportMulticamError(new MulticamException(MulticamStageCode.Edit,
                "请把播放头放在未锁定、正向原速的多机位切段内。"));
            return;
        }
        if (!double.IsFinite(current.MulticamLocalStartSeconds) ||
            current.MulticamLocalStartSeconds < 0 ||
            current.MulticamLocalStartSeconds + current.DurationSeconds >
            group.DurationSeconds + .001)
        {
            ReportMulticamError(new MulticamException(MulticamStageCode.Edit,
                "当前切段不在多机位共享范围内。"));
            return;
        }
        if (current.MulticamAngleId == angle.Id) return;
        MediaAsset? replacementAsset = _project.Media.FirstOrDefault(item =>
            item.Id == angle.SourceClip.MediaAssetId);
        if (replacementAsset is null)
        {
            ReportMulticamError(new MulticamException(MulticamStageCode.Source,
                $"找不到机位“{angle.Name}”的本地源文件。"));
            return;
        }
        ProjectDocument sourceProject = _project;
        long revision = _projectRevision;
        string sourceSequenceId = _project.Sequence.Id;
        double originalStart = current.StartSeconds;
        double originalDuration = current.DurationSeconds;
        double originalLocalStart = current.MulticamLocalStartSeconds;
        double originalSourceIn = current.SourceInSeconds;
        string? originalAngleId = current.MulticamAngleId;
        string replacementPath = replacementAsset.EffectiveExportPath;
        bool sourceExists = await Task.Run(() => File.Exists(replacementPath));
        if (!ReferenceEquals(sourceProject, _project) || revision != _projectRevision ||
            _project.Sequence.Id != sourceSequenceId ||
            !_project.Tracks.Contains(track) || !track.Clips.Contains(current) || track.IsLocked ||
            !_project.MulticamGroups.Contains(group) || !group.Angles.Contains(angle) ||
            !_project.Media.Contains(replacementAsset) ||
            current.StartSeconds != originalStart ||
            current.DurationSeconds != originalDuration ||
            current.MulticamLocalStartSeconds != originalLocalStart ||
            current.SourceInSeconds != originalSourceIn ||
            current.MulticamAngleId != originalAngleId ||
            current.IsReversed || current.TimeRemapPoints.Count > 0 ||
            Math.Abs(current.Speed - 1) > .000001 ||
            Math.Abs(playhead - _project.Sequence.PlayheadSeconds) > 1e-9)
        {
            ReportMulticamError(new MulticamException(MulticamStageCode.Edit,
                "检查素材期间时间轴已变化，请重新选择机位。"));
            return;
        }
        if (!sourceExists)
        {
            ReportMulticamError(new MulticamException(MulticamStageCode.Source,
                $"找不到机位“{angle.Name}”的本地源文件。"));
            return;
        }
        if (!double.IsFinite(_project.Sequence.FramesPerSecond) ||
            _project.Sequence.FramesPerSecond <= 0)
        {
            ReportMulticamError(new MulticamException(MulticamStageCode.Edit,
                "序列帧率无效。"));
            return;
        }
        double frame = 1 / _project.Sequence.FramesPerSecond;
        if (current.DurationSeconds < frame - .000001)
        {
            ReportMulticamError(new MulticamException(MulticamStageCode.Edit,
                "当前切段短于一帧，无法切换机位。"));
            return;
        }
        double cutTime = current.StartSeconds +
            Math.Floor((playhead - current.StartSeconds) / frame + 1e-9) * frame;
        cutTime = Math.Clamp(cutTime, current.StartSeconds,
            current.StartSeconds + current.DurationSeconds - frame);
        double leftDuration = cutTime - current.StartSeconds;
        double rightDuration = current.DurationSeconds - leftDuration;
        double localStart = current.MulticamLocalStartSeconds + leftDuration;
        if (rightDuration < frame * .999 ||
            localStart + rightDuration > group.DurationSeconds + .001)
        {
            ReportMulticamError(new MulticamException(MulticamStageCode.Edit,
                "切点超出多机位共同画面区间。"));
            return;
        }
        TimelineClip replacement;
        try
        {
            replacement = MakeMulticamCut(group, angle, cutTime, localStart,
                rightDuration);
            SaveUndoState();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
            NotSupportedException)
        {
            ReportMulticamError(new MulticamException(MulticamStageCode.Edit,
                "无法生成选中机位的切段。", exception));
            return;
        }
        if (leftDuration < frame)
            track.Clips.Remove(current);
        else
        {
            ClipAnimation.KeepRange(current, 0, leftDuration);
            if (current.TimeRemapPoints.Count > 0)
                ClipTimeMap.KeepRange(current, 0, leftDuration);
            current.DurationSeconds = leftDuration;
        }
        track.Clips.Add(replacement);
        SelectClip(replacement);
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(playhead);
        StatusText.Text = $"已切换到机位“{angle.Name}”";
    }

    private void ReportMulticamError(MulticamException exception)
    {
        StatusText.Text = $"多机位操作失败 · MULTICAM-{exception.StageCode.ToString().ToUpperInvariant()}";
        MessageBox.Show(this,
            $"{exception.Message}\n阶段代码：MULTICAM-{exception.StageCode.ToString().ToUpperInvariant()}",
            "多机位操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
