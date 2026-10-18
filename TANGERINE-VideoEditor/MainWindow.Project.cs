using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Keeps project file operations separate from timeline interaction code.
/// Every project read or write failure is reported with its stage code.
/// </summary>
public partial class MainWindow
{
    private ProjectDocument _project = ProjectDocument.CreateDefault();
    private string? _projectPath;
    private bool _isProjectDirty;
    private long _projectRevision;
    private long _projectGeneration;
    private bool _projectIoInProgress;
    private bool _projectCreated;

    private void InitializeProjectCommands()
    {
        NewMenuItem.Click += (_, _) => NewProject();
        NewProjectButton.Click += (_, _) => NewProject();
        OpenMenuItem.Click += async (_, _) => await OpenProjectAsync();
        OpenProjectButton.Click += async (_, _) => await OpenProjectAsync();
        SaveMenuItem.Click += async (_, _) => await SaveProjectAsync();
        SaveProjectButton.Click += async (_, _) => await SaveProjectAsync();
        SaveAsMenuItem.Click += async (_, _) => await SaveProjectAsAsync();

        InputBindings.Add(new KeyBinding(new RelayCommand(NewProject), Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(async () => await OpenProjectAsync()), Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(async () => await SaveProjectAsync()), Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(async () => await SaveProjectAsAsync()), Key.S, ModifierKeys.Control | ModifierKeys.Shift));
        RefreshProjectChrome();
    }

    private void NewProject()
    {
        if (_projectIoInProgress || _exportInProgress || _autoSaveInProgress) return;
        if (!ConfirmDiscardChanges()) return;
        ProjectPickerItem? aspect = PromptChoice("新建项目", "选择项目宽高比",
            [new(null, "16:9 横屏"), new("4:3", "4:3 传统横屏"), new("1:1", "1:1 方形"),
             new("9:16", "9:16 竖屏"), new("4:5", "4:5 社交竖屏")], "16:9");
        if (aspect is null) return;
        ProjectDocument outgoing = _project;
        _project = ProjectDocument.CreateDefault();
        ApplyAspectRatio(_project.Sequence, aspect.Id ?? "16:9");
        _projectCreated = true;
        _projectGeneration++;
        _projectRevision++;
        _projectPath = null;
        _isProjectDirty = false;
        _sourceMonitor?.Close();
        ResetEditorSession();
        RefreshEditorFromProject();
        _ = ReleaseProjectCacheSafelyAsync(outgoing);
        RefreshProjectChrome();
        StatusText.Text = "已新建项目";
    }

