using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>Edits sequence and source metadata through undoable project changes.</summary>
public partial class MainWindow
{
    private void CreateSequence()
    {
        string? name = PromptText("新建序列", "序列名称", $"序列 {_project.Sequences.Count + 2}");
        if (string.IsNullOrWhiteSpace(name)) return;
        ProjectPickerItem? aspect = PromptChoice("序列宽高比", "选择序列宽高比",
            [new("16:9", "16:9 横屏"), new("4:3", "4:3 横屏"), new("1:1", "1:1 方形"),
             new("9:16", "9:16 竖屏"), new("4:5", "4:5 竖屏")], "16:9");
        if (aspect is null) return;
        SaveUndoState();
        SaveActiveSequenceInCollection();
        _project.Sequence = new ProjectSequence { Name = name.Trim(), Width = 1920,
            Height = 1080, FramesPerSecond = 30 };
        ApplyAspectRatio(_project.Sequence, aspect.Id ?? "16:9");
        _project.Tracks = DefaultSequenceTracks();
        _project.Markers = [];
        FinishSequenceSwitch("已创建新序列");
    }

    private void CreateSequenceFromMedia()
    {
        MediaAsset? asset = SelectedMedia();
        if (asset is null) { StatusText.Text = "请先选择一个媒体素材"; return; }
        double? sourceEnd = asset.SourceOutSeconds ?? asset.DurationSeconds;
        if (asset.Kind != MediaKind.Image && sourceEnd is double end &&
            (!double.IsFinite(end) || end <= asset.SourceInSeconds))
        {
            ShowProjectMediaError("SEQ-SOURCE-RANGE", "源媒体的可用入出点范围为空，请先检查入点和出点。");
            return;
        }
        SaveUndoState();
        SaveActiveSequenceInCollection();
        var sequence = new ProjectSequence
        {
            Name = Path.GetFileNameWithoutExtension(asset.Name),
            Width = 1920, Height = 1080, FramesPerSecond = 30
        };
        ApplyAspectRatio(sequence, asset.Width is > 0 && asset.Height is > 0
            ? AspectRatioOf(new ProjectSequence { Width = asset.Width.Value, Height = asset.Height.Value }) : "16:9");
        var track = new TimelineTrack { Name = asset.Kind == MediaKind.Audio ? "音频 1" : "视频 1",
            Kind = asset.Kind == MediaKind.Audio ? TrackKind.Audio : TrackKind.Video };
        // Unknown source duration gets a provisional five-second edit. When
        // the end is known, never extend the new clip beyond the source range.
        double usable = sourceEnd is double knownEnd ? knownEnd - asset.SourceInSeconds : 5;
        track.Clips.Add(new TimelineClip { Name = asset.Name,
            Kind = asset.Kind switch { MediaKind.Video => ClipKind.Video, MediaKind.Audio => ClipKind.Audio, _ => ClipKind.Image },
            MediaAssetId = asset.Id, SourceInSeconds = asset.SourceInSeconds,
            DurationSeconds = asset.Kind == MediaKind.Image ? 5 : usable });
        _project.Sequence = sequence;
        _project.Tracks = [track];
        _project.Markers = [];
        FinishSequenceSwitch("已由媒体创建序列");
    }

    private static List<TimelineTrack> DefaultSequenceTracks() =>
    [
        new TimelineTrack { Name = "视频 1", Kind = TrackKind.Video, Order = 0 },
        new TimelineTrack { Name = "音频 1", Kind = TrackKind.Audio, Order = 1 }
    ];

    private void SwitchSequence(string sequenceId)
    {
        if (sequenceId == _project.Sequence.Id) return;
        ProjectSequenceWorkspace? target = _project.Sequences.FirstOrDefault(item => item.Sequence.Id == sequenceId);
        if (target is null) { ShowProjectMediaError("SEQ-SWITCH", "找不到目标序列。"); return; }
        // Switching is an editor view operation. The project data stays the
        // same, but we keep an undo snapshot so edits after switching restore
        // a consistent active workspace.
        SaveUndoState();
        _project.Sequences.Remove(target);
        SaveActiveSequenceInCollection();
        _project.Sequence = target.Sequence;
        _project.Tracks = target.Tracks;
        _project.Markers = target.Markers;
        FinishSequenceSwitch($"已切换到“{_project.Sequence.Name}”");
    }

    private void FinishSequenceSwitch(string status)
    {
        ClearTimelineSelection();
        _pendingDropBatches.Clear();
        _isPlaying = false;
        _playbackTimer.Stop();
        PlayPauseButton.Content = "▶";
        MarkProjectDirty();
        RefreshProjectChrome();
        RefreshProjectMediaCommands();
        RefreshEditorFromProject();
        StatusText.Text = status;
    }

