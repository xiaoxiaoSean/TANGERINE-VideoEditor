using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Presents imported media and bundled creative objects as drag sources.
/// </summary>
public partial class MainWindow
{
    private readonly string[] _categories = ["媒体", "文字", "效果", "转场", "贴纸"];
    private int _lastLibraryCategory;
    private Point _libraryDragStart;
    private LibraryItem? _libraryPressedItem;
    private bool _libraryDragInProgress;
    private bool _catalogPreviewPending;
    private readonly SemaphoreSlim _mediaImportGate = new(1, 1);
    private readonly SemaphoreSlim _mediaProbeGate = new(2, 2);
    private CancellationTokenSource? _mediaMetadataCancellation;

    private void InitializeLibrary()
    {
        CategoryList.ItemsSource = _categories;
        CategoryList.SelectionChanged += (_, _) =>
        {
            StopCatalogPreview();
            if (CategoryList.SelectedIndex >= _categories.Length)
            {
                SettingsHost.Visibility = Visibility.Visible;
                AssetList.Visibility = Visibility.Collapsed;
                LibraryContentGrid.Visibility = Visibility.Collapsed;
                return;
            }
            SettingsHost.Visibility = Visibility.Collapsed;
            _lastLibraryCategory = Math.Max(0, CategoryList.SelectedIndex);
            AssetList.Visibility = Visibility.Visible;
            LibraryContentGrid.Visibility = Visibility.Visible;
            RefreshLibrary();
        };
        CategoryList.SelectedIndex = 0;
        AssetSearchBox.TextChanged += (_, _) => RefreshLibrary();
        AssetList.PreviewMouseLeftButtonDown += AssetList_PreviewMouseLeftButtonDown;
        AssetList.PreviewMouseMove += AssetList_PreviewMouseMove;
        AssetList.LostMouseCapture += (_, args) =>
        {
            if (ReferenceEquals(args.OriginalSource, AssetList) && !AssetList.IsMouseCaptured && !_libraryDragInProgress) _libraryPressedItem = null;
        };
        AssetList.PreviewMouseLeftButtonUp += AssetList_PreviewMouseLeftButtonUp;
        PreviewMediaElement.MediaEnded += (_, _) =>
        {
            bool wasCatalogPreview = _catalogPreviewActive;
            if (wasCatalogPreview)
            {
                _catalogPreviewActive = false;
                CatalogPlaybackBar.Visibility = Visibility.Collapsed;
                StatusText.Text = "示例预览已结束";
            }
            // Keep ownership until the media element releases the file. Its
            // last frame can remain open after MediaEnded on Windows; the next
            // selection, category change or window close releases this lease.
        };
        LibraryAddButton.Click += async (_, _) =>
        {
            if (CategoryList.SelectedIndex == 0) await ImportMediaAsync();
            else AddSelectedLibraryItem();
        };
        ImportMenuItem.Click += async (_, _) => await ImportMediaAsync();
        ImportMediaButton.Click += async (_, _) => await ImportMediaAsync();
        LibraryDropSurface.PreviewDragOver += LibraryDropSurface_DragOver;
        LibraryDropSurface.PreviewDrop += LibraryDropSurface_Drop;
    }

    private CancellationTokenSource? _catalogPreviewCancellation;
    private string? _catalogPreviewPath;
    private bool _catalogPreviewActive;
    private long _catalogPreviewGeneration;

    /// <summary>
    /// Resolve the card at the actual pointer, not OriginalSource or SelectedItem.
    /// A captured ListBox can route later events through the preceding card.
    /// </summary>
    private LibraryItem? LibraryItemAt(Point point)
    {
        if (AssetList.InputHitTest(point) is not DependencyObject source) return null;
        return (ItemsControl.ContainerFromElement(AssetList, source) as ListBoxItem)?.Content as LibraryItem;
    }

