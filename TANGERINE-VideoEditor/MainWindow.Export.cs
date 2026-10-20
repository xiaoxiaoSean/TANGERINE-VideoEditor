using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Presents export options and progress while keeping the editing window
/// responsive. The render uses a snapshot so in-progress edits cannot alter
/// the file being encoded.
/// </summary>
public partial class MainWindow
{
    private bool _exportInProgress;

    private void InitializeExportCommands()
    {
        ExportMenuItem.Click += async (_, _) => await ExportVideoAsync();
        ExportButton.Click += async (_, _) => await ExportVideoAsync();
        if (ExportMenuItem.Parent is MenuItem fileMenu)
        {
            var exportExchange = new MenuItem { Header = "导出项目交换文件…" };
            exportExchange.Click += async (_, _) => await ExportProjectExchangeAsync();
            fileMenu.Items.Insert(fileMenu.Items.IndexOf(ExportMenuItem) + 1, exportExchange);
            var importExchange = new MenuItem { Header = "导入项目交换文件…" };
            importExchange.Click += async (_, _) => await ImportProjectExchangeAsync();
            fileMenu.Items.Insert(fileMenu.Items.IndexOf(ExportMenuItem) + 1, importExchange);
        }
        InputBindings.Add(new KeyBinding(new RelayCommand(async () => await ExportVideoAsync()), Key.E, ModifierKeys.Control));
        ExportButton.ToolTip = "将可见轨道和音频混合导出为 H.264 / AAC MP4";
    }

    private async Task ExportVideoAsync()
    {
        if (_exportInProgress || _projectIoInProgress) return;
        if (!_project.Tracks.Any(track => track.Clips.Count > 0) &&
            !_project.Sequences.Any(sequence => sequence.Tracks.Any(track => track.Clips.Count > 0)))
        {
            StatusText.Text = "时间线上没有可导出的片段";
            return;
        }
        var dialog = new LocalExportDialog(_project, this,
            async () => await ExportProjectExchangeAsync());
        if (dialog.ShowDialog() != true) return;
        if (dialog.SelectedJob is not LocalExportJob job) return;

        string tempDirectory = Path.Combine(Path.GetTempPath(), $"tangerine-titles-{Guid.NewGuid():N}");
        using var cancelSource = new CancellationTokenSource();
        ExportProgressPresenter? progressWindow = null;

        _exportInProgress = true;
        ExportMenuItem.IsEnabled = false;
        ExportButton.IsEnabled = false;
        try
        {
            progressWindow = new ExportProgressPresenter(this, job.OutputPath, cancel: cancelSource.Cancel);
            ProjectDocument source = _project;
            long sourceRevision = _projectRevision;
            long sourceGeneration = _projectGeneration;
            ProjectDocument queuedProject = await Task.Run(() =>
                JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(source))!, cancelSource.Token);
            if (!ReferenceEquals(source, _project) || sourceGeneration != _projectGeneration || sourceRevision != _projectRevision)
                throw new ExportException(ExportStageCode.Prepare,
                    "准备导出期间项目已修改，请重新导出以确保画面一致。");
            cancelSource.Token.ThrowIfCancellationRequested();
            progressWindow.Prepare($"准备序列：{job.SequenceName}");
            ProjectDocument snapshot;
            try
            {
                snapshot = await Task.Run(() => PrepareExportSequence(queuedProject, job.SequenceId),
                    cancelSource.Token);
            }
            catch (ProjectFileException exception)
            {
                throw new ExportException(ExportStageCode.Validate,
                    $"无法展开序列“{job.SequenceName}”：{exception.Message}", exception);
            }
            // WPF title rasterization runs on a dedicated STA worker.
            progressWindow.Prepare("正在准备文字与图形…");
            var titlePaths = await RenderTitlesAsync(snapshot, tempDirectory, cancelSource.Token);
            progressWindow.Encode(0);
            var progress = new Progress<double>(ratio =>
            {
                if (!progressWindow.IsOpen || cancelSource.IsCancellationRequested) return;
                int percent = (int)Math.Round(ratio * 100);
                progressWindow.Encode(ratio);
                StatusText.Text = $"正在导出：{percent}%";
            });
            await Task.Run(() => TimelineExporter.ExportMp4Async(
                snapshot, job.OutputPath, titlePaths, progress, cancelSource.Token, job.Settings),
                cancelSource.Token);
            progressWindow.Complete();
            StatusText.Text = $"导出完成：{Path.GetFileName(job.OutputPath)}";
            MessageBox.Show(this, $"视频已导出：{job.OutputPath}", "导出完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            progressWindow?.Complete();
            StatusText.Text = "导出已取消";
        }
        catch (Exception exception)
        {
            progressWindow?.Complete();
            StatusText.Text = $"导出失败 · {Tools.StageCodes.Resolve(exception, "EXPRT0900")}";
            Tools.ErrorDialog.Show(this, "EXPRT0900", exception);
        }
        finally
        {
            progressWindow?.Complete();
            _exportInProgress = false;
            ExportMenuItem.IsEnabled = true;
            ExportButton.IsEnabled = true;
            try { await Task.Run(() => { if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true); }); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Activates a queued sequence on a copy, then expands nested clips. The
    /// inactive list must not contain the new active sequence because the
    /// flattener indexes all sequence IDs and rejects duplicates.
    /// </summary>
    private static ProjectDocument PrepareExportSequence(ProjectDocument queuedProject, string sequenceId)
    {
        ProjectDocument copy = JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(queuedProject))
            ?? throw new ExportException(ExportStageCode.Prepare, "无法复制导出项目。");
        if (copy.Sequence.Id != sequenceId)
        {
            ProjectSequenceWorkspace? selected = copy.Sequences.FirstOrDefault(item => item.Sequence.Id == sequenceId);
            if (selected is null)
                throw new ExportException(ExportStageCode.Validate, "队列中的序列已不存在。");
            copy.Sequences.Remove(selected);
            copy.Sequences.Add(new ProjectSequenceWorkspace
            {
                Sequence = copy.Sequence, Tracks = copy.Tracks, Markers = copy.Markers
            });
            copy.Sequence = selected.Sequence;
            copy.Tracks = selected.Tracks;
            copy.Markers = selected.Markers;
        }
        return SequenceFlattener.Flatten(copy);
    }

    private static Task<Dictionary<string, string>> RenderTitlesAsync(ProjectDocument snapshot,
        string directory, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<Dictionary<string, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(directory);
                var paths = new Dictionary<string, string>();
                foreach (TimelineClip clip in snapshot.Tracks
                    .Where(track => !track.IsHidden && track.Kind != TrackKind.Audio)
                    .SelectMany(track => track.Clips)
                    .Where(clip => clip.Kind == ClipKind.Text))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string path;
                    if (DynamicTitlePreset.HasChangingText(clip))
                        path = DynamicTitleFrames.Render(clip, snapshot.Sequence,
                            Path.Combine(directory, clip.Id), cancellationToken);
                    else
                    {
                        path = Path.Combine(directory, $"{Guid.NewGuid():N}.png");
                        TitleBitmapRenderer.Render(clip, snapshot.Sequence, path);
                    }
                    paths[clip.Id] = path;
                }
                completion.TrySetResult(paths);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        worker.IsBackground = true;
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }
}
