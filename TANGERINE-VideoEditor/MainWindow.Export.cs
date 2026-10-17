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
        var progressWindow = new Window
        {
            Title = "正在导出视频",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            Background = Brush("#191B20"),
            Foreground = Brushes.White
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        var message = new TextBlock { Text = "准备渲染…", Margin = new Thickness(0, 0, 0, 12) };
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, IsIndeterminate = true };
        var cancelButton = new Button
        {
            Content = "取消导出",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 12, 0, 0)
        };
        cancelButton.Click += (_, _) => { cancelButton.IsEnabled = false; message.Text = "正在取消…"; cancelSource.Cancel(); };
        bool exportFinished = false;
        progressWindow.Closing += (_, args) =>
        {
            if (exportFinished) return;
            args.Cancel = true;
            cancelButton.IsEnabled = false;
            message.Text = "正在取消…";
            cancelSource.Cancel();
        };
        panel.Children.Add(message);
        panel.Children.Add(bar);
        panel.Children.Add(cancelButton);
        progressWindow.Content = panel;

        _exportInProgress = true;
        ExportMenuItem.IsEnabled = false;
        ExportButton.IsEnabled = false;
        progressWindow.Show();
        try
        {
            ProjectDocument source = _project;
            long sourceRevision = _projectRevision;
            long sourceGeneration = _projectGeneration;
            ProjectDocument queuedProject = await Task.Run(() =>
                JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(source))!, cancelSource.Token);
            if (!ReferenceEquals(source, _project) || sourceGeneration != _projectGeneration || sourceRevision != _projectRevision)
                throw new ExportException(ExportStageCode.Prepare,
                    "准备导出期间项目已修改，请重新导出以确保画面一致。");
            cancelSource.Token.ThrowIfCancellationRequested();
            message.Text = $"准备序列：{job.SequenceName}";
            bar.IsIndeterminate = true;
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
            var titlePaths = await RenderTitlesAsync(snapshot, tempDirectory, cancelSource.Token);
            bar.IsIndeterminate = false;
            message.Text = "正在编码：0%";
            var progress = new Progress<double>(ratio =>
            {
                int percent = (int)Math.Round(ratio * 100);
                bar.Value = percent;
                message.Text = $"正在编码：{percent}%";
                StatusText.Text = $"正在导出：{percent}%";
            });
            await Task.Run(() => TimelineExporter.ExportMp4Async(
                snapshot, job.OutputPath, titlePaths, progress, cancelSource.Token, job.Settings),
                cancelSource.Token);
            StatusText.Text = $"导出完成：{Path.GetFileName(job.OutputPath)}";
            MessageBox.Show(this, $"视频已导出：{job.OutputPath}", "导出完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "导出已取消";
        }
        catch (ExportException exception)
        {
            StatusText.Text = $"导出失败 · {exception.StageCode}";
            MessageBox.Show(this, $"{exception.Message}\n阶段代码：{exception.StageCode}", "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (AudioOperationException exception)
        {
            StatusText.Text = $"导出失败 · AUDIO-{exception.StageCode}";
            MessageBox.Show(this, $"{exception.Message}\n阶段代码：AUDIO-{exception.StageCode}", "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (GraphicShapeException exception)
        {
            StatusText.Text = $"导出失败 · GRAPHIC-{exception.StageCode}";
            MessageBox.Show(this, $"{exception.Message}\n阶段代码：GRAPHIC-{exception.StageCode}", "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            StatusText.Text = "导出失败 · Prepare";
            MessageBox.Show(this, $"准备导出时失败：{exception.Message}\n阶段代码：Prepare", "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception exception)
        {
            StatusText.Text = "导出失败 · Prepare";
            MessageBox.Show(this, $"视频导出发生错误：{exception.Message}\n阶段代码：Prepare", "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            exportFinished = true;
            progressWindow.Close();
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
