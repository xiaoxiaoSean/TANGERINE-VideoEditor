using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Connects workspace preferences and small editing commands to the actual
/// project and timeline. View settings are persisted separately from edits.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, InputBinding> _customShortcutBindings = [];
    private readonly SemaphoreSlim _workspaceSettingsGate = new(1, 1);
    private bool _autoSaveEnabled = true;
    private string _previewGpuMode = "自动（GPU 优先）";
    private sealed record WorkspaceLayout(double UpperRatio, double LibraryRatio, int CategoryIndex,
        int PreviewQualityIndex, double TimelineZoom, bool AutoSave,
        Dictionary<string, string>? Shortcuts = null, string PreviewGpuMode = "自动（GPU 优先）", double PreviewViewZoom = 1, int MediaViewIndex = 1);
    private static string WorkspaceSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TANGERINE-VideoEditor", "workspace.json");
    private readonly System.Windows.Threading.DispatcherTimer _autoSaveTimer = new()
    {
        Interval = TimeSpan.FromMinutes(2)
    };
    private bool _autoSaveInProgress;
    private bool _updatingAspectRatio;

    private void InitializeExtendedWorkspace()
    {
        InitializePreviewExperience();
        PreviewQualityBox.ItemsSource = new[] { "预览：适配窗口", "预览：1/2", "预览：1/4" };
        PreviewQualityBox.SelectedIndex = 0;
        PreviewQualityBox.SelectionChanged += (_, _) =>
        {
            InvalidateCompositedPreview();
            InvalidateIdlePreview();
            StatusText.Text = $"已选择 {PreviewQualityBox.SelectedItem}";
        };
        AspectRatioBox.ItemsSource = new[] { "16:9", "4:3", "1:1", "9:16", "4:5" };
        AspectRatioBox.SelectedIndex = 0;
        AspectRatioBox.SelectionChanged += (_, _) =>
        {
            if (_updatingAspectRatio || AspectRatioBox.SelectedItem is not string ratio) return;
            ApplyAspectRatio(_project.Sequence, ratio);
            MarkProjectDirty();
            RefreshTimeline();
            InvalidateCompositedPreview();
            StatusText.Text = $"项目宽高比已切换为 {ratio}";
        };
        _autoSaveTimer.Tick += async (_, _) => await AutoSaveProjectCopyAsync();
        _autoSaveTimer.Start();
        ExportFrameMenuItem.Click += async (_, _) => await ExportCurrentFrameAsync();
        InputBindings.Add(new KeyBinding(new RelayCommand(FreezeSelectedFrame), Key.F, ModifierKeys.Control));
        EditingWorkspaceMenuItem.Click += (_, _) => ApplyWorkspacePreset(.56, .44, 0);
        ColorWorkspaceMenuItem.Click += (_, _) => ApplyWorkspacePreset(.67, .33, 2);
        AudioWorkspaceMenuItem.Click += (_, _) => ApplyWorkspacePreset(.47, .42, 0);
        SaveWorkspaceMenuItem.Click += async (_, _) => await SaveCurrentWorkspaceAsync();
        RestoreWorkspaceMenuItem.Click += async (_, _) => await RestoreSavedWorkspaceAsync();
        Loaded += async (_, _) => await RestoreSavedWorkspaceAsync(false);
    }

    private double PreviewResolutionRatio => PreviewQualityBox.SelectedIndex switch
    {
        1 => .5, 2 => .25, _ => 1
    };

    private void ShowEditorPreferences()
    {
        var dialog = new Window
        {
            Title = "编辑偏好设置", Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Max(1, SystemParameters.PrimaryScreenWidth * .38),
            Height = Math.Max(1, SystemParameters.PrimaryScreenHeight * .45),
            Background = Brush("#191B20"), Foreground = Brushes.White
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "预览硬件加速", FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6) });
        var gpu = new ComboBox { ItemsSource = new[] { "自动（GPU 优先）", "CPU 软件渲染", "NVIDIA CUDA", "Intel QSV", "AMD AMF" },
            SelectedItem = _previewGpuMode, Margin = new Thickness(0, 0, 0, 12) };
        gpu.SelectionChanged += (_, _) =>
        {
            if (gpu.SelectedItem is string mode)
            {
                _previewGpuMode = mode;
                InvalidateCompositedPreview();
                StatusText.Text = $"预览加速已切换：{mode}";
                _ = SaveCurrentWorkspaceAsync();
            }
        };
        panel.Children.Add(gpu);
        var autoSave = new CheckBox { Content = "每两分钟自动保存工程副本", IsChecked = _autoSaveEnabled,
            Margin = new Thickness(0, 0, 0, 12) };
        autoSave.Click += async (_, _) =>
        {
            _autoSaveEnabled = autoSave.IsChecked == true;
            if (_autoSaveEnabled) _autoSaveTimer.Start(); else _autoSaveTimer.Stop();
            await SaveCurrentWorkspaceAsync();
        };
        panel.Children.Add(autoSave);
        panel.Children.Add(new TextBlock { Text = "快捷键设置", FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8) });
        var command = new ComboBox { ItemsSource = McpShortcutNames,
            SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 8) };
        var shortcut = new TextBox { ToolTip = "例如 Ctrl+Alt+K、F8、Left；文字输入时不执行剪辑命令", Margin = new Thickness(0, 0, 0, 8) };
        var assign = new Button { Content = "设置快捷键", Padding = new Thickness(12, 6, 12, 6) };
        assign.Click += async (_, _) =>
        {
            string? name = command.SelectedItem as string;
            if (name is null || !TryParseShortcut(shortcut.Text, out Key key, out ModifierKeys modifiers))
            {
                MessageBox.Show(dialog, "请输入有效快捷键，例如 Ctrl+Alt+K。\n阶段代码：WORKSPACE-SHORTCUT",
                    "快捷键无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try { ApplyCustomShortcut(name, key, modifiers); }
            catch (TANGERINE_VideoEditor.Tools.StageException exception) { StatusText.Text = $"{exception.Message} · {exception.StageCode}"; return; }
            StatusText.Text = $"已设置“{name}”快捷键：{shortcut.Text.Trim()}";
            await SaveCurrentWorkspaceAsync();
        };
        panel.Children.Add(command);
        panel.Children.Add(shortcut);
        panel.Children.Add(assign);
        var close = new Button { Content = "关闭", HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 18, 0, 0) };
        close.Click += (_, _) => dialog.Close();
        panel.Children.Add(close);
        dialog.Content = panel;
        dialog.ShowDialog();
    }

    private static bool TryParseShortcut(string value, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;
        string[] parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 4) return false;
        foreach (string part in parts[..^1])
        {
            ModifierKeys flag = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModifierKeys.Control,
                "alt" => ModifierKeys.Alt,
                "shift" => ModifierKeys.Shift,
                _ => ModifierKeys.None
            };
            if (flag == ModifierKeys.None || modifiers.HasFlag(flag)) return false;
            modifiers |= flag;
        }
        return Enum.TryParse(parts[^1], true, out key) &&
            (key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.F1 and <= Key.F12 or Key.Left or Key.Right or Key.Space or Key.Delete);
    }

    private void ApplyCustomShortcut(string name, Key key, ModifierKeys modifiers)
    {
        if (_customShortcutBindings.Any(p => p.Key != name && p.Value is KeyBinding existing && existing.Key == key && existing.Modifiers == modifiers))
            throw new TANGERINE_VideoEditor.Tools.StageException("WKSPC0005", "该按键已被另一个自定义命令使用。");
        if (_customShortcutBindings.Remove(name, out InputBinding? previous)) InputBindings.Remove(previous);
        Action action = name switch
        {
            "分割片段" => SplitSelectedClip,
            "删除片段" => DeleteSelectedClip,
            "复制片段" => CopySelectedClip,
            "剪切片段" => CutSelectedClip,
            "粘贴片段" => PasteClipboardClip,
            "冻结帧" => FreezeSelectedFrame,
            "上一帧" => () => NudgePlayhead(-1), "下一帧" => () => NudgePlayhead(1),
            "波纹删除" => RippleDeleteSelectedClip, "波纹修剪开头" => RippleTrimStartToPlayhead,
            "波纹修剪结尾" => RippleTrimEndToPlayhead, "切换吸附" => ToggleTimelineSnapping,
            "切换磁性模式" => ToggleMagneticTimeline, "剃刀分割" => SplitSelectedClip,
            "播放暂停" => TogglePlayback, "停止播放" => StopPlayback,
            _ => throw new ArgumentException("未知快捷键命令。", nameof(name))
        };
        var binding = new KeyBinding(new RelayCommand(() => { if (!IsTextInputFocused()) action(); }), key, modifiers);
        InputBindings.Insert(0, binding);
        _customShortcutBindings[name] = binding;
    }

    private void ResetEditorWorkspace()
    {
        ApplyWorkspacePreset(.56, .44, 0);
        TimelineZoomSlider.Value = 1;
        TimelineScrollViewer.ScrollToHome();
        AssetSearchBox.Clear();
        PreviewQualityBox.SelectedIndex = 0;
        StatusText.Text = "工作区视图已重置";
    }

    private void ApplyWorkspacePreset(double upperRatio, double libraryRatio, int categoryIndex)
    {
        UpperWorkspaceRow.Height = new GridLength(upperRatio * 100, GridUnitType.Star);
        TimelineWorkspaceRow.Height = new GridLength((1 - upperRatio) * 100, GridUnitType.Star);
        LibraryWorkspaceColumn.Width = new GridLength(libraryRatio * 100, GridUnitType.Star);
        PreviewWorkspaceColumn.Width = new GridLength((1 - libraryRatio) * 100, GridUnitType.Star);
        CategoryList.SelectedIndex = Math.Clamp(categoryIndex, 0, Math.Max(0, CategoryList.Items.Count - 1));
        StatusText.Text = "已切换工作区布局";
    }

    private WorkspaceLayout CaptureWorkspaceLayout()
    {
        double upper = UpperWorkspaceRow.ActualHeight /
            Math.Max(1, UpperWorkspaceRow.ActualHeight + TimelineWorkspaceRow.ActualHeight);
        double library = LibraryWorkspaceColumn.ActualWidth /
            Math.Max(1, LibraryWorkspaceColumn.ActualWidth + PreviewWorkspaceColumn.ActualWidth);
        var shortcuts = _customShortcutBindings.Where(item => item.Value is KeyBinding)
            .ToDictionary(item => item.Key, item =>
            {
                var binding = (KeyBinding)item.Value;
                string modifier = binding.Modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl" : string.Empty;
                if (binding.Modifiers.HasFlag(ModifierKeys.Alt)) modifier += "+Alt";
                if (binding.Modifiers.HasFlag(ModifierKeys.Shift)) modifier += "+Shift";
                return modifier.Length == 0 ? binding.Key.ToString() : $"{modifier.TrimStart('+')}+{binding.Key}";
            });
        return new WorkspaceLayout(Math.Clamp(upper, .15, .85), Math.Clamp(library, .15, .85),
            CategoryList.SelectedIndex >= _categories.Length ? _lastLibraryCategory : CategoryList.SelectedIndex, PreviewQualityBox.SelectedIndex, TimelineZoomSlider.Value,
            _autoSaveEnabled, shortcuts, _previewGpuMode, PreviewZoomSlider.Value, MediaViewBox.SelectedIndex);
    }

    private async Task SaveCurrentWorkspaceAsync()
    {
        // Capture WPF controls on the dispatcher, then move filesystem work
        // to a worker so a slow profile directory cannot freeze editing.
        WorkspaceLayout layout = CaptureWorkspaceLayout();
        await _workspaceSettingsGate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                string? directory = Path.GetDirectoryName(WorkspaceSettingsPath);
                if (directory is not null) Directory.CreateDirectory(directory);
                File.WriteAllText(WorkspaceSettingsPath, JsonSerializer.Serialize(layout));
            });
            StatusText.Text = "工作区布局已保存";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            StatusText.Text = "保存工作区失败 · WORKSPACE-SAVE";
            MessageBox.Show(this, $"无法保存工作区布局：{exception.Message}\n阶段代码：WORKSPACE-SAVE",
                "工作区错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _workspaceSettingsGate.Release(); }
    }

    private async Task RestoreSavedWorkspaceAsync(bool showMissing = true)
    {
        await _workspaceSettingsGate.WaitAsync();
        try
        {
            WorkspaceLayout? layout = await Task.Run(() =>
            {
                if (!File.Exists(WorkspaceSettingsPath)) return null;
                return JsonSerializer.Deserialize<WorkspaceLayout>(File.ReadAllText(WorkspaceSettingsPath));
            });
            if (layout is null)
            {
                if (showMissing) StatusText.Text = "尚未保存工作区布局";
                return;
            }
            if (!double.IsFinite(layout.UpperRatio) || !double.IsFinite(layout.LibraryRatio) ||
                !double.IsFinite(layout.TimelineZoom))
                throw new JsonException("工作区比例或时间线缩放无效。");
            ApplyWorkspacePreset(Math.Clamp(layout.UpperRatio, .15, .85),
                Math.Clamp(layout.LibraryRatio, .15, .85), layout.CategoryIndex);
            PreviewQualityBox.SelectedIndex = Math.Clamp(layout.PreviewQualityIndex, 0, 2);
            TimelineZoomSlider.Value = Math.Clamp(layout.TimelineZoom, TimelineZoomSlider.Minimum, TimelineZoomSlider.Maximum);
            PreviewZoomSlider.Value = double.IsFinite(layout.PreviewViewZoom) ? Math.Clamp(layout.PreviewViewZoom, .1, 4) : 1;
            MediaViewBox.SelectedIndex = Math.Clamp(layout.MediaViewIndex, 0, 2);
            _autoSaveEnabled = layout.AutoSave;
            _previewGpuMode = string.IsNullOrWhiteSpace(layout.PreviewGpuMode)
                ? "自动（GPU 优先）" : layout.PreviewGpuMode;
            if (_autoSaveEnabled) _autoSaveTimer.Start(); else _autoSaveTimer.Stop();
            foreach (InputBinding binding in _customShortcutBindings.Values) InputBindings.Remove(binding);
            _customShortcutBindings.Clear();
            foreach (var shortcut in layout.Shortcuts ?? [])
            {
                if (shortcut.Key is "分割片段" or "删除片段" or "复制片段" or "剪切片段" or "粘贴片段" or "冻结帧" &&
                    TryParseShortcut(shortcut.Value, out Key key, out ModifierKeys modifiers))
                    ApplyCustomShortcut(shortcut.Key, key, modifiers);
            }
            StatusText.Text = "已恢复工作区布局";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            StatusText.Text = "恢复工作区失败 · WORKSPACE-RESTORE";
            if (showMissing) MessageBox.Show(this,
                $"无法读取工作区布局：{exception.Message}\n阶段代码：WORKSPACE-RESTORE",
                "工作区错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _workspaceSettingsGate.Release(); }
    }

    private void FreezeSelectedFrame()
    {
        if (_selectedClip is not { Kind: ClipKind.Video } clip || _selectedTrack?.IsLocked != false)
        { StatusText.Text = "请在未锁定的轨道上选择视频片段"; return; }
        MediaAsset? asset = _project.Media.FirstOrDefault(item => item.Id == clip.MediaAssetId);
        if (asset is null || string.IsNullOrWhiteSpace(asset.SourcePath))
        { StatusText.Text = "源媒体离线 · EDIT-FREEZE-SOURCE"; return; }
        _ = FreezeFrameAsync(clip, asset);
    }

    private async Task FreezeFrameAsync(TimelineClip clip, MediaAsset asset)
    {
        if (_projectIoInProgress || _exportInProgress) return;
        var picker = new SaveFileDialog { Title = "保存冻结帧", Filter = "PNG 图片|*.png",
            DefaultExt = ".png", AddExtension = true, FileName = Path.GetFileNameWithoutExtension(asset.Name) + "_静帧.png" };
        if (picker.ShowDialog(this) != true) return;
        _exportInProgress = true;
        try
        {
            ProjectDocument sourceProject = _project;
            long sourceRevision = _projectRevision;
            long sourceGeneration = _projectGeneration;
            double frameTime = sourceProject.Sequence.PlayheadSeconds;
            string[] mediaPaths = sourceProject.Media
                .SelectMany(item => new[] { item.SourcePath, item.ProxyPath })
                .Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().ToArray();
            await StillFrameExporter.ExportSourceFrameAsync(clip, asset, sourceProject.Sequence,
                frameTime, picker.FileName, mediaPaths);
            if (!ReferenceEquals(sourceProject, _project) || sourceRevision != _projectRevision ||
                sourceGeneration != _projectGeneration)
            { StatusText.Text = "冻结帧已导出；工程在渲染期间修改，未自动加入时间线"; return; }
            string[] projectedPaths = [.. mediaPaths, picker.FileName];
            if (_projectPath is string projectPath)
                await ProjectFileService.EnsureImportCapacityAsync(projectPath, projectedPaths);
            else
                await ProjectFileService.EnsureImportCapacityAsync(projectedPaths);
            if (!ReferenceEquals(sourceProject, _project) || sourceRevision != _projectRevision ||
                sourceGeneration != _projectGeneration)
            { StatusText.Text = "冻结帧已导出；工程在检查空间期间修改，未自动加入时间线"; return; }
            TimelineTrack? track = sourceProject.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
            if (track is null) return;
            SaveUndoState();
            var imageAsset = new MediaAsset { Name = Path.GetFileName(picker.FileName), Kind = MediaKind.Image,
                SourcePath = picker.FileName, Width = _project.Sequence.Width, Height = _project.Sequence.Height };
            _project.Media.Add(imageAsset);
            var still = new TimelineClip { Kind = ClipKind.Image, Name = imageAsset.Name,
                MediaAssetId = imageAsset.Id, StartSeconds = Math.Max(0, frameTime),
                DurationSeconds = Math.Max(1 / Math.Max(1, _project.Sequence.FramesPerSecond), 1) };
            track.Clips.Add(still);
            MarkProjectDirty(); RefreshLibrary(); RefreshTimeline(); SeekPreview(still.StartSeconds);
            bool saved = _projectPath is string targetPath
                ? await SaveProjectToAsync(targetPath)
                : await SaveProjectAsAsync();
            if (saved)
                StatusText.Text = "冻结帧已保存并加入时间线";
        }
        catch (StillFrameException exception)
        {
            StatusText.Text = $"冻结帧失败 · {exception.StageCode}";
            MessageBox.Show(this, $"{exception.Message}\n阶段代码：{exception.StageCode}", "冻结帧失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (ProjectFileException exception) { ShowProjectError(exception); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
            PathTooLongException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "冻结帧路径无效 · EDIT-FREEZE-PATH";
            MessageBox.Show(this, $"无法使用冻结帧路径：{exception.Message}\n阶段代码：EDIT-FREEZE-PATH",
                "冻结帧失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _exportInProgress = false; }
    }

    private async Task ExportCurrentFrameAsync()
    {
        if (_projectIoInProgress || _exportInProgress) return;
        var picker = new SaveFileDialog { Title = "导出当前帧", Filter = "PNG 图片|*.png",
            DefaultExt = ".png", AddExtension = true, FileName = _project.Name + "_当前帧.png" };
        if (picker.ShowDialog(this) != true) return;
        _exportInProgress = true;
        try
        {
            // Capture the editable project before the long render begins. A
            // revision guard rejects a snapshot made while an edit replaced
            // the project, and later edits cannot change this export frame.
            ProjectDocument source = _project;
            long revision = _projectRevision;
            long generation = _projectGeneration;
            double frameTime = source.Sequence.PlayheadSeconds;
            ProjectDocument snapshot = await Task.Run(() =>
                JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(source))
                ?? throw new StillFrameException(StillFrameStageCode.Prepare, "无法复制当前序列。"));
            if (!ReferenceEquals(source, _project) || revision != _projectRevision ||
                generation != _projectGeneration)
                throw new StillFrameException(StillFrameStageCode.Prepare,
                    "准备导出帧时项目已修改，请重新导出。");
            await StillFrameExporter.ExportCompositedFrameAsync(snapshot, frameTime, picker.FileName);
            StatusText.Text = $"当前帧已导出：{Path.GetFileName(picker.FileName)}";
        }
        catch (StillFrameException exception)
        {
            StatusText.Text = $"导出帧失败 · {exception.StageCode}";
            MessageBox.Show(this, $"{exception.Message}\n阶段代码：{exception.StageCode}", "导出帧失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
            ArgumentException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "导出帧失败 · Prepare";
            MessageBox.Show(this, $"准备导出帧时失败：{exception.Message}\n阶段代码：Prepare",
                "导出帧失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _exportInProgress = false; }
    }

    private async Task AutoSaveProjectCopyAsync()
    {
        if (!_autoSaveEnabled || _autoSaveInProgress || _exportInProgress ||
            _projectIoInProgress || !_isProjectDirty) return;
        _autoSaveInProgress = true;
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TANGERINE-VideoEditor", "自动保存", _project.Id);
            string path = Path.Combine(root, $"{DateTime.Now:yyyyMMdd_HHmmss}.tvep");
            ProjectDocument source = _project;
            long revision = _projectRevision;
            long generation = _projectGeneration;
            ProjectDocument snapshot = await Task.Run(() =>
                JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(source))!);
            if (!ReferenceEquals(source, _project) || revision != _projectRevision ||
                generation != _projectGeneration) return;
            // SaveAsync embeds working media into the autosave archive and
            // rewrites paths in a private manifest copy.
            await Task.Run(() => Directory.CreateDirectory(root));
            if (!ReferenceEquals(source, _project) || revision != _projectRevision ||
                generation != _projectGeneration) return;
            await ProjectFileService.SaveAsync(snapshot, path);
            StatusText.Text = $"自动保存副本：{Path.GetFileName(path)}";
        }
        catch (ProjectFileException exception)
        { StatusText.Text = $"自动保存失败 · {exception.StageCode}"; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        { StatusText.Text = $"自动保存失败 · WORKSPACE-AUTOSAVE：{exception.Message}"; }
        finally { _autoSaveInProgress = false; }
    }
}
