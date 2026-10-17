using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using Microsoft.Win32;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>Local editing commands that do not require external services.</summary>
public partial class MainWindow
{
    private MediaAsset? _sourceMonitorOverride;
    private double? _sourceMonitorInitialSeconds;

    /// <summary>One row of the sequence index: where a clip sits and what it is.</summary>
    private sealed record SequenceIndexEntry(TimelineTrack Track, TimelineClip Clip, string MediaName);

    /// <summary>
    /// Lists every clip of the open sequence by time, searchable by clip, track
    /// or media name. Rows are rebuilt from live project data on each search,
    /// and a click re-checks that the clip still exists, so the panel never
    /// selects something that undo or a delete already removed.
    /// </summary>
    private void ShowSequenceIndex()
    {
        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(SettingsHeader("序列索引", () => CloseSettingsTabs()));
        var search = new TextBox { ToolTip = "搜索片段名称、轨道名称或媒体名称", Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(search, Dock.Top);
        root.Children.Add(search);
        var count = new TextBlock { Foreground = Brush("#9B9EA7"), FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(count, Dock.Bottom);
        root.Children.Add(count);
        var list = new ListBox { Background = Brush("#101114"), Foreground = Brushes.White,
            BorderBrush = Brush("#626874") };
        // Virtualize: a long sequence can hold thousands of clips, and only the
        // visible rows should become controls.
        VirtualizingPanel.SetIsVirtualizing(list, true);
        root.Children.Add(list);

        void RefreshIndex()
        {
            string query = search.Text.Trim();
            var entries = _project.Tracks
                .SelectMany(track => track.Clips.Select(clip => new SequenceIndexEntry(track, clip,
                    _project.Media.FirstOrDefault(media => media.Id == clip.MediaAssetId)?.Name ?? string.Empty)))
                .Where(item => query.Length == 0 ||
                    item.Clip.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    item.Track.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    item.MediaName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(item => item.Clip.StartSeconds)
                .ThenBy(item => _project.Tracks.IndexOf(item.Track))
                .ToList();
            list.Items.Clear();
            foreach (SequenceIndexEntry entry in entries)
                list.Items.Add(new ListBoxItem
                {
                    Content = $"{FormatTime(entry.Clip.StartSeconds)}  {entry.Track.Name}  ·  {entry.Clip.Name}",
                    Tag = entry,
                    Padding = new Thickness(8, 5, 8, 5),
                    Foreground = Brushes.White
                });
            count.Text = query.Length == 0
                ? $"共 {entries.Count} 个片段"
                : $"匹配 {entries.Count} 个片段";
        }

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: SequenceIndexEntry entry }) return;
            // The row was built earlier; the clip may have been deleted since.
            if (!_project.Tracks.Contains(entry.Track) || !entry.Track.Clips.Contains(entry.Clip))
            {
                RefreshIndex();
                StatusText.Text = "该片段已不存在，索引已刷新";
                return;
            }
            SelectClipFromIndex(entry.Track, entry.Clip);
        };
        search.TextChanged += (_, _) => RefreshIndex();
        RefreshIndex();
        // The list virtualizes and scrolls itself; a page-level scroll viewer
        // would hand it unlimited height and build every row.
        OpenSettingsCategory("序列索引", root, selfScrolling: true);
    }

    /// <summary>
    /// Selects a clip from the index: moves the playhead to it and scrolls the
    /// timeline so the clip is actually visible, not just selected off-screen.
    /// </summary>
    private void SelectClipFromIndex(TimelineTrack track, TimelineClip clip)
    {
        _selectedTrack = track;
        _selectedClip = clip;
        _selectedClips.Clear();
        _selectedClips.Add(clip);
        _project.Sequence.PlayheadSeconds = clip.StartSeconds;
        RefreshTimeline();
        double x = HeaderSpace + clip.StartSeconds * PixelsPerSecond;
        double viewLeft = TimelineScrollViewer.HorizontalOffset + HeaderSpace;
        double viewRight = TimelineScrollViewer.HorizontalOffset + TimelineScrollViewer.ViewportWidth;
        if (x < viewLeft || x > viewRight)
            TimelineScrollViewer.ScrollToHorizontalOffset(Math.Max(0, x - HeaderSpace - TimelineScrollViewer.ViewportWidth * .25));
        double y = RulerSpace + _project.Tracks.IndexOf(track) * TrackSpace;
        if (y < TimelineScrollViewer.VerticalOffset ||
            y + TrackSpace > TimelineScrollViewer.VerticalOffset + TimelineScrollViewer.ViewportHeight)
            TimelineScrollViewer.ScrollToVerticalOffset(Math.Max(0, y - TrackSpace));
        SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = $"已定位到：{clip.Name}";
    }