    private void SaveActiveSequenceInCollection()
    {
        _project.Sequences.Add(new ProjectSequenceWorkspace
        {
            Sequence = _project.Sequence,
            Tracks = _project.Tracks,
            Markers = _project.Markers
        });
    }

    private void DuplicateSequence()
    {
        SaveUndoState();
        var copy = JsonSerializer.Deserialize<ProjectSequenceWorkspace>(JsonSerializer.Serialize(new ProjectSequenceWorkspace
        {
            Sequence = _project.Sequence,
            Tracks = _project.Tracks,
            Markers = _project.Markers
        }))!;
        copy.Sequence.Id = Guid.NewGuid().ToString("N");
        copy.Sequence.Name += " 副本";
        foreach (TimelineTrack track in copy.Tracks)
        {
            track.Id = Guid.NewGuid().ToString("N");
            foreach (TimelineClip clip in track.Clips) clip.Id = Guid.NewGuid().ToString("N");
        }
        foreach (TimelineMarker marker in copy.Markers) marker.Id = Guid.NewGuid().ToString("N");
        SaveActiveSequenceInCollection();
        _project.Sequence = copy.Sequence;
        _project.Tracks = copy.Tracks;
        _project.Markers = copy.Markers;
        FinishSequenceSwitch("已复制序列");
    }

    private void RenameSequence()
    {
        string? name = PromptText("重命名序列", "序列名称", _project.Sequence.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == _project.Sequence.Name) return;
        SaveUndoState();
        _project.Sequence.Name = name.Trim();
        CommitProjectMediaChange("序列已重命名");
    }

