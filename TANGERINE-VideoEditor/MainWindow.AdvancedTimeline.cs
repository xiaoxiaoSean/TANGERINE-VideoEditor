using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Implements edit decisions that change clip boundaries and associations.
/// All calculations use sequence seconds and source in-points, so the saved
/// .tvep project remains independent of the current timeline zoom.
/// </summary>
public partial class MainWindow
{
    private IEnumerable<TimelineClip> ExistingSelection()
    {
        var selected = _selectedClips.Where(clip => _project.Tracks.Any(track => track.Clips.Contains(clip))).ToList();
        if (selected.Count == 0 && _selectedClip is not null &&
            _project.Tracks.Any(track => track.Clips.Contains(_selectedClip)))
            selected.Add(_selectedClip);
        return selected;
    }

    private void RippleTrimStartToPlayhead()
    {
        if (!TryGetTargetTrack(out TimelineTrack track) || _selectedClip is null) return;
        double cut = _project.Sequence.PlayheadSeconds;
        TimelineClip clip = _selectedClip;
        if (cut <= clip.StartSeconds || cut >= clip.StartSeconds + clip.DurationSeconds) return;
        double removed = cut - clip.StartSeconds;
        SaveUndoState();
        ClipAnimation.KeepRange(clip, removed, clip.DurationSeconds - removed);
        AdvanceMulticamLocalStart(clip, removed);
        if (clip.TimeRemapPoints.Count > 0)
            ClipTimeMap.KeepRange(clip, removed, clip.DurationSeconds - removed);
        else if (!clip.IsReversed) clip.SourceInSeconds += removed * clip.Speed;
        clip.DurationSeconds -= removed;
        double removedStart = clip.StartSeconds;
        foreach (TimelineTrack affected in _project.Tracks.Where(candidate => !candidate.IsLocked &&
            (ReferenceEquals(candidate, track) || candidate.IsSyncLocked)))
            RemoveTimelineInterval(affected, removedStart, removedStart + removed, clip);
        foreach (TimelineMarker marker in _project.Markers)
            if (marker.TimeSeconds >= removedStart + removed) marker.TimeSeconds -= removed;
            else if (marker.TimeSeconds > removedStart) marker.TimeSeconds = removedStart;
        _project.Sequence.PlayheadSeconds = Math.Max(removedStart,
            _project.Sequence.PlayheadSeconds - removed);
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = "已波纹修剪入点";
    }

    private void RippleTrimEndToPlayhead()
    {
        if (!TryGetTargetTrack(out TimelineTrack track) || _selectedClip is null) return;
        double cut = _project.Sequence.PlayheadSeconds;
        TimelineClip clip = _selectedClip;
        double oldEnd = clip.StartSeconds + clip.DurationSeconds;
        if (cut <= clip.StartSeconds || cut >= oldEnd) return;
        double removed = oldEnd - cut;
        SaveUndoState();
        ClipAnimation.KeepRange(clip, 0, clip.DurationSeconds - removed);
        if (clip.TimeRemapPoints.Count > 0)
            ClipTimeMap.KeepRange(clip, 0, clip.DurationSeconds - removed);
        else if (clip.IsReversed) clip.SourceInSeconds += removed * clip.Speed;
        clip.DurationSeconds -= removed;
        foreach (TimelineTrack affected in _project.Tracks.Where(candidate => !candidate.IsLocked &&
            (ReferenceEquals(candidate, track) || candidate.IsSyncLocked)))
            RemoveTimelineInterval(affected, oldEnd - removed, oldEnd, clip);
        foreach (TimelineMarker marker in _project.Markers)
            if (marker.TimeSeconds >= oldEnd) marker.TimeSeconds -= removed;
            else if (marker.TimeSeconds > oldEnd - removed) marker.TimeSeconds = oldEnd - removed;
        _project.Sequence.PlayheadSeconds = Math.Min(_project.Sequence.PlayheadSeconds,
            oldEnd - removed);
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = "已波纹修剪出点";
    }