    private async Task OpenProjectAsync()
    {
        if (_projectIoInProgress || _exportInProgress || _autoSaveInProgress) return;
        if (!ConfirmDiscardChanges()) return;

        var dialog = new OpenFileDialog
        {
            Title = "打开 TANGERINE 项目",
            Filter = "TANGERINE 项目 (*.tvep)|*.tvep",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        await OpenProjectPathAsync(dialog.FileName, confirmDiscard: false);
    }

    /// <summary>
    /// Shares the validated asynchronous project-load path between the Open
    /// dialog and Explorer's quoted file-association command line. A failed
    /// load leaves the current project and its editing history intact.
    /// </summary>
    internal async Task OpenProjectPathAsync(string path, bool confirmDiscard = true)
    {
        if (_projectIoInProgress || _exportInProgress || _autoSaveInProgress) return;
        if (confirmDiscard && !ConfirmDiscardChanges()) return;
        _projectIoInProgress = true;
        long openingGeneration = _projectGeneration;
        long openingRevision = _projectRevision;
        try
        {
            if (!Path.GetExtension(path).Equals(ProjectDocument.FileExtension, StringComparison.OrdinalIgnoreCase))
                throw new ProjectFileException(ProjectStageCode.Read, "只能打开 .tvep 项目文件。");
            string fullPath = Path.GetFullPath(path);
            ProjectDocument loaded = await ProjectFileService.LoadAsync(fullPath);
            // The file service resolves embedded archive entries to its
            // working cache. Legacy path references are resolved there too.
            if (openingGeneration != _projectGeneration || openingRevision != _projectRevision)
            {
                StatusText.Text = "打开已取消：加载期间当前项目已修改";
                await ProjectFileService.ReleaseCachedMediaAsync(loaded);
                return;
            }
            ProjectDocument outgoing = _project;
            _project = loaded;
            _projectCreated = true;
            _sourceMonitor?.Close();
            _projectGeneration++;
            _projectPath = fullPath;
            _isProjectDirty = false;
            _projectRevision++;
            ResetEditorSession();
            RefreshEditorFromProject();
            _ = ReleaseProjectCacheSafelyAsync(outgoing);
            RefreshProjectChrome();
            StatusText.Text = $"已打开 {Path.GetFileName(fullPath)}";
        }
        catch (ProjectFileException exception)
        {
            ShowProjectError(exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowProjectError(new ProjectFileException(ProjectStageCode.Read, "打开项目时无法解析媒体路径。", exception));
        }
        finally { _projectIoInProgress = false; }
    }

    private async Task SaveProjectAsync()
    {
        if (_projectIoInProgress) return;
        if (_projectPath is null)
        {
            await SaveProjectAsAsync();
            return;
        }
        await SaveProjectToAsync(_projectPath);
    }

    private async Task<bool> SaveProjectAsAsync()
    {
        if (_projectIoInProgress) return false;
        var dialog = new SaveFileDialog
        {
            Title = "保存 TANGERINE 项目",
            Filter = "TANGERINE 项目 (*.tvep)|*.tvep",
            DefaultExt = ProjectDocument.FileExtension,
            AddExtension = true,
            FileName = (_project.Name == "未命名项目" ? "未命名项目" : _project.Name) + ProjectDocument.FileExtension
        };
        if (dialog.ShowDialog(this) != true) return false;
        return await SaveProjectToAsync(dialog.FileName);
    }

    private async Task<bool> SaveProjectToAsync(string path)
    {
        if (_projectIoInProgress) return false;
        _projectIoInProgress = true;
        try
        {
            // Serialize a snapshot on a worker. ProjectFileService embeds the
            // media bytes and rewrites paths only inside its archive manifest;
            // the editor keeps its absolute working paths for preview.
            long savedRevision = _projectRevision;
            long savedGeneration = _projectGeneration;
            ProjectDocument source = _project;
            var stored = await Task.Run(() =>
            {
                var copy = System.Text.Json.JsonSerializer.Deserialize<ProjectDocument>(
                    System.Text.Json.JsonSerializer.Serialize(source))!;
                copy.Name = Path.GetFileNameWithoutExtension(path);
                return copy;
            });
            if (!ReferenceEquals(source, _project) || savedGeneration != _projectGeneration || savedRevision != _projectRevision)
            {
                StatusText.Text = "保存已取消：准备期间项目已修改，请重新保存";
                return false;
            }
            await ProjectFileService.SaveAsync(stored, path);
            if (ReferenceEquals(source, _project) && savedGeneration == _projectGeneration &&
                savedRevision == _projectRevision)
            {
                _project.Name = stored.Name;
                _project.ModifiedUtc = stored.ModifiedUtc;
                _projectPath = path;
                _isProjectDirty = false;
            }
            RefreshProjectChrome();
            bool currentSaved = ReferenceEquals(source, _project) && savedGeneration == _projectGeneration &&
                savedRevision == _projectRevision;
            StatusText.Text = currentSaved
                ? $"已保存 {Path.GetFileName(path)}"
                : ReferenceEquals(source, _project) && savedGeneration == _projectGeneration
                    ? "保存已完成，但有新修改尚未保存"
                : "保存已完成，但当前项目已切换";
            return currentSaved;
        }
        catch (ProjectFileException exception)
        {
            ShowProjectError(exception);
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            ShowProjectError(new ProjectFileException(ProjectStageCode.Write, "保存项目时无法处理媒体路径。", exception));
            return false;
        }
        finally { _projectIoInProgress = false; }
    }

    private bool ConfirmDiscardChanges()
    {
        if (!_isProjectDirty) return true;
        return MessageBox.Show(this, "当前项目有未保存的修改。要放弃这些修改吗？", "未保存的修改",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void MarkProjectDirty()
    {
        if (_project.Sequence.MagneticTimeline) ApplyMagneticTimeline();
        _projectRevision++;
        InvalidateIdlePreview();
        _isProjectDirty = true;
        RefreshProjectChrome();
    }

    private void ResetEditorSession()
    {
        InvalidateIdlePreview();
        _mediaMetadataCancellation?.Cancel();
        _mediaMetadataCancellation = null;
        _undoStates.Clear();
        _redoStates.Clear();
        _pendingDropBatches.Clear();
        ClearTimelineSelection();
        _clipboardClip = null;
        PasteMenuItem.IsEnabled = false;
        TimelinePasteMenuItem.IsEnabled = false;
        _previewSourcePath = null;
        _previewImageRequest++;
        _previewImageLoadingPath = null;
        _previewImageFailedPath = null;
        _lastTitleLayoutKey = null;
        _lastTitleRangeStart = double.NaN;
        _lastTitleRangeEnd = double.NaN;
        _isPlaying = false;
        _playbackTimer.Stop();
        StopAudioPreview();
        PreviewMediaElement.Stop();
        PreviewMediaElement.Source = null;
        PreviewImage.Source = null;
        PlayPauseButton.Content = "▶";
    }

    private void RefreshProjectChrome()
    {
        string fileName = _projectPath is null ? "未命名项目.tvep" : Path.GetFileName(_projectPath);
        ProjectNameText.Text = (_isProjectDirty ? "● " : string.Empty) + fileName;
        ProjectAspectRatioText.Text = $"宽高比 {AspectRatioOf(_project.Sequence)}";
        if (AspectRatioBox is not null && AspectRatioBox.Items.Count > 0)
        {
            string ratio = AspectRatioOf(_project.Sequence);
            _updatingAspectRatio = true;
            AspectRatioBox.SelectedItem = ratio;
            _updatingAspectRatio = false;
        }
        SaveStateText.Text = _isProjectDirty ? "未保存" : "已保存";
    }

    private static string AspectRatioOf(ProjectSequence sequence)
    {
        double ratio = sequence.Height <= 0 ? 16d / 9d : sequence.Width / (double)sequence.Height;
        (string Id, double Value)[] values = [("16:9", 16d / 9), ("4:3", 4d / 3), ("1:1", 1), ("9:16", 9d / 16), ("4:5", 4d / 5)];
        return values.OrderBy(item => Math.Abs(item.Value - ratio)).First().Id;
    }

    private static void ApplyAspectRatio(ProjectSequence sequence, string ratio)
    {
        (int width, int height) = ratio switch
        {
            "4:3" => (1440, 1080), "1:1" => (1080, 1080),
            "9:16" => (1080, 1920), "4:5" => (1080, 1350), _ => (1920, 1080)
        };
        sequence.Width = width; sequence.Height = height;
    }

    private void ShowProjectError(ProjectFileException exception)
    {
        StatusText.Text = $"项目操作失败 · {exception.StageCode}";
        MessageBox.Show(this, $"{exception.Message}\n阶段代码：{exception.StageCode}", "项目操作失败",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async Task ReleaseProjectCacheSafelyAsync(ProjectDocument document)
    {
        try
        {
            await McpWaitForProjectJobsAsync(document);
            await ProjectFileService.ReleaseCachedMediaAsync(document);
        }
        catch (ProjectFileException exception)
        {
            StatusText.Text = $"旧项目缓存清理失败 · {exception.StageCode}";
        }
    }

    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
