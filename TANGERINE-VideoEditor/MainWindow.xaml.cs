using System.Windows;
using System.Windows.Input;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Coordinates the object library, timeline, preview, and project file commands.
/// The working project remains the single source of truth for edit decisions.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = $"TANGERINE · 视频剪辑 {AppVersion.DisplayName}";
        VersionText.Text = AppVersion.DisplayName;
        InitializeProjectCommands();
        InitializeLibrary();
        InitializeImageSequenceCommands();
        InitializeProjectMediaCommands();
        InitializeTimeline();
        InitializePreview();
        InitializeAudioPreview();
        InitializeWorkspaceCommands();
        InitializePropertyClipboardCommands();
        InitializeHistoryPanelCommand();
        InitializeKeyframeCommands();
        InitializeAudioCommands();
        InitializeSubtitleCommands();
        InitializeVisualEffectsCommands();
        InitializeExtendedWorkspace();
        InitializeExportCommands();
        InitializeMcp();
        RefreshEditorFromProject();
    }

    /// <summary>
    /// True while keyboard focus sits in a control the user types into. An
    /// editable ComboBox hosts a template TextBox, so testing for TextBoxBase
    /// also covers the font and size boxes.
    /// </summary>
    private static bool IsTextInputFocused() =>
        Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
            or System.Windows.Controls.PasswordBox;

    private void RefreshEditorFromProject()
    {
        _selectedTransition = null;
        _selectedTransitionIncoming = null;
        _selectedTransitionPrior = null;
        _selectedTransitionTrack = null;
        _previewSourcePath = null;
        _previewImageRequest++;
        _previewImageLoadingPath = null;
        _previewImageFailedPath = null;
        _lastTitleLayoutKey = null;
        _lastTitleRangeStart = double.NaN;
        _lastTitleRangeEnd = double.NaN;
        PreviewMediaElement.Stop();
        PreviewMediaElement.Source = null;
        PreviewImage.Source = null;
        RefreshLibrary();
        RefreshProjectMediaCommands();
        RefreshTimeline();
        SeekPreview(_project.Sequence.PlayheadSeconds);
    }

    private void InitializeWorkspaceCommands()
    {
        ExitMenuItem.Click += (_, _) => Close();
        AboutMenuItem.Click += (_, _) => new AboutWindow { Owner = this }.ShowDialog();
        DefaultOpenWithMenuItem.Click += (_, _) => new DefaultOpenWithWindow { Owner = this }.ShowDialog();
        ShortcutsMenuItem.Click += (_, _) => MessageBox.Show(this,
            "空格：播放 / 暂停\nJ / K / L：倒放 / 暂停 / 正放\nCtrl+N：新建\nCtrl+O：打开\nCtrl+S：保存\nCtrl+I：导入媒体\nCtrl+M：添加序列标记\nCtrl+B：分割\nDelete：删除选中片段\nCtrl+Z / Ctrl+Y：撤销 / 重做",
            "快捷键", MessageBoxButton.OK, MessageBoxImage.Information);
        PreferencesMenuItem.Click += (_, _) => ShowEditorPreferences();
        GroupMenuItem.Click += (_, _) => GroupSelectedClips(false);
        UngroupMenuItem.Click += (_, _) => UngroupSelectedClips(false);
        LinkMenuItem.Click += (_, _) => GroupSelectedClips(true);
        UnlinkMenuItem.Click += (_, _) => UngroupSelectedClips(true);
        DeleteTrackMenuItem.Click += (_, _) => RemoveContextTrack();
        ResetWorkspaceMenuItem.Click += (_, _) => ResetEditorWorkspace();
        TimelineFreezeItem.Click += (_, _) => FreezeSelectedFrame();
        TimelineSpeedItem.Click += (_, _) => EditSelectedSpeed();
        TimelineReverseItem.Click += (_, _) => ToggleSelectedReverse();
        TimelinePasteMenuItem.IsEnabled = false;
        CutMenuItem.Click += (_, _) => CutSelectedClip();
        PasteMenuItem.IsEnabled = false;
        PasteMenuItem.Click += (_, _) => PasteClipboardClip();
        CopyMenuItem.IsEnabled = true;
        CopyMenuItem.Click += (_, _) => CopySelectedClip();
        TimelinePasteMenuItem.IsEnabled = false;
        Closing += (_, args) =>
        {
            if (_exportInProgress)
            {
                args.Cancel = true;
                StatusText.Text = "请先在导出窗口中取消导出";
                return;
            }
            if (!ConfirmDiscardChanges()) args.Cancel = true;
        };
        Closed += (_, _) =>
        {
            _mediaMetadataCancellation?.Cancel();
            _catalogPreviewCancellation?.Cancel();
            Editor.CatalogPreviewService.StopPreview();
            StopAudioPreview();
            _sourceMonitor?.Close();
            PreviewMediaElement.Close();
            if (_catalogPreviewPath is string catalogPath) DeleteCatalogPlaybackFile(catalogPath);
            _catalogPreviewPath = null;
            PreviewImage.Source = null;
            PreviewRenderedFrame.Source = null;
            _ = ReleaseProjectCacheSafelyAsync(_project);
        };
        // Handle Space before focused buttons or fields can consume it as a
        // click or text input. The editor reserves this key for transport.
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None) return;
            TogglePlayback();
            args.Handled = true;
        };
        PreviewKeyDown += (_, args) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            if (args.Key is not (Key.J or Key.K or Key.L)) return;
            // These are letters. While the user types in a search box, a name
            // field or any other text input, they must reach the text, not
            // start playback; swallowing them would make "j", "k" and "l"
            // impossible to type anywhere in the editor.
            if (IsTextInputFocused()) return;
            if (args.Key == Key.J) SetPlaybackDirection(-1);
            else if (args.Key == Key.K) { if (_isPlaying) TogglePlayback(); }
            else SetPlaybackDirection(1);
            args.Handled = true;
        };
        InputBindings.Add(new KeyBinding(new RelayCommand(SplitSelectedClip), Key.B, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(DeleteSelectedClip), Key.Delete, ModifierKeys.None));
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Delete || Keyboard.Modifiers != ModifierKeys.None ||
                _selectedTransition is null) return;
            DeleteSelectedClip();
            args.Handled = true;
        };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Delete || Keyboard.Modifiers != ModifierKeys.None ||
                _selectedKeyframe is null || _selectedClip is null) return;
            TimelineTrack? track = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(_selectedClip));
            if (track is not null) RemoveSelectedKeyframe(track, _selectedClip, _selectedKeyframe);
            args.Handled = true;
        };
        InputBindings.Add(new KeyBinding(new RelayCommand(Undo), Key.Z, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(Redo), Key.Y, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(async () => await ImportMediaAsync()), Key.I, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(CopySelectedClip), Key.C, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(CutSelectedClip), Key.X, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(PasteClipboardClip), Key.V, ModifierKeys.Control));
    }
}