    private void RemoveSelectedClipKeepingGap()
    {
        if (_selectedClip is null || _selectedTrack is null)
        { StatusText.Text = "请先选择要提升的片段"; return; }
        if (_selectedTrack.IsLocked) { StatusText.Text = "轨道已锁定"; return; }
        SaveUndoState();
        _selectedTrack.Clips.Remove(_selectedClip);
        _selectedClip = null; _selectedClips.Clear();
        MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = "已删除原片段，保留时间线空隙";
    }

    /// <summary>
    /// Extracts a source-backed image, video, or audio clip to a user-selected
    /// local file. Deletion is deliberately asked only after the file is
    /// complete, so a failed export never removes the timeline edit.
    /// </summary>
    private async void ExtractSelectedClip()
    {
        TimelineClip? clip = _selectedClip;
        TimelineTrack? track = _selectedTrack;
        if (clip is null || track is null || track.IsLocked)
        { StatusText.Text = "请先选择未锁定轨道上的图片、视频或音频片段"; return; }
        if (clip.Kind is not (ClipKind.Image or ClipKind.Video or ClipKind.Audio) ||
            clip.MediaAssetId is not string assetId)
        { StatusText.Text = "提取只支持图片、视频和音频片段"; return; }
        MediaAsset? asset = _project.Media.FirstOrDefault(item => item.Id == assetId);
        string? source = asset?.EffectiveExportPath;
        if (asset is null || string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        { StatusText.Text = "源媒体不存在或已离线，无法提取"; return; }
        double sourceStart = ClipTimeMap.SourceTime(clip, clip.StartSeconds);
        double sourceEnd = ClipTimeMap.SourceTime(clip, clip.StartSeconds + clip.DurationSeconds);
        double inPoint = Math.Max(0, Math.Min(sourceStart, sourceEnd));
        double duration = Math.Abs(sourceEnd - sourceStart);
        if (clip.Kind == ClipKind.Image) duration = 0;
        if (duration <= 0 && clip.Kind != ClipKind.Image)
        { StatusText.Text = "片段源范围为空，无法提取"; return; }

        string defaultExtension = clip.Kind == ClipKind.Audio ? ".wav" : clip.Kind == ClipKind.Image
            ? (Path.GetExtension(source).Length > 1 ? Path.GetExtension(source) : ".png") : ".mp4";
        var picker = new SaveFileDialog
        {
            Title = $"提取{ClipKindName(clip.Kind)}片段",
            FileName = string.IsNullOrWhiteSpace(clip.Name) ? "提取片段" : clip.Name,
            DefaultExt = defaultExtension.TrimStart('.'), AddExtension = true,
            Filter = clip.Kind == ClipKind.Image ? "图片文件|*.png;*.jpg;*.jpeg|所有文件|*.*" :
                clip.Kind == ClipKind.Audio ? "音频文件|*.wav;*.mp3;*.m4a;*.flac|所有文件|*.*" :
                "视频文件|*.mp4;*.mov;*.mkv|所有文件|*.*",
            OverwritePrompt = true
        };
        if (picker.ShowDialog(this) != true) return;
        string output = picker.FileName;
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
        { StatusText.Text = "提取目标不能覆盖源媒体"; return; }

        ProjectDocument expectedProject = _project;
        long expectedRevision = _projectRevision;
        StatusText.Text = "正在提取媒体片段…";
        try
        {
            await ExtractMediaFileAsync(source, output, clip.Kind, inPoint, duration);
            if (!ReferenceEquals(expectedProject, _project) || expectedRevision != _projectRevision)
            { StatusText.Text = "媒体已提取，但项目在操作期间发生变化，未删除时间线片段"; return; }
            StatusText.Text = $"已提取到：{output}";
            if (MessageBox.Show(this, "媒体提取完成。是否删除时间线中的原片段？", "提取完成",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            bool keepGap = MessageBox.Show(this, "删除后是否保留时间线空隙？\n选择“是”保留空隙，选择“否”关闭空隙。", "删除原片段",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            SelectClip(clip);
            if (keepGap) RemoveSelectedClipKeepingGap(); else RemoveSelectedClipAndCloseGap("已提取并删除原片段，时间线空隙已关闭");
        }
        catch (OperationCanceledException) { StatusText.Text = "提取已取消"; }
        // This method is async void, so an exception that escapes it is raised
        // on the UI thread with nothing to catch it and ends the application.
        // Process.Start throws Win32Exception when ffmpeg.exe is missing, which
        // is why that type is listed with the I/O ones.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        { StatusText.Text = $"提取失败：{exception.Message} · EDIT-EXTRACT"; }
    }

    private static async Task ExtractMediaFileAsync(string source, string output, ClipKind kind,
        double startSeconds, double durationSeconds)
    {
        string destination = Path.GetFullPath(output);
        string? directory = Path.GetDirectoryName(destination);
        if (directory is null || !Directory.Exists(directory)) throw new IOException("目标文件夹不存在");
        if (kind == ClipKind.Image)
        {
            await Task.Run(() => File.Copy(source, destination, overwrite: true));
            return;
        }
        string ffmpeg = TimelineExporter.ResolveEncoderPath();
        var start = startSeconds.ToString("0.######", CultureInfo.InvariantCulture);
        var duration = durationSeconds.ToString("0.######", CultureInfo.InvariantCulture);
        // Audio is re-encoded to PCM rather than stream-copied: the target is
        // usually a .wav, which cannot hold the AAC or MP3 a typical source
        // carries, so "-c copy" would fail. -map 0 is also dropped because it
        // drags every stream (subtitles, data) into a container that may not
        // accept them. Video keeps stream copy, which is lossless and fast.
        string streams = kind == ClipKind.Audio
            ? "-vn -c:a pcm_s16le"
            : "-map 0:v:0? -map 0:a? -c copy";
        string args = $"-hide_banner -y -nostdin -loglevel error -ss {start} -t {duration} " +
                      $"-i \"{source}\" {streams} \"{destination}\"";
        ProcessResult result = await ExternalProcessRunner.RunAsync(ffmpeg, args);
        if (!result.Succeeded)
            throw new InvalidOperationException(result.ErrorTail().Length > 0
                ? result.ErrorTail() : "本地媒体处理失败");
    }

    private void MatchFrameSelectedClip()
    {
        if (_selectedClip?.MediaAssetId is not string assetId) { StatusText.Text = "请选择带源媒体的片段"; return; }
        MediaAsset? asset = _project.Media.FirstOrDefault(item => item.Id == assetId);
        if (asset is null) { StatusText.Text = "找不到该片段的源媒体，请先重新链接 · EDIT-MATCH-SOURCE"; return; }
        double playhead = _project.Sequence.PlayheadSeconds;
        // SourceTime clamps a playhead outside the clip to its nearest frame,
        // and already accounts for the in-point, speed, remap knots and
        // reversal. Rebuilding it from SourceOffset alone gets a reversed clip
        // wrong, because a reversed clip plays its source backwards.
        double source = Math.Max(0, ClipTimeMap.SourceTime(_selectedClip, playhead));
        _sourceMonitorOverride = asset; _sourceMonitorInitialSeconds = source;
        OpenSelectedSourceMonitor();
    }

    private void AddLocalEditingCommands(ContextMenu menu, TimelineClip clip)
    {
        AddContextCommand(menu, "提取（关闭空隙）", () => { SelectClip(clip); ExtractSelectedClip(); });
        AddContextCommand(menu, "匹配帧到源监视器", () => { SelectClip(clip); MatchFrameSelectedClip(); });
        if (clip.Kind == ClipKind.Video)
            AddContextCommand(menu, "场景编辑检测并切段…", () => { SelectClip(clip); _ = DetectScenesAndSplitAsync(); });
    }

    private async Task DetectScenesAndSplitAsync()
    {
        if (_selectedClip?.MediaAssetId is not string assetId || _selectedTrack is null || _selectedTrack.IsLocked)
        { StatusText.Text = "请选择未锁定轨道上的视频片段"; return; }
        MediaAsset? asset = _project.Media.FirstOrDefault(item => item.Id == assetId);
        if (asset is null || string.IsNullOrWhiteSpace(asset.SourcePath) || !File.Exists(asset.SourcePath))
        { StatusText.Text = "场景检测需要可用的本地视频源"; return; }
        string? thresholdText = PromptText("场景编辑检测", "敏感度（0.01–1，默认 0.30）", "0.30");
        // A null answer is the user cancelling the dialog, not a request to
        // run with defaults.
        if (thresholdText is null) { StatusText.Text = "已取消场景检测"; return; }
        if (!double.TryParse(thresholdText, NumberStyles.Float, CultureInfo.CurrentCulture, out double threshold) ||
            !double.IsFinite(threshold) || threshold < .01 || threshold > 1)
        { StatusText.Text = "敏感度必须是 0.01 到 1 之间的数字 · EDIT-SCENE-INPUT"; return; }

        // Capture the target now. The user can select another clip while the
        // analysis runs, and the cuts must land on the clip that was analyzed.
        TimelineClip target = _selectedClip;
        TimelineTrack targetTrack = _selectedTrack;
        ProjectDocument expectedProject = _project;
        long expectedGeneration = _projectGeneration;
        long expectedRevision = _projectRevision;
        StatusText.Text = "正在本地分析场景切换…";
        try
        {
            // Scan only the part of the source this clip actually plays, with a
            // small margin. A long source with a short clip would otherwise be
            // analysed end to end for nothing. SourceTime is reversal-aware, so
            // the two ends are ordered rather than assumed.
            double usedA = ClipTimeMap.SourceTime(target, target.StartSeconds);
            double usedB = ClipTimeMap.SourceTime(target, target.StartSeconds + target.DurationSeconds);
            double scanFrom = Math.Max(0, Math.Min(usedA, usedB) - 0.5);
            double scanSpan = Math.Abs(usedB - usedA) + 1.0;
            var result = await SceneCutDetector.DetectSceneCutsAsync(asset.SourcePath, threshold,
                startSeconds: scanFrom, durationSeconds: scanSpan);
            if (!ReferenceEquals(expectedProject, _project) || expectedGeneration != _projectGeneration ||
                expectedRevision != _projectRevision || !targetTrack.Clips.Contains(target))
            { StatusText.Text = "场景检测已作废：分析期间项目已被修改"; return; }

            // Cut points are SOURCE times, but a clip only plays part of its
            // source, possibly sped up, remapped or reversed. Map each cut onto
            // this clip's own timeline; cuts the clip never shows are dropped.
            // Ask for every cut (minimum 0): the detector's spacing filter works
            // on source time, so it would be skewed by the clip's in-point and
            // speed. Spacing is applied below, on the timeline, where it matters.
            var mapped = new List<double>();
            foreach (double sourceCut in SceneCutDetector.SuggestCutPoints(result, 0))
            {
                double? local = ClipTimeMap.LocalTimeForSource(target, sourceCut);
                if (local is null) continue;
                double sequencePoint = target.StartSeconds + local.Value;
                if (sequencePoint > target.StartSeconds + .001 &&
                    sequencePoint < target.StartSeconds + target.DurationSeconds - .001)
                    mapped.Add(sequencePoint);
            }
            // Keep fragments at least a second or twelve frames long, whichever
            // is longer, so a fast clip cannot shatter into a few-frame slivers.
            double minimumSegment = Math.Max(1.0, 12.0 / Math.Max(1, _project.Sequence.FramesPerSecond));
            var sequencePoints = new List<double>();
            double previousEdge = target.StartSeconds;
            double clipEnd = target.StartSeconds + target.DurationSeconds;
            foreach (double point in mapped.Distinct().OrderBy(point => point))
            {
                if (point - previousEdge < minimumSegment || clipEnd - point < minimumSegment) continue;
                sequencePoints.Add(point);
                previousEdge = point;
            }
            if (sequencePoints.Count == 0) { StatusText.Text = "未在此片段使用的范围内检测到场景切点"; return; }

            SaveUndoState();
            TimelineClip current = target;
            foreach (double point in sequencePoints)
            {
                if (point <= current.StartSeconds || point >= current.StartSeconds + current.DurationSeconds) continue;
                TimelineClip right = SplitForEdit(current, point);
                targetTrack.Clips.Add(right);
                current = right;
            }
            _selectedClips.Clear();
            MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
            StatusText.Text = $"已按场景切分为 {sequencePoints.Count + 1} 段";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or UnauthorizedAccessException or OperationCanceledException)
        { StatusText.Text = $"场景检测失败：{exception.Message} · EDIT-SCENE-DETECT"; }
    }

    private void ToggleGlobalMute()
    {
        _globalMute = !_globalMute;
        // Monitor-only state: invalidate the rendered monitor window without
        // touching the project, so the save indicator stays accurate.
        _monitorStateRevision++;
        StatusText.Text = _globalMute ? "全局静音已开启（仅影响预览监听）" : "全局静音已关闭";
        if (_isPlaying) UpdateAudioPreview(_project.Sequence.PlayheadSeconds);
    }

    private void SoloSelectedTrack()
    {
        if (_selectedTrack is null) { StatusText.Text = "请先选择一条轨道"; return; }
        _soloTrackId = string.Equals(_soloTrackId, _selectedTrack.Id, StringComparison.Ordinal)
            ? null : _selectedTrack.Id;
        _monitorStateRevision++;
        StatusText.Text = _soloTrackId is null ? "已取消轨道独奏" : $"正在独奏：{_selectedTrack.Name}";
        if (_isPlaying) UpdateAudioPreview(_project.Sequence.PlayheadSeconds);
    }
}
