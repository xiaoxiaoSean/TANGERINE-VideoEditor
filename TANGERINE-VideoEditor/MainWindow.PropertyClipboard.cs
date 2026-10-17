using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Copies clip appearance and processing settings separately from media and
/// timeline placement. Pasting into several selected clips is one undo step.
/// </summary>
public partial class MainWindow
{
    private sealed record ClipPropertiesSnapshot(ClipKind Kind, ClipTransform Transform, ClipText? Text,
        List<ClipEffect> Effects, List<ClipTransition> Transitions, ClipMask? Mask,
        List<ClipKeyframe> Keyframes, double Opacity, double Volume, string BlendMode,
        AudioSettings Audio);

    private ClipPropertiesSnapshot? _propertyClipboard;

    private void InitializePropertyClipboardCommands()
    {
        var editMenu = CopyMenuItem.Parent as MenuItem;
        if (editMenu is null) return;
        var copy = new MenuItem { Header = "复制片段属性" };
        copy.Click += (_, _) => CopySelectedClipProperties();
        var paste = new MenuItem { Header = "粘贴片段属性到选中项" };
        paste.Click += (_, _) => PasteSelectedClipProperties();
        int insertion = editMenu.Items.IndexOf(CopyMenuItem) + 1;
        editMenu.Items.Insert(insertion, copy);
        editMenu.Items.Insert(insertion + 1, paste);
    }

    private void CopySelectedClipProperties()
    {
        if (_selectedClip is null)
        {
            StatusText.Text = "请先选择要复制属性的片段 · PROP-COPY-TARGET";
            return;
        }
        TimelineClip clip = _selectedClip;
        try
        {
            _propertyClipboard = CloneProperties(new ClipPropertiesSnapshot(clip.Kind, clip.Transform,
                clip.Text, clip.Effects, clip.Transitions, clip.Mask, clip.Keyframes,
                clip.Opacity, clip.Volume, clip.BlendMode, AudioSettings.FromClip(clip)));
            StatusText.Text = "已复制片段属性";
        }
        catch (Exception exception) when (exception is JsonException or AudioOperationException or NotSupportedException)
        {
            ShowPropertyClipboardError("PROP-COPY", "无法复制片段属性。", exception);
        }
    }

    private void PasteSelectedClipProperties()
    {
        if (_propertyClipboard is null)
        {
            StatusText.Text = "请先复制片段属性 · PROP-PASTE-EMPTY";
            return;
        }
        IEnumerable<TimelineClip> candidates = _selectedClips.Count > 0
            ? _selectedClips.AsEnumerable() : _selectedClip is null
                ? Enumerable.Empty<TimelineClip>() : new[] { _selectedClip };
        TimelineClip[] targets = candidates.Where(clip =>
                _project.Tracks.Any(track => !track.IsLocked && track.Clips.Contains(clip))).ToArray();
        if (targets.Length == 0)
        {
            StatusText.Text = "没有可编辑的目标片段 · PROP-PASTE-TARGET";
            return;
        }
        try
        {
            ClipPropertiesSnapshot[] copies = targets.Select(_ =>
                CloneProperties(_propertyClipboard)).ToArray();
            foreach (ClipPropertiesSnapshot copy in copies) copy.Audio.Validate();
            SaveUndoState();
            for (int index = 0; index < targets.Length; index++)
            {
                TimelineClip clip = targets[index];
                ClipPropertiesSnapshot snapshot = copies[index];
                // Keep source media and placement intact. A visual target can
                // receive visual processing, and audio settings can transfer
                // between any clips that contain sound.
                if (clip.Kind != ClipKind.Audio && snapshot.Kind != ClipKind.Audio)
                {
                    clip.Transform = snapshot.Transform;
                    if (clip.Kind == ClipKind.Text && snapshot.Text is not null)
                        clip.Text = snapshot.Text;
                    if (clip.Kind is ClipKind.Video or ClipKind.Image or ClipKind.Color or ClipKind.Adjustment)
                    {
                        clip.Effects = snapshot.Effects;
                        clip.Transitions = snapshot.Transitions;
                        clip.Mask = snapshot.Mask;
                    }
                    // Text effects, masks, and transitions also belong to
                    // the drawable title layer when copied from a title.
                    else if (clip.Kind == ClipKind.Text && snapshot.Kind == ClipKind.Text)
                    {
                        clip.Effects = snapshot.Effects;
                        clip.Transitions = snapshot.Transitions;
                        clip.Mask = snapshot.Mask;
                    }
                    clip.Opacity = snapshot.Opacity;
                    clip.BlendMode = snapshot.BlendMode;
                    clip.Keyframes = snapshot.Keyframes.Where(key => key.Property != "Volume" &&
                        key.TimeSeconds <= clip.DurationSeconds + .000001 &&
                        (clip.Kind is ClipKind.Video or ClipKind.Image or ClipKind.Color or ClipKind.Adjustment ||
                            clip.Kind == ClipKind.Text && snapshot.Kind == ClipKind.Text ||
                            !key.Property.StartsWith("Effect:", StringComparison.Ordinal) &&
                            !key.Property.StartsWith("Mask.", StringComparison.Ordinal))).ToList();
                }
                bool canReceiveAudio = clip.Kind == ClipKind.Audio ||
                    clip.Kind == ClipKind.Video && _project.Media.Any(media =>
                        media.Id == clip.MediaAssetId && media.HasAudio);
                if (canReceiveAudio && (snapshot.Kind is ClipKind.Audio or ClipKind.Video))
                {
                    clip.Volume = snapshot.Volume;
                    snapshot.Audio.Save(clip);
                    clip.Keyframes.RemoveAll(key => key.Property == "Volume");
                    clip.Keyframes.AddRange(snapshot.Keyframes.Where(key => key.Property == "Volume" &&
                        key.TimeSeconds <= clip.DurationSeconds + .000001));
                }
            }
            MarkProjectDirty();
            RefreshTimeline();
            SeekPreview(_project.Sequence.PlayheadSeconds);
            StatusText.Text = $"已粘贴属性到 {targets.Length} 个片段";
        }
        catch (Exception exception) when (exception is JsonException or AudioOperationException or NotSupportedException)
        {
            ShowPropertyClipboardError("PROP-PASTE", "无法粘贴片段属性。", exception);
        }
    }

    private static ClipPropertiesSnapshot CloneProperties(ClipPropertiesSnapshot properties) =>
        JsonSerializer.Deserialize<ClipPropertiesSnapshot>(JsonSerializer.Serialize(properties))!;

    private void ShowPropertyClipboardError(string stageCode, string message, Exception exception)
    {
        StatusText.Text = $"{message} · {stageCode}";
        MessageBox.Show(this, $"{message}\n阶段代码：{stageCode}\n{exception.Message}",
            "片段属性", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