    private void GroupSelectedClips(bool link)
    {
        var clips = ExistingSelection().Where(clip =>
            _project.Tracks.FirstOrDefault(track => track.Clips.Contains(clip))?.IsLocked == false).ToList();
        if (clips.Count < 2)
        {
            StatusText.Text = link ? "按住 Ctrl 选择至少两个片段后链接" : "按住 Ctrl 选择至少两个片段后编组";
            return;
        }
        var priorIds = clips.Select(clip => link ? clip.LinkGroupId : clip.GroupId)
            .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet();
        if (_project.Tracks.Where(track => track.IsLocked).SelectMany(track => track.Clips)
            .Any(clip => priorIds.Contains(link ? clip.LinkGroupId : clip.GroupId)))
        {
            StatusText.Text = "关联组包含锁定轨道片段 · EDIT-GROUP-LOCKED";
            return;
        }
        SaveUndoState();
        string associationId = Guid.NewGuid().ToString("N");
        // Merging an existing group must include its unselected members;
        // otherwise a partial reassignment silently breaks the association.
        foreach (TimelineClip clip in _project.Tracks.SelectMany(track => track.Clips)
            .Where(clip => clips.Contains(clip) ||
                priorIds.Contains(link ? clip.LinkGroupId : clip.GroupId)))
        {
            if (link) clip.LinkGroupId = associationId;
            else clip.GroupId = associationId;
        }
        MarkProjectDirty();
        DrawTimeline();
        StatusText.Text = link ? $"已链接 {clips.Count} 个片段" : $"已编组 {clips.Count} 个片段";
    }

    private void UngroupSelectedClips(bool unlink)
    {
        var selected = ExistingSelection().ToList();
        string[] ids = selected.Select(clip => unlink ? clip.LinkGroupId : clip.GroupId)
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().Cast<string>().ToArray();
        if (ids.Length == 0)
        {
            StatusText.Text = unlink ? "所选片段没有链接" : "所选片段没有编组";
            return;
        }
        if (_project.Tracks.Where(track => track.IsLocked).SelectMany(track => track.Clips)
            .Any(clip => ids.Contains(unlink ? clip.LinkGroupId : clip.GroupId)))
        {
            StatusText.Text = "关联组包含锁定轨道片段 · EDIT-GROUP-LOCKED";
            return;
        }
        SaveUndoState();
        foreach (TimelineClip clip in _project.Tracks.SelectMany(track => track.Clips))
        {
            if (unlink && ids.Contains(clip.LinkGroupId)) clip.LinkGroupId = null;
            if (!unlink && ids.Contains(clip.GroupId)) clip.GroupId = null;
        }
        MarkProjectDirty();
        DrawTimeline();
        StatusText.Text = unlink ? "已解除链接" : "已取消编组";
    }

    private void CaptureMoveCompanions()
    {
        _moveCompanionOriginalStarts.Clear();
        if (_selectedClip is null) return;
        var selected = ExistingSelection().ToHashSet();
        var groupIds = selected.Select(clip => clip.GroupId)
            .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet();
        var linkIds = selected.Select(clip => clip.LinkGroupId)
            .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet();
        foreach (TimelineTrack track in _project.Tracks.Where(track => !track.IsLocked))
        foreach (TimelineClip clip in track.Clips)
        {
            if (ReferenceEquals(clip, _selectedClip)) continue;
            if (selected.Contains(clip) ||
                (!string.IsNullOrWhiteSpace(clip.GroupId) && groupIds.Contains(clip.GroupId)) ||
                (!string.IsNullOrWhiteSpace(clip.LinkGroupId) && linkIds.Contains(clip.LinkGroupId)))
                _moveCompanionOriginalStarts[clip] = clip.StartSeconds;
        }
    }

    private bool TryGetTargetTrack(out TimelineTrack track)
    {
        track = _selectedTrack!;
        if (_selectedClip is null || track is null || !_project.Tracks.Contains(track) ||
            track.IsLocked || !track.Clips.Contains(_selectedClip))
        {
            StatusText.Text = "请在未锁定的轨道上选择片段";
            return false;
        }
        return true;
    }