    private void EditCurrentSequence()
    {
        string original = $"{_project.Sequence.Width}x{_project.Sequence.Height}@{_project.Sequence.FramesPerSecond:0.###}";
        string? value = PromptText("序列设置", "宽x高@帧率，例如 1920x1080@30", original);
        if (value is null) return;
        string[] parts = value.ToLowerInvariant().Split(['x', '@'], StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !int.TryParse(parts[0], out int width) ||
            !int.TryParse(parts[1], out int height) ||
            !double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double fps) ||
            width < 16 || width > 8192 || height < 16 || height > 8192 || fps < 1 || fps > 120)
        {
            ShowProjectMediaError("SEQ-SETTINGS", "序列规格无效。宽和高须在 16 到 8192 之间，帧率须在 1 到 120 之间。");
            return;
        }
        SaveUndoState();
        _project.Sequence.Width = width;
        _project.Sequence.Height = height;
        _project.Sequence.FramesPerSecond = fps;
        RefreshProjectChrome();
        CommitProjectMediaChange("序列设置已更新", true);
    }

    private void DeleteCurrentSequence()
    {
        if (_project.Sequences.Count == 0) { ShowProjectMediaError("SEQ-DELETE", "项目至少需要保留一个序列。"); return; }
        int nestedReferences = _project.Sequences.SelectMany(sequence => sequence.Tracks)
            .SelectMany(track => track.Clips)
            .Count(clip => clip.Kind == ClipKind.Sequence && clip.NestedSequenceId == _project.Sequence.Id);
        if (nestedReferences > 0)
        {
            ShowProjectMediaError("SEQ-DELETE-REFERENCED",
                $"有 {nestedReferences} 个嵌套序列片段仍引用当前序列。请先删除这些片段，再删除序列。");
            return;
        }
        if (MessageBox.Show(this, $"删除序列“{_project.Sequence.Name}”及其所有轨道？", "删除序列",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SaveUndoState();
        ProjectSequenceWorkspace next = _project.Sequences[0];
        _project.Sequences.RemoveAt(0);
        _project.Sequence = next.Sequence;
        _project.Tracks = next.Tracks;
        _project.Markers = next.Markers;
        FinishSequenceSwitch("序列已删除");
    }

    private void AddNestedSequence()
    {
        if (_project.Sequences.Count == 0) { StatusText.Text = "项目中没有其他序列"; return; }
        ProjectPickerItem? choice = PromptChoice("嵌套序列", "选择要嵌入的序列",
            _project.Sequences.Select(item => new ProjectPickerItem(item.Sequence.Id, item.Sequence.Name)).ToList());
        if (choice?.Id is null) return;
        if (SequenceContainsTarget(choice.Id, _project.Sequence.Id, new HashSet<string>(StringComparer.Ordinal)))
        { ShowProjectMediaError("SEQ-NEST-CYCLE", "该嵌套序列会形成循环引用。"); return; }
        ProjectSequenceWorkspace? nested = _project.Sequences.FirstOrDefault(item => item.Sequence.Id == choice.Id);
        if (nested is null) return;
        double duration = nested.Tracks.SelectMany(track => track.Clips).Select(clip => clip.StartSeconds + clip.DurationSeconds)
            .DefaultIfEmpty(5).Max();
        if (duration <= 0) duration = 5;
        SaveUndoState();
        TimelineTrack? destination = _project.Tracks.FirstOrDefault(track => track.Kind == TrackKind.Video && !track.IsLocked);
        if (destination is null)
        {
            destination = new TimelineTrack { Name = $"视频 {_project.Tracks.Count + 1}", Kind = TrackKind.Video,
                Order = _project.Tracks.Count };
            _project.Tracks.Add(destination);
        }
        var clip = new TimelineClip { Name = nested.Sequence.Name, Kind = ClipKind.Sequence,
            NestedSequenceId = nested.Sequence.Id, StartSeconds = _project.Sequence.PlayheadSeconds,
            DurationSeconds = duration };
        destination.Clips.Add(clip);
        _selectedClip = clip;
        _selectedTrack = destination;
        CommitProjectMediaChange("已嵌入序列", true);
    }

    private bool SequenceContainsTarget(string sequenceId, string targetId, HashSet<string> visited)
    {
        if (sequenceId == targetId) return true;
        if (!visited.Add(sequenceId)) return false;
        ProjectSequenceWorkspace? sequence = _project.FindSequence(sequenceId);
        return sequence is not null && sequence.Tracks.SelectMany(track => track.Clips)
            .Where(clip => clip.Kind == ClipKind.Sequence && clip.NestedSequenceId is not null)
            .Any(clip => SequenceContainsTarget(clip.NestedSequenceId!, targetId, visited));
    }

    private void AddSequenceMarker()
    {
        string? name = PromptText("添加序列标记", "标记名称", "标记");
        if (string.IsNullOrWhiteSpace(name)) return;
        string? notes = PromptText("标记备注", "备注（可留空）", string.Empty);
        if (notes is null) return;
        SaveUndoState();
        _project.Markers.Add(new TimelineMarker { Name = name.Trim(), Notes = notes,
            TimeSeconds = _project.Sequence.PlayheadSeconds });
        CommitProjectMediaChange("已添加序列标记", true);
    }

    private void ManageSequenceMarkers()
    {
        if (_project.Markers.Count == 0) { StatusText.Text = "当前序列没有标记"; return; }
        ProjectPickerItem? chosen = PromptChoice("序列标记", "选择要定位的标记",
            _project.Markers.OrderBy(marker => marker.TimeSeconds)
                .Select(marker => new ProjectPickerItem(marker.Id,
                    $"{TimeSpan.FromSeconds(marker.TimeSeconds):hh\\:mm\\:ss} · {marker.Name} {marker.Notes}"))
                .ToList());
        TimelineMarker? marker = _project.Markers.FirstOrDefault(item => item.Id == chosen?.Id);
        if (marker is null) return;
        SeekPreview(marker.TimeSeconds);
        var menu = new ContextMenu();
        menu.Items.Add(ContextCommand("编辑名称和备注…", () => EditMarker(marker)));
        menu.Items.Add(ContextCommand("删除标记", () =>
        {
            SaveUndoState(); _project.Markers.Remove(marker); CommitProjectMediaChange("标记已删除", true);
        }));
        menu.PlacementTarget = _sequenceSelector;
        menu.IsOpen = true;
    }

    private void EditMarker(TimelineMarker marker)
    {
        string? name = PromptText("编辑标记", "名称", marker.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        string? notes = PromptText("编辑标记", "备注", marker.Notes);
        if (notes is null) return;
        SaveUndoState();
        marker.Name = name.Trim(); marker.Notes = notes;
        CommitProjectMediaChange("标记已更新", true);
    }

    private void EditSelectedMediaDetails()
    {
        MediaAsset? asset = SelectedMedia();
        if (asset is null) return;
        string? name = PromptText("媒体信息", "显示名称", asset.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        string? tags = PromptText("媒体标签", "标签，用逗号分隔", string.Join(", ", asset.Tags));
        if (tags is null) return;
        string? description = PromptText("媒体说明", "说明 / 可搜索备注", asset.Description);
        if (description is null) return;
        ProjectPickerItem? color = PromptChoice("标签颜色", "选择标签颜色",
        [
            new("#75AEEB", "蓝色"), new("#FF9957", "橙色"), new("#80C597", "绿色"),
            new("#CB93E8", "紫色"), new("#E9C46A", "黄色"), new("#EE7777", "红色")
        ], asset.LabelColor);
        if (color is null) return;
        SaveUndoState();
        asset.Name = name.Trim();
        asset.Tags = tags.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        asset.Description = description;
        asset.LabelColor = color.Id!;
        CommitProjectMediaChange("媒体信息已更新");
    }

    private void EditSelectedSourceRange()
    {
        MediaAsset? asset = SelectedMedia();
        if (asset is null || asset.Kind == MediaKind.Image) return;
        string? start = PromptText("源素材入点", "入点（秒）", asset.SourceInSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        if (start is null) return;
        string? end = PromptText("源素材出点", "出点（秒，留空表示结尾）",
            asset.SourceOutSeconds?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        if (end is null) return;
        bool startValid = double.TryParse(start, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double inPoint) && double.IsFinite(inPoint) && inPoint >= 0;
        double? outPoint = null;
        if (!string.IsNullOrWhiteSpace(end) && double.TryParse(end,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out double parsedOut)) outPoint = parsedOut;
        bool endValid = string.IsNullOrWhiteSpace(end) ||
            outPoint is double endSeconds && double.IsFinite(endSeconds) && endSeconds > inPoint;
        if (!startValid || !endValid || (asset.DurationSeconds is > 0 && inPoint >= asset.DurationSeconds) ||
            (asset.DurationSeconds is > 0 && outPoint is double boundedOut &&
             boundedOut > asset.DurationSeconds + 0.01))
        {
            ShowProjectMediaError("MED-SOURCE-RANGE", "源入出点无效或超出媒体时长。");
            return;
        }
        SaveUndoState();
        asset.SourceInSeconds = inPoint;
        asset.SourceOutSeconds = outPoint;
        CommitProjectMediaChange("源入出点已更新，之后添加到时间轴的片段会使用该范围");
    }

    private void AddSelectedSourceMarker()
    {
        MediaAsset? asset = SelectedMedia();
        if (asset is null) return;
        string? time = PromptText("源素材标记", "时间（秒）", asset.SourceInSeconds.ToString("0.###",
            System.Globalization.CultureInfo.InvariantCulture));
        if (time is null) return;
        if (!double.TryParse(time, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double seconds) || !double.IsFinite(seconds) || seconds < 0 ||
            (asset.DurationSeconds is > 0 && seconds > asset.DurationSeconds))
        { ShowProjectMediaError("MED-MARKER", "标记时间无效。"); return; }
        string? name = PromptText("源素材标记", "名称", "标记");
        if (string.IsNullOrWhiteSpace(name)) return;
        string? notes = PromptText("源素材标记", "备注", string.Empty);
        if (notes is null) return;
        SaveUndoState();
        asset.Markers.Add(new TimelineMarker { TimeSeconds = seconds, Name = name.Trim(), Notes = notes });
        CommitProjectMediaChange("源素材标记已添加");
    }

    private void ShowSelectedMediaMetadata()
    {
        MediaAsset? asset = SelectedMedia();
        if (asset is null) return;
        string values = $"名称：{asset.Name}\n路径：{asset.SourcePath}\n类型：{asset.Kind}\n" +
            $"时长：{asset.DurationSeconds?.ToString("0.###") ?? "未知"} 秒\n" +
            $"画面：{asset.Width?.ToString() ?? "—"} × {asset.Height?.ToString() ?? "—"}\n" +
            $"帧率：{asset.FramesPerSecond?.ToString("0.###") ?? "—"} fps\n" +
            $"视频编码：{asset.VideoCodec ?? "—"}\n音频编码：{asset.AudioCodec ?? "—"}\n" +
            $"音频声道：{asset.AudioChannels?.ToString() ?? "—"}\n采样率：{asset.AudioSampleRate?.ToString() ?? "—"}\n" +
            $"文件大小：{asset.FileSizeBytes?.ToString("N0") ?? "—"} 字节\n" +
            $"文件修改：{asset.FileModifiedUtc?.ToLocalTime().ToString("g") ?? "—"}\n" +
            $"源入点：{asset.SourceInSeconds:0.###} 秒\n源出点：{asset.SourceOutSeconds?.ToString("0.###") ?? "结尾"}\n" +
            $"标签：{string.Join("、", asset.Tags)}\n说明：{asset.Description}\n" +
            string.Join("\n", asset.Metadata.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}：{pair.Value}"));
        MessageBox.Show(this, values, "媒体元数据", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowProjectMediaError(string stageCode, string message, Exception? exception = null)
    {
        StatusText.Text = $"项目媒体操作失败 · {stageCode}";
        MessageBox.Show(this, $"{message}\n阶段代码：{stageCode}" +
            (exception is null ? string.Empty : $"\n{exception.Message}"),
            "项目媒体操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}


