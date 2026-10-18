using TANGERINE_VideoEditor.Project;
using TANGERINE_VideoEditor.Tools;

namespace TANGERINE_VideoEditor;

public partial class MainWindow
{
    private void PromptRenameTrack(TimelineTrack? track)
    {
        if (track is null || !_project.Tracks.Contains(track))
        { StatusText.Text = "请先选择要重命名的轨道 · TMLNE0015"; return; }
        ProjectDocument owner = _project;
        string? name = PromptText("重命名轨道", "轨道名称（1–128 个字符）", track.Name);
        if (name is null) return;
        // Dialogs pump dispatcher work, so do not rename a stale track if
        // another operation replaced the active sequence while it was open.
        if (!ReferenceEquals(owner, _project) || !_project.Tracks.Contains(track))
        { StatusText.Text = "轨道已变化，请重新选择 · TMLNE0015"; return; }
        try { RenameTimelineTrack(track, name); }
        catch (StageException exception) { StatusText.Text = exception.Message + " · " + exception.StageCode; }
    }

    private void RenameTimelineTrack(TimelineTrack track, string name)
    {
        string normalized = name.Trim();
        if (normalized.Length is < 1 or > 128 || normalized.Any(char.IsControl))
            throw new StageException("TMLNE0015", "轨道名称须为 1–128 个字符，且不能包含换行或控制字符。");
        if (!_project.Tracks.Contains(track)) throw new StageException("TMLNE0015", "轨道已不在当前序列中。");
        if (normalized == track.Name) return;
        SaveUndoState();
        track.Name = normalized;
        _selectedTrack = track; _contextTrack = track;
        MarkProjectDirty();
        RefreshTimeline();
        RefreshProjectMediaCommands();
        StatusText.Text = "轨道已重命名为：" + normalized;
    }
}