    private static TimelineClip SplitForEdit(TimelineClip clip, double splitSeconds)
    {
        TimelineClip right = CloneClip(clip);
        right.Id = Guid.NewGuid().ToString("N");
        double leftLength = splitSeconds - clip.StartSeconds;
        right.StartSeconds = splitSeconds;
        double rightLength = clip.DurationSeconds - leftLength;
        ClipAnimation.KeepRange(right, leftLength, rightLength);
        AdvanceMulticamLocalStart(right, leftLength);
        ClipAnimation.KeepRange(clip, 0, leftLength);
        if (clip.TimeRemapPoints.Count > 0)
        {
            ClipTimeMap.KeepRange(right, leftLength, rightLength);
            ClipTimeMap.KeepRange(clip, 0, leftLength);
        }
        else if (clip.IsReversed)
            clip.SourceInSeconds += rightLength * clip.Speed;
        else
            right.SourceInSeconds += leftLength * clip.Speed;
        clip.DurationSeconds = leftLength;
        right.DurationSeconds = rightLength;
        right.GroupId = null;
        right.LinkGroupId = null;
        return right;
    }


    /// <summary>
    /// Remove a sequence-time interval from one unlocked track. Splitting
    /// both boundaries first preserves source in-points, remap knots,
    /// animations, and multicamera local clocks on the retained fragments.
    /// </summary>
    private static void RemoveTimelineInterval(TimelineTrack track, double start,
        double end, TimelineClip? excludedClip = null)
    {
        if (end <= start) return;
        foreach (TimelineClip crossing in track.Clips.Where(clip =>
            !ReferenceEquals(clip, excludedClip) && clip.StartSeconds < start &&
            clip.StartSeconds + clip.DurationSeconds > start).ToArray())
            track.Clips.Add(SplitForEdit(crossing, start));
        foreach (TimelineClip crossing in track.Clips.Where(clip =>
            !ReferenceEquals(clip, excludedClip) && clip.StartSeconds >= start &&
            clip.StartSeconds < end && clip.StartSeconds + clip.DurationSeconds > end).ToArray())
            track.Clips.Add(SplitForEdit(crossing, end));
        track.Clips.RemoveAll(clip => !ReferenceEquals(clip, excludedClip) &&
            clip.StartSeconds >= start && clip.StartSeconds < end &&
            clip.StartSeconds + clip.DurationSeconds <= end);
        foreach (TimelineClip later in track.Clips.Where(clip =>
            !ReferenceEquals(clip, excludedClip) && clip.StartSeconds >= end))
            later.StartSeconds -= end - start;
    }

    private void InsertClipboardAtPlayhead()
    {
        if (_clipboardClip is null)
        {
            StatusText.Text = "请先复制片段";
            return;
        }
        TimelineTrack? track = ResolveCommandTarget(_clipboardClip);
        if (track is null)
        {
            StatusText.Text = "没有可用的目标轨道";
            return;
        }
        if (!double.IsFinite(_clipboardClip.DurationSeconds) || _clipboardClip.DurationSeconds <= 0)
        {
            StatusText.Text = "剪贴板片段时长无效 · EDIT-INSERT";
            return;
        }
        double insertion = Math.Max(0, _snapEnabled ? SnapTime(_project.Sequence.PlayheadSeconds) : _project.Sequence.PlayheadSeconds);
        if (!double.IsFinite(insertion) || insertion + _clipboardClip.DurationSeconds > 24 * 3600)
        {
            StatusText.Text = "插入时间超出有效范围 · EDIT-INSERT";
            return;
        }
        SaveUndoState();
        // First divide a clip spanning the insertion point. Its right half
        // then participates in the same shift as all later clips.
        foreach (TimelineTrack affected in _project.Tracks.Where(candidate => !candidate.IsLocked &&
            (ReferenceEquals(candidate, track) || candidate.IsSyncLocked)))
        {
            foreach (TimelineClip crossing in affected.Clips.Where(clip => clip.StartSeconds < insertion &&
                clip.StartSeconds + clip.DurationSeconds > insertion).ToList())
                affected.Clips.Add(SplitForEdit(crossing, insertion));
            foreach (TimelineClip later in affected.Clips.Where(clip => clip.StartSeconds >= insertion))
                later.StartSeconds += _clipboardClip.DurationSeconds;
        }
        foreach (TimelineMarker marker in _project.Markers)
            if (marker.TimeSeconds >= insertion) marker.TimeSeconds += _clipboardClip.DurationSeconds;
        TimelineClip inserted = CloneClip(_clipboardClip);
        inserted.Id = Guid.NewGuid().ToString("N");
        inserted.StartSeconds = insertion;
        inserted.GroupId = null;
        inserted.LinkGroupId = null;
        track.Clips.Add(inserted);
        SelectClip(inserted);
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(insertion);
        StatusText.Text = "已插入片段并推开后续内容";
    }

