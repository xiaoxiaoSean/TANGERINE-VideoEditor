using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TANGERINE_VideoEditor.Project;
using TANGERINE_VideoEditor.Editor;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Provides local project organization without writing to source media. The
/// active sequence uses the legacy project fields; inactive sequences retain
/// their own tracks and markers in ProjectDocument.Sequences.
/// </summary>
public partial class MainWindow
{
    private string? _selectedBinId;
    private ComboBox? _binSelector;
    private ComboBox? _sequenceSelector;
    private bool _updatingProjectMediaUi;
    private bool _proxyInProgress;
    private bool _consolidationInProgress;

    private void InitializeProjectMediaCommands()
    {
        // Insert the controls into the existing proportional library grid.
        // No pixel size is assigned to a control or panel.
        if (LibraryContentGrid is Grid content)
        {
            int toolbarRow = 2;
            content.RowDefinitions.Insert(toolbarRow, new RowDefinition { Height = GridLength.Auto });
            foreach (UIElement child in content.Children)
                if (Grid.GetRow(child) >= toolbarRow) Grid.SetRow(child, Grid.GetRow(child) + 1);
            var toolbar = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(toolbar, toolbarRow);
            content.Children.Add(toolbar);

            var binRow = new DockPanel { LastChildFill = true };
            toolbar.Children.Add(binRow);
            var binButton = NewToolButton("素材箱 ▾", (_, _) => ShowBinMenu((Button)_binSelector!.Tag));
            DockPanel.SetDock(binButton, Dock.Right);
            binRow.Children.Add(binButton);
            _binSelector = new ComboBox { ToolTip = "选择素材箱；搜索同时匹配名称、标签、元数据和路径" };
            _binSelector.Tag = binButton;
            _binSelector.SelectionChanged += (_, _) =>
            {
                if (_updatingProjectMediaUi) return;
                _selectedBinId = (_binSelector.SelectedItem as ProjectPickerItem)?.Id;
                RefreshLibrary();
            };
            binRow.Children.Add(_binSelector);

            var sequenceRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 5, 0, 0) };
            toolbar.Children.Add(sequenceRow);
            var sequenceButton = NewToolButton("序列 ▾", (_, _) => ShowSequenceMenu((Button)_sequenceSelector!.Tag));
            DockPanel.SetDock(sequenceButton, Dock.Right);
            sequenceRow.Children.Add(sequenceButton);
            _sequenceSelector = new ComboBox { ToolTip = "切换工程中的序列" };
            _sequenceSelector.Tag = sequenceButton;
            _sequenceSelector.SelectionChanged += (_, _) =>
            {
                if (_updatingProjectMediaUi || _sequenceSelector.SelectedItem is not ProjectPickerItem selected ||
                    selected.Id == _project.Sequence.Id) return;
                SwitchSequence(selected.Id!);
            };
            sequenceRow.Children.Add(_sequenceSelector);
        }

        var context = new ContextMenu();
        context.Items.Add(ContextCommand("在源监视器中打开", () => OpenSelectedSourceMonitor()));
        context.Items.Add(ContextCommand("编辑名称、标签和说明…", EditSelectedMediaDetails));
        context.Items.Add(ContextCommand("编辑源入出点…", EditSelectedSourceRange));
        context.Items.Add(ContextCommand("创建子片段…", CreateSelectedSubclip));
        context.Items.Add(ContextCommand("管理子片段…", ManageSelectedSubclips));
        context.Items.Add(ContextCommand("添加源标记…", AddSelectedSourceMarker));
        context.Items.Add(ContextCommand("查看媒体元数据…", ShowSelectedMediaMetadata));
        context.Items.Add(new Separator());
        context.Items.Add(ContextCommand("附加代理…", AttachSelectedProxy));
        context.Items.Add(ContextCommand("创建本地代理", async () => await CreateSelectedProxyAsync()));
        context.Items.Add(ContextCommand("切换代理预览", ToggleSelectedProxy));
        context.Items.Add(ContextCommand("重新链接媒体…", RelinkSelectedMedia));
        context.Items.Add(ContextCommand("重新链接代理…", RelinkProxyMedia));
        context.Items.Add(ContextCommand("移到素材箱…", MoveSelectedMedia));
        context.Items.Add(new Separator());
        context.Items.Add(ContextCommand("添加到当前序列", AddSelectedLibraryItem));
        AssetList.ContextMenu = context;
        AssetList.ContextMenuOpening += (_, _) =>
        {
            bool hasMedia = SelectedMedia() is not null;
            foreach (MenuItem item in context.Items.OfType<MenuItem>())
            {
                string header = item.Header?.ToString() ?? string.Empty;
                item.IsEnabled = header == "添加到当前序列"
                    ? AssetList.SelectedItem is LibraryItem selected && selected.Category <= 1
                    : hasMedia;
            }
        };

        // The menu is reachable even when the media panel is narrow.
        if (FindMainMenu() is Menu menu)
        {
            var projectMenu = new MenuItem { Header = "项目与序列(_P)" };
            projectMenu.Items.Add(ContextCommand("新建序列…", CreateSequence));
            projectMenu.Items.Add(ContextCommand("由所选媒体创建序列", CreateSequenceFromMedia));
            projectMenu.Items.Add(ContextCommand("编辑当前序列设置…", EditCurrentSequence));
            projectMenu.Items.Add(ContextCommand("添加序列标记…", AddSequenceMarker));
            projectMenu.Items.Add(ContextCommand("管理序列标记…", ManageSequenceMarkers));
            projectMenu.Items.Add(ContextCommand("打开序列索引", ShowSequenceIndex));
            projectMenu.Items.Add(ToggleCommand("全局静音（预览）", ToggleGlobalMute, () => _globalMute));
            projectMenu.Items.Add(ToggleCommand("独奏当前轨道（预览）", SoloSelectedTrack, () =>
                _soloTrackId is not null && _selectedTrack is not null &&
                string.Equals(_soloTrackId, _selectedTrack.Id, StringComparison.Ordinal)));
            projectMenu.Items.Add(new Separator());
            projectMenu.Items.Add(ContextCommand("创建素材箱…", CreateBin));
            projectMenu.Items.Add(ContextCommand("重链接离线媒体…", RelinkOfflineMedia));
            projectMenu.Items.Add(ContextCommand("整合项目副本…", async () => await ConsolidateProjectAsync()));
            menu.Items.Insert(Math.Max(0, menu.Items.Count - 1), projectMenu);
        }
        LibraryAddButton.ToolTip = "媒体：导入文件；文字、特效和转场：添加所选对象";
        // WPF rejects an unmodified letter as a KeyGesture while creating the
        // window. Bind Ctrl+M so the command remains available at startup.
        InputBindings.Add(new KeyBinding(new RelayCommand(AddSequenceMarker), Key.M, ModifierKeys.Control));
        RefreshProjectMediaCommands();
    }

    private void RefreshProjectMediaCommands()
    {
        if (_binSelector is null || _sequenceSelector is null) return;
        if (_selectedBinId is not null && _project.Bins.All(item => item.Id != _selectedBinId))
        {
            _selectedBinId = null;
            RefreshLibrary();
        }
        _updatingProjectMediaUi = true;
        try
        {
            if (_binSelector is not null)
            {
                var choices = new List<ProjectPickerItem> { new(null, "所有素材") };
                foreach (ProjectBin bin in _project.Bins.OrderBy(item => BinDepth(item)).ThenBy(item => item.Name))
                    choices.Add(new ProjectPickerItem(bin.Id, $"{new string('　', BinDepth(bin))}▣ {bin.Name}"));
                if (_selectedBinId is not null && choices.All(item => item.Id != _selectedBinId)) _selectedBinId = null;
                _binSelector.ItemsSource = choices;
                _binSelector.SelectedItem = choices.First(item => item.Id == _selectedBinId);
            }
            if (_sequenceSelector is not null)
            {
                var choices = _project.Sequences.Select(item => new ProjectPickerItem(item.Sequence.Id, item.Sequence.Name))
                    .Append(new ProjectPickerItem(_project.Sequence.Id, _project.Sequence.Name))
                    .OrderBy(item => item.Name, StringComparer.CurrentCulture).ToList();
                _sequenceSelector.ItemsSource = choices;
                _sequenceSelector.SelectedItem = choices.First(item => item.Id == _project.Sequence.Id);
            }
        }
        finally { _updatingProjectMediaUi = false; }
    }

    private Menu? FindMainMenu() => (Content as Grid)?.Children.OfType<Menu>().FirstOrDefault();

    private void CreateSelectedSubclip()
    {
        MediaAsset? asset = SelectedMedia();
        if (asset is null) return;
        // Never invent a range. With neither an out point nor a known duration
        // (a failed probe, say) any length would be a guess the user did not
        // make, so ask for real in/out points instead.
        double? knownEnd = asset.SourceOutSeconds ?? asset.DurationSeconds;
        if (knownEnd is null)
        { StatusText.Text = "该媒体没有出点或时长，请先在源监视器设置入点和出点 · MED-SUBCLIP-RANGE"; return; }
        double sourceEnd = knownEnd.Value;
        if (!double.IsFinite(sourceEnd) || sourceEnd <= asset.SourceInSeconds)
        { StatusText.Text = "源素材入出点无效，无法创建子片段 · MED-SUBCLIP-RANGE"; return; }
        string? name = PromptText("创建子片段", "名称", $"{asset.Name} - 子片段");
        if (string.IsNullOrWhiteSpace(name)) return;
        var subclip = SubclipService.CreateSubclip(asset, asset.SourceInSeconds, sourceEnd, name.Trim());
        _project.Subclips.Add(SubclipService.ToProjectSubclip(subclip));
        CommitProjectMediaChange($"已创建子片段“{subclip.Name}”");
    }

    private void ManageSelectedSubclips()
    {
        MediaAsset? asset = SelectedMedia();
        if (asset is null) return;
        List<ProjectSubclip> items = _project.Subclips.Where(item => item.ParentAssetId == asset.Id)
            .OrderBy(item => item.InSeconds).ToList();
        if (items.Count == 0) { StatusText.Text = "该媒体还没有子片段"; return; }
        var choices = items.Select(item => new ProjectPickerItem(item.Id,
            $"{item.Name}  [{item.InSeconds:0.###}–{item.OutSeconds:0.###} 秒]")).ToList();
        ProjectPickerItem? selected = PromptChoice("管理子片段", "选择要加入时间线的子片段", choices);
        if (selected?.Id is null) return;
        ProjectSubclip? sub = items.FirstOrDefault(item => item.Id == selected.Id);
        if (sub is null) return;
        // Reuse the shared conversion so the clip kind follows the media (an
        // image is not a video) instead of being re-derived here.
        TimelineClip clip = SubclipService.SubclipToTimelineClip(new SubclipService.Subclip
        {
            Id = sub.Id, Name = sub.Name, ParentAssetId = sub.ParentAssetId,
            InSeconds = sub.InSeconds, OutSeconds = sub.OutSeconds
        }, asset, _project.Sequence.PlayheadSeconds);
        clip.DurationSeconds = Math.Max(1 / Math.Max(1, _project.Sequence.FramesPerSecond), clip.DurationSeconds);
        // Honor the targeted track and skip locked ones, like paste and insert.
        TimelineTrack? track = ResolveCommandTarget(clip);
        if (track is null) { StatusText.Text = "没有可用的未锁定兼容轨道 · MED-SUBCLIP-TRACK"; return; }
        double requested = clip.StartSeconds;
        clip.StartSeconds = FirstFreeStart(track, requested, clip.DurationSeconds);
        SaveUndoState();
        track.Clips.Add(clip);
        _selectedClip = clip; _selectedTrack = track;
        MarkProjectDirty(); RefreshTimeline(); SeekPreview(clip.StartSeconds);
        StatusText.Text = clip.StartSeconds > requested + .0001
            ? $"播放头处已有片段，已将子片段“{sub.Name}”放到后面的空位"
            : $"已将子片段“{sub.Name}”加入时间线";
    }

    /// <summary>
    /// Earliest start at or after <paramref name="from"/> where a clip of the
    /// given length fits on the track without touching an existing clip, so
    /// adding a sub-clip never silently stacks on top of other material.
    /// </summary>
    private static double FirstFreeStart(TimelineTrack track, double from, double length)
    {
        double start = Math.Max(0, from);
        foreach (TimelineClip existing in track.Clips.OrderBy(item => item.StartSeconds))
        {
            double existingEnd = existing.StartSeconds + existing.DurationSeconds;
            if (existingEnd <= start + .0001) continue;
            if (existing.StartSeconds >= start + length - .0001) break;
            start = existingEnd;
        }
        return start;
    }

    private static Button NewToolButton(string label, RoutedEventHandler action)
    {
        var button = new Button { Content = label, Padding = new Thickness(9, 5, 9, 5),
            Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand };
        button.Click += action;
        return button;
    }

    private static MenuItem ContextCommand(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// A command that is an on/off switch. The check mark is read from
    /// <paramref name="isOn"/> each time the menu opens, because the menu is
    /// built once but the switch can change from elsewhere (a shortcut, or the
    /// same command in another menu) and a snapshot would go stale.
    /// </summary>
    private static MenuItem ToggleCommand(string label, Action action, Func<bool> isOn)
    {
        var item = new MenuItem { Header = label, IsChecked = isOn() };
        // Not IsCheckable: the click handler decides the new state, so the
        // check mark can never disagree with the real flag.
        item.Click += (_, _) => { action(); item.IsChecked = isOn(); };
        // A submenu's items turn visible every time the menu pops up, so this
        // refreshes the mark on each open. Loaded would fire only the first time.
        item.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true) item.IsChecked = isOn();
        };
        return item;
    }

    private void ShowBinMenu(Button owner)
    {
        var menu = new ContextMenu();
        menu.Items.Add(ContextCommand("新建素材箱…", CreateBin));
        menu.Items.Add(ContextCommand("重命名当前素材箱…", RenameBin));
        menu.Items.Add(ContextCommand("删除当前素材箱", DeleteBin));
        menu.Items.Add(ContextCommand("将所选媒体移到素材箱…", MoveSelectedMedia));
        owner.ContextMenu = menu;
        menu.PlacementTarget = owner;
        menu.IsOpen = true;
    }

    private void ShowSequenceMenu(Button owner)
    {
        var menu = new ContextMenu();
        menu.Items.Add(ContextCommand("新建序列…", CreateSequence));
        menu.Items.Add(ContextCommand("由所选媒体创建序列", CreateSequenceFromMedia));
        menu.Items.Add(ContextCommand("复制当前序列", DuplicateSequence));
        menu.Items.Add(ContextCommand("重命名当前序列…", RenameSequence));
        menu.Items.Add(ContextCommand("编辑序列设置…", EditCurrentSequence));
        menu.Items.Add(ContextCommand("删除当前序列", DeleteCurrentSequence));
        menu.Items.Add(new Separator());
        menu.Items.Add(ContextCommand("将其他序列嵌入时间轴…", AddNestedSequence));
        menu.Items.Add(ContextCommand("添加序列标记…", AddSequenceMarker));
        menu.Items.Add(ContextCommand("管理序列标记…", ManageSequenceMarkers));
        menu.Items.Add(ContextCommand("打开序列索引", ShowSequenceIndex));
        menu.Items.Add(ToggleCommand("全局静音（预览）", ToggleGlobalMute, () => _globalMute));
        menu.Items.Add(ToggleCommand("独奏当前轨道（预览）", SoloSelectedTrack, () =>
                _soloTrackId is not null && _selectedTrack is not null &&
                string.Equals(_soloTrackId, _selectedTrack.Id, StringComparison.Ordinal)));
        owner.ContextMenu = menu;
        menu.PlacementTarget = owner;
        menu.IsOpen = true;
    }

    private int BinDepth(ProjectBin bin)
    {
        int depth = 0;
        var visited = new HashSet<string>(StringComparer.Ordinal) { bin.Id };
        string? parentId = bin.ParentId;
        while (parentId is not null && visited.Add(parentId) && depth < 16)
        {
            ProjectBin? parent = _project.Bins.FirstOrDefault(item => item.Id == parentId);
            if (parent is null) break;
            depth++;
            parentId = parent.ParentId;
        }
        return depth;
    }

    private bool IsAssetInSelectedBin(MediaAsset asset)
    {
        if (_selectedBinId is null) return true;
        string? binId = asset.BinId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (binId is not null && visited.Add(binId))
        {
            if (binId == _selectedBinId) return true;
            binId = _project.Bins.FirstOrDefault(item => item.Id == binId)?.ParentId;
        }
        return false;
    }

    private readonly Dictionary<string, (string SourcePath, bool SourceExists, string? ProxyPath,
        bool ProxyExists)> _mediaAvailability = new(StringComparer.Ordinal);
    private bool _mediaAvailabilityRefreshRunning;
    private bool _mediaAvailabilityRefreshPending;
    private long _mediaAvailabilityLastRefreshTicks;
    private long _mediaAvailabilityProjectGeneration = -1;

    private string DescribeMediaAsset(MediaAsset asset)
    {
        // Description is part of the searchable list text even when the
        // compact row is the only visible view of the media bin.
        string kind = asset.Kind switch { MediaKind.Video => "视频", MediaKind.Audio => "音频", _ => "图片" };
        string resolution = asset.Width is > 0 && asset.Height is > 0 ? $" · {asset.Width}×{asset.Height}" : string.Empty;
        string duration = asset.DurationSeconds is double seconds &&
            double.IsFinite(seconds) && seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds
            ? $" · {TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss}" : string.Empty;
        string tags = asset.Tags.Count > 0 ? $" · {string.Join("、", asset.Tags)}" : string.Empty;
        string description = string.IsNullOrWhiteSpace(asset.Description) ? string.Empty : $" · {asset.Description}";
        // Give every tuple field a definite value even when no status has
        // been collected yet; WPF can render this row before the worker ends.
        var status = (SourcePath: string.Empty, SourceExists: false,
            ProxyPath: (string?)null, ProxyExists: false);
        bool foundStatus = _mediaAvailability.TryGetValue(asset.Id, out status);
        bool hasStatus = foundStatus && _mediaAvailabilityProjectGeneration == _projectGeneration &&
            status.SourcePath == asset.SourcePath && status.ProxyPath == asset.ProxyPath;
        string offline = hasStatus && !status.SourceExists ? " · 离线" : string.Empty;
        string proxy = asset.UseProxy && asset.CachedProxyAvailable
            ? " · 代理" : string.Empty;
        return kind + resolution + duration + tags + description + offline + proxy;
    }

    /// <summary>
    /// Refresh filesystem status outside the dispatcher. The list only reads
    /// snapshots, so a disconnected share cannot freeze scrolling or search.
    /// </summary>
    private void ScheduleMediaAvailabilityRefresh()
    {
        if (_mediaAvailabilityRefreshRunning) return;
        _mediaAvailabilityRefreshRunning = true;
        _ = RefreshMediaAvailabilityCoreAsync();
    }

    private async Task RefreshMediaAvailabilityCoreAsync()
    {
        ProjectDocument project = _project;
        long generation = _projectGeneration;
        try
        {
            var paths = project.Media.Select(asset =>
                (asset.Id, asset.SourcePath, asset.ProxyPath)).ToArray();
            var availability = await Task.Run(() => paths.Select(item =>
                (item.Id, item.SourcePath, SourceExists: File.Exists(item.SourcePath), item.ProxyPath,
                    ProxyExists: !string.IsNullOrWhiteSpace(item.ProxyPath) && File.Exists(item.ProxyPath)))
                .ToArray());
            if (!ReferenceEquals(project, _project) || generation != _projectGeneration)
            {
                _mediaAvailabilityRefreshPending = true;
                return;
            }
            if (_mediaAvailabilityProjectGeneration != generation)
            {
                _mediaAvailability.Clear();
                _mediaAvailabilityProjectGeneration = generation;
            }
            var byId = new Dictionary<string, MediaAsset>(StringComparer.Ordinal);
            foreach (MediaAsset asset in project.Media) byId.TryAdd(asset.Id, asset);
            foreach (var item in availability)
            {
                if (!byId.TryGetValue(item.Id, out MediaAsset? asset) ||
                    asset.SourcePath != item.SourcePath || asset.ProxyPath != item.ProxyPath)
                {
                    continue;
                }
                _mediaAvailability[item.Id] = (item.SourcePath, item.SourceExists,
                    item.ProxyPath, item.ProxyExists);
                asset.SetProxyAvailability(item.ProxyExists);
            }
            foreach (string staleId in _mediaAvailability.Keys
                .Where(id => !byId.ContainsKey(id)).ToArray())
                _mediaAvailability.Remove(staleId);
            _mediaAvailabilityLastRefreshTicks = Environment.TickCount64;
            if (CategoryList.SelectedIndex == 0) RefreshLibrary();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Debug.WriteLine($"MED-AVAILABILITY: {exception}");
            StatusText.Text = "媒体在线状态检查失败 · MED-AVAILABILITY";
        }
        finally
        {
            _mediaAvailabilityRefreshRunning = false;
            if (_mediaAvailabilityRefreshPending)
            {
                _mediaAvailabilityRefreshPending = false;
                ScheduleMediaAvailabilityRefresh();
            }
        }
    }

    private MediaAsset? SelectedMedia() => (AssetList.SelectedItem as LibraryItem)?.Source as MediaAsset;

    private void CreateBin()
    {
        string? name = PromptText("新建素材箱", "素材箱名称", "新建素材箱");
        if (string.IsNullOrWhiteSpace(name)) return;
        SaveUndoState();
        var bin = new ProjectBin { Name = name.Trim(), ParentId = _selectedBinId };
        _project.Bins.Add(bin);
        _selectedBinId = bin.Id;
        CommitProjectMediaChange($"已创建素材箱“{bin.Name}”");
    }

    private void RenameBin()
    {
        ProjectBin? bin = _project.Bins.FirstOrDefault(item => item.Id == _selectedBinId);
        if (bin is null) return;
        string? name = PromptText("重命名素材箱", "素材箱名称", bin.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == bin.Name) return;
        SaveUndoState();
        bin.Name = name.Trim();
        CommitProjectMediaChange("素材箱已重命名");
    }

    private void DeleteBin()
    {
        ProjectBin? bin = _project.Bins.FirstOrDefault(item => item.Id == _selectedBinId);
        if (bin is null || MessageBox.Show(this, $"删除素材箱“{bin.Name}”？其中的素材会移到上一级。",
                "删除素材箱", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SaveUndoState();
        foreach (ProjectBin child in _project.Bins.Where(item => item.ParentId == bin.Id)) child.ParentId = bin.ParentId;
        foreach (MediaAsset asset in _project.Media.Where(item => item.BinId == bin.Id)) asset.BinId = bin.ParentId;
        _project.Bins.Remove(bin);
        _selectedBinId = bin.ParentId;
        CommitProjectMediaChange("素材箱已删除，素材已移到上一级");
    }

    private void MoveSelectedMedia()
    {
        MediaAsset? asset = SelectedMedia();
        if (asset is null) return;
        var choices = new List<ProjectPickerItem> { new(null, "根目录") };
        choices.AddRange(_project.Bins.Select(bin => new ProjectPickerItem(bin.Id,
            $"{new string('　', BinDepth(bin))}▣ {bin.Name}")));
        ProjectPickerItem? choice = PromptChoice("移动素材", "选择目标素材箱", choices, asset.BinId);
        if (choice is null || choice.Id == asset.BinId) return;
        SaveUndoState();
        asset.BinId = choice.Id;
        CommitProjectMediaChange($"已移动素材“{asset.Name}”");
    }

    private void CommitProjectMediaChange(string status, bool redrawTimeline = false)
    {
        MarkProjectDirty();
        RefreshProjectMediaCommands();
        RefreshLibrary();
        if (redrawTimeline)
        {
            InvalidateCompositedPreview();
            RefreshTimeline();
            SeekPreview(_project.Sequence.PlayheadSeconds);
        }
        StatusText.Text = status;
    }

    private sealed record ProjectPickerItem(string? Id, string Name)
    {
        public override string ToString() => Name;
    }
}
