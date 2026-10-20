using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;
using TANGERINE_VideoEditor.Tools;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Auditions packaged music independently from the timeline monitor. Each
/// audition owns its player, so delayed decoder events cannot affect the next
/// selection. Audio is never removed when an audition is stopped.
/// </summary>
public partial class MainWindow
{
    private MediaPlayer? _builtinMusicPlayer;
    private BuiltinMusicTrack? _builtinMusicTrack;
    private bool _builtinMusicPlaying;
    private bool _builtinMusicProgressUpdating;
    private long _builtinMusicArtworkRequest;
    private bool _builtinMusicArtworkLoading;
    private bool BuiltinMusicOwnsMonitor => _builtinMusicPlayer is not null && _builtinMusicTrack is not null;
    private readonly System.Windows.Controls.Image _builtinMusicArtwork = new()
    {
        Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed, IsHitTestVisible = false
    };
    private readonly DispatcherTimer _builtinMusicTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private static string FormatMusicTime(double seconds)
    {
        TimeSpan time = TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Max(0, seconds) : 0);
        return $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
    }

    private void InitializeBuiltinMusicPreview()
    {
        System.Windows.Controls.Panel.SetZIndex(_builtinMusicArtwork, 25);
        RenderOptions.SetBitmapScalingMode(_builtinMusicArtwork, BitmapScalingMode.HighQuality);
        PreviewStage.Children.Add(_builtinMusicArtwork);
        _builtinMusicTimer.Tick += (_, _) => UpdateBuiltinMusicProgress();
        MusicPlaybackButton.Click += (_, _) =>
        {
            if (_builtinMusicPlayer is not MediaPlayer player) return;
            if (_builtinMusicPlaying) player.Pause();
            else player.Play();
            _builtinMusicPlaying = !_builtinMusicPlaying;
            MusicPlaybackButton.Content = _builtinMusicPlaying ? "❚❚" : "▶";
        };
        MusicPlaybackStopButton.Click += (_, _) => StopBuiltinMusicPreview();
        MusicPlaybackSlider.ValueChanged += (_, _) =>
        {
            if (_builtinMusicProgressUpdating || !MusicPlaybackSlider.IsMouseOver ||
                _builtinMusicPlayer is not MediaPlayer player) return;
            player.Position = TimeSpan.FromSeconds(MusicPlaybackSlider.Value);
            UpdateBuiltinMusicProgress();
        };
        Closed += (_, _) => StopBuiltinMusicPreview(restoreTimeline: false);
    }

    private void PreviewBuiltinMusic(BuiltinMusicTrack track)
    {
        StopCatalogPreview(restoreTimeline: false);
        try
        {
            string path = ValidateBuiltinMusicTrack(track);
            // MCP can request an audition while an object inspector is open.
            // Reveal the music controls before opening the player, and suppress
            // only the programmatic selection event to avoid a second audition.
            if (CategoryList.SelectedIndex != 5) CategoryList.SelectedIndex = 5;
            if (!AssetList.Items.Cast<LibraryItem>().Any(item => item.Id == track.Id)) AssetSearchBox.Clear();
            bool refreshing = _refreshingLibrary;
            _refreshingLibrary = true;
            try { AssetList.SelectedItem = AssetList.Items.Cast<LibraryItem>().FirstOrDefault(item => item.Id == track.Id); }
            finally { _refreshingLibrary = refreshing; }
            if (_isPlaying) TogglePlayback();
            StopAudioPreview();
            var player = new MediaPlayer { Volume = .75 };
            _builtinMusicPlayer = player;
            _builtinMusicTrack = track;
            _builtinMusicPlaying = true;
            BeginBuiltinMusicArtwork(track, player);
            MusicPlaybackTitle.Text = $"♫ {track.Title} · {track.Genre}";
            MusicPlaybackTitle.ToolTip = $"{track.Artist} · {track.Mood}\n{track.LicenseId}\n{track.SourceUrl}";
            MusicPlaybackSlider.Maximum = Math.Max(.1, track.DurationSeconds);
            MusicPlaybackSlider.Value = 0;
            MusicPlaybackTime.Text = $"00:00 / {FormatMusicTime(track.DurationSeconds)}";
            MusicPlaybackBar.Visibility = Visibility.Visible;
            MusicPlaybackButton.Content = "❚❚";
            player.MediaOpened += (_, _) =>
            {
                if (!ReferenceEquals(player, _builtinMusicPlayer)) return;
                if (player.NaturalDuration.HasTimeSpan)
                    MusicPlaybackSlider.Maximum = Math.Max(.1, player.NaturalDuration.TimeSpan.TotalSeconds);
                if (_builtinMusicPlaying) player.Play();
                else player.Pause();
                _builtinMusicTimer.Start();
                StatusText.Text = $"正在试听：{track.Title} · {track.Genre}";
            };
            player.MediaEnded += (_, _) =>
            {
                if (!ReferenceEquals(player, _builtinMusicPlayer)) return;
                player.Pause();
                player.Position = TimeSpan.Zero;
                _builtinMusicPlaying = false;
                MusicPlaybackButton.Content = "▶";
                UpdateBuiltinMusicProgress();
            };
            player.MediaFailed += (_, args) =>
            {
                if (!ReferenceEquals(player, _builtinMusicPlayer)) return;
                StopBuiltinMusicPreview();
                StatusText.Text = $"音乐试听失败 · MUSIC0004：{args.ErrorException.Message}";
            };
            player.Open(new Uri(path, UriKind.Absolute));
        }
        catch (Exception exception) when (exception is StageException or IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or NotSupportedException)
        {
            StopBuiltinMusicPreview(restoreTimeline: false);
            RestoreTimelineAfterBuiltinMusic();
            StatusText.Text = $"音乐试听失败 · {StageCodes.Resolve(exception, "MUSIC0004")}：{exception.Message}";
        }
    }

    private void UpdateBuiltinMusicProgress()
    {
        if (_isPlaying) { StopBuiltinMusicPreview(); return; }
        if (_builtinMusicPlayer is not MediaPlayer player || _builtinMusicTrack is null) return;
        double seconds = Math.Max(0, player.Position.TotalSeconds);
        _builtinMusicProgressUpdating = true;
        try
        {
            if (!MusicPlaybackSlider.IsMouseCaptureWithin)
                MusicPlaybackSlider.Value = Math.Min(MusicPlaybackSlider.Maximum, seconds);
            MusicPlaybackTime.Text = $"{FormatMusicTime(seconds)} / {FormatMusicTime(MusicPlaybackSlider.Maximum)}";
        }
        finally { _builtinMusicProgressUpdating = false; }
    }

    private void BeginBuiltinMusicArtwork(BuiltinMusicTrack track, MediaPlayer player)
    {
        // Revoke every timeline producer before loading the new cover. The
        // music owns the monitor during loading as well as after audio opens.
        ResetPreviewInteractionState();
        StopCachedPreview();
        DeferIdlePreview();
        ClearRenderedPreviewFrame();
        _previewImageRequest++;
        _previewImageLoadingPath = null;
        _previewImageFailedPath = null;
        _previewSourcePath = null;
        _activePreviewVisualClip = null;
        _activePreviewVisualSourcePath = null;
        _lastMediaTransformKey = null;
        _lastTitleLayoutKey = null;
        PreviewMediaElement.Close();
        PreviewMediaElement.Source = null;
        PreviewMediaElement.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewRenderedFrame.Visibility = Visibility.Collapsed;
        PreviewOverlay.Children.Clear();
        _mediaSelectionVisuals.Clear();
        PreviewOverlay.Opacity = 1;
        _builtinMusicArtwork.Source = null;
        _builtinMusicArtwork.Visibility = Visibility.Collapsed;
        _builtinMusicArtworkLoading = true;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewMessage.Text = "正在加载音乐封面…";
        long request = ++_builtinMusicArtworkRequest;
        ProjectDocument owner = _project;
        long generation = _projectGeneration;
        _ = LoadArtworkAsync();

        bool IsCurrent() => request == _builtinMusicArtworkRequest &&
            ReferenceEquals(player, _builtinMusicPlayer) && ReferenceEquals(track, _builtinMusicTrack) &&
            ReferenceEquals(owner, _project) && generation == _projectGeneration &&
            !_experienceLifetime.IsCancellationRequested;

        async Task LoadArtworkAsync()
        {
            try
            {
                BitmapSource artwork = await Task.Run(() =>
                {
                    using FileStream stream = File.OpenRead(track.ThumbnailPath);
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }, _experienceLifetime.Token);
                if (!IsCurrent()) return;
                _builtinMusicArtworkLoading = false;
                _builtinMusicArtwork.Source = artwork;
                _builtinMusicArtwork.Visibility = Visibility.Visible;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                ArgumentException or InvalidOperationException or NotSupportedException)
            {
                if (!IsCurrent()) return;
                _builtinMusicArtworkLoading = false;
                PreviewMessage.Text = "音乐封面无法读取";
                StatusText.Text = $"音乐封面读取失败 · MUSIC0005：{exception.Message}";
            }
        }
    }

    private void StopBuiltinMusicPreview(bool restoreTimeline = true)
    {
        MediaPlayer? player = _builtinMusicPlayer;
        bool ownedMonitor = BuiltinMusicOwnsMonitor;
        // Revoke ownership before Close can deliver decoder events.
        _builtinMusicPlayer = null;
        _builtinMusicTrack = null;
        _builtinMusicPlaying = false;
        _builtinMusicArtworkRequest++;
        _builtinMusicArtworkLoading = false;
        _builtinMusicArtwork.Source = null;
        _builtinMusicArtwork.Visibility = Visibility.Collapsed;
        _builtinMusicTimer.Stop();
        player?.Close();
        MusicPlaybackBar.Visibility = Visibility.Collapsed;
        if (ownedMonitor && restoreTimeline && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            RestoreTimelineAfterBuiltinMusic();
    }

    private void RestoreTimelineAfterBuiltinMusic()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _lastMediaTransformKey = null;
        _lastTitleLayoutKey = null;
        DrawTextOverlay();
        SeekPreview(_project.Sequence.PlayheadSeconds);
    }

    private static string ValidateBuiltinMusicTrack(BuiltinMusicTrack track)
    {
        string path = Path.GetFullPath(track.AudioPath);
        if (!File.Exists(path) || !double.IsFinite(track.DurationSeconds) || track.DurationSeconds <= 0 ||
            track.DurationSeconds >= 86400)
            throw new StageException("MUSIC0003", "内置音乐文件缺失或时长无效，请重新安装完整资源。");
        return path;
    }

    /// <summary>
    /// Registers an ordinary project media asset. ProjectFileService embeds
    /// its bytes in a .tvep archive exactly like an imported audio file. The
    /// caller owns undo and the project revision, including MCP mutations.
    /// </summary>
    private MediaAsset EnsureBuiltinMusicAsset(BuiltinMusicTrack track)
    {
        string path = ValidateBuiltinMusicTrack(track);
        // A user may relink an earlier music asset to an unrelated recording
        // while its provenance metadata remains. Reuse only this exact source
        // path, not a former catalog ID attached to different audio bytes.
        MediaAsset? asset = _project.Media.FirstOrDefault(item => item.Kind == MediaKind.Audio &&
            string.Equals(item.SourcePath, path, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            asset = new MediaAsset
            {
                Name = track.Title, Kind = MediaKind.Audio, SourcePath = path,
                DurationSeconds = track.DurationSeconds, HasAudio = true, BinId = _selectedBinId,
                Tags = ["内置音乐", track.Genre, track.Mood], LabelColor = "#66C8B0"
            };
            _project.Media.Add(asset);
        }
        asset.HasAudio = true;
        asset.DurationSeconds ??= track.DurationSeconds;
        asset.Metadata["BuiltinMusicId"] = track.Id;
        asset.Metadata["Artist"] = track.Artist;
        asset.Metadata["Genre"] = track.Genre;
        asset.Metadata["Mood"] = track.Mood;
        asset.Metadata["SourceUrl"] = track.SourceUrl;
        asset.Metadata["LicenseId"] = track.LicenseId;
        asset.Metadata["LicenseUrl"] = track.LicenseUrl;
        return asset;
    }

    /// <summary>
    /// Explicit replacement can point the same media ID at an unrelated song.
    /// Drop the former catalog identity and its automatic attribution rather
    /// than display the old cover or claim CC0 for the replacement recording.
    /// Archive extraction and consolidation copy bytes and do not call this.
    /// </summary>
    private static void ForgetBuiltinMusicProvenance(MediaAsset asset)
    {
        if (!asset.Metadata.ContainsKey("BuiltinMusicId")) return;
        string? genre = asset.Metadata.GetValueOrDefault("Genre");
        string? mood = asset.Metadata.GetValueOrDefault("Mood");
        foreach (string key in new[] { "BuiltinMusicId", "Artist", "Genre", "Mood", "SourceUrl", "LicenseId", "LicenseUrl" })
            asset.Metadata.Remove(key);
        asset.Tags.RemoveAll(tag => tag == "内置音乐" || tag == genre || tag == mood);
    }
}