    private void OverwriteClipboardAtPlayhead()
    {
        if (_clipboardClip is null)
        {
            StatusText.Text = "请先复制片段";
            return;
        }
        TimelineTrack? track = ResolveCommandTarget(_clipboardClip);
        if (track is null)
        {
            StatusText.Text = "没有可用的目标轨道";
            return;
        }
        if (!double.IsFinite(_clipboardClip.DurationSeconds) || _clipboardClip.DurationSeconds <= 0)
        {
            StatusText.Text = "剪贴板片段时长无效 · EDIT-OVERWRITE";
            return;
        }
        double start = Math.Max(0, _snapEnabled ? SnapTime(_project.Sequence.PlayheadSeconds) : _project.Sequence.PlayheadSeconds);
        double end = start + _clipboardClip.DurationSeconds;
        if (!double.IsFinite(end) || end > 24 * 3600)
        {
            StatusText.Text = "覆盖时间超出有效范围 · EDIT-OVERWRITE";
            return;
        }
        SaveUndoState();
        foreach (TimelineClip old in track.Clips.Where(clip =>
            clip.StartSeconds < end && clip.StartSeconds + clip.DurationSeconds > start).ToList())
        {
            double oldEnd = old.StartSeconds + old.DurationSeconds;
            if (old.StartSeconds < start && oldEnd > end)
            {
                TimelineClip right = CloneClip(old);
                right.Id = Guid.NewGuid().ToString("N");
                right.StartSeconds = end;
                double rightLength = oldEnd - end;
                ClipAnimation.KeepRange(right, end - old.StartSeconds, rightLength);
                AdvanceMulticamLocalStart(right, end - old.StartSeconds);
                ClipAnimation.KeepRange(old, 0, start - old.StartSeconds);
                if (old.TimeRemapPoints.Count > 0)
                {
                    ClipTimeMap.KeepRange(right, end - old.StartSeconds, rightLength);
                    ClipTimeMap.KeepRange(old, 0, start - old.StartSeconds);
                }
                else if (old.IsReversed)
                    old.SourceInSeconds += (oldEnd - start) * old.Speed;
                else
                    right.SourceInSeconds += (end - old.StartSeconds) * old.Speed;
                old.DurationSeconds = start - old.StartSeconds;
                right.DurationSeconds = rightLength;
                right.GroupId = null;
                right.LinkGroupId = null;
                track.Clips.Add(right);
            }
            else if (old.StartSeconds < start)
            {
                ClipAnimation.KeepRange(old, 0, start - old.StartSeconds);
                if (old.TimeRemapPoints.Count > 0)
                    ClipTimeMap.KeepRange(old, 0, start - old.StartSeconds);
                else if (old.IsReversed) old.SourceInSeconds += (oldEnd - start) * old.Speed;
                old.DurationSeconds = start - old.StartSeconds;
            }
            else if (oldEnd > end)
            {
                ClipAnimation.KeepRange(old, end - old.StartSeconds, oldEnd - end);
                AdvanceMulticamLocalStart(old, end - old.StartSeconds);
                if (old.TimeRemapPoints.Count > 0)
                    ClipTimeMap.KeepRange(old, end - old.StartSeconds, oldEnd - end);
                else if (!old.IsReversed) old.SourceInSeconds += (end - old.StartSeconds) * old.Speed;
                old.StartSeconds = end;
                old.DurationSeconds = oldEnd - end;
            }
            else track.Clips.Remove(old);
        }
        TimelineClip inserted = CloneClip(_clipboardClip);
        inserted.Id = Guid.NewGuid().ToString("N");
        inserted.StartSeconds = start;
        inserted.GroupId = null;
        inserted.LinkGroupId = null;
        track.Clips.Add(inserted);
        SelectClip(inserted);
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(start);
        StatusText.Text = "已覆盖目标轨道的指定时间范围";
    }

