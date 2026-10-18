using System.Windows;
using System.Windows.Controls;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

public partial class MainWindow
{
    private void InitializeMagneticTimeline()
    {
        MagneticButton.Click += (_, _) => ToggleMagneticTimeline();
    }
    private void ToggleMagneticTimeline()
    {
        SaveUndoState(); _project.Sequence.MagneticTimeline = !_project.Sequence.MagneticTimeline;
        MarkProjectDirty(); RefreshTimeline(); SeekPreview(_project.Sequence.PlayheadSeconds);
        StatusText.Text = _project.Sequence.MagneticTimeline ? "磁性模式开启：普通删除、修剪和移动后自动闭合空隙；锁定及跨轨关联片段保留位置" : "磁性模式关闭：允许保留空隙";
    }
    private void ApplyMagneticTimeline()
    {
        // Preserve overlaps and cross-track links. Moving one member silently
        // would break AV synchronization; the UI explains this explicit boundary.
        var linked = _project.Tracks.SelectMany(t => t.Clips).Where(c => c.LinkGroupId is not null || c.GroupId is not null || c.DetachedVideoClipId is not null || c.AudioDetached).ToArray();
        foreach (var track in _project.Tracks.Where(t => !t.IsLocked))
        {
            double cursor = 0;
            foreach (var clip in track.Clips.OrderBy(c => c.StartSeconds))
            {
                if (clip.StartSeconds > cursor + .00001 && !linked.Contains(clip)) clip.StartSeconds = cursor;
                cursor = Math.Max(cursor, clip.StartSeconds + clip.DurationSeconds);
            }
        }
        if (MagneticButton is not null) MagneticButton.Content = "磁性：开";
    }
    private void NudgePlayhead(int frames) => SeekPreview(Math.Max(0, _project.Sequence.PlayheadSeconds + frames / Math.Max(1, _project.Sequence.FramesPerSecond)));
    private void ToggleTimelineSnapping()
    {
        _snapEnabled = !_snapEnabled; SnapButton.Content = _snapEnabled ? "⊞ 吸附" : "□ 吸附";
    }
}
