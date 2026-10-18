using System.IO;
using System.Windows.Controls;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;
using TANGERINE_VideoEditor.Tools;

namespace TANGERINE_VideoEditor;
public partial class MainWindow
{
    private async Task StabilizeVideoAsync(TimelineClip clip, int smoothing, int shakiness, double zoom, CancellationToken token)
    {
        ProjectDocument owner = _project; long revision = _projectRevision;
        var track = owner.Tracks.FirstOrDefault(t => t.Clips.Contains(clip));
        if (track is null || track.IsLocked || clip.Stabilization.Enabled) throw new StageException("STABL0004", "请先关闭已有防抖，并选择未锁定视频。");
        var asset = owner.Media.FirstOrDefault(m => m.Id == clip.MediaAssetId) ?? throw new StageException("STABL0005", "视频源不存在");
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TANGERINE-VideoEditor", "generated"); Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, "stabilized-" + Guid.NewGuid().ToString("N") + ".mkv");
        var settings = new StabilizationSettings { Enabled = true, Smoothing = smoothing, Shakiness = shakiness, MaxZoomPercent = zoom, OriginalAssetId = asset.Id, OriginalSourceIn = clip.SourceInSeconds };
        try
        {
            await VideoStabilization.RenderAsync(CloneClip(clip), asset, settings, output, token);
            var metadata = await MediaMetadataProbe.TryReadAsync(output, token);
            if (!ReferenceEquals(owner, _project) || revision != _projectRevision) throw new StageException("STABL0006", "防抖分析期间项目已修改，请重试。");
            var media = new MediaAsset { Name = clip.Name + " · 防抖", SourcePath = output, Kind = MediaKind.Video, HasAudio = metadata?.HasAudio ?? asset.HasAudio,
                Width = asset.Width, Height = asset.Height, FramesPerSecond = asset.FramesPerSecond, DurationSeconds = metadata?.DurationSeconds,
                AudioStreams = metadata?.AudioStreams?.ToList() ?? asset.AudioStreams.ToList() };
            SaveUndoState(); owner.Media.Add(media); clip.MediaAssetId = media.Id; clip.SourceInSeconds = 0; clip.Stabilization = settings;
            MarkProjectDirty(); RefreshLibrary(); RefreshTimeline(); InvalidateCompositedPreview(); StatusText.Text = "防抖完成：无损派生素材已接入预览和导出";
        }
        catch { try { File.Delete(output); } catch (IOException) { } throw; }
    }
    private void DisableVideoStabilization(TimelineClip clip)
    {
        if (!clip.Stabilization.Enabled) return;
        if (_project.Tracks.Any(t => t.IsLocked && t.Clips.Contains(clip))) throw new StageException("STABL0004", "轨道已锁定");
        if (!_project.Media.Any(m => m.Id == clip.Stabilization.OriginalAssetId)) throw new StageException("STABL0005", "原始源已被移除");
        SaveUndoState(); clip.MediaAssetId = clip.Stabilization.OriginalAssetId; clip.SourceInSeconds += clip.Stabilization.OriginalSourceIn; clip.Stabilization.Enabled = false;
        MarkProjectDirty(); RefreshTimeline(); InvalidateCompositedPreview();
    }
    private void AddStabilizationControls(StackPanel panel, TimelineClip clip)
    {
        if (clip.Kind != ClipKind.Video) return;
        var smooth = AddToolTextBox(panel, "防抖平滑半径（帧，1–120）", clip.Stabilization.Smoothing);
        var shake = AddToolTextBox(panel, "抖动检测强度（1–10）", clip.Stabilization.Shakiness);
        var zoom = AddToolTextBox(panel, "边缘裁切放大（%，0–50）", clip.Stabilization.MaxZoomPercent);
        var apply = new Button { Style = (System.Windows.Style)FindResource("ToolButton"), Content = "分析并应用视频防抖" }; panel.Children.Add(apply);
        apply.Click += async (_, _) =>
        {
            if (!TryNumberQuiet(smooth.Text, 1, 120, out double s) || !TryNumberQuiet(shake.Text, 1, 10, out double h) || !TryNumberQuiet(zoom.Text, 0, 50, out double z)) { StatusText.Text = "防抖参数无效 · STABL0001"; return; }
            apply.IsEnabled = false; StatusText.Text = "正在分析相机运动并生成防抖素材…";
            try { await StabilizeVideoAsync(clip, (int)s, (int)h, z, _experienceLifetime.Token); }
            catch (Exception ex) { StatusText.Text = $"防抖失败 · STABL0003：{ex.Message}"; }
            finally { apply.IsEnabled = true; }
        };
        var disable = new Button { Style = (System.Windows.Style)FindResource("ToolButton"), Content = "关闭防抖，恢复原始视频" }; panel.Children.Add(disable);
        disable.Click += (_, _) => { try { DisableVideoStabilization(clip); } catch (StageException ex) { StatusText.Text = $"{ex.Message} · {ex.StageCode}"; } };
    }
}