    private void SlipSelectedClip()
    {
        if (!TryGetTargetTrack(out _) || _selectedClip is null) return;
        TimelineClip clip = _selectedClip;
        if (clip.Kind is not (ClipKind.Video or ClipKind.Audio))
        {
            StatusText.Text = "滑移编辑适用于视频和音频片段";
            return;
        }
        double? offset = RequestTimeOffset("滑移编辑", "源素材偏移（秒，可填负数）");
        if (offset is null) return;
        if (Math.Abs(offset.Value) < .000001) return;
        if (!_project.Tracks.Any(track => track.Clips.Contains(clip)))
        {
            StatusText.Text = "片段已离开当前项目 · EDIT-SLIP-STATE";
            return;
        }
        if (!ReferenceEquals(_selectedClip, clip))
        {
            StatusText.Text = "片段已变化 · EDIT-SLIP-STATE";
            return;
        }
        if (_selectedTrack is null || _selectedTrack.IsLocked ||
            !_selectedTrack.Clips.Contains(clip))
        {
            StatusText.Text = "片段或轨道已变化 · EDIT-SLIP-STATE";
            return;
        }
        if (clip.IsReversed) offset = -offset.Value;
        if (clip.MulticamGroupId is not null)
        {
            StatusText.Text = "多机位切段不能单独滑移源素材 · MULTICAM-EDIT";
            return;
        }
        MediaAsset? source = _project.Media.FirstOrDefault(media => media.Id == clip.MediaAssetId);
        double newSourceIn = clip.SourceInSeconds + offset.Value;
        double usedSource = ClipTimeMap.SourceOffset(clip, clip.DurationSeconds);
        if (!double.IsFinite(usedSource) || newSourceIn < 0 ||
            (source?.DurationSeconds is double available && newSourceIn + usedSource > available + 0.001))
        {
            StatusText.Text = "滑移超出源素材范围";
            return;
        }
        SaveUndoState();
        clip.SourceInSeconds = newSourceIn;
        MarkProjectDirty();
        SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = "已滑移片段源画面，时间线位置保持不变";
    }

    /// <summary>
    /// Prepare a boundary edit on a detached copy. The forward and reversed
    /// source windows, speed knots, and animation keys are all rebased before
    /// the live project is changed. This also permits a negative local start
    /// when a neighboring clip is extended into its available source handles.
    /// </summary>
    private bool TryPrepareBoundaryClip(TimelineClip original, double localStart,
        double newDuration, out TimelineClip prepared)
    {
        prepared = original;
        double minimum = 1 / Math.Max(1, _project.Sequence.FramesPerSecond);
        if (!double.IsFinite(localStart) || !double.IsFinite(newDuration) ||
            newDuration < minimum || newDuration > 24 * 3600)
            return false;
        prepared = CloneClip(original);
        ClipAnimation.KeepRange(prepared, localStart, newDuration);
        if (prepared.TimeRemapPoints.Count > 0)
            ClipTimeMap.KeepRange(prepared, localStart, newDuration);
        else
        {
            prepared.SourceInSeconds += original.IsReversed
                ? (original.DurationSeconds - localStart - newDuration) * original.Speed
                : localStart * original.Speed;
        }
        prepared.DurationSeconds = newDuration;
        double sourceSpan = ClipTimeMap.SourceOffset(prepared, newDuration);
        MediaAsset? media = _project.Media.FirstOrDefault(item => item.Id == original.MediaAssetId);
        return double.IsFinite(prepared.SourceInSeconds) &&
            prepared.SourceInSeconds >= -0.000001 && double.IsFinite(sourceSpan) &&
            sourceSpan > 0 && (media?.DurationSeconds is not double available ||
                prepared.SourceInSeconds + sourceSpan <= available + .001);
    }

    private static void ApplyPreparedBoundary(TimelineClip destination, TimelineClip prepared)
    {
        destination.SourceInSeconds = Math.Max(0, prepared.SourceInSeconds);
        destination.DurationSeconds = prepared.DurationSeconds;
        destination.TimeRemapPoints = prepared.TimeRemapPoints;
        destination.Keyframes = prepared.Keyframes;
    }