    private void AssetList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs args)
    {
        if (CompleteLibraryClick(args.GetPosition(AssetList))) args.Handled = true;
    }

    private bool CompleteLibraryClick(Point point)
    {
        LibraryItem? pressed = _libraryPressedItem;
        _libraryPressedItem = null;
        if (AssetList.IsMouseCaptured) AssetList.ReleaseMouseCapture();
        if (_libraryDragInProgress || pressed is null || !ReferenceEquals(pressed, LibraryItemAt(point))) return false;
        AssetList.SelectedItem = pressed;
        // One completed click schedules one preview; selection changes do not
        // start a competing worker before mouse-up can distinguish a drag.
        PreviewSelectedCatalogItem();
        return true;
    }

    private void AssetList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (BeginLibraryGesture(args.GetPosition(AssetList), args.ClickCount)) args.Handled = true;
    }

    private bool BeginLibraryGesture(Point point, int clickCount)
    {
        if (_libraryDragInProgress) return false;
        _libraryDragStart = point;
        _libraryPressedItem = LibraryItemAt(point);
        if (_libraryPressedItem is null) return false;
        AssetList.Focus();
        AssetList.SelectedItem = _libraryPressedItem;
        // Own the gesture instead of letting ListBoxItem capture a stale card.
        if (clickCount == 2)
        {
            _libraryPressedItem = null;
            AssetList.ReleaseMouseCapture();
            if (CategoryList.SelectedIndex == 0) OpenSelectedSourceMonitor();
            else AddSelectedLibraryItem();
        }
        return true;
    }

    private void FinishLibraryDrag()
    {
        _libraryPressedItem = null;
        _libraryDragInProgress = false;
        if (AssetList.IsMouseCaptured) AssetList.ReleaseMouseCapture();
        RemoveDropGhost();
    }

    private void StopCatalogPreview()
    {
        // Read before clearing: only a preview that actually took over the
        // monitor needs the timeline restored afterwards.
        bool wasActive = _catalogPreviewActive || _catalogPreviewPending;
        _catalogPreviewPending = false;
        Interlocked.Increment(ref _catalogPreviewGeneration);
        _catalogPreviewActive = false;
        _catalogPreviewCancellation?.Cancel();
        _catalogPreviewCancellation = null;
        CatalogPreviewService.StopPreview();
        if (_catalogPreviewPath is string path)
        {
            PreviewMediaElement.Stop();
            PreviewMediaElement.Source = null;
            DeleteCatalogPlaybackFile(path);
        }
        _catalogPreviewPath = null;
        CatalogPlaybackBar.Visibility = Visibility.Collapsed;
        CatalogPreviewProgressBar.Visibility = Visibility.Collapsed;
        CatalogPlaybackSlider.Value = 0;
        CatalogPlaybackTime.Text = "00:00 / 00:00";
        // A demonstration hid the timeline's overlays and re-laid out the media
        // element. Rebuild both so the monitor returns to the real sequence.
        if (wasActive)
        {
            _lastMediaTransformKey = null;
            _lastTitleLayoutKey = null;
            DrawTextOverlay();
            SeekPreview(_project.Sequence.PlayheadSeconds);
        }
    }

    private void PreviewSelectedCatalogItem()
    {
        LibraryItem? selected = AssetList.SelectedItem as LibraryItem;
        if (selected?.Source is not BuiltinPreset preset || selected.Category is not (1 or 2 or 3 or 5)) return;
        int category = selected.Category;
        StopCatalogPreview();
        if (_isPlaying) TogglePlayback();
        StopCachedPreview();
        ClearRenderedPreviewFrame();
        _previewImageRequest++;
        _previewImageLoadingPath = null;
        _previewSourcePath = null;
        _catalogPreviewPending = true;
        long generation = Interlocked.Increment(ref _catalogPreviewGeneration);
        StatusText.Text = $"正在生成“{preset.Name}”示例预览…";
        PreviewMediaElement.Stop();
        PreviewMediaElement.Source = null;
        if (_catalogPreviewPath is string previousPath) DeleteCatalogPlaybackFile(previousPath);
        _catalogPreviewPath = null;
        _catalogPreviewActive = false;
        CatalogPlaybackBar.Visibility = Visibility.Collapsed;
        PreviewMediaElement.Visibility = Visibility.Collapsed;
        PreviewRenderedFrame.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewMessage.Text = "正在生成示例视频…";
        CatalogPreviewProgressBar.Value = 0;
        CatalogPreviewProgressBar.Visibility = Visibility.Visible;
        // Catalog demonstrations always use the fixed, easy-to-read sample
        // word. They demonstrate the selected preset's visual style and
        // animation, independent of any text clip currently on the timeline.
        const string sampleText = "example";
        _catalogPreviewCancellation = new CancellationTokenSource();
        CancellationToken token = _catalogPreviewCancellation.Token;
        string ffmpeg = TimelineExporter.ResolveEncoderPath();
        _ = Task.Run(async () =>
        {
            string? path = null;
            bool handedToPlayer = false;
            try
            {
                void Play(string generated) => path = generated;
                void Progress(double value)
                {
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested && generation == Volatile.Read(ref _catalogPreviewGeneration))
                        {
                            CatalogPreviewProgressBar.Value = Math.Clamp(value, 0, 100);
                            PreviewMessage.Text = $"正在生成示例视频 {CatalogPreviewProgressBar.Value:0}%…";
                        }
                    });
                }
                if (category == 3)
                    await CatalogPreviewService.PlayTransitionPreviewAsync(preset.Id, ffmpeg, Play, token, Progress);
                else if (category is 2 or 5)
                    await CatalogPreviewService.PlayEffectPreviewAsync(preset.Id, ffmpeg, Play, token, Progress);
                else
                    await CatalogPreviewService.PlayTextPreviewAsync(preset.Id, sampleText, ffmpeg,
                        CreateTitleStyle, Play, token, Progress);
                if (token.IsCancellationRequested || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested && generation == Volatile.Read(ref _catalogPreviewGeneration))
                    {
                        PlayCatalogPreview(path);
                        handedToPlayer = true;
                    }
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || generation != Volatile.Read(ref _catalogPreviewGeneration)) return;
                    _catalogPreviewPending = false;
                    CatalogPreviewProgressBar.Visibility = Visibility.Collapsed;
                    PreviewMessage.Text = "示例预览生成失败";
                    StatusText.Text = $"示例预览失败：{exception.Message}";
                });
            }
            finally
            {
                // The service transfers a disposable playback copy. If this
                // selection became obsolete before the dispatcher accepted it,
                // no player owns that copy and the worker must release it.
                if (!handedToPlayer && path is not null) DeleteCatalogPlaybackFile(path);
            }
        }, token);
    }

    private static void DeleteCatalogPlaybackFile(string path)
    {
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { File.Delete(path); return; }
                catch (IOException) when (attempt < 2)
                { await Task.Delay(150 * (attempt + 1)).ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return; }
            }
        });
    }

    /// <summary>
    /// Gives the media element a clean, frame-filling layout for a catalog
    /// demonstration. Timeline playback sizes this element through
    /// ApplyPreviewMediaTransform, which needs a timeline clip and returns early
    /// without one; a demo has none, so the element kept no size (or kept the
    /// crop, scale and opacity of the last clip) and nothing showed but black.
    /// </summary>
    private void FitMediaElementToFrame()
    {
        double stageWidth = PreviewOverlay.ActualWidth;
        double stageHeight = PreviewOverlay.ActualHeight;
        if (!double.IsFinite(stageWidth) || !double.IsFinite(stageHeight) ||
            stageWidth <= 0 || stageHeight <= 0) return;
        double scale = Math.Min(stageWidth / Math.Max(1, _project.Sequence.Width),
            stageHeight / Math.Max(1, _project.Sequence.Height));
        double frameWidth = _project.Sequence.Width * scale;
        double frameHeight = _project.Sequence.Height * scale;
        // The visual layer is what centers the frame; size it before placing
        // the element inside it.
        PreviewVisualLayer.Width = frameWidth;
        PreviewVisualLayer.Height = frameHeight;
        PreviewMediaElement.Width = frameWidth;
        PreviewMediaElement.Height = frameHeight;
        PreviewMediaElement.Stretch = Stretch.Uniform;
        // Drop whatever the previous timeline clip left behind.
        PreviewMediaElement.Clip = null;
        PreviewMediaElement.RenderTransform = Transform.Identity;
        PreviewMediaElement.RenderTransformOrigin = new Point(.5, .5);
        PreviewMediaElement.Opacity = 1;
        Canvas.SetLeft(PreviewMediaElement, 0);
        Canvas.SetTop(PreviewMediaElement, 0);
        // Force the next timeline preview to lay itself out again instead of
        // trusting a cached key that describes the state we just overwrote.
        _lastMediaTransformKey = null;
    }

    private void PlayCatalogPreview(string path)
    {
        _catalogPreviewPending = false;
        _catalogPreviewActive = true;
        PreviewRenderedFrame.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        PreviewOverlay.Children.Clear();
        _mediaSelectionVisuals.Clear();
        FitMediaElementToFrame();
        PreviewMediaElement.Visibility = Visibility.Visible;
        try
        {
            PreviewMediaElement.Stop();
            PreviewMediaElement.Source = new Uri(path);
            _catalogPreviewPath = path;
            CatalogPreviewProgressBar.Visibility = Visibility.Collapsed;
            CatalogPlaybackBar.Visibility = Visibility.Visible;
            CatalogPlaybackButton.Content = "❚❚";
            StatusText.Text = "示例预览播放中";
            PreviewMediaElement.Play();
        }
        catch (UriFormatException exception) { StatusText.Text = $"示例预览路径无效：{exception.Message}"; }
    }

    private void RefreshLibrary()
    {
        if (AssetList is null || CategoryList is null) return;
        // Object inspectors append temporary category tabs. Refresh the
        // underlying library using its last real category, never the tab's
        // index, while keeping the active inspector visible.
        int category = CategoryList.SelectedIndex >= _categories.Length
            ? Math.Clamp(_lastLibraryCategory, 0, _categories.Length - 1)
            : Math.Max(0, CategoryList.SelectedIndex);
        string query = AssetSearchBox?.Text.Trim() ?? string.Empty;
        LibraryTitle.Text = _categories[category];
        if (MediaViewBox.ItemsSource is not null) ApplyMediaView();
        LibrarySubtitle.Text = category switch
        {
            0 => "导入媒体，拖入时间线开始创作",
            1 => "双击添加文字，或拖到时间线",
            2 => "滤镜与特效统一在此，拖到片段上并调整参数",
            3 => "把转场拖到两个相邻片段之间",
            4 => "把贴纸拖到时间线或预览区",
            _ => "把滤镜拖到可见片段上，可继续调整并添加关键帧"
        };
        LibraryAddButton.Content = category == 0 ? "＋ 导入" : "＋ 添加";

        var items = new List<LibraryItem>();
        if (category == 0)
        {
            items.AddRange(_project.Media.Where(asset => _selectedBinId is null || IsAssetInSelectedBin(asset))
                .Select(asset => new LibraryItem(asset.Id, asset.Name,
                    DescribeMediaAsset(asset), asset.LabelColor, category, asset)));
            bool newProjectStatus = _mediaAvailabilityProjectGeneration != _projectGeneration;
            bool missingStatus = _project.Media.Any(asset =>
                !_mediaAvailability.TryGetValue(asset.Id, out var status) ||
                status.SourcePath != asset.SourcePath || status.ProxyPath != asset.ProxyPath);
            bool staleStatus = Environment.TickCount64 - _mediaAvailabilityLastRefreshTicks >= 10000;
            if ((newProjectStatus || missingStatus || staleStatus) &&
                Environment.TickCount64 - _mediaAvailabilityLastRefreshTicks >= 500)
            {
                _mediaAvailabilityLastRefreshTicks = Environment.TickCount64;
                ScheduleMediaAvailabilityRefresh();
            }
        }
        else
        {
            IReadOnlyList<BuiltinPreset> presets = category switch
            {
                1 => BuiltinCatalog.Titles,
                2 => BuiltinCatalog.Effects.Concat(BuiltinCatalog.Filters).ToArray(),
                3 => BuiltinCatalog.Transitions,
                4 => BuiltinCatalog.Stickers,
                _ => BuiltinCatalog.Filters
            };
            items.AddRange(presets.Select(preset => new LibraryItem(preset.Id, preset.Name,
                $"{preset.Group} · {preset.Description}", preset.AccentHex, category, preset)));
        }
        if (query.Length > 0)
        {
            items = items.Where(item => item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                (item.Source is MediaAsset asset && (asset.Tags.Any(tag => tag.Contains(query, StringComparison.CurrentCultureIgnoreCase)) ||
                    asset.Metadata.Any(tag => tag.Key.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                        tag.Value.Contains(query, StringComparison.CurrentCultureIgnoreCase)) ||
                    asset.SourcePath.Contains(query, StringComparison.CurrentCultureIgnoreCase)))).ToList();
        }
        string? selectedAsset = (AssetList.SelectedItem as LibraryItem)?.Source is MediaAsset selected ? selected.Id : null;
        AssetList.ItemsSource = items;
        LoadLibraryThumbnails(items);
        if (category == 0 && selectedAsset is not null) AssetList.SelectedItem = items.FirstOrDefault(item => item.Id == selectedAsset);
        EmptyLibraryMessage.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyLibraryMessage.Text = category == 0 ? "点击导入，或把媒体文件直接拖到这里" : "没有匹配的内置对象";
    }

    private void AssetList_PreviewMouseMove(object sender, MouseEventArgs args)
    {
        if (_libraryDragInProgress) return;
        if (args.LeftButton != MouseButtonState.Pressed) { FinishLibraryDrag(); return; }
        if (_libraryPressedItem is not LibraryItem item) return;
        Point point = args.GetPosition(AssetList);
        if (Math.Abs(point.X - _libraryDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _libraryDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (!AssetList.Items.Cast<object>().Any(candidate => ReferenceEquals(candidate, item)) ||
            item.Source is MediaAsset source && !_project.Media.Any(asset => asset.Id == source.Id))
        { FinishLibraryDrag(); return; }
        _libraryPressedItem = null;
        _libraryDragInProgress = true;
        if (AssetList.IsMouseCaptured) AssetList.ReleaseMouseCapture();
        StopCatalogPreview();
        try
        {
            // The immutable press target belongs only to this native drag loop.
            DragDrop.DoDragDrop(AssetList, new DataObject(typeof(LibraryItem), item), DragDropEffects.Copy);
        }
        finally
        {
            // Covers successful drops, returning to the library, Escape,
            // release outside the window, and exceptions from the native loop.
            FinishLibraryDrag();
        }
        args.Handled = true;
    }

    private void AddSelectedLibraryItem()
    {
        if (AssetList.SelectedItem is not LibraryItem item) return;
        if (item.Category is 2 or 3 or 5)
        {
            ApplyPresetToSelectedClip(item);
            return;
        }
        if (item.Source is MediaAsset source)
        {
            MediaAsset? current = _project.Media.FirstOrDefault(asset => asset.Id == source.Id);
            if (current is null)
            {
                StatusText.Text = "所选媒体已从当前项目移除，请重新选择";
                RefreshLibrary();
                return;
            }
            item = item with { Source = current };
        }
        AddLibraryItemToTimeline(item, _project.Sequence.PlayheadSeconds, -1);
    }

    private async Task ImportMediaAsync()
    {
        if (!_projectCreated)
        {
            MessageBox.Show(this, "请先点击“新建项目”并选择项目宽高比，再导入媒体。",
                "需要先创建项目", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "导入媒体",
            Filter = "所有媒体|*.mp4;*.mov;*.m4v;*.mkv;*.avi;*.wmv;*.webm;*.mpeg;*.mpg;*.ts;*.mts;*.m2ts;*.flv;*.3gp;*.ogv;*.vob;*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.mp3;*.wav;*.m4a;*.flac;*.aac;*.ogg;*.wma;*.aif;*.aiff|视频文件|*.mp4;*.mov;*.m4v;*.mkv;*.avi;*.wmv;*.webm;*.mpeg;*.mpg;*.ts;*.mts;*.m2ts;*.flv;*.3gp;*.ogv;*.vob|图片文件|*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.gif;*.webp;*.tif;*.tiff|音频文件|*.mp3;*.wav;*.m4a;*.flac;*.aac;*.ogg;*.wma;*.aif;*.aiff|所有文件|*.*",
            FilterIndex = 1,
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        await ImportMediaPathsAsync(dialog.FileNames);
    }

    private void LibraryDropSurface_DragOver(object sender, DragEventArgs args)
    {
        RemoveDropGhost();
        args.Effects = DragDropEffects.None;
        args.Handled = true;
        try
        {
            if (args.Data.GetDataPresent(DataFormats.FileDrop))
            {
                args.Effects = DragDropEffects.Copy;
                args.Handled = true;
            }
        }
        catch (System.Runtime.InteropServices.COMException) { args.Effects = DragDropEffects.None; }
    }

    private async void LibraryDropSurface_Drop(object sender, DragEventArgs args)
    {
        RemoveDropGhost();
        try
        {
            if (args.Data.GetDataPresent(typeof(LibraryItem)))
            { args.Effects = DragDropEffects.None; args.Handled = true; return; }
        }
        catch (System.Runtime.InteropServices.COMException)
        { args.Effects = DragDropEffects.None; args.Handled = true; return; }
        if (!_projectCreated)
        {
            args.Handled = true;
            MessageBox.Show(this, "请先创建项目并选择宽高比，再拖入媒体。",
                "需要先创建项目", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { if (!args.Data.GetDataPresent(DataFormats.FileDrop)) return; }
        catch (System.Runtime.InteropServices.COMException) { return; }
        args.Handled = true;
        string[]? paths;
        try { paths = args.Data.GetData(DataFormats.FileDrop) as string[]; }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            StatusText.Text = $"媒体导入失败 · MED-IMPORT-INPUT：{exception.Message}";
            return;
        }
        if (paths is null) return;
        await ImportMediaPathsAsync(paths);
    }

    /// <summary>
    /// Resolves paths in a worker, then commits media on the UI thread. Returning
    /// existing assets lets every import source use the same project media bin.
    /// </summary>
    private async Task<IReadOnlyList<MediaAsset>> ImportMediaPathsAsync(IEnumerable<string> paths)
    {
        ProjectDocument targetProject = _project;
        long targetGeneration = _projectGeneration;
        string[] pathSnapshot;
        try { pathSnapshot = paths.ToArray(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "媒体导入失败 · MED-IMPORT-INPUT";
            MessageBox.Show(this, $"无法获取拖入的文件路径。\n阶段代码：MED-IMPORT-INPUT\n{exception.Message}",
                "媒体导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return [];
        }
        if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration) return [];
        if (pathSnapshot.Length == 0) return [];
        StatusText.Text = "正在导入媒体…";
        await _mediaImportGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration) return [];
            // File and directory inspection can stall on slow disks or shares.
            ImportScan scan = await Task.Run(() => ScanImportPaths(pathSnapshot));
            List<ImportCandidate> candidates = scan.Candidates;
            if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration) return [];
            if (candidates.Count == 0)
            {
                StatusText.Text = scan.Skipped > 0
                    ? $"没有可导入的媒体；跳过 {scan.Skipped} 个不可读取或不支持的路径 · MED-IMPORT-SCAN"
                    : "没有找到支持的视频、音频或图片文件";
                return [];
            }
            // Every imported asset is stored inside the .tvep archive. Choose
            // its destination before checking the projected archive footprint;
            // a project with no path has no meaningful target disk to inspect.
            if (_projectPath is null)
            {
                if (!await SaveProjectAsAsync() || _projectPath is null || !ReferenceEquals(targetProject, _project) ||
                    targetGeneration != _projectGeneration) return [];
            }
            if (_projectPath is not null)
            {
                long capacityRevision = _projectRevision;
                string capacityPath = _projectPath;
                try
                {
                    // Each media and proxy slot is stored as its own archive
                    // entry, even when two slots refer to the same file.
                    // Count only candidates that will create a new asset,
                    // while preserving repeated paths already in the model.
                    var knownPaths = _project.Media.Select(asset => asset.SourcePath)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    string[] projectedPaths = _project.Media.SelectMany(asset =>
                            new[] { asset.SourcePath, asset.ProxyPath })
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Cast<string>()
                        .Concat(candidates.Where(candidate => !knownPaths.Contains(candidate.Path))
                            .Select(candidate => candidate.Path)).ToArray();
                    await ProjectFileService.EnsureImportCapacityAsync(capacityPath, projectedPaths);
                }
                catch (ProjectFileException exception)
                {
                    ShowProjectError(exception);
                    return [];
                }
                if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration ||
                    capacityRevision != _projectRevision ||
                    !string.Equals(capacityPath, _projectPath, StringComparison.OrdinalIgnoreCase))
                {
                    StatusText.Text = "空间检查期间项目已修改，请重新导入";
                    return [];
                }
            }
            // Show the media tab before committing the scanned files. Small
            // batches below keep pointer and keyboard input responsive.
            AssetSearchBox.Clear();
            CategoryList.SelectedIndex = 0;
            var imported = new List<MediaAsset>(candidates.Count);
            var known = new Dictionary<string, MediaAsset>(StringComparer.OrdinalIgnoreCase);
            foreach (MediaAsset existing in _project.Media)
            {
                if (string.IsNullOrWhiteSpace(existing.SourcePath)) continue;
                known.TryAdd(existing.SourcePath, existing);
            }
            int added = 0;
            var newAssets = new List<MediaAsset>();
            int lastRenderedAdded = 0;
            string? importUndoSnapshot = null;
            long importRevision = _projectRevision;
            foreach (ImportCandidate candidate in candidates)
            {
                if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration) return [];
                // Commit each unique path once, preserving one undo snapshot
                // for the import action.
                if (!known.TryGetValue(candidate.Path, out MediaAsset? asset))
                {
                    if (added == 0)
                    {
                        SaveUndoState();
                        importUndoSnapshot = _undoStates.Peek();
                    }
                    asset = new MediaAsset { Name = Path.GetFileName(candidate.Path), Kind = candidate.Kind,
                        SourcePath = candidate.Path, BinId = _selectedBinId };
                    _project.Media.Add(asset);
                    known.Add(candidate.Path, asset);
                    newAssets.Add(asset);
                    added++;
                }
                imported.Add(asset);
                if (imported.Count % 50 == 0)
                {
                    if (added > lastRenderedAdded)
                    {
                        MarkProjectDirty();
                        RefreshLibrary();
                        lastRenderedAdded = added;
                        importRevision = _projectRevision;
                    }
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration) return [];
                    if (importUndoSnapshot is not null &&
                        (_undoStates.Count == 0 || _undoStates.Peek() != importUndoSnapshot ||
                         _projectRevision != importRevision ||
                         newAssets.Any(asset => !_project.Media.Any(current => current.Id == asset.Id))))
                    {
                        StatusText.Text = "媒体导入已因撤销或其他编辑中止";
                        return [];
                    }
                }
            }
            if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration) return [];
            if (importUndoSnapshot is not null &&
                (_undoStates.Count == 0 || _undoStates.Peek() != importUndoSnapshot ||
                 (added == lastRenderedAdded && _projectRevision != importRevision) ||
                 newAssets.Any(asset => !_project.Media.Any(current => current.Id == asset.Id))))
            {
                StatusText.Text = "媒体导入已因撤销或其他编辑中止";
                return [];
            }
            if (added > lastRenderedAdded) MarkProjectDirty();
            // Show the completed media bin so every source remains discoverable.
            CategoryList.SelectedIndex = 0;
            AssetSearchBox.Clear();
            // Refresh the visible list after the last insertion batch.
            RefreshLibrary();
            // Resolve returned assets against current IDs before a timeline
            // drop uses them after asynchronous import.
            var byId = new Dictionary<string, MediaAsset>(StringComparer.Ordinal);
            foreach (MediaAsset current in _project.Media) byId.TryAdd(current.Id, current);
            imported = imported.Where(asset => byId.ContainsKey(asset.Id))
                .Select(asset => byId[asset.Id]).ToList();
            newAssets = newAssets.Where(asset => byId.ContainsKey(asset.Id))
                .Select(asset => byId[asset.Id]).ToList();
            added = newAssets.Count;
            if (scan.Skipped > 0)
                StatusText.Text = $"已导入 {added} 个媒体文件；跳过 {scan.Skipped} 个不可读取或不支持的路径 · MED-IMPORT-SCAN";
            else
                StatusText.Text = added == 0 ? "媒体已在媒体区" : $"已导入 {added} 个媒体文件";
            // Commit the archive before announcing the import as durable.
            // SaveProjectToAsync streams the media on a worker and keeps the
            // dispatcher responsive while the archive is replaced atomically.
            if (added > 0 && _projectPath is not null &&
                !await SaveProjectToAsync(_projectPath))
                StatusText.Text = "媒体已加入当前编辑会话，但尚未写入项目文件 · MED-IMPORT-SAVE";
            if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration)
                return [];
            // Metadata collection is independent from the visible bin commit.
            // It never blocks the drop gesture or a subsequent user action.
            MediaAsset[] metadataTargets = newAssets
                .Concat(imported.Where(asset => asset.Kind == MediaKind.Image
                    ? asset.Width is not > 0 || asset.Height is not > 0
                    : asset.DurationSeconds is not > 0))
                .DistinctBy(asset => asset.Id)
                .ToArray();
            if (metadataTargets.Length > 0)
            {
                _mediaMetadataCancellation ??= new CancellationTokenSource();
                CancellationToken token = _mediaMetadataCancellation.Token;
                _ = PopulateMediaMetadataAsync(targetProject, metadataTargets, token);
            }
            return imported;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText.Text = "媒体导入失败 · MED-IMPORT-SCAN";
            MessageBox.Show(this, $"无法读取媒体文件。\n阶段代码：MED-IMPORT-SCAN\n{exception.Message}",
                "媒体导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return [];
        }
        finally
        {
            _mediaImportGate.Release();
        }
    }

    private async Task PopulateMediaMetadataAsync(ProjectDocument targetProject, IReadOnlyList<MediaAsset> assets,
        CancellationToken cancellationToken)
    {
        var probes = new List<Task>(assets.Count);
        try
        {
            foreach (MediaAsset asset in assets)
            {
                await _mediaProbeGate.WaitAsync(cancellationToken);
                probes.Add(ProbeOneAsync(asset));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"MED-METADATA-PROBE: {exception}");
        }
        finally
        {
            try { await Task.WhenAll(probes); }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"MED-METADATA-PROBE: {exception}");
            }
        }

        async Task ProbeOneAsync(MediaAsset asset)
        {
            try
            {
                // Process.Start and the JSON decode inside the probe can block
                // before and after its awaits. Run the entire probe on a
                // worker; only the model commit returns to the dispatcher.
                MediaMetadata? metadata = await Task.Run(
                    () => MediaMetadataProbe.TryReadAsync(asset.SourcePath, cancellationToken),
                    cancellationToken);
                if (metadata is null) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(targetProject, _project) || cancellationToken.IsCancellationRequested) return;
                    MediaAsset? current = targetProject.Media.FirstOrDefault(candidate => candidate.Id == asset.Id);
                    if (current is null) return;
                    bool changed = System.Text.Json.JsonSerializer.Serialize(current.AudioStreams) != System.Text.Json.JsonSerializer.Serialize(metadata.AudioStreams ?? []) ||
                        (metadata.DurationSeconds is > 0 && current.DurationSeconds != metadata.DurationSeconds) ||
                        current.Width != metadata.Width || current.Height != metadata.Height ||
                        current.FramesPerSecond != metadata.FramesPerSecond || current.HasAudio != metadata.HasAudio ||
                        current.VideoCodec != metadata.VideoCodec || current.AudioCodec != metadata.AudioCodec ||
                        current.AudioChannels != metadata.AudioChannels || current.AudioSampleRate != metadata.AudioSampleRate ||
                        current.FileSizeBytes != metadata.FileSizeBytes ||
                        !current.Metadata.OrderBy(tag => tag.Key, StringComparer.OrdinalIgnoreCase)
                            .SequenceEqual(metadata.Tags.OrderBy(tag => tag.Key, StringComparer.OrdinalIgnoreCase)) ||
                        current.FileModifiedUtc != metadata.FileModifiedUtc;
                    if (metadata.DurationSeconds is > 0) current.DurationSeconds = metadata.DurationSeconds;
                    current.Width = metadata.Width;
                    current.Height = metadata.Height;
                    current.FramesPerSecond = metadata.FramesPerSecond;
                    current.HasAudio = metadata.HasAudio;
                    current.AudioStreams = metadata.AudioStreams?.ToList() ?? [];
                    current.VideoCodec = metadata.VideoCodec;
                    current.AudioCodec = metadata.AudioCodec;
                    current.AudioChannels = metadata.AudioChannels;
                    current.AudioSampleRate = metadata.AudioSampleRate;
                    current.FileSizeBytes = metadata.FileSizeBytes;
                    current.Metadata = new Dictionary<string, string>(metadata.Tags, StringComparer.OrdinalIgnoreCase);
                    current.FileModifiedUtc = metadata.FileModifiedUtc;
                    // A save can finish while a probe runs. Mark the project
                    // dirty when optional metadata arrives afterward.
                    if (changed)
                    {
                        // A timeline drop can precede this probe. Restore its
                        // provisional clip length without touching user edits.
                        if (!ReconcilePendingDropDurations()) MarkProjectDirty();
                        RefreshLibrary();
                    }
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"MED-METADATA-PROBE: {exception}");
            }
            finally { _mediaProbeGate.Release(); }
        }
    }

    private static ImportScan ScanImportPaths(IEnumerable<string> paths)
    {
        var found = new List<ImportCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int skipped = 0;
        foreach (string input in paths)
        {
            if (string.IsNullOrWhiteSpace(input)) continue;
            try
            {
                string path = Path.GetFullPath(input);
                if (Directory.Exists(path))
                {
                    // Folder drops are scanned off the UI thread as well.
                    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                    foreach (string nested in Directory.EnumerateFiles(path, "*", options)) AddCandidate(nested);
                }
                else if (File.Exists(path)) AddCandidate(path);
                else skipped++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                skipped++;
                System.Diagnostics.Debug.WriteLine($"MED-IMPORT-SCAN: {input}: {exception.Message}");
            }
        }
        return new ImportScan(found, skipped);

        void AddCandidate(string path)
        {
            MediaKind? kind = DetectMediaKind(path);
            if (kind is null)
            {
                skipped++;
                return;
            }
            if (!seen.Add(path)) return;
            found.Add(new ImportCandidate(path, kind.Value));
        }
    }

    private readonly record struct ImportScan(List<ImportCandidate> Candidates, int Skipped);
    private readonly record struct ImportCandidate(string Path, MediaKind Kind);

    private static MediaKind? DetectMediaKind(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp4" or ".mov" or ".m4v" or ".mkv" or ".avi" or ".wmv" or ".webm" or
                ".mpeg" or ".mpg" or ".ts" or ".mts" or ".m2ts" or ".flv" or ".3gp" or
                ".ogv" or ".vob" => MediaKind.Video,
            ".mp3" or ".wav" or ".m4a" or ".flac" or ".aac" or ".ogg" or ".wma" or
                ".aif" or ".aiff" => MediaKind.Audio,
            ".png" or ".jpg" or ".jpeg" or ".jfif" or ".bmp" or ".gif" or ".webp" or
                ".tif" or ".tiff" => MediaKind.Image,
            _ => null
        };
    }

    private sealed record LibraryItem(string Id, string Name, string Description, string AccentHex, int Category, object Source) : System.ComponentModel.INotifyPropertyChanged
    {
        public System.Windows.Media.Imaging.BitmapSource? Thumbnail { get; set; }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public void SetThumbnail(System.Windows.Media.Imaging.BitmapSource bitmap)
        { Thumbnail = bitmap; PropertyChanged?.Invoke(this, new(nameof(Thumbnail))); }
        public override string ToString() => $"{Name}  ·  {Description}";
    }
}