    private void RollSelectedCut()
    {
        if (!TryGetTargetTrack(out TimelineTrack track) || _selectedClip is null) return;
        TimelineClip left = _selectedClip;
        double cut = left.StartSeconds + left.DurationSeconds;
        TimelineClip? right = track.Clips.FirstOrDefault(candidate => !ReferenceEquals(candidate, left) &&
            Math.Abs(candidate.StartSeconds - cut) < 0.5 / Math.Max(1, _project.Sequence.FramesPerSecond));
        if (right is null)
        {
            StatusText.Text = "请选择紧邻下一片段的左侧片段";
            return;
        }
        if (track.Clips.Any(candidate => !ReferenceEquals(candidate, left) &&
            !ReferenceEquals(candidate, right) &&
            candidate.StartSeconds < right.StartSeconds + right.DurationSeconds &&
            candidate.StartSeconds + candidate.DurationSeconds > left.StartSeconds))
        {
            StatusText.Text = "切点附近存在重叠片段 · EDIT-ROLL-OVERLAP";
            return;
        }
        if (left.Kind is not (ClipKind.Video or ClipKind.Audio) ||
            right.Kind is not (ClipKind.Video or ClipKind.Audio))
        {
            StatusText.Text = "滚动编辑适用于相邻视频或音频片段 · EDIT-ROLL-TYPE";
            return;
        }
        if (left.Kind != right.Kind)
        {
            StatusText.Text = "滚动编辑需在同类相邻片段之间进行 · EDIT-ROLL-TYPE";
            return;
        }
        if (left.MulticamGroupId is not null || right.MulticamGroupId is not null)
        {
            StatusText.Text = "多机位切段暂不支持滚动编辑 · MULTICAM-EDIT";
            return;
        }
        double? offset = RequestTimeOffset("滚动编辑", "切点偏移（秒，可填负数）");
        if (offset is null) return;
        if (Math.Abs(offset.Value) < .000001) return;
        if (!ReferenceEquals(_selectedClip, left) || !_project.Tracks.Contains(track) ||
            Math.Abs(left.StartSeconds + left.DurationSeconds - cut) > .000001 ||
            Math.Abs(right.StartSeconds - cut) >
                .5 / Math.Max(1, _project.Sequence.FramesPerSecond))
        {
            StatusText.Text = "切点已变化 · EDIT-ROLL-STATE";
            return;
        }
        if (!track.Clips.Contains(left) || !track.Clips.Contains(right) || track.IsLocked)
        {
            StatusText.Text = "片段或轨道已变化 · EDIT-ROLL-STATE";
            return;
        }
        double leftDuration = left.DurationSeconds + offset.Value;
        double rightDuration = right.DurationSeconds - offset.Value;
        if (!TryPrepareBoundaryClip(left, 0, leftDuration, out TimelineClip preparedLeft) ||
            !TryPrepareBoundaryClip(right, offset.Value, rightDuration, out TimelineClip preparedRight))
        {
            StatusText.Text = "滚动切点超出相邻片段可用范围";
            return;
        }
        SaveUndoState();
        ApplyPreparedBoundary(left, preparedLeft);
        ApplyPreparedBoundary(right, preparedRight);
        right.StartSeconds = cut + offset.Value;
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = "已滚动编辑切点，总时长保持不变";
    }

    private void SlideSelectedClip()
    {
        if (!TryGetTargetTrack(out TimelineTrack track) || _selectedClip is null) return;
        TimelineClip middle = _selectedClip;
        TimelineClip? left = track.Clips.FirstOrDefault(candidate => !ReferenceEquals(candidate, middle) &&
            Math.Abs(candidate.StartSeconds + candidate.DurationSeconds - middle.StartSeconds) <
                0.5 / Math.Max(1, _project.Sequence.FramesPerSecond));
        TimelineClip? right = track.Clips.FirstOrDefault(candidate => !ReferenceEquals(candidate, middle) &&
            Math.Abs(candidate.StartSeconds - middle.StartSeconds - middle.DurationSeconds) <
                0.5 / Math.Max(1, _project.Sequence.FramesPerSecond));
        if (left is null || right is null)
        {
            StatusText.Text = "滑动编辑需要片段两侧都有紧邻片段";
            return;
        }
        if (track.Clips.Any(candidate => !ReferenceEquals(candidate, left) &&
            !ReferenceEquals(candidate, middle) && !ReferenceEquals(candidate, right) &&
            candidate.StartSeconds < right.StartSeconds &&
            candidate.StartSeconds + candidate.DurationSeconds > left.StartSeconds + left.DurationSeconds))
        {
            StatusText.Text = "滑动范围存在重叠片段 · EDIT-SLIDE-OVERLAP";
            return;
        }
        if (middle.Kind is not (ClipKind.Video or ClipKind.Audio) ||
            left.Kind is not (ClipKind.Video or ClipKind.Audio) ||
            right.Kind is not (ClipKind.Video or ClipKind.Audio))
        {
            StatusText.Text = "滑动编辑适用于视频或音频片段 · EDIT-SLIDE-TYPE";
            return;
        }
        if (left.Kind != middle.Kind || right.Kind != middle.Kind)
        {
            StatusText.Text = "滑动编辑需在同类相邻片段之间进行 · EDIT-SLIDE-TYPE";
            return;
        }
        if (left.MulticamGroupId is not null || right.MulticamGroupId is not null ||
            middle.MulticamGroupId is not null)
        {
            StatusText.Text = "多机位切段暂不支持滑动编辑 · MULTICAM-EDIT";
            return;
        }
        double? offset = RequestTimeOffset("滑动编辑", "位置偏移（秒，可填负数）");
        if (offset is null) return;
        if (Math.Abs(offset.Value) < .000001) return;
        if (!ReferenceEquals(_selectedClip, middle) || !_project.Tracks.Contains(track) ||
            Math.Abs(left.StartSeconds + left.DurationSeconds - middle.StartSeconds) >
                .5 / Math.Max(1, _project.Sequence.FramesPerSecond) ||
            Math.Abs(right.StartSeconds - middle.StartSeconds - middle.DurationSeconds) >
                .5 / Math.Max(1, _project.Sequence.FramesPerSecond))
        {
            StatusText.Text = "相邻切点已变化 · EDIT-SLIDE-STATE";
            return;
        }
        if (!track.Clips.Contains(left) || !track.Clips.Contains(middle) ||
            !track.Clips.Contains(right) || track.IsLocked)
        {
            StatusText.Text = "片段或轨道已变化 · EDIT-SLIDE-STATE";
            return;
        }
        double newLeftDuration = left.DurationSeconds + offset.Value;
        double newRightDuration = right.DurationSeconds - offset.Value;
        if (middle.StartSeconds + offset.Value < 0 ||
            !TryPrepareBoundaryClip(left, 0, newLeftDuration, out TimelineClip preparedLeft) ||
            !TryPrepareBoundaryClip(right, offset.Value, newRightDuration, out TimelineClip preparedRight))
        {
            StatusText.Text = "滑动编辑超出相邻片段可用范围";
            return;
        }
        SaveUndoState();
        ApplyPreparedBoundary(left, preparedLeft);
        ApplyPreparedBoundary(right, preparedRight);
        middle.StartSeconds += offset.Value;
        right.StartSeconds += offset.Value;
        MarkProjectDirty();
        RefreshTimeline();
        SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = "已滑动片段，序列总时长保持不变";
    }

    private double? RequestTimeOffset(string title, string instruction)
    {
        double screenWidth = Math.Max(1, SystemParameters.PrimaryScreenWidth);
        var dialog = new Window
        {
            Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = screenWidth * .24, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            Background = Brush("#191B20"), Foreground = Brushes.White
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = instruction, Margin = new Thickness(0, 0, 0, 8) });
        var input = new TextBox { Text = "0", Margin = new Thickness(0, 0, 0, 12),
            Foreground = Brushes.White, Background = Brush("#292B31"), CaretBrush = Brushes.White };
        panel.Children.Add(input);
        var confirm = new Button { Content = "确定", HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(18, 5, 18, 5) };
        confirm.Click += (_, _) => dialog.DialogResult = true;
        panel.Children.Add(confirm);
        dialog.Content = panel;
        if (dialog.ShowDialog() != true) return null;
        if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ||
            !double.IsFinite(value))
        {
            MessageBox.Show(this, "请输入有效的秒数。\n阶段代码：EDIT-TIME", "输入无效",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        // A sub-frame offset rounds to zero when snapping is enabled. Treat
        // that as a canceled edit so callers do not save a no-op undo state.
        double offset = _snapEnabled ? SnapTime(value) : value;
        return Math.Abs(offset) < .000001 ? null : offset;
    }
}
